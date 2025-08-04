using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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

            ParseTreeByColumns(data, columnContext, entities);
            await StoreTreeInDatabase(entities, rootNodeId, nodeContext);
        }
        else if (importType == Constants.TreeOrderImportType)
        {
            await etlContext.TenantRepository.LoadCacheForTenantAsync(ckCacheService);

            var entities = new List<IEntityUpdateInfo<RtEntity>>();
            var assocs = new List<AssociationUpdateInfo>();
            await ParseOrderAsync(data, columnContext, entities, assocs);
            await ApplyChangesInRepositoryAsync(nodeContext, entities, assocs);
        }
        else if (importType == Constants.TreeOrderFeedbackImportType)
        {
            await etlContext.TenantRepository.LoadCacheForTenantAsync(ckCacheService);


            int skip = 0;
            do
            {
                var entities = new List<IEntityUpdateInfo<RtEntity>>();
                var assocs = new List<AssociationUpdateInfo>();

                await ParseOrderFeedbackAsync(data, columnContext, entities, assocs, skip, 15000);

                await ApplyChangesInRepositoryAsync(nodeContext, entities, assocs);

                skip += 15000;
            } while (skip < data.Count);
        }
        else
        {
            nodeContext.Error("Unknown import type");
            return;
        }


        await next(dataContext, nodeContext);
    }

    private async Task ParseOrderFeedbackAsync(JArray data, ColumnContext columnContext,
        List<IEntityUpdateInfo<RtEntity>> entitiesList, List<AssociationUpdateInfo> associationList, int skip, int take)
    {
        var ckTypeId = columnContext.GetCkTypeId(1, "Industry.Maintenance/OrderFeedback");
        var validPaths = ckCacheService.GetCkTypeQueryColumnPaths(etlContext.TenantId, ckTypeId)
            .ToDictionary(k => k.Path, v => v);

        var loadedEntities = new Dictionary<Tuple<CkId<CkTypeId>, string>, RtEntity>();
        foreach (var jToken in data.Skip(skip).Take(take))
        {
            var entry = (JArray)jToken;

            var rtEntity = await etlContext.TenantRepository.CreateTransientRtEntityAsync(ckTypeId);
            foreach (var columnIndex in columnContext.GetColumns())
            {
                var value = columnContext.GetValueByIndex<string>(entry, columnIndex.Index);
                if (value == null)
                {
                    continue;
                }
#if FALSE
                if (validPaths.TryGetValue(columnIndex.AttributePath, out var typeQueryColumn))
                {
                    switch (typeQueryColumn.ValueType)
                    {
                        case AttributeValueTypesDto.Association:
                        {
                            if (typeQueryColumn.AssociationTuple == null)
                            {
                                throw new Exception($"Association tuple is null for {columnIndex}");
                            }

                            var associationDirectionTuple = typeQueryColumn.AssociationTuple;

                            var keyTuple = new Tuple<CkId<CkTypeId>, string>(associationDirectionTuple.CkTypeId, value);
                            if (!loadedEntities.TryGetValue(keyTuple, out RtEntity? treeEntity))
                            {
                                var dataOperation = DataQueryOperation.Create();

                                if (associationDirectionTuple.CkTypeId == "Industry.Maintenance/Order")
                                {
                                    dataOperation.FieldEquals("OrderNumber", value);
                                }
                                else if (associationDirectionTuple.CkTypeId == "Industry.Maintenance/Employee")
                                {
                                    dataOperation.FieldEquals("StaffNumber", int.Parse(value));
                                }
                                else
                                {
                                    dataOperation.FieldEquals(nameof(RtEntity.RtWellKnownName), value);
                                }

                                using var session = etlContext.TenantRepository.GetSession();
                                session.StartTransaction();
                                var r = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(session,
                                    associationDirectionTuple.CkTypeId, dataOperation, 0, 1);

                                await session.CommitTransactionAsync();

                                if (r.TotalCount == 0)
                                {
                                    throw new Exception($"No entity found for {columnIndex} with value {value}");
                                }

                                treeEntity = r.Items.First();
                                loadedEntities.Add(keyTuple, treeEntity);
                            }

                            var association = AssociationUpdateInfo.CreateCreate(
                                rtEntity.ToRtEntityId(),
                                treeEntity.ToRtEntityId(),
                                associationDirectionTuple.CkAssociationRoleId);
                            associationList.Add(association);

                            break;
                        }
                        default:
                            object convertedValue = value;
                            if (typeQueryColumn.ValueType == AttributeValueTypesDto.DateTime)
                            {
                                if (columnIndex.ColumnType == ColumnContext.ColumnType.ScalarDate)
                                {
                                    var date = rtEntity.GetAttributeValueOrDefault(columnIndex.AttributePath
                                        .ToPascalCase());
                                    convertedValue = DateTime.Parse(value,
                                        CultureInfo.GetCultureInfoByIetfLanguageTag("de-DE"));
                                    if (date is DateTime dateTime)
                                    {
                                        convertedValue = new DateTime(((DateTime)convertedValue).Year,
                                            ((DateTime)convertedValue).Month, ((DateTime)convertedValue).Day,
                                            dateTime.Hour, dateTime.Minute,
                                            dateTime.Second);
                                    }
                                }
                                else if (columnIndex.ColumnType == ColumnContext.ColumnType.ScalarTime)
                                {
                                    var date = rtEntity.GetAttributeValueOrDefault(columnIndex.AttributePath
                                        .ToPascalCase());

                                    if (!double.TryParse(value, CultureInfo.InvariantCulture, out double doubleValue))
                                    {
                                        throw new Exception($"Cannot parse {value} to double to get time");
                                    }

                                    convertedValue = DateTime.FromOADate(doubleValue);
                                    if (date is DateTime dateTime)
                                    {
                                        convertedValue = new DateTime(dateTime.Year, dateTime.Month, dateTime.Day,
                                            ((DateTime)convertedValue).Hour, ((DateTime)convertedValue).Minute,
                                            ((DateTime)convertedValue).Second);
                                    }
                                }
                                else
                                {
                                    convertedValue = DateTime.Parse(value,
                                        CultureInfo.GetCultureInfoByIetfLanguageTag("de-DE"));
                                }
                            }

                            rtEntity.SetAttributeValue(columnIndex.AttributePath.ToPascalCase(),
                                typeQueryColumn.ValueType,
                                convertedValue);
                            break;
                    }
                }
#endif
            }

            var insert = EntityUpdateInfo<RtEntity>.CreateInsert(rtEntity);
            entitiesList.Add(insert);
        }
    }

    private async Task ParseOrderAsync(JArray data, ColumnContext columnContext,
        List<IEntityUpdateInfo<RtEntity>> entities, List<AssociationUpdateInfo> assocs)
    {
        var ckTypeId = columnContext.GetCkTypeId(1, "Industry.Maintenance/Order");
        var validPaths = ckCacheService.GetCkTypeQueryColumnPaths(etlContext.TenantId, ckTypeId)
            .ToDictionary(k => k.Path, v => v);

        var loadedEntities = new Dictionary<string, RtEntity>();


        foreach (var jToken in data)
        {
            var entry = (JArray)jToken;

            var rtEntity = await etlContext.TenantRepository.CreateTransientRtEntityAsync(ckTypeId);
            foreach (var columnIndex in columnContext.GetColumns())
            {
                var value = columnContext.GetValueByIndex<string>(entry, columnIndex.Index);
                if (value == null)
                {
                    continue;
                }

                if (validPaths.TryGetValue(columnIndex.AttributePath, out var typeQueryColumn))
                {
                    switch (typeQueryColumn.ValueType)
                    {
                        #if FALSE
                        case AttributeValueTypesDto.Association:
                        {
                            if (typeQueryColumn.AssociationTuple == null)
                            {
                                throw new Exception($"Association tuple is null for {columnIndex}");
                            }

                            var associationDirectionTuple = typeQueryColumn.AssociationTuple;

                            if (!loadedEntities.TryGetValue(value, out RtEntity? treeEntity))
                            {
                                var dataOperation = DataQueryOperation.Create();
                                dataOperation.FieldEquals(nameof(RtEntity.RtWellKnownName), value);

                                using var session = etlContext.TenantRepository.GetSession();
                                session.StartTransaction();
                                var r = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(session,
                                    associationDirectionTuple.CkTypeId, dataOperation, 0, 1);

                                await session.CommitTransactionAsync();

                                if (r.TotalCount == 0)
                                {
                                    throw new Exception($"No entity found for {columnIndex} with value {value}");
                                }

                                treeEntity = r.Items.First();
                                loadedEntities.Add(value, treeEntity);
                            }

                            var association = AssociationUpdateInfo.CreateCreate(
                                rtEntity.ToRtEntityId(),
                                treeEntity.ToRtEntityId(),
                                associationDirectionTuple.CkAssociationRoleId);
                            assocs.Add(association);

                            break;
                        }
#endif
                        case AttributeValueTypesDto.Enum:
                            if (typeQueryColumn.CkEnumId == null)
                            {
                                throw new Exception($"CkEnumId is null for {columnIndex}");
                            }

                            var ckEnumGraph =
                                ckCacheService.GetCkEnum(etlContext.TenantId, typeQueryColumn.CkEnumId);

                            var ckEnumValueDto =
                                ckEnumGraph.Values.FirstOrDefault(ev =>
                                    ev.Name == value || ev.Description == value);
                            if (ckEnumValueDto == null)
                            {
                                if (ckEnumGraph.CkEnumId == "Industry.Maintenance/ServiceType")
                                {
                                    switch (value)
                                    {
                                        case "C00":
                                        case "C10":
                                        case "C15":
                                            ckEnumValueDto =
                                                ckEnumGraph.Values.FirstOrDefault(ev =>
                                                    ev.Description == "01-Korrektiv");
                                            break;
                                        case "C20":
                                        case "C22":
                                        case "C25":
                                            ckEnumValueDto =
                                                ckEnumGraph.Values.FirstOrDefault(ev =>
                                                    ev.Description == "02-Präventiv");
                                            break;
                                        case "C50":
                                            ckEnumValueDto = ckEnumGraph.Values.FirstOrDefault(ev =>
                                                ev.Description == "04-Verbesserungen/Änderungen");
                                            break;
                                        default:
                                            ckEnumValueDto =
                                                ckEnumGraph.Values.FirstOrDefault(ev =>
                                                    ev.Description == "03-Sonstige");
                                            break;
                                    }
                                }
                            }

                            if (ckEnumValueDto == null)
                            {
                                throw new Exception($"No enum value found for {columnIndex} with value {value}");
                            }

                            rtEntity.SetAttributeValue(columnIndex.AttributePath.ToPascalCase(),
                                typeQueryColumn.ValueType,
                                ckEnumValueDto.Key);
                            break;
                        default:
                            object convertedValue = value;
                            if (typeQueryColumn.ValueType == AttributeValueTypesDto.DateTime)
                            {
                                convertedValue = DateTime.Parse(value,
                                    CultureInfo.GetCultureInfoByIetfLanguageTag("de-DE"));
                            }

                            rtEntity.SetAttributeValue(columnIndex.AttributePath.ToPascalCase(),
                                typeQueryColumn.ValueType,
                                convertedValue);
                            break;
                    }
                }
            }

            var insert = EntityUpdateInfo<RtEntity>.CreateInsert(rtEntity);
            entities.Add(insert);
        }
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

                // Get ck type id and name. Be aware of the layer for column import!
                var ckTypeId = columnContext.GetCkTypeId(iLayer);
                var name = columnContext.GetValueByPath<string>(entry, "name", iLayer)!;

                var parentName = ParentNameParser.ParseLayerBasedName(columnContext, entry, iLayer);

                CkId<CkTypeId>? parentCkTypeId = null;
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
        }
    }

    private static void ParseTreeByPath(JArray data, ColumnContext columnContext, List<HierarchicalEntity> entities)
    {
        foreach (var jToken in data)
        {
            var entry = (JArray)jToken;
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
            var entityParent =
                buffer.FirstOrDefault(x => x.Name == entity.ParentName && x.CkTypeId == entity.ParentCkTypeId);

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
                if (ckTypeGraph.AllAttributesByName.TryGetValue(attribute.Item1.ToPascalCase(),
                        out var typeAttributeGraph))
                {
                    rtEntity.SetAttributeValue(attribute.Item1.ToPascalCase(), typeAttributeGraph.ValueType,
                        attribute.Item2);
                }
            }

            var insert = EntityUpdateInfo<RtEntity>.CreateInsert(rtEntity);

            entities.Add(insert);

            var association = AssociationUpdateInfo.CreateCreate(new RtEntityId(entity.CkTypeId, entity.RtId.Value),
                new RtEntityId(entityParent?.CkTypeId ?? "Basic/Tree", entityParent?.RtId ?? rootId),
                new CkId<CkAssociationRoleId>("System/ParentChild"));

            assocs.Add(association);
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
        importType = null;
        var o = dataContext.Current as JObject;

        if (o?["body"] is not JObject body)
        {
            nodeContext.Error("Body is null");
            return false;
        }

        var i = body.Value<string>("importType");
        if (i == null)
        {
            nodeContext.Error("Import type is not TreeModel");
            return false;
        }

        importType = i;
        return true;
    }

    private static bool EnsureAndValidateTreeData(
        IDataContext dataContext,
        INodeContext nodeContext,
        [NotNullWhen(true)] out string? rootNodeId)
    {
        rootNodeId = null;

        var o = dataContext.Current as JObject;

        if (o?["body"] is not JObject body)
        {
            nodeContext.Error("Body is null");
            return false;
        }

        var r = body.Value<string>("treeModelRootRtId");
        if (r == null)
        {
            nodeContext.Error("Root node id is not set");
            return false;
        }

        rootNodeId = r;

        return true;
    }

    private static bool EnsureAndValidateData(
        IDataContext dataContext,
        INodeContext nodeContext,
        [NotNullWhen(true)] out JArray? data,
        [NotNullWhen(true)] out JArray? columns)
    {
        data = null;
        columns = null;

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

        data = d;
        columns = c;

        return true;
    }
}