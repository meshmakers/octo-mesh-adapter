using System.Text;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Creates an update item for a file system item
/// </summary>
[NodeConfiguration(typeof(CreateFileSystemUpdateNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class CreateFileSystemItemUpdateNode(NodeDelegate next, IMeshEtlContext etlContext)
    : IPipelineNode
{
    private static readonly CkId<CkAssociationRoleId> CkAssociationIdParentChild = new(SystemCkIds.ModelId,
        SystemCkIds.ParentChild);

    private static readonly CkId<CkTypeId> CkTypeIdFileSystemItem =
        new("System.Reporting", "FileSystemItem");

    internal record FileSystemItemResult
    {
        public required OctoObjectId RtId { get; init; }
        public required CkId<CkTypeId> CkTypeId { get; init; }
        public required string FileName { get; init; }
    }

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<CreateFileSystemUpdateNodeConfiguration>();

        var rtId = GetRtId(dataContext, c);
        var rtWellKnownName = GetRtWellKnownName(dataContext, c);
        var contentType = GetContentType(dataContext, c);
        var contentLength = GetContentLength(dataContext, c);

        if (contentType == null)
        {
            throw MeshAdapterPipelineExecutionException.ContentTypeNull(nodeContext, c.ContentTypePath);
        }

        var fileName = GetFileName(dataContext, contentType, c);

        var entityUpdateInfoList = new List<IEntityUpdateInfo<RtEntity>>();
        var assocUpdateInfoList = new List<AssociationUpdateInfo>();
        if (fileName == null)
        {
            throw MeshAdapterPipelineExecutionException.FileNameNull(nodeContext, c.FileNamePath);
        }

        if (contentLength == null)
        {
            throw MeshAdapterPipelineExecutionException.ContentLengthNull(nodeContext, c.ContentLengthPath);
        }

        if (string.IsNullOrWhiteSpace(c.RootFolderWellKnownName))
        {
            throw MeshAdapterPipelineExecutionException.RootFolderWellKnownNameNotSet(nodeContext);
        }

        var folder = await GetFolderRootAsync(etlContext.TenantRepository, c.RootFolderWellKnownName);

        var rtFileSystemItem = await etlContext.TenantRepository.CreateTransientRtEntityAsync(CkTypeIdFileSystemItem);
        if (rtId != null)
        {
            rtFileSystemItem.RtId = rtId.Value;
        }

        var content = GetPath(nodeContext, dataContext, c);
        // Convert content UTF8 string to stream

        var bytes = Convert.FromBase64String(content);

        var memoryStream = new MemoryStream(bytes);

        if (rtWellKnownName != null)
        {
            rtFileSystemItem.RtWellKnownName = rtWellKnownName;
        }

        var entityBinaryInfo = new EntityBinaryInfo
        {
            ContentType = contentType,
            Filename = fileName,
            Size = contentLength,
            Stream = memoryStream
        };
        rtFileSystemItem.SetAttributeValue("Content",
            AttributeValueTypesDto.BinaryLinked, entityBinaryInfo);
        rtFileSystemItem.SetAttributeValue("Name", AttributeValueTypesDto.String, fileName);

        var updateItem = EntityUpdateInfo<RtEntity>.CreateInsert(CkTypeIdFileSystemItem, rtFileSystemItem);
        entityUpdateInfoList.Add(updateItem);
        assocUpdateInfoList.Add(
            AssociationUpdateInfo.CreateCreate(rtFileSystemItem.ToRtEntityId(), folder.ToRtEntityId(),
                CkAssociationIdParentChild));

        var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();
        OperationResult operationResult = new();
        await etlContext.TenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, assocUpdateInfoList,
            operationResult);

        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw MeshAdapterPipelineExecutionException.RepositoryUpdateOperationFailed(operationResult);
        }

        await session.CommitTransactionAsync();

        dataContext.SetValueByPath(c.TargetPath,
            new FileSystemItemResult
                { CkTypeId = CkTypeIdFileSystemItem, RtId = rtFileSystemItem.RtId, FileName = fileName },
            c.DocumentMode, c.TargetValueKind,
            c.TargetValueWriteMode, RtNewtonsoftSerializer.DefaultSerializer);

        await next(dataContext, nodeContext);
    }

    private static long? GetContentLength(IDataContext dataContext, CreateFileSystemUpdateNodeConfiguration config)
    {
        if (config.ContentLength != null)
        {
            return config.ContentLength;
        }

        if (config.ContentLengthPath == null || dataContext.Current == null)
        {
            return null;
        }

        var contentLength = dataContext.GetComplexObjectByPath<long>(config.ContentLengthPath,
            RtNewtonsoftSerializer.DefaultSerializer);


        return contentLength;
    }

    private static string? GetContentType(IDataContext dataContext, CreateFileSystemUpdateNodeConfiguration config)
    {
        if (config.ContentType != null)
        {
            return config.ContentType;
        }

        if (config.ContentTypePath == null || dataContext.Current == null)
        {
            return null;
        }

        var contentType = dataContext.GetComplexObjectByPath<string>(config.ContentTypePath,
            RtNewtonsoftSerializer.DefaultSerializer);


        return contentType;
    }

    private static string GetPath(INodeContext nodeContext, IDataContext dataContext,
        CreateFileSystemUpdateNodeConfiguration config)
    {
        var pathValue = dataContext.GetComplexObjectByPath<string>(config.Path,
            RtNewtonsoftSerializer.DefaultSerializer);

        if (string.IsNullOrWhiteSpace(pathValue))
        {
            throw MeshAdapterPipelineExecutionException
                .ValueNotSet(nodeContext, nameof(config.Path));
        }

        return pathValue;
    }

    private static string? GetFileName(IDataContext dataContext, string contentType,
        CreateFileSystemUpdateNodeConfiguration config)
    {
        if (config.FileName != null)
        {
            return config.FileName;
        }

        if (config.FileNamePath == null || dataContext.Current == null)
        {
            return $"{Guid.NewGuid()}.{GetFileExtensionFromContentType(contentType)}";
        }

        var fileName = dataContext.GetComplexObjectByPath<string>(config.FileNamePath,
            RtNewtonsoftSerializer.DefaultSerializer);


        return fileName;
    }

    private static string GetFileExtensionFromContentType(string contentType)
    {
        return contentType.Split('/').Last();
    }

    private static OctoObjectId? GetRtId(IDataContext dataContext, CreateFileSystemUpdateNodeConfiguration config)
    {
        if (config.RtId != null)
        {
            return config.RtId.Value;
        }

        if (config.RtIdPath == null || dataContext.Current == null)
        {
            return null;
        }

        var rtId = dataContext.GetComplexObjectByPath<OctoObjectId?>(config.RtIdPath,
            RtNewtonsoftSerializer.DefaultSerializer);

        if (rtId == null && config.GenerateRtId)
        {
            rtId = OctoObjectId.GenerateNewId();
        }

        return rtId;
    }

    private static string? GetRtWellKnownName(IDataContext dataContext, CreateFileSystemUpdateNodeConfiguration config)
    {
        if (config.RtWellKnownNamePath == null || dataContext.Current == null)
        {
            return null;
        }

        var rtWellKnownName =
            dataContext.GetComplexObjectByPath<string?>(config.RtWellKnownNamePath,
                RtNewtonsoftSerializer.DefaultSerializer);
        return rtWellKnownName;
    }

    private static async Task<RtEntity> GetFolderRootAsync(ITenantRepository tenantRepository,
        string rootFolderWellKnownName)
    {
        try
        {
            var session = await tenantRepository.GetSessionAsync();
            session.StartTransaction();

            DataQueryOperation queryOperation = DataQueryOperation.Create();
            queryOperation.FieldEquals(nameof(RtEntity.RtWellKnownName), rootFolderWellKnownName);

            var r = await tenantRepository.GetRtEntitiesByTypeAsync(session, "System.Reporting/FolderRoot",
                queryOperation);

            await session.CommitTransactionAsync();
            if (r.Items.Count() == 1)
            {
                return r.Items.First();
            }

            throw MeshAdapterPipelineExecutionException.RootFolderNotFound(rootFolderWellKnownName);
        }
        catch (Exception ex)
        {
            throw MeshAdapterPipelineExecutionException.RepositoryOperationFailed(ex);
        }
    }
}