using System.Security.Cryptography;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Captcha;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// <c>VerifyCaptcha@1</c>: verifies a CAPTCHA token with the tenant's own secret, else with the platform's secret
/// default, and writes only the verdict. See <see cref="VerifyCaptchaNodeConfiguration" />.
/// </summary>
[NodeConfiguration(typeof(VerifyCaptchaNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class VerifyCaptchaNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ICkCacheService ckCacheService,
    ISecretAttributeProtector secretAttributeProtector,
    IHttpClientFactory httpClientFactory,
    TimeProvider? timeProvider = null) : IPipelineNode
{
    /// <summary>Name of the HTTP client the node sends its requests with.</summary>
    public const string HttpClientName = "VerifyCaptcha";

    /// <summary>The Cloudflare Turnstile siteverify endpoint; fixed, never taken from the configuration.</summary>
    public static readonly Uri TurnstileSiteVerifyEndpoint = new("https://challenges.cloudflare.com/turnstile/v0/siteverify");

    private const string SecretDefaultValueAttribute = "Value";

    private sealed record ResolvedSecret(string Source, string? Plaintext, bool Unusable);

    private sealed record TenantSecretTarget(RtCkId<CkTypeId> CkTypeId, string AttributeName);

    private static readonly ResolvedSecret NotSet = new(CaptchaVerdict.SourceNone, null, false);

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<VerifyCaptchaNodeConfiguration>();
        var tenantSecretTarget = Validate(c, nodeContext);

        var secret = await ResolveSecretAsync(c, tenantSecretTarget, dataContext, nodeContext);
        var verdict = await DecideAsync(c, secret, dataContext, nodeContext);

        nodeContext.Debug("CAPTCHA verdict: valid {0}, source {1}, reason {2}", verdict.Valid, verdict.Source,
            verdict.Reason ?? "-");
        dataContext.Set(c.TargetPath, verdict.ToJson(), c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }

    private async Task<CaptchaVerdict> DecideAsync(VerifyCaptchaNodeConfiguration c, ResolvedSecret secret,
        IDataContext dataContext, INodeContext nodeContext)
    {
        if (secret.Source == CaptchaVerdict.SourceNone)
        {
            return CaptchaVerdict.Unconfigured();
        }

        if (secret.Unusable || secret.Plaintext == null)
        {
            return CaptchaVerdict.Refused(secret.Source, CaptchaVerdict.VerificationUnavailable);
        }

        var token = ReadText(dataContext, c.TokenPath);
        if (string.IsNullOrWhiteSpace(token))
        {
            return CaptchaVerdict.Refused(secret.Source, CaptchaVerdict.InvalidToken);
        }

        var remoteIp = string.IsNullOrWhiteSpace(c.RemoteIpPath) ? null : ReadText(dataContext, c.RemoteIpPath);
        using var httpClient = httpClientFactory.CreateClient(HttpClientName);
        var refusal = await TurnstileSiteVerify.VerifyAsync(httpClient, TurnstileSiteVerifyEndpoint,
            secret.Plaintext, token, remoteIp, TimeSpan.FromSeconds(c.TimeoutSeconds),
            timeProvider ?? TimeProvider.System, nodeContext);
        return refusal == null
            ? CaptchaVerdict.Accepted(secret.Source)
            : CaptchaVerdict.Refused(secret.Source, refusal);
    }

    private TenantSecretTarget? Validate(VerifyCaptchaNodeConfiguration c, INodeContext nodeContext)
    {
        if (!Enum.IsDefined(c.Provider))
        {
            throw MeshAdapterPipelineExecutionException.VerifyCaptchaMisconfigured(nodeContext,
                $"provider '{c.Provider}' is not supported; supported: {string.Join(", ", Enum.GetNames<CaptchaProvider>())}.");
        }

        RequireText(c.TokenPath, "tokenPath", nodeContext);
        RequireText(c.DefaultKey, "defaultKey", nodeContext);
        RequireText(c.TargetPath, "targetPath", nodeContext);
        if (c.TimeoutSeconds is < 1 or > 60)
        {
            throw MeshAdapterPipelineExecutionException.VerifyCaptchaMisconfigured(nodeContext,
                "timeoutSeconds must be between 1 and 60.");
        }

        if (c.Identity == NodeExecutionIdentity.System)
        {
            throw MeshAdapterPipelineExecutionException.VerifyCaptchaMisconfigured(nodeContext,
                "identity 'System' is not allowed. Use 'Caller' or 'ServiceAccount'; the identity must be allowed " +
                "to read the tenant secret's entity.");
        }

        if (c.TenantSecret is not { } tenantSecret)
        {
            return null;
        }

        var ckTypeId = tenantSecret.CkTypeId;
        if (PlatformOnlySecrets.IsPlatformOnly(ckCacheService, etlContext.TenantId, ckTypeId) ||
            PlatformOnlySecrets.IsPlatformOnlyAttribute(ckTypeId, tenantSecret.AttributeName))
        {
            throw MeshAdapterPipelineExecutionException.PlatformOnlySecretRefused(nodeContext,
                $"VerifyCaptcha: tenantSecret of '{ckTypeId}' is platform-only; use defaultKey for the secret default");
        }

        RequireText(tenantSecret.AttributeName, "tenantSecret.attributeName", nodeContext);
        if (tenantSecret.RtId == null && string.IsNullOrWhiteSpace(tenantSecret.RtIdPath) &&
            string.IsNullOrWhiteSpace(tenantSecret.WellKnownName))
        {
            throw MeshAdapterPipelineExecutionException.VerifyCaptchaMisconfigured(nodeContext,
                "tenantSecret needs rtId, rtIdPath or wellKnownName.");
        }

        if (!ckCacheService.TryGetRtCkType(etlContext.TenantId, ckTypeId, out var typeGraph))
        {
            throw MeshAdapterPipelineExecutionException.VerifyCaptchaMisconfigured(nodeContext,
                $"CK type '{ckTypeId}' is unknown in tenant '{etlContext.TenantId}'.");
        }

        var attribute = SecretAttributes.FindAttribute(typeGraph, tenantSecret.AttributeName.Trim());
        if (attribute == null || attribute.ValueType != AttributeValueTypesDto.Secret)
        {
            throw MeshAdapterPipelineExecutionException.VerifyCaptchaMisconfigured(nodeContext,
                $"attribute '{tenantSecret.AttributeName}' of '{ckTypeId}' is missing or not Secret.");
        }

        return new TenantSecretTarget(ckTypeId, attribute.AttributeName);
    }

    private async Task<ResolvedSecret> ResolveSecretAsync(VerifyCaptchaNodeConfiguration c,
        TenantSecretTarget? tenantSecretTarget, IDataContext dataContext, INodeContext nodeContext)
    {
        if (tenantSecretTarget != null)
        {
            var tenantEntity = await ReadTenantEntityAsync(c, tenantSecretTarget.CkTypeId, dataContext, nodeContext);
            var tenantSecret = Decrypt(tenantEntity, tenantSecretTarget.CkTypeId, tenantSecretTarget.AttributeName,
                CaptchaVerdict.SourceTenant, nodeContext);
            if (tenantSecret != NotSet)
            {
                return tenantSecret;
            }
        }

        var defaultEntity = await ReadSecretDefaultAsync(c.DefaultKey.Trim(), nodeContext);
        return Decrypt(defaultEntity, PlatformOnlySecrets.SecretDefaultCkTypeId, SecretDefaultValueAttribute,
            CaptchaVerdict.SourceDefault, nodeContext);
    }

    private async Task<RtEntity?> ReadTenantEntityAsync(VerifyCaptchaNodeConfiguration c, RtCkId<CkTypeId> ckTypeId,
        IDataContext dataContext, INodeContext nodeContext)
    {
        var tenantSecret = c.TenantSecret!;
        // AB#5127 — config-selected (Caller by default): the tenant's own secret is tenant data, so data
        // permissions decide whether the execution identity may read its entity; System is refused in Validate.
        var session = await etlContext.GetSessionForAsync(c.Identity);
        session.StartTransaction();
        RtEntity? entity;
        var rtId = tenantSecret.RtId ?? ParseRtId(dataContext, tenantSecret.RtIdPath);
        if (rtId is { } id)
        {
            entity = await etlContext.TenantRepository.GetRtEntityByRtIdAsync(session, new RtEntityId(ckTypeId, id));
        }
        else
        {
            entity = await FindByWellKnownNameAsync(session, ckTypeId, tenantSecret.WellKnownName, nodeContext);
        }

        await session.CommitTransactionAsync();
        return entity;
    }

    private async Task<RtEntity?> ReadSecretDefaultAsync(string defaultKey, INodeContext nodeContext)
    {
        var ckTypeId = PlatformOnlySecrets.SecretDefaultCkTypeId;
        if (!ckCacheService.TryGetRtCkType(etlContext.TenantId, ckTypeId, out _))
        {
            nodeContext.Debug("No {0} type in this tenant; no secret default", ckTypeId);
            return null;
        }

        // AB#5028 — SYSTEM by decision: no tenant identity may read a SecretDefault, the platform reads it here
        // itself, only in the executing tenant's repository and only by the configured well-known name; a scoped
        // read would find nothing, and the value never leaves this node.
        var session = await etlContext.GetSystemSessionAsync();
        session.StartTransaction();
        var entity = await FindByWellKnownNameAsync(session, ckTypeId, defaultKey, nodeContext);
        await session.CommitTransactionAsync();
        return entity;
    }

    private async Task<RtEntity?> FindByWellKnownNameAsync(IOctoSession session, RtCkId<CkTypeId> ckTypeId,
        string? wellKnownName, INodeContext nodeContext)
    {
        if (string.IsNullOrWhiteSpace(wellKnownName))
        {
            return null;
        }

        var query = RtEntityQueryOptions.Create().FieldEquals(nameof(RtEntity.RtWellKnownName), wellKnownName);
        var found = (await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(session, ckTypeId, query, 0, 2))
            .Items.Where(e => e.RtWellKnownName == wellKnownName).ToList();
        if (found.Count > 1)
        {
            nodeContext.Error("More than one {0} has the well-known name '{1}'; none is used", ckTypeId, wellKnownName);
            return null;
        }

        return found.SingleOrDefault();
    }

    private ResolvedSecret Decrypt(RtEntity? entity, RtCkId<CkTypeId> ckTypeId, string attributeName, string source,
        INodeContext nodeContext)
    {
        var secret = entity?.GetAttributeSecretValueOrDefault(attributeName);
        if (entity == null || secret == null)
        {
            return NotSet;
        }

        var accessContext = new SecretAccessContext(etlContext.TenantId, ckTypeId.ToString(), attributeName);
        var readInfo = secretAttributeProtector.DescribeSecret(secret, accessContext);
        if (readInfo.KeyMissing && secretAttributeProtector.IsConfigured)
        {
            LogKeyMissing(nodeContext, ckTypeId, attributeName, readInfo.KeyId);
            return NotSet;
        }

        if (!readInfo.IsSet && !readInfo.KeyMissing)
        {
            return NotSet;
        }

        try
        {
            var plaintext = secretAttributeProtector.Unprotect(secret, accessContext);
            if (string.IsNullOrEmpty(plaintext))
            {
                return NotSet;
            }

            nodeContext.RegisterSecret(plaintext);
            return new ResolvedSecret(source, plaintext, false);
        }
        catch (UnknownSecretKeyIdException)
        {
            LogKeyMissing(nodeContext, ckTypeId, attributeName, secret.KeyId);
            return NotSet;
        }
        catch (Exception e) when (e is SecretEncryptionNotConfiguredException or LegacyPlaintextSecretRejectedException
                                      or CryptographicException)
        {
            nodeContext.Error("Secret attribute {0} of {1} cannot be decrypted ({2}); CAPTCHA verification unavailable",
                attributeName, ckTypeId, e.GetType().Name);
            return new ResolvedSecret(source, null, true);
        }
    }

    private static void LogKeyMissing(INodeContext nodeContext, RtCkId<CkTypeId> ckTypeId, string attributeName,
        string? keyId)
    {
        nodeContext.Error(
            "Secret attribute {0} of {1} is encrypted with key id '{2}', which this adapter's key ring does not " +
            "contain; treated as not set.", attributeName, ckTypeId, keyId ?? "?");
    }

    private static OctoObjectId? ParseRtId(IDataContext dataContext, string? rtIdPath)
    {
        var raw = string.IsNullOrWhiteSpace(rtIdPath) ? null : ReadText(dataContext, rtIdPath);
        return !string.IsNullOrWhiteSpace(raw) && OctoObjectId.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static string? ReadText(IDataContext dataContext, string path) =>
        dataContext.GetKind(path) == DataKind.String ? dataContext.Get<string>(path) : null;

    private static void RequireText(string? value, string name, INodeContext nodeContext)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw MeshAdapterPipelineExecutionException.VerifyCaptchaMisconfigured(nodeContext, $"{name} is not set.");
        }
    }
}
