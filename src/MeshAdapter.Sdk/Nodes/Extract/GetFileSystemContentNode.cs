using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Reads the binary content of a System.Reporting/FileSystemItem back into the
/// pipeline as base64. Read counterpart of <c>CreateFileSystemUpdate@1</c> —
/// used by pipelines that process previously uploaded files asynchronously
/// (e.g. a polling analysis pipeline picking up staged uploads).
/// </summary>
[NodeConfiguration(typeof(GetFileSystemContentNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class GetFileSystemContentNode(NodeDelegate next, IMeshEtlContext etlContext)
    : IPipelineNode
{
    private static readonly RtCkId<CkTypeId> RtCkTypeIdFileSystemItem =
        new("System.Reporting", "FileSystemItem");

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<GetFileSystemContentNodeConfiguration>();

        var rtIdValue = dataContext.Get<string>(c.RtIdPath);
        if (string.IsNullOrWhiteSpace(rtIdValue))
        {
            throw PipelineExecutionException.ValueNotSet(nodeContext, c.RtIdPath);
        }

        var rtId = OctoObjectId.Parse(rtIdValue);

        var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();

        var entity = await etlContext.TenantRepository.GetRtEntityByRtIdAsync(session,
            new RtEntityId(RtCkTypeIdFileSystemItem, rtId));
        if (entity == null)
        {
            throw MeshAdapterPipelineExecutionException.EntityNotFound(nodeContext, rtId);
        }

        var binaryInfo = entity.GetAttributeLinkedBinaryValueOrDefault("Content");
        if (binaryInfo?.BinaryId == null)
        {
            throw MeshAdapterPipelineExecutionException.FileContentNotFound(nodeContext, rtId);
        }

        using var downloadHandler =
            await etlContext.TenantRepository.DownloadLargeBinaryAsync(session, binaryInfo.BinaryId.Value);
        using var memoryStream = new MemoryStream();
        await downloadHandler.Stream.CopyToAsync(memoryStream);

        await session.CommitTransactionAsync();

        var bytes = memoryStream.ToArray();
        nodeContext.Debug($"Read {bytes.Length} bytes from file system item '{rtId}' ('{binaryInfo.Filename}')");

        dataContext.Set(c.TargetPath, Convert.ToBase64String(bytes),
            c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);

        if (!string.IsNullOrEmpty(c.FileNameTargetPath))
        {
            dataContext.Set(c.FileNameTargetPath, binaryInfo.Filename,
                c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);
        }

        if (!string.IsNullOrEmpty(c.ContentTypeTargetPath))
        {
            dataContext.Set(c.ContentTypeTargetPath, binaryInfo.ContentType,
                c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);
        }

        if (!string.IsNullOrEmpty(c.ContentLengthTargetPath))
        {
            dataContext.Set(c.ContentLengthTargetPath, (long)bytes.Length,
                c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);
        }

        await next(dataContext, nodeContext);
    }
}
