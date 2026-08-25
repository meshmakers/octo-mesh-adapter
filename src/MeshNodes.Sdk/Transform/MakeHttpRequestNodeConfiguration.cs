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
        [PropertyGroup("Connection", 0)]
        public required string Method { get; set; } = "GET";
        /// <summary>
        /// The path to the body of the request
        /// </summary>
        [PropertyGroup("Data Mapping", 0, "jsonpath")]
        public string? BodyPath { get; set; }

        /// <summary>
        /// The body of the request as a string
        /// </summary>
        [PropertyGroup("Data Mapping", 1)]
        public string? Body { get; set; }

        /// <summary>
        /// the path to the URL of the request
        /// </summary>
        [PropertyGroup("Connection", 1, "jsonpath")]
        public string? UrlPath { get; set; }

        /// <summary>
        /// The URL of the request
        /// </summary>
        [PropertyGroup("Connection", 2)]
        public string? Url { get; set; }

        /// <summary>
        /// Path parameters to be replaced in the URL
        /// </summary>
        [PropertyGroup("Connection", 3)]
        public List<HttpPathParameter> PathParameters { get; set; } = new();

        /// <summary>
        /// Header parameters to be included in the HTTP request
        /// </summary>
        [PropertyGroup("Connection", 4)]
        public List<HttpHeaderParameter> HeaderParameters { get; set; } = new();

        /// <summary>
        /// Name of a GlobalConfiguration entry providing the API base URL and key. When set, the
        /// request URL is a path relative to that base and the key is sent in
        /// <see cref="AuthHeaderName" />.
        /// </summary>
        [PropertyGroup("Connection", 5)]
        public string? ApiConfiguration { get; set; }

        /// <summary>
        /// Header the key from <see cref="ApiConfiguration" /> is sent in. The key is inserted as
        /// it is; with the default header and no prefix it goes out scheme-less, which suits a
        /// target expecting a bare token.
        /// </summary>
        [PropertyGroup("Connection", 6)]
        public string AuthHeaderName { get; set; } = "Authorization";

        /// <summary>
        /// Scheme prefix placed before the key, for example "Bearer ". Empty by default.
        /// </summary>
        [PropertyGroup("Connection", 7)]
        public string AuthHeaderValuePrefix { get; set; } = "";

        /// <summary>
        /// The media type of the request body (values: application/json, application/x-www-form-urlencoded).
        /// For application/x-www-form-urlencoded the body must be a JSON object whose properties become the form fields.
        /// </summary>
        [PropertyGroup("Data Mapping", 2)]
        public string BodyContentType { get; set; } = "application/json";

        /// <summary>
        /// How the response body is stored at the target path (values: Auto, Text, Base64).
        /// Auto stores JSON objects as JSON and everything else as text; Base64 stores the raw
        /// response bytes base64-encoded (for binary downloads such as PDFs).
        /// </summary>
        [PropertyGroup("Data Mapping", 3)]
        public string ResponseFormat { get; set; } = "Auto";

        /// <summary>
        /// Optional path where the response content length in bytes is stored (before base64 encoding).
        /// Useful for feeding nodes that require a content length, e.g. CreateFileSystemUpdate.
        /// </summary>
        [PropertyGroup("Data Mapping", 4, "jsonpath")]
        public string? ContentLengthTargetPath { get; set; }
    }