using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for Anthropic AI Query node that uses Claude to extract information from data
/// </summary>
[NodeName("AnthropicAiQuery", 1)]
public record AnthropicAiQueryNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// The Anthropic API key for authentication
    /// </summary>
    public required string ApiKey { get; set; } =
        "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";
    
    /// <summary>
    /// The Claude model to use (e.g., "claude-3-5-sonnet-20241022", "claude-3-haiku-20240307")
    /// </summary>
    public string Model { get; set; } = "claude-sonnet-4-20250514";
    
    /// <summary>
    /// The question/prompt to ask the AI about the data
    /// </summary>
    public required string Question { get; set; }
    
    /// <summary>
    /// Optional context data paths to include in the query (e.g., ["$.ExtractedText", "$.DocumentMetadata"])
    /// </summary>
    public string[]? DataPaths { get; set; }
    
    /// <summary>
    /// System prompt to set the context for the AI
    /// </summary>
    public string SystemPrompt { get; set; } = "You are a helpful AI assistant that extracts specific information from documents. Always provide accurate, structured responses based only on the information provided.";
    
    /// <summary>
    /// Maximum tokens for the response
    /// </summary>
    public int MaxTokens { get; set; } = 1000;
    
    /// <summary>
    /// Temperature for response generation (0.0 to 1.0)
    /// </summary>
    public double Temperature { get; set; } = 0.1;
    
    /// <summary>
    /// Expected response format (e.g., "json", "text")
    /// </summary>
    public string ResponseFormat { get; set; } = "json";
    
    /// <summary>
    /// Whether to include the raw AI response in the output
    /// </summary>
    public bool IncludeRawResponse { get; set; } = false;
    
    /// <summary>
    /// Output path for the raw AI response (if IncludeRawResponse is true)
    /// </summary>
    public string? RawResponseOutputPath { get; set; }
    
    /// <summary>
    /// Whether to continue processing if the AI query fails
    /// </summary>
    public bool ContinueOnError { get; set; } = false;
    
    /// <summary>
    /// Timeout in seconds for the AI request
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
}