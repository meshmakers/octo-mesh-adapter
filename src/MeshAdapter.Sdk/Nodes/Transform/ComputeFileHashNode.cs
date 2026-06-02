using System.Security.Cryptography;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Pipeline node that computes a SHA-256 hash from base64-encoded file data
/// </summary>
[NodeConfiguration(typeof(ComputeFileHashNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class ComputeFileHashNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<ComputeFileHashNodeConfiguration>();

        var base64Data = dataContext.Get<string>(config.Path);
        if (string.IsNullOrEmpty(base64Data))
        {
            nodeContext.Warning($"No data found at path: {config.Path}");
            await next(dataContext, nodeContext);
            return;
        }

        var bytes = Convert.FromBase64String(base64Data);
        var hashBytes = SHA256.HashData(bytes);
        var hashHex = Convert.ToHexStringLower(hashBytes);

        dataContext.Set(config.TargetPath, hashHex, config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode);

        nodeContext.Debug($"Computed file hash: {hashHex}");

        await next(dataContext, nodeContext);
    }
}
