using System.ComponentModel.DataAnnotations;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>CAPTCHA providers <c>VerifyCaptcha@1</c> can verify with.</summary>
public enum CaptchaProvider
{
    /// <summary>Cloudflare Turnstile; the siteverify endpoint is fixed in code.</summary>
    Turnstile
}

/// <summary>The tenant's own CAPTCHA secret, a Secret attribute of one entity.</summary>
public record VerifyCaptchaTenantSecretConfiguration
{
    /// <summary>Runtime CK type of the entity that holds the secret.</summary>
    [PropertyGroup("Tenant secret", 0, "ckTypeSelector")]
    public required RtCkId<CkTypeId> CkTypeId { get; set; }

    /// <summary>Runtime id of the entity (one of RtId, RtIdPath or WellKnownName).</summary>
    [PropertyGroup("Tenant secret", 1)]
    public OctoObjectId? RtId { get; set; }

    /// <summary>JSONPath to the runtime id of the entity.</summary>
    [PropertyGroup("Tenant secret", 2, "jsonpath")]
    public string? RtIdPath { get; set; }

    /// <summary>Well-known name of the entity.</summary>
    [PropertyGroup("Tenant secret", 3)]
    public string? WellKnownName { get; set; }

    /// <summary>Name of the Secret attribute, e.g. <c>CaptchaSecret</c>.</summary>
    [PropertyGroup("Tenant secret", 4)]
    public required string AttributeName { get; set; }
}

/// <summary>
/// Configuration of <c>VerifyCaptcha@1</c>: verifies a CAPTCHA token with the tenant's own secret, else with the
/// platform's secret default, and writes only <c>{ valid, source, reason }</c> to the target path.
/// </summary>
/// <remarks>
/// The secret is decrypted inside the node and never written to the data context. The provider endpoint is fixed
/// in code and cannot be configured, so a pipeline cannot send the secret to another server.
/// </remarks>
[NodeName("VerifyCaptcha", 1)]
public record VerifyCaptchaNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>The CAPTCHA provider.</summary>
    [PropertyGroup("Provider", 0)]
    public required CaptchaProvider Provider { get; set; }

    /// <summary>JSONPath to the token the client submitted.</summary>
    [PropertyGroup("Provider", 1, "jsonpath")]
    public required string TokenPath { get; set; }

    /// <summary>Optional JSONPath to the client's IP address, passed on to the provider.</summary>
    [PropertyGroup("Provider", 2, "jsonpath")]
    public string? RemoteIpPath { get; set; }

    /// <summary>Seconds to wait for the provider before the result is <c>verification-unavailable</c> (1 to 60).</summary>
    [PropertyGroup("Provider", 3)]
    [Range(1, 60)]
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Optional tenant override; used when its Secret attribute is set.</summary>
    [PropertyGroup("Secret", 0)]
    public VerifyCaptchaTenantSecretConfiguration? TenantSecret { get; set; }

    /// <summary>Well-known name of the <c>System.Communication/SecretDefault</c> used when no tenant secret is set.</summary>
    [PropertyGroup("Secret", 1)]
    public required string DefaultKey { get; set; }

    /// <summary>
    /// Identity the tenant secret is read as: <c>Caller</c> (default) or <c>ServiceAccount</c>; <c>System</c> is
    /// refused. The secret default is always read by the platform itself.
    /// </summary>
    [PropertyGroup("Execution", 100)]
    public NodeExecutionIdentity Identity { get; set; } = NodeExecutionIdentity.Caller;
}
