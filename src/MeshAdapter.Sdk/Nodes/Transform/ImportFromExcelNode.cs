using System.Diagnostics.CodeAnalysis;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.ExcelImport;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Pipeline node that imports data from an Excel file
/// </summary>
/// <param name="next">Next node in the pipeline</param>
/// <param name="ckCacheService">Construction Kit cache service</param>
/// <param name="etlContext">The ETL context</param>
[NodeConfiguration(typeof(ImportFromExcelNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class ImportFromExcelNode(
    NodeDelegate next,
    ICkCacheService ckCacheService,
    IMeshEtlContext etlContext)
    : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        if (!EnsureAndValidateData(dataContext, nodeContext, out var data, out var columns, out var importType, out var rootNodeId))
        {
            return;
        }

        var columnContext = new ColumnContext(columns);


        var entities = new List<HierarchicalEntity>();

        if (importType == Constants.TreePathImportType)
        {
            ParseTreeByPath(data, columnContext, entities);
            EnsureTreeParentsExist(entities);

        }
        else if(importType == Constants.TreeModelImportType2)
        {
            ParseTreeByColumns(data, columnContext, entities);
        }
        else
        {
            nodeContext.Error("Unknown import type");
            return;
        }


        await StoreInDatabase(entities, rootNodeId, nodeContext);


        await next(dataContext, nodeContext);
    }

    private void ParseTreeByColumns(JArray data, ColumnContext columnContext, List<HierarchicalEntity> entities)
    {
        var maxLayers = columnContext.GetMaxLayer();
        for (var iLayer = 1; iLayer <= maxLayers; iLayer++)
        {
            foreach (var e in data)
            {
                if (e is not JArray entry)
                {
                    continue;
                }
                
                // we already created this entity
                var ckTypeId = columnContext.GetCkTypeId(entry, iLayer);
                var name = columnContext.GetValue<string>(entry, "name", iLayer)!;
                if(entities.Any(x=> x.Name == name && ckTypeId == x.CkTypeId))
                { 
                    // we can't ignore this entity, because it might have different attributes
                }
                
                var parentName = ParentNameParser.ParseLayerBasedName(columnContext, entry, iLayer);

                CkId<CkTypeId>? parentCkTypeId = null;
                if (!string.IsNullOrWhiteSpace(parentName))
                {
                    parentCkTypeId = columnContext.GetCkTypeId(entry, iLayer - 1);
                }

                var entity = new HierarchicalEntity(ckTypeId, name, parentName, parentCkTypeId);
                foreach(var attributePath in columnContext.GetAttributePaths(iLayer))
                {
                    var value = columnContext.GetValue<string>(entry, attributePath, iLayer);
                    if(value == null || attributePath == "name")
                    {
                        continue;
                    }

                    entity.Attributes.Add(new(attributePath, value));
                }
                
                var possibleDuplicate = entities.FirstOrDefault(x => x.Name == name && x.CkTypeId == ckTypeId);
                if (possibleDuplicate == null)
                {
                    entities.Add(entity);
                }
                else
                {
                    // we need to check if the attributes are the same
                    var sameAttributes = entity.Attributes.All(x => possibleDuplicate.Attributes.Any(y => y.Item1 == x.Item1 && y.Item2 == x.Item2));
                    if (!sameAttributes)
                    {
                        entities.Add(entity);
                    }
                }
            }
        }
    }
    
    private static void ParseTreeByPath(JArray data, ColumnContext columnContext, List<HierarchicalEntity> entities)
    {
        foreach (var jToken in data)
        {
            var entry = (JArray)jToken;
            var ckTypeId = columnContext.GetCkTypeId(entry);
            var name = columnContext.GetValue<string>(entry, "name");
            if (name == null)
            {
                continue;
            }
        
            name = name.Trim();
        
            var parentName = ParentNameParser.ParseSeparatorBased(name, out var itemName);
        
            var entity = new HierarchicalEntity(ckTypeId, name, parentName, "Basic/TreeNode");
            entity.Attributes.Add(new ("Name", itemName));
            entities.Add(entity);
            foreach (var columnName in columnContext.GetAttributePaths())
            {
                if (columnName == "name")
                {
                    continue;
                }
        
                var value = columnContext.GetValue<string>(entry, columnName);
                if (value == null)
                {
                    continue;
                }
        
                entity.Attributes.Add(new(columnName, value));
            }
        }
    }


    private async Task StoreInDatabase(List<HierarchicalEntity> buffer, string rootNodeId, INodeContext nodeContext)
    {

        var rootId = new OctoObjectId(rootNodeId);

        var assocs = new List<AssociationUpdateInfo>();
        var entities = new List<IEntityUpdateInfo<RtEntity>>();

        foreach (var entity in buffer)
        {
            var entityParent = buffer.FirstOrDefault(x => x.Name == entity.ParentName && x.CkTypeId == entity.ParentCkTypeId);

            entity.RtId ??= OctoObjectId.GenerateNewId();

            if (entityParent is { RtId: null })
            {
                entityParent.RtId = OctoObjectId.GenerateNewId();
            }

            var rtEntity = await etlContext.TenantRepository.CreateTransientRtEntityAsync(entity.CkTypeId);
            rtEntity.RtId = entity.RtId.Value;
            rtEntity.RtWellKnownName = entity.Name;

            var ckTypeGraph = ckCacheService.GetCkType(etlContext.TenantId, entity.CkTypeId);

            foreach (var attribute in entity.Attributes)
            {
                if (ckTypeGraph.AllAttributesByName.TryGetValue(attribute.Item1.ToPascalCase(), out var typeAttributeGraph))
                {
                    rtEntity.SetAttributeValue(attribute.Item1.ToPascalCase(), typeAttributeGraph.ValueType, attribute.Item2);
                }
            }

            var insert = EntityUpdateInfo<RtEntity>.CreateInsert(rtEntity);

            entities.Add(insert);

            var association = AssociationUpdateInfo.CreateCreate(new RtEntityId(entity.CkTypeId, entity.RtId.Value),
                new RtEntityId(entityParent?.CkTypeId ?? "Basic/Tree", entityParent?.RtId ?? rootId),
                new CkId<CkAssociationRoleId>("System/ParentChild"));

            assocs.Add(association);
        }

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
            entityParent.Attributes.Add(new ("Name", parentItemName));

            buffer.Add(entityParent);
            entityStack.Push(entityParent);
        }
    }

    private static bool EnsureAndValidateData(
        IDataContext dataContext,
        INodeContext nodeContext,
        [NotNullWhen(true)] out JArray? data,
        [NotNullWhen(true)] out JArray? columns,
        [NotNullWhen(true)] out string? importType,
        [NotNullWhen(true)] out string? rootNodeId)
    {
        data = null;
        columns = null;
        importType = null;
        rootNodeId = null;

        var o = dataContext.Current as JObject;

        if (o?["body"] is not JObject body)
        {
            nodeContext.Error("Body is null");
            return false;
        }

        if (body["columns"] is not JArray c)
        {
            nodeContext.Error("Columns are null");
            return false;
        }


        if (body["data"] is not JArray d)
        {
            nodeContext.Error("Data is null");
            return false;
        }

        var i = body.Value<string>("importType");
        if (i == null)
        {
            nodeContext.Error("Import type is not TreeModel");
            return false;
        }

        var r = body.Value<string>("treeModelRootRtId");
        if (r == null)
        {
            nodeContext.Error("Root node id is not set");
            return false;
        }

        data = d;
        columns = c;
        importType = i;
        rootNodeId = r;

        return true;
    }
}