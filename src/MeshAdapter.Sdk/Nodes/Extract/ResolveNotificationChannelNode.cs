using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Resolves a recipient subjectId to the user's verified channel identifiers, channel preference
/// and the deterministic <c>effectiveChannel</c> for a system-initiated message (AB#5152). See
/// <see cref="ResolveNotificationChannelNodeConfiguration" /> for the contract and
/// <see cref="ISubjectChannelLookup" /> for the directory read.
/// <para>
/// The fallback chain is fixed: the user's <c>PreferredChannel</c> ("TEAMS" | "SIGNAL", AB#5149)
/// wins only when set AND a valid binding of the matching kind exists (SIGNAL → PhoneNumber,
/// TEAMS → EntraIdObjectId); every other case — no preference, unknown preference value, no valid
/// binding, unresolved subjectId — is <c>"EMAIL"</c>, with a warning where the preference could
/// not be honored. Never guesses between channels, never multi-delivers (one effective channel).
/// </para>
/// </summary>
[NodeConfiguration(typeof(ResolveNotificationChannelNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
// Internal (unlike most nodes) because its ISubjectChannelLookup seam is internal like the sibling
// caller-binding lookups; registration and tests live in / see this assembly.
internal class ResolveNotificationChannelNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ISubjectChannelLookup subjectChannelLookup) : IPipelineNode
{
    internal const string EmailChannel = "EMAIL";
    internal const string SignalChannel = "SIGNAL";
    internal const string TeamsChannel = "TEAMS";

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<ResolveNotificationChannelNodeConfiguration>();

        var subjectId = ResolveSubjectId(dataContext, c);
        if (string.IsNullOrWhiteSpace(subjectId))
        {
            nodeContext.Warning(
                "ResolveNotificationChannel: no subjectId (SubjectId/SubjectIdPath) — falling back to e-mail");
            WriteResult(dataContext, c, NotFound(string.Empty));
            await next(dataContext, nodeContext);
            return;
        }

        var record = await subjectChannelLookup.FindBySubjectIdAsync(etlContext.TenantId, subjectId);
        if (record == null)
        {
            nodeContext.Warning(
                $"ResolveNotificationChannel: subjectId '{subjectId}' did not resolve to a user — falling back to e-mail");
            WriteResult(dataContext, c, NotFound(subjectId));
            await next(dataContext, nodeContext);
            return;
        }

        var result = BuildResult(record, nodeContext);
        WriteResult(dataContext, c, result);
        nodeContext.Info(
            $"ResolveNotificationChannel: subjectId '{subjectId}' resolved — effective channel " +
            $"'{result["effectiveChannel"]!.GetValue<string>()}'");

        await next(dataContext, nodeContext);
    }

    private static string? ResolveSubjectId(IDataContext dataContext,
        ResolveNotificationChannelNodeConfiguration c)
    {
        if (!string.IsNullOrWhiteSpace(c.SubjectIdPath))
        {
            var resolved = dataContext.Get<string>(c.SubjectIdPath);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return c.SubjectId;
    }

    private static JsonObject NotFound(string subjectId) => new()
    {
        ["found"] = false,
        ["subjectId"] = subjectId,
        ["effectiveChannel"] = EmailChannel,
        ["identifiers"] = new JsonArray()
    };

    private static JsonObject BuildResult(SubjectChannelRecord record, INodeContext nodeContext)
    {
        var obj = new JsonObject
        {
            ["found"] = true,
            ["subjectId"] = record.SubjectId
        };

        if (!string.IsNullOrWhiteSpace(record.Email))
        {
            obj["email"] = record.Email;
        }

        if (!string.IsNullOrWhiteSpace(record.Name))
        {
            obj["name"] = record.Name;
        }

        var (effectiveChannel, channelAddress, preferredChannel) = ResolveEffectiveChannel(record, nodeContext);

        // AB#5149 cross-repo contract: "preferredChannel" is ABSENT (not null) without one, so
        // consumers can gate on field presence (same as WriteVerifiedCaller@1). Binding-specific
        // preferences emit the channel DERIVED from the referenced binding's kind; a legacy
        // kind-level preference is emitted verbatim.
        if (preferredChannel != null)
        {
            obj["preferredChannel"] = preferredChannel;
        }

        obj["effectiveChannel"] = effectiveChannel;
        if (channelAddress != null)
        {
            obj["channelAddress"] = channelAddress;
        }

        var identifiers = new JsonArray();
        foreach (var identifier in record.Identifiers)
        {
            identifiers.Add(new JsonObject
            {
                ["kind"] = identifier.Kind.ToString(),
                ["value"] = identifier.Value
            });
        }

        obj["identifiers"] = identifiers;
        return obj;
    }

    /// <summary>
    ///     Applies the AB#5152 fallback chain. PRIMARY path (System.Identity 2.18.0, binding-specific
    ///     preference): the user's <c>PreferredChannelBindingId</c> references the exact
    ///     <c>VerifiedExternalIdentifier</c> to deliver on — when it is among the VALID bindings, its
    ///     kind yields the channel and its value the exact target; a dangling/invalid reference
    ///     (deleted mid-flight, expired, unreadable) is warned and treated as "no preference" →
    ///     e-mail. TRANSITIONAL path (pre-2.18.0 rollout skew): a legacy kind-level
    ///     <c>PreferredChannel</c> string without a binding id applies the old kind-level logic with
    ///     the deterministic newest-binding selection. Everything else is EMAIL; cases that fall to
    ///     EMAIL despite a set preference are warned — the WI's "invalid/removed binding falls back
    ///     to e-mail with a warning log". Never guesses, never multi-delivers.
    /// </summary>
    private static (string EffectiveChannel, string? ChannelAddress, string? PreferredChannel)
        ResolveEffectiveChannel(SubjectChannelRecord record, INodeContext nodeContext)
    {
        // PRIMARY: binding-specific preference (System.Identity 2.18.0).
        if (record.PreferredChannelBindingId is { } bindingId)
        {
            var referenced = record.Identifiers.FirstOrDefault(i => i.BindingRtId == bindingId);
            if (referenced == null)
            {
                nodeContext.Warning(
                    $"ResolveNotificationChannel: subject '{record.SubjectId}' prefers binding '{bindingId}' " +
                    "which is no valid verified identifier (removed or expired) — falling back to e-mail");
                return (EmailChannel, null, null);
            }

            var channel = PreferredChannelResolver.ChannelForKind(referenced.Kind);
            if (channel == null)
            {
                nodeContext.Warning(
                    $"ResolveNotificationChannel: subject '{record.SubjectId}' prefers binding '{bindingId}' " +
                    $"of kind {referenced.Kind}, which no outbound channel maps to — falling back to e-mail");
                return (EmailChannel, null, null);
            }

            return (channel, referenced.Value, channel);
        }

        // TRANSITIONAL: legacy kind-level preference string (pre-2.18.0 rollout skew) — the
        // preference names a channel, not a binding, so the binding is selected deterministically.
        if (record.LegacyPreferredChannel is { } preference)
        {
            ChannelIdentifierKind requiredKind;
            switch (preference)
            {
                case SignalChannel:
                    requiredKind = ChannelIdentifierKind.PhoneNumber;
                    break;
                case TeamsChannel:
                    requiredKind = ChannelIdentifierKind.EntraIdObjectId;
                    break;
                default:
                    // An unknown preference value is never guessed at — deterministic e-mail fallback.
                    nodeContext.Warning(
                        $"ResolveNotificationChannel: unknown preferred channel '{preference}' for subject " +
                        $"'{record.SubjectId}' — falling back to e-mail");
                    return (EmailChannel, null, preference);
            }

            var candidates = record.Identifiers.Where(i => i.Kind == requiredKind).ToList();
            if (candidates.Count == 0)
            {
                nodeContext.Warning(
                    $"ResolveNotificationChannel: preferred channel '{preference}' of subject " +
                    $"'{record.SubjectId}' has no valid {requiredKind} binding — falling back to e-mail");
                return (EmailChannel, null, preference);
            }

            var chosen = SelectBinding(candidates);
            if (candidates.Count > 1)
            {
                // The kind-level preference does not say WHICH binding, so the most recently
                // verified one wins deterministically. The warning names the choice so the routing
                // stays auditable.
                nodeContext.Warning(
                    $"ResolveNotificationChannel: subject '{record.SubjectId}' has {candidates.Count} valid " +
                    $"{requiredKind} bindings for preferred channel '{preference}' — deterministically using the " +
                    $"most recently verified one ('{chosen.Value}', {candidates.Count - 1} alternative(s) ignored)");
            }

            return (preference, chosen.Value, preference);
        }

        return (EmailChannel, null, null);
    }

    /// <summary>
    ///     Deterministic multi-binding selection for the TRANSITIONAL kind-level preference: the
    ///     most recently verified binding wins — <c>LastVerifiedAt</c>, falling back to
    ///     <c>EnrolledAt</c>, unstamped bindings sort oldest — with ties broken by the LOWEST
    ///     binding rtId (ordinal), so the choice never varies between runs for unchanged data.
    /// </summary>
    internal static SubjectChannelIdentifier SelectBinding(
        IReadOnlyList<SubjectChannelIdentifier> candidates)
        => candidates
            .OrderByDescending(i => i.LastVerifiedAt ?? i.EnrolledAt ?? DateTime.MinValue)
            .ThenBy(i => i.BindingRtId, StringComparer.Ordinal)
            .First();

    private static void WriteResult(IDataContext dataContext,
        ResolveNotificationChannelNodeConfiguration c, JsonObject result)
        => dataContext.Set(c.TargetPath, result, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);
}
