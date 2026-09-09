namespace Meshmakers.Octo.Sdk.MeshAdapter.Configuration;

/// <summary>
/// Configuration for the mesh adapter.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class MeshAdapterConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MeshAdapterConfiguration"/> class.
    /// </summary>
    public MeshAdapterConfiguration()
    {
        StreamDataHost = "127.0.0.1";
        StreamDataUser = "crate";
    }

    /// <summary>
    /// Internal URI to the reporting service.
    /// </summary>
    public string ReportingServiceUrl { get; set; } = "https://localhost:5007";

    /// <summary>
    /// Public URI of the identity service issuing the access tokens accepted by secured trigger nodes.
    /// </summary>
    public string AuthorityUrl { get; set; } = "https://localhost:5003";

    /// <summary>
    /// Additional issuer values accepted on top of <see cref="AuthorityUrl"/>. Needed in
    /// split-horizon setups where the adapter reaches the identity service under a different
    /// host than the token audience does (e.g. an in-cluster adapter fetching discovery via
    /// an internal name while browser tokens carry the public issuer): the issuer claim names
    /// the caller's view, not ours. Discovery and signing keys still come exclusively from
    /// <see cref="AuthorityUrl"/> — entries here widen only the issuer STRING comparison.
    /// </summary>
    public string[] AdditionalValidIssuers { get; set; } = [];

    /// <summary>
    /// Records an event per invocation of an anonymous trigger route. Off by default because such
    /// a route serves public webhooks, whose volume would dominate a tenant's event log - nothing
    /// prunes it. Opt in per environment with <c>OCTO_ADAPTER__AUDITANONYMOUSINVOCATIONS=true</c>
    /// or <c>--Adapter:AuditAnonymousInvocations=true</c>. Turning it off hides nothing: the
    /// decision always reaches the adapter log at debug level, so raising
    /// <c>OCTO_ADAPTER__MINIMUMLOGLEVEL</c> traces the same traffic, and every invocation is
    /// recorded as a pipeline execution, which unlike the event log has retention.
    /// </summary>
    public bool AuditAnonymousInvocations { get; set; }

    /// <summary>
    /// Hostname of crate db server
    /// </summary>
    public string StreamDataHost { get; set; }

    /// <summary>
    /// User of crate db
    /// </summary>
    public string StreamDataUser { get; set; }

    /// <summary>
    /// Password for crate db
    /// </summary>
    public string? StreamDataPassword { get; set; }

    /// <summary>
    /// How long a measured archive coverage answer (AB#5157) is served without re-probing CrateDB, in
    /// seconds. The adapter's equivalent of the platform-wide <c>StreamData:Coverage:CacheTtlSeconds</c>
    /// setting — bound onto <c>ArchiveCoverageOptions</c> by
    /// <see cref="ConfigureArchiveCoverageOptions"/>, so the whole adapter configuration keeps living in
    /// the one <c>Adapter</c> section (<c>OCTO_ADAPTER__STREAMDATACOVERAGECACHETTLSECONDS</c>). Zero
    /// disables memoisation and measures on every resolution; a negative value is a misconfiguration and
    /// fails when the cache is created.
    /// </summary>
    public int StreamDataCoverageCacheTtlSeconds { get; set; } = 60;
}