using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Transform.ExcelImport;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Transform;

[NodeConfiguration(typeof(ImportFromExcelNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class ImportFromExcelNode(
    NodeDelegate next,
    IMeshEtlContext etlContext)
    : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        if (!EnsureAndValidateData(dataContext, out var data, out var columns, out var importType, out var rootNodeId))
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
            dataContext.NodeContext.Error("Unknown import type");
            return;
        }


        await StoreInDatabase(entities, rootNodeId, dataContext.NodeContext);


        await next(dataContext);
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
                var name = columnContext.GetValue<string>(entry, "name", iLayer)!;
                if(entities.Any(x=> x.Name == name))
                { 
                    continue;
                }
                
                var parentName = ParentNameParser.ParseLayerBasedName(columnContext, entry, iLayer);

                var entity = new HierarchicalEntity(name, parentName);
                foreach(var attributeName in columnContext.GetColumnNames(iLayer))
                {
                    var value = columnContext.GetValue<string>(entry, attributeName, iLayer);
                    if(value == null || attributeName == "name")
                    {
                        continue;
                    }
                    entity.Attributes.Add(new(attributeName, value));
                }
                
                
                entities.Add(entity);
            }
        }
    }

    private static void ParseTreeByPath(JArray data, ColumnContext columnContext, List<HierarchicalEntity> entities)
    {
        foreach (var jToken in data)
        {
            var entry = (JArray)jToken;
            var name = columnContext.GetValue<string>(entry, "name");
            if (name == null)
            {
                continue;
            }

            name = name.Trim();

            var parentName = ParentNameParser.ParseSeparatorBased(name);

            var entity = new HierarchicalEntity(name, parentName);
            entities.Add(entity);
            foreach (var columnName in columnContext.GetColumnNames())
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
            var entityParent = buffer.FirstOrDefault(x => x.Name == entity.ParentName);

            if (entity.RtId == null)
            {
                entity.RtId = OctoObjectId.GenerateNewId();
            }

            if (entityParent is { RtId: null })
            {
                entityParent.RtId = OctoObjectId.GenerateNewId();
            }

            var rtEntity = new RtEntity
            {
                RtWellKnownName = entity.Name,
                RtId = entity.RtId.Value,
                CkTypeId = new CkId<CkTypeId>(new CkModelId("Basic"), new CkTypeId("TreeNode"))
            };

            rtEntity.SetAttributeValue("Name", AttributeValueTypesDto.String, entity.Name);

            foreach (var attribute in entity.Attributes)
            {
                rtEntity.SetAttributeValue(attribute.Item1, AttributeValueTypesDto.String, attribute.Item2);
            }

            var insert = EntityUpdateInfo<RtEntity>.CreateInsert(rtEntity);

            entities.Add(insert);

            var targetCkId = entityParent == null
                ? new CkId<CkTypeId>(new CkModelId("Basic"), new CkTypeId("Tree"))
                : new CkId<CkTypeId>(new CkModelId("Basic"), new CkTypeId("TreeNode"));

            var association = AssociationUpdateInfo.CreateCreate(new RtEntityId(rtEntity.CkTypeId, entity.RtId.Value),
                new RtEntityId(targetCkId, entityParent?.RtId ?? rootId),
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
                continue;

            var entityParent = buffer.FirstOrDefault(x => x.Name == entity.ParentName);

            if (entityParent != null)
            {
                continue;
            }

            var name = entity.ParentName;
            var parentName = ParentNameParser.ParseSeparatorBased(name);

            entityParent = new HierarchicalEntity(name, parentName);
            entityParent.RtId = OctoObjectId.GenerateNewId();
            entityParent.Attributes.Add(new("description", "---GENERATED---"));

            buffer.Add(entityParent);
            entityStack.Push(entityParent);
        }
    }

    private static bool EnsureAndValidateData(
        IDataContext dataContext,
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
            dataContext.NodeContext.Error("Body is null");
            return false;
        }

        if (body["columns"] is not JArray c)
        {
            dataContext.NodeContext.Error("Columns are null");
            return false;
        }


        if (body["data"] is not JArray d)
        {
            dataContext.NodeContext.Error("Data is null");
            return false;
        }

        var i = body.Value<string>("importType");
        if (i == null)
        {
            dataContext.NodeContext.Error("Import type is not TreeModel");
            return false;
        }

        var r = body.Value<string>("treeModelRootRtId");
        if (r == null)
        {
            dataContext.NodeContext.Error("Root node id is not set");
            return false;
        }

        data = d;
        columns = c;
        importType = i;
        rootNodeId = r;

        return true;
    }
}