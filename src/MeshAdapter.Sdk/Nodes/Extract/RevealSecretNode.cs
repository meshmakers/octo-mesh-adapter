using System.Security.Cryptography;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// <c>RevealSecret@1</c> (AB#5538, concept AB#5528 decision 6): the privileged, counted way for a
/// pipeline to obtain the plaintext of one <c>Secret</c> attribute. See
/// <see cref="RevealSecretNodeConfiguration" /> for the contract.
/// </summary>
/// <remarks>
/// <para>
/// The entity is read in process through the tenant repository and the value decrypted with
/// <see cref="ISecretAttributeProtector.Unprotect(RtSecretValue, SecretAccessContext?)" />, which
/// counts <c>octo.secrets.decrypt</c> tagged with tenant, CK type, attribute and service. There is no
/// public decrypt endpoint behind it — the plaintext exists only inside this adapter process and the
/// data context of this execution.
/// </para>
/// <para>
/// Order matters: the plaintext is registered with <see cref="INodeContext.RegisterSecret" />
/// <b>before</b> it is written, so no snapshot can ever capture it unmasked.
/// </para>
/// </remarks>
[NodeConfiguration(typeof(RevealSecretNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class RevealSecretNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ICkCacheService ckCacheService,
    ISecretAttributeProtector secretAttributeProtector) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<RevealSecretNodeConfiguration>();

        if (string.IsNullOrWhiteSpace(c.AttributeName))
        {
            throw MeshAdapterPipelineExecutionException.RevealSecretFailed(nodeContext,
                "attributeName is not set.");
        }

        // AB#5538 review: System bypasses every data permission (AB#5127) and is ungated at deploy time
        // until AB#5128. On a decrypting node that would let anyone who may edit a pipeline reveal every
        // credential of the tenant, so the read must run as a principal whose roles an administrator
        // controls: the caller or the pipeline's service account. Refused before anything is read.
        if (c.Identity == NodeExecutionIdentity.System)
        {
            throw MeshAdapterPipelineExecutionException.RevealSecretFailed(nodeContext,
                "identity 'System' is not allowed. Use 'Caller' or 'ServiceAccount'; the identity must be " +
                "allowed to read the entity.");
        }

        var ckTypeId = CkTypeIdHelper.ResolveRtCkTypeId(c.CkTypeId, c.CkTypeIdPath, dataContext, nodeContext);
        var rtId = ResolveRtId(c, dataContext, nodeContext);
        var segments = ResolveSecretPath(ckTypeId, c.AttributeName.Trim(), nodeContext);
        var attributePath = string.Join('.', segments);

        // AB#5028 — scoped by default (AB#5127 Identity): the entity is read as whoever the
        // execution acts as, so data permissions decide whether the caller may see the entity at all.
        // Revealing never widens reach: a secret on an entity the identity cannot read is "not found".
        var session = await etlContext.GetSessionForAsync(c.Identity);
        session.StartTransaction();
        var entity = await etlContext.TenantRepository.GetRtEntityByRtIdAsync(session, new RtEntityId(ckTypeId, rtId));
        await session.CommitTransactionAsync();

        if (entity == null)
        {
            throw MeshAdapterPipelineExecutionException.RevealSecretFailed(nodeContext,
                $"entity '{ckTypeId}@{rtId}' not found (or not readable by the execution identity).");
        }

        var secret = ReadSecretValue(entity, segments);
        string? plaintext = null;
        if (secret != null && RtSecretValueWireFormat.IsSet(secret))
        {
            try
            {
                plaintext = secretAttributeProtector.Unprotect(secret,
                    new SecretAccessContext(etlContext.TenantId, ckTypeId.ToString(), attributePath));
            }
            catch (UnknownSecretKeyIdException e)
            {
                throw MeshAdapterPipelineExecutionException.RevealSecretFailed(nodeContext,
                    $"attribute '{attributePath}' of '{ckTypeId}@{rtId}' was encrypted with key id " +
                    $"'{secret.KeyId}', which this adapter's key ring does not contain. The value has to be " +
                    "entered again.", e);
            }
            catch (SecretEncryptionNotConfiguredException e)
            {
                throw MeshAdapterPipelineExecutionException.RevealSecretFailed(nodeContext,
                    $"attribute '{attributePath}' of '{ckTypeId}@{rtId}' cannot be decrypted: the adapter has no " +
                    "secret key ring configured (SecretEncryption:Keys / OCTO_SECRETENCRYPTION__KEYS__<kid>).", e);
            }
            catch (CryptographicException e)
            {
                throw MeshAdapterPipelineExecutionException.RevealSecretFailed(nodeContext,
                    $"attribute '{attributePath}' of '{ckTypeId}@{rtId}' cannot be decrypted: the stored value " +
                    "is damaged or was encrypted with a different key.", e);
            }
        }

        if (plaintext != null)
        {
            // Registered BEFORE the write: every snapshot taken from here on masks it.
            nodeContext.RegisterSecret(plaintext);
            nodeContext.Debug("Revealed Secret attribute {0} of {1}@{2}", attributePath, ckTypeId, rtId);
        }
        else
        {
            nodeContext.Debug("Secret attribute {0} of {1}@{2} is not set; writing null", attributePath,
                ckTypeId, rtId);
        }

        dataContext.Set(c.TargetPath, plaintext, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }

    private static OctoObjectId ResolveRtId(RevealSecretNodeConfiguration c, IDataContext dataContext,
        INodeContext nodeContext)
    {
        if (c.RtId is { } rtId)
        {
            return rtId;
        }

        if (string.IsNullOrWhiteSpace(c.RtIdPath))
        {
            throw MeshAdapterPipelineExecutionException.RevealSecretFailed(nodeContext,
                "neither rtId nor rtIdPath is set.");
        }

        var raw = dataContext.Get<string>(c.RtIdPath);
        if (string.IsNullOrWhiteSpace(raw) || !OctoObjectId.TryParse(raw, out var parsed))
        {
            throw MeshAdapterPipelineExecutionException.RevealSecretFailed(nodeContext,
                $"no valid rtId found at '{c.RtIdPath}'.");
        }

        return parsed;
    }

    /// <summary>
    /// Validates the configured attribute against the CK model and returns its path in the model's
    /// own spelling. Every step but the last must be a single <c>Record</c>; the last must be a Secret.
    /// Refusing a non-Secret attribute is deliberate: this node decrypts, it is not a general reader.
    /// </summary>
    private List<string> ResolveSecretPath(RtCkId<CkTypeId> ckTypeId, string attributeName, INodeContext nodeContext)
    {
        if (!ckCacheService.TryGetRtCkType(etlContext.TenantId, ckTypeId, out var typeGraph))
        {
            throw MeshAdapterPipelineExecutionException.RevealSecretFailed(nodeContext,
                $"CK type '{ckTypeId}' is unknown in tenant '{etlContext.TenantId}'.");
        }

        var parts = attributeName.Split('.');
        var segments = new List<string>(parts.Length);
        CkTypeWithAttributesGraph graph = typeGraph;
        for (var i = 0; i < parts.Length; i++)
        {
            var attribute = SecretAttributes.FindAttribute(graph, parts[i]);
            if (attribute == null)
            {
                throw MeshAdapterPipelineExecutionException.RevealSecretFailed(nodeContext,
                    $"attribute '{attributeName}' is not defined on '{ckTypeId}'.");
            }

            segments.Add(attribute.AttributeName);
            var isLast = i == parts.Length - 1;
            if (isLast)
            {
                if (attribute.ValueType != AttributeValueTypesDto.Secret)
                {
                    throw MeshAdapterPipelineExecutionException.RevealSecretFailed(nodeContext,
                        $"attribute '{attributeName}' of '{ckTypeId}' is of type {attribute.ValueType}, not Secret. " +
                        "RevealSecret only decrypts Secret attributes; read other attributes with GetRtEntitiesById@1.");
                }

                break;
            }

            if (attribute.ValueType != AttributeValueTypesDto.Record || attribute.ValueCkRecordId == null ||
                !ckCacheService.TryGetCkRecord(etlContext.TenantId, attribute.ValueCkRecordId, out var recordGraph))
            {
                throw MeshAdapterPipelineExecutionException.RevealSecretFailed(nodeContext,
                    $"attribute path '{attributeName}' of '{ckTypeId}': '{parts[i]}' is not a single record " +
                    "(record arrays are not supported).");
            }

            graph = recordGraph;
        }

        return segments;
    }

    private static RtSecretValue? ReadSecretValue(RtTypeWithAttributes entity, IReadOnlyList<string> segments)
    {
        var current = entity;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            if (current.GetAttributeValueOrDefault(segments[i]) is not RtTypeWithAttributes next)
            {
                return null;
            }

            current = next;
        }

        return current.GetAttributeSecretValueOrDefault(segments[^1]);
    }
}
