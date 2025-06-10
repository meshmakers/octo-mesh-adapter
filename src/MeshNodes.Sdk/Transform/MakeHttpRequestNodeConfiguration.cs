using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Represents a path parameter for HTTP requests
/// </summary>
public record HttpPathParameter
{
    /// <summary>
    /// The name of the path parameter (e.g., "userId" for {userId})
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The path to the value that will replace the parameter (optional if Value is provided)
    /// </summary>
    public string? ValuePath { get; set; }

    /// <summary>
    /// The direct value to use for the parameter (optional if ValuePath is provided)
    /// </summary>
    public string? Value { get; set; }
}

    /// <summary>
    /// Represents a header parameter for HTTP requests
    /// </summary>
    public record HttpHeaderParameter
    {
        /// <summary>
        /// The name of the header (e.g., "Authorization", "Content-Type")
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The path to the value for the header (optional if Value is provided)
        /// </summary>
        public string? ValuePath { get; set; }

        /// <summary>
        /// The direct value to use for the header (optional if ValuePath is provided)
        /// </summary>
        public string? Value { get; set; }
    }

    /// <summary>
    /// Make a http request
    /// </summary>
    [NodeName("MakeHttpRequest", 1)]
    public record MakeHttpRequestNodeConfiguration : TargetPathNodeConfiguration
    {
        /// <summary>
        /// the HTTP method to use for the request (values: GET, POST, PUT, DELETE)
        /// </summary>
        public required string Method { get; set; } = "GET";
        /// <summary>
        /// The path to the body of the request
        /// </summary>
        public string? BodyPath { get; set; }

        /// <summary>
        /// The body of the request as a string
        /// </summary>
        public string? Body { get; set; }

        /// <summary>
        /// the path to the URL of the request
        /// </summary>
        public string? UrlPath { get; set; }

        /// <summary>
        /// The URL of the request
        /// </summary>
        public string? Url { get; set; }

        /// <summary>
        /// Path parameters to be replaced in the URL
        /// </summary>
        public List<HttpPathParameter> PathParameters { get; set; } = new();

        /// <summary>
        /// Header parameters to be included in the HTTP request
        /// </summary>
        public List<HttpHeaderParameter> HeaderParameters { get; set; } = new();
    }