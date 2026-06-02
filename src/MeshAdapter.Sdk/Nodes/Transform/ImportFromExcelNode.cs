using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.ExcelImport;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Pipeline node that imports data from an Excel file
/// </summary>
/// <param name="next">Next node in the pipeline</param>
/// <param name="ckCacheService">Construction Kit cache service</param>
/// <param name="wellKnownNameLoader">Well-known name loader service</param>
/// <param name="etlContext">The ETL context</param>
[NodeConfiguration(typeof(ImportFromExcelNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class ImportFromExcelNode(
    NodeDelegate next,
    ICkCacheService ckCacheService,
    IWellKnownNameLoader wellKnownNameLoader,
    IMeshEtlContext etlContext)
    : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        if (!GetImportType(dataContext, nodeContext, out var importType))
        {
            await next(dataContext, nodeContext);
            return;
        }

        if (!EnsureAndValidateData(dataContext, nodeContext, out var data, out var columns))
        {
            await next(dataContext, nodeContext);
            return;
        }

        var columnContext = new ColumnContext(columns);

        if (importType == Constants.TreePathImportType)
        {
            var entities = new List<HierarchicalEntity>();
            if (!EnsureAndValidateTreeData(dataContext, nodeContext, out var rootNodeId))
            {
                await next(dataContext, nodeContext);
                return;
            }

            ParseTreeByPath(data, columnContext, entities);
            EnsureTreeParentsExist(entities);
            await StoreTreeInDatabase(entities, rootNodeId, nodeContext);
        }
        else if (importType == Constants.TreeColumnImportType)
        {
            var entities = new List<HierarchicalEntity>();
            if (!EnsureAndValidateTreeData(dataContext, nodeContext, out var rootNodeId))
            {
                await next(dataContext, nodeContext);
                return;
            }

            var entitiesByWellKnownName = await GetEntitiesByWellKnownName(data, columnContext);
            ParseTreeByColumns(data, columnContext, entities, entitiesByWellKnownName);
            await StoreTreeInDatabase(entities, rootNodeId, nodeContext);
        }
        else
        {
            throw MeshAdapterPipelineExecutionException.UnknownImportType(importType);
        }


        await next(dataContext, nodeContext);
    }

    private async Task<Dictionary<int, IDictionary<string, RtEntity>>> GetEntitiesByWellKnownName(JsonArray data,
        ColumnContext columnContext)
    {
        Dictionary<int, IDictionary<string, RtEntity>> entities = new();
        var maxLayers = columnContext.GetMaxLayer();
        for (var iLayer = 1; iLayer <= maxLayers; iLayer++)
        {
            foreach (var columnIndex in columnContext.GetColumns(iLayer))
            {
                if (columnIndex.Action != ColumnContext.ActionType.AssignByWellKnownName)
                {
                    continue;
                }

                List<string> values = new List<string>();
                for (int i = 0; i < data.Count; i++)
                {
                    var e = data[i];
                    if (e is not JsonArray entry)
                    {
                        continue;
                    }

                    var wellKnownName = columnContext.GetValueByPath<string?>(entry, "name", iLayer);
                    if (string.IsNullOrWhiteSpace(wellKnownName))
                    {
                        throw MeshAdapterPipelineExecutionException.NoWellKnownNameValue(iLayer, i + 1);
                    }

                    wellKnownName = wellKnownName.Trim();
                    values.Add(wellKnownName);
                }

                var ckTypeId = columnContext.GetCkTypeId(iLayer);
                var wellKnownNames = values.Distinct().ToList();
                var loadedEntities = await wellKnownNameLoader.LoadAsync(wellKnownNames, ckTypeId);
                if (!loadedEntities.Any())
                {
                    throw MeshAdapterPipelineExecutionException.NoWellKnownNamesFound(iLayer);
                }

                entities.Add(iLayer, loadedEntities);
            }
        }

        return entities;
    }

    private void ParseTreeByColumns(JsonArray data, ColumnContext columnContext, List<HierarchicalEntity> entities,
        IDictionary<int, IDictionary<string, RtEntity>> entitiesByWellKnownName)
    {
        var maxLayers = columnContext.GetMaxLayer();
        for (var iLayer = 1; iLayer <= maxLayers; iLayer++)
        {
            foreach (var e in data)
            {
                if (e is not JsonArray entry)
                {
                    continue;
                }

                var actionType = columnContext.GetActionType(iLayer);
                if (actionType == ColumnContext.ActionType.Create)
                {
                    // Get ck type id and name. Be aware of the layer for column import!
                    var ckTypeId = columnContext.GetCkTypeId(iLayer);
                    var name = columnContext.GetValueByPath<string>(entry, "name", iLayer)!;
                    var parentName = ParentNameParser.ParseLayerBasedName(columnContext, entry, iLayer);

                    RtCkId<CkTypeId>? parentCkTypeId = null;
                    if (!string.IsNullOrWhiteSpace(parentName))
                    {
                        parentCkTypeId = columnContext.GetCkTypeId(iLayer - 1);
                    }

                    var entity = new HierarchicalEntity(ckTypeId, name, parentName, parentCkTypeId);
                    foreach (var columnIndex in columnContext.GetColumns(iLayer))
                    {
                        var value = columnContext.GetValueByIndex<string>(entry, columnIndex.Index);
                        if (value == null)
                        {
                            continue;
                        }

                        entity.Attributes.Add(new(columnIndex.AttributePath, value));
                    }

                    var possibleDuplicate = entities.FirstOrDefault(x => x.Name == name && x.CkTypeId == ckTypeId);
                    if (possibleDuplicate == null)
                    {
                        entities.Add(entity);
                    }
                    else
                    {
                        // we need to check if the attributes are the same
                        var sameAttributes = entity.Attributes.All(x =>
                            possibleDuplicate.Attributes.Any(y => y.Item1 == x.Item1 && y.Item2 == x.Item2));
                        if (!sameAttributes)
                        {
                            entities.Add(entity);
                        }
                    }
                }
                else if (actionType == ColumnContext.ActionType.AssignByWellKnownName)
                {
                    var name = columnContext.GetValueByPath<string>(entry, "name", iLayer)!;
                    var parentName = ParentNameParser.ParseLayerBasedName(columnContext, entry, iLayer);
                    if (string.IsNullOrWhiteSpace(parentName))
                    {
                        throw MeshAdapterPipelineExecutionException.ParentNotFound(iLayer);
                    }

                    var parentCkTypeId = columnContext.GetCkTypeId(iLayer - 1);

                    if (!entitiesByWellKnownName.TryGetValue(iLayer, out var wellKnownNameDictionary))
                    {
                        throw MeshAdapterPipelineExecutionException.NoWellKnownNamesFoundForLayer(iLayer);
                    }

                    if (!wellKnownNameDictionary.TryGetValue(name.Trim().ToLower(), out var rtEntity))
                    {
                        throw MeshAdapterPipelineExecutionException.NoEntityFound(iLayer, name);
                    }


                    var entity = new HierarchicalEntity(rtEntity.RtId, rtEntity.CkTypeId!, parentName, parentCkTypeId);
                    entities.Add(entity);
                }
                else
                {
                    throw MeshAdapterPipelineExecutionException.UnknownActionType(actionType);
                }
            }
        }
    }

    private static void ParseTreeByPath(JsonArray data, ColumnContext columnContext, List<HierarchicalEntity> entities)
    {
        foreach (var jToken in data)
        {
            var entry = (JsonArray)jToken!;
            var ckTypeId = columnContext.GetCkTypeId();
            var name = columnContext.GetValueByPath<string>(entry, "name");
            if (name == null)
            {
                continue;
            }

            name = name.Trim();

            var parentName = ParentNameParser.ParseSeparatorBased(name, out var itemName);

            var entity = new HierarchicalEntity(ckTypeId, name, parentName, "Basic/TreeNode");
            entity.Attributes.Add(new("Name", itemName));
            entities.Add(entity);
            foreach (var columnIndex in columnContext.GetColumns())
            {
                if (columnIndex.AttributePath == "name")
                {
                    continue;
                }

                var value = columnContext.GetValueByIndex<string>(entry, columnIndex.Index);
                if (value == null)
                {
                    continue;
                }

                entity.Attributes.Add(new(columnIndex.AttributePath, value));
            }
        }
    }


    private async Task StoreTreeInDatabase(List<HierarchicalEntity> buffer, string rootNodeId,
        INodeContext nodeContext)
    {
        var rootId = new OctoObjectId(rootNodeId);

        var assocs = new List<AssociationUpdateInfo>();
        var entities = new List<IEntityUpdateInfo<RtEntity>>();

        foreach (var entity in buffer)
        {
            var entityParent = string.IsNullOrWhiteSpace(entity.ParentName)
                ? null
                : buffer.FirstOrDefault(x => x.Name == entity.ParentName);

            if (entity.IsObjectInRepository && entity.RtId.HasValue && entity.ParentCkTypeId != entity.CkTypeId)
            {
                var association = AssociationUpdateInfo.CreateInsert(new RtEntityId(entity.CkTypeId, entity.RtId.Value),
                    new RtEntityId(entityParent?.CkTypeId ?? "Basic/Tree", entityParent?.RtId ?? rootId),
                    entity.AssociationRoleId);

                assocs.Add(association);
            }
            else
            {
                entity.RtId ??= OctoObjectId.GenerateNewId();

                if (entityParent is { RtId: null })
                {
                    entityParent.RtId = OctoObjectId.GenerateNewId();
                }

                var rtEntity = await etlContext.TenantRepository.CreateTransientRtEntityByRtCkIdAsync(entity.CkTypeId);
                rtEntity.RtId = entity.RtId.Value;
                rtEntity.RtWellKnownName = entity.Name;

                var ckTypeGraph = ckCacheService.GetRtCkType(etlContext.TenantId, entity.CkTypeId);

                foreach (var attribute in entity.Attributes)
                {
                    if (ckTypeGraph.AllAttributesByName.TryGetValue(attribute.Item1.ToPascalCase(),
                            out var typeAttributeGraph))
                    {
                        rtEntity.SetAttributeValue(attribute.Item1.ToPascalCase(), typeAttributeGraph.ValueType,
                            attribute.Item2);
                    }
                }

                var insert = EntityUpdateInfo<RtEntity>.CreateInsert(rtEntity);

                entities.Add(insert);

                var association = AssociationUpdateInfo.CreateInsert(new RtEntityId(entity.CkTypeId, entity.RtId.Value),
                    new RtEntityId(entityParent?.CkTypeId ?? "Basic/Tree", entityParent?.RtId ?? rootId),
                    entity.AssociationRoleId);

                assocs.Add(association);
            }
        }

        await ApplyChangesInRepositoryAsync(nodeContext, entities, assocs);
    }

    private async Task ApplyChangesInRepositoryAsync(INodeContext nodeContext,
        List<IEntityUpdateInfo<RtEntity>> entities, List<AssociationUpdateInfo> assocs)
    {
        using var session = etlContext.TenantRepository.GetSession();

        try
        {
            session.StartTransaction();
            var res = new OperationResult();
            await etlContext.TenantRepository.ApplyChangesAsync(session, entities, assocs, res);
            await session.CommitTransactionAsync();
        }
        catch (Exception e)
        {
            await session.AbortTransactionAsync();
            nodeContext.Error(e, "Error while storing data in database");
        }
    }


    private void EnsureTreeParentsExist(List<HierarchicalEntity> buffer)
    {
        var entityStack = new Stack<HierarchicalEntity>();

        buffer.ForEach(x => entityStack.Push(x));

        while (entityStack.Count != 0)
        {
            var entity = entityStack.Pop();

            if (entity.ParentName == null) // we found a root node
            {
                continue;
            }

            var entityParent = buffer.FirstOrDefault(x => x.Name == entity.ParentName);

            if (entityParent != null)
            {
                continue;
            }

            var name = entity.ParentName;
            var parentName = ParentNameParser.ParseSeparatorBased(name, out var parentItemName);

            entityParent = new HierarchicalEntity("Basic/TreeNode", name, parentName, "Basic/TreeNode")
            {
                RtId = OctoObjectId.GenerateNewId()
            };
            entityParent.Attributes.Add(new("Name", parentItemName));

            buffer.Add(entityParent);
            entityStack.Push(entityParent);
        }
    }

    private static bool GetImportType(
        IDataContext dataContext,
        INodeContext nodeContext,
        [NotNullWhen(true)] out string? importType
    )
    {
        importType = dataContext.Get<string>("$.body.importType");
        if (importType == null)
        {
            nodeContext.Error("Import type is not TreeModel");
            return false;
        }
        return true;
    }

    private static bool EnsureAndValidateTreeData(
        IDataContext dataContext,
        INodeContext nodeContext,
        [NotNullWhen(true)] out string? rootNodeId)
    {
        rootNodeId = dataContext.Get<string>("$.body.treeModelRootRtId");
        if (rootNodeId == null)
        {
            nodeContext.Error("Root node id is not set");
            return false;
        }
        return true;
    }

    private static bool EnsureAndValidateData(
        IDataContext dataContext,
        INodeContext nodeContext,
        [NotNullWhen(true)] out JsonArray? data,
        [NotNullWhen(true)] out JsonArray? columns)
    {
        data = null;
        columns = dataContext.Get<JsonArray>("$.body.columns");
        if (columns == null)
        {
            nodeContext.Error("Columns are null");
            return false;
        }

        data = dataContext.Get<JsonArray>("$.body.data");
        if (data == null)
        {
            nodeContext.Error("Data is null");
            return false;
        }

        return true;
    }
}