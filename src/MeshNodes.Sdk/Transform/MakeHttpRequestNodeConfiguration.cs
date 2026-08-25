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
    /// Page-number paging over a collection endpoint. Absent means a single request. The property
    /// names are page-number specific so a cursor mode can be added later without renaming.
    /// </summary>
    /// <remarks>
    /// Every optional member is nullable even though it carries an initializer. An initializer
    /// applies only when the key is absent from the definition; a key that is present and null
    /// overwrites it, and the reader would get a zero or a null it never wrote. Nullable here plus
    /// a fallback where the value is read makes "absent" and "present but null" mean the same
    /// thing. The defaults live in the constants so the initializer and the fallback cannot drift.
    /// </remarks>
    public record HttpPagingOptions
    {
        /// <summary>The <see cref="PageParameterName" /> an unset property resolves to.</summary>
        public const string DefaultPageParameterName = "page";

        /// <summary>The <see cref="PageSizeParameterName" /> an unset property resolves to.</summary>
        public const string DefaultPageSizeParameterName = "pageSize";

        /// <summary>The <see cref="PageSize" /> an unset property resolves to.</summary>
        public const int DefaultPageSize = 100;

        /// <summary>The <see cref="FirstPageNumber" /> an unset property resolves to.</summary>
        public const int DefaultFirstPageNumber = 1;

        /// <summary>The <see cref="StopOnShortPage" /> an unset property resolves to.</summary>
        public const bool DefaultStopOnShortPage = true;

        /// <summary>The <see cref="MaxPages" /> an unset property resolves to.</summary>
        public const int DefaultMaxPages = 500;

        /// <summary>
        /// Single-level path of the form "$.name" addressing the array inside one response, for
        /// example "$.result". Deeper addressing belongs to a downstream transformation. Required
        /// when paging is configured: a walk with nothing to read cannot report a result.
        /// </summary>
        public string ItemsPath { get; set; } = "";

        /// <summary>Query parameter carrying the page number.</summary>
        public string? PageParameterName { get; set; } = DefaultPageParameterName;

        /// <summary>Query parameter carrying the page size.</summary>
        public string? PageSizeParameterName { get; set; } = DefaultPageSizeParameterName;

        /// <summary>Elements requested per page.</summary>
        public int? PageSize { get; set; } = DefaultPageSize;

        /// <summary>Number of the first page; some APIs count from zero.</summary>
        public int? FirstPageNumber { get; set; } = DefaultFirstPageNumber;

        /// <summary>
        /// Treat a page holding fewer elements than requested as the last one. Turn it off for an
        /// API that caps the page size server-side, where every page looks short.
        /// </summary>
        public bool? StopOnShortPage { get; set; } = DefaultStopOnShortPage;

        /// <summary>
        /// Upper bound on pages. Reaching it fails: a target that ignores the page parameter
        /// answers with the same page forever, and a silent stop would truncate the result.
        /// </summary>
        public int? MaxPages { get; set; } = DefaultMaxPages;
    }

    /// <summary>
    /// What a failed request does to the pipeline.
    /// </summary>
    public enum HttpErrorHandling
    {
        /// <summary>Report the failure and stop this branch, leaving the execution successful.</summary>
        LogAndStop,

        /// <summary>Fail the execution, so a surrounding loop or the run itself reports it.</summary>
        Throw
    }

    /// <summary>
    /// Retry behaviour for one request. Absent means a single attempt, which is what the node did
    /// before the option existed.
    /// </summary>
    /// <remarks>
    /// Nullable members with constant defaults, for the reason given on
    /// <see cref="HttpPagingOptions" />.
    /// </remarks>
    public record HttpRetryOptions
    {
        /// <summary>The <see cref="MaxAttempts" /> an unset property resolves to.</summary>
        public const int DefaultMaxAttempts = 1;

        /// <summary>The <see cref="BackoffBaseSeconds" /> an unset property resolves to.</summary>
        public const double DefaultBackoffBaseSeconds = 1;

        /// <summary>Total attempts per request, so 1 means no retry.</summary>
        public int? MaxAttempts { get; set; } = DefaultMaxAttempts;

        /// <summary>Delay before attempt n is base * 2^(n-1) seconds; 0 disables waiting.</summary>
        public double? BackoffBaseSeconds { get; set; } = DefaultBackoffBaseSeconds;
    }

    /// <summary>
    /// Make a http request
    /// </summary>
    [NodeName("MakeHttpRequest", 1)]
    public record MakeHttpRequestNodeConfiguration : TargetPathNodeConfiguration
    {
        /// <summary>The <see cref="AuthHeaderName" /> an unset property resolves to.</summary>
        public const string DefaultAuthHeaderName = "Authorization";

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
        public string? AuthHeaderName { get; set; } = DefaultAuthHeaderName;

        /// <summary>
        /// Scheme prefix placed before the key, for example "Bearer ". Empty by default.
        /// </summary>
        [PropertyGroup("Connection", 7)]
        public string? AuthHeaderValuePrefix { get; set; } = "";

        /// <summary>
        /// Retry behaviour for transient failures: 5xx, 408, 429, network errors and timeouts.
        /// Absent means a single attempt.
        /// </summary>
        /// <remarks>
        /// Nullable on purpose. A definition carrying an explicit null overwrites a property
        /// initializer, so a non-nullable property with a default would hand the node a null it
        /// cannot see coming - the same shape of mistake that a null integer in a settings entry
        /// once caused. Read it through <c>Retry ?? new HttpRetryOptions()</c> at every use site.
        /// </remarks>
        [PropertyGroup("Connection", 8)]
        public HttpRetryOptions? Retry { get; set; }

        /// <summary>
        /// Timeout in seconds applied to each attempt. Unset leaves the HTTP client's own default
        /// in place; the client is shared, so its timeout is never changed.
        /// </summary>
        [PropertyGroup("Connection", 9)]
        public int? TimeoutSeconds { get; set; }

        /// <summary>
        /// How a failed request is answered. The default keeps the behaviour the node had before
        /// the option existed: the failure is logged and the following nodes are skipped, while
        /// the execution still succeeds. It governs runtime outcomes only - a configuration
        /// mistake always fails.
        /// </summary>
        [PropertyGroup("Connection", 10)]
        public HttpErrorHandling OnHttpError { get; set; } = HttpErrorHandling.LogAndStop;

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

        /// <summary>
        /// Collects every page of a paged endpoint into one flat array at the target path.
        /// </summary>
        [PropertyGroup("Data Mapping", 5)]
        public HttpPagingOptions? Paging { get; set; }
    }