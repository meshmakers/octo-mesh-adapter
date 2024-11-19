using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration node object for converting an array to a Markdown table
/// </summary>
[NodeName("QueryResultToMarkdownTable", 1)]
// ReSharper disable once ClassNeverInstantiated.Global
public record QueryResultToMarkdownTableNodeConfiguration : SourceTargetPathNodeConfiguration
{
}