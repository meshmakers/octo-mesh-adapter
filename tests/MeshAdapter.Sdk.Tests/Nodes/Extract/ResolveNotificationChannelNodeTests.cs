using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

/// <summary>
///     Proves ResolveNotificationChannel@1 (AB#5152): the deterministic fallback chain for
///     system-initiated messages. PRIMARY path — the binding-specific preference
///     (<c>PreferredChannelBindingId</c>, System.Identity 2.18.0) selects the EXACT delivery
///     target when the referenced binding is valid; a dangling reference falls back to e-mail with
///     a warning. TRANSITIONAL path — a legacy kind-level preference string is honored only with a
///     valid binding of the matching kind, disambiguating multiple bindings deterministically
///     (newest verified wins). Everything else — no preference, unresolved subject, missing
///     subjectId — is EMAIL. Never guesses, never multi-delivers.
/// </summary>
public class ResolveNotificationChannelNodeTests : NodeTestBase
{
    private const string TenantId = "acme";
    private const string SubjectId = "665544332211009988776655";

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly ISubjectChannelLookup _lookup = A.Fake<ISubjectChannelLookup>();

    private static SubjectChannelIdentifier Phone(string value, string rtId,
        DateTime? enrolledAt = null, DateTime? lastVerifiedAt = null)
        => new(ChannelIdentifierKind.PhoneNumber, value, rtId, enrolledAt, lastVerifiedAt);

    private static SubjectChannelIdentifier EntraId(string value, string rtId,
        DateTime? enrolledAt = null, DateTime? lastVerifiedAt = null)
        => new(ChannelIdentifierKind.EntraIdObjectId, value, rtId, enrolledAt, lastVerifiedAt);

    private async Task<(JsonObject? Result, IPipelineLogger Logger)> RunAsync(
        SubjectChannelRecord? record, string? subjectIdInContext = SubjectId)
    {
        A.CallTo(() => _etlContext.TenantId).Returns(TenantId);
        A.CallTo(() => _lookup.FindBySubjectIdAsync(TenantId, A<string>._, A<CancellationToken>._))
            .Returns(record);

        var config = new ResolveNotificationChannelNodeConfiguration { SubjectIdPath = "$.body.toSubjectIds[0]" };
        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger<ResolveNotificationChannelNodeConfiguration>(config);

        A.CallTo(() => dataContext.Get<string>("$.body.toSubjectIds[0]")).Returns(subjectIdInContext);

        JsonObject? emitted = null;
        A.CallTo(() => dataContext.Set(config.TargetPath, A<JsonObject>._, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes((string _, JsonObject value, DocumentModes _, ValueKinds _, TargetValueWriteModes _) =>
                emitted = value);

        var node = new ResolveNotificationChannelNode(next, _etlContext, _lookup);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        return (emitted, logger);
    }

    // --- PRIMARY path: binding-specific preference (System.Identity 2.18.0) -------------------

    [Fact]
    public async Task Preferred_phone_binding_resolves_to_signal_with_the_exact_number()
    {
        var (result, _) = await RunAsync(new SubjectChannelRecord(
            SubjectId, "u@example.com", "u",
            PreferredChannelBindingId: "b1", LegacyPreferredChannel: null,
            Identifiers: [Phone("+43660111", "b1"), Phone("+43660222", "b2")]));

        Assert.NotNull(result);
        Assert.True(result!["found"]!.GetValue<bool>());
        Assert.Equal("SIGNAL", result["effectiveChannel"]!.GetValue<string>());
        Assert.Equal("+43660111", result["channelAddress"]!.GetValue<string>());
        Assert.Equal("SIGNAL", result["preferredChannel"]!.GetValue<string>());
    }

    [Fact]
    public async Task Preferred_entra_binding_resolves_to_teams_with_the_exact_object_id()
    {
        var (result, _) = await RunAsync(new SubjectChannelRecord(
            SubjectId, "u@example.com", "u",
            PreferredChannelBindingId: "b7", LegacyPreferredChannel: null,
            Identifiers: [Phone("+43660111", "b1"), EntraId("oid-123", "b7")]));

        Assert.Equal("TEAMS", result!["effectiveChannel"]!.GetValue<string>());
        Assert.Equal("oid-123", result["channelAddress"]!.GetValue<string>());
    }

    [Fact]
    public async Task Dangling_preferred_binding_reference_falls_back_to_email_with_a_warning()
    {
        // The referenced binding was removed (or expired — the lookup only surfaces valid ones):
        // deterministic e-mail fallback, warned, per the decided chain.
        var (result, logger) = await RunAsync(new SubjectChannelRecord(
            SubjectId, "u@example.com", "u",
            PreferredChannelBindingId: "gone", LegacyPreferredChannel: null,
            Identifiers: [Phone("+43660111", "b1")]));

        Assert.Equal("EMAIL", result!["effectiveChannel"]!.GetValue<string>());
        Assert.False(result.ContainsKey("channelAddress"));
        Assert.False(result.ContainsKey("preferredChannel"));
        A.CallTo(() => logger.Warning(A<string>._, A<string>._, A<string>._, A<object[]>._))
            .MustHaveHappened();
    }

    // --- TRANSITIONAL path: legacy kind-level preference (pre-2.18.0 rollout skew) -------------

    [Fact]
    public async Task Legacy_signal_preference_with_a_valid_phone_binding_is_honored()
    {
        var (result, _) = await RunAsync(new SubjectChannelRecord(
            SubjectId, "u@example.com", "u",
            PreferredChannelBindingId: null, LegacyPreferredChannel: "SIGNAL",
            Identifiers: [Phone("+43660111", "b1")]));

        Assert.Equal("SIGNAL", result!["effectiveChannel"]!.GetValue<string>());
        Assert.Equal("+43660111", result["channelAddress"]!.GetValue<string>());
        Assert.Equal("SIGNAL", result["preferredChannel"]!.GetValue<string>());
    }

    [Fact]
    public async Task Legacy_teams_preference_with_a_valid_entra_binding_is_honored()
    {
        var (result, _) = await RunAsync(new SubjectChannelRecord(
            SubjectId, "u@example.com", "u",
            PreferredChannelBindingId: null, LegacyPreferredChannel: "TEAMS",
            Identifiers: [EntraId("oid-123", "b1")]));

        Assert.Equal("TEAMS", result!["effectiveChannel"]!.GetValue<string>());
        Assert.Equal("oid-123", result["channelAddress"]!.GetValue<string>());
    }

    [Fact]
    public async Task Legacy_preference_without_a_matching_binding_falls_back_to_email_with_a_warning()
    {
        // Preference says SIGNAL but no valid phone binding exists (e.g. removed): e-mail, warned.
        var (result, logger) = await RunAsync(new SubjectChannelRecord(
            SubjectId, "u@example.com", "u",
            PreferredChannelBindingId: null, LegacyPreferredChannel: "SIGNAL",
            Identifiers: [EntraId("oid-123", "b1")]));

        Assert.Equal("EMAIL", result!["effectiveChannel"]!.GetValue<string>());
        Assert.False(result.ContainsKey("channelAddress"));
        A.CallTo(() => logger.Warning(A<string>._, A<string>._, A<string>._, A<object[]>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task Legacy_preference_with_two_phone_bindings_picks_the_newest_verified_and_warns()
    {
        // Deterministic multi-binding rule: most recently verified wins; the warning names the
        // chosen identifier and the alternative count. Never varies for unchanged data.
        var (result, logger) = await RunAsync(new SubjectChannelRecord(
            SubjectId, "u@example.com", "u",
            PreferredChannelBindingId: null, LegacyPreferredChannel: "SIGNAL",
            Identifiers:
            [
                Phone("+43660OLD", "b1", lastVerifiedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                Phone("+43660NEW", "b2", lastVerifiedAt: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc))
            ]));

        Assert.Equal("SIGNAL", result!["effectiveChannel"]!.GetValue<string>());
        Assert.Equal("+43660NEW", result["channelAddress"]!.GetValue<string>());
        A.CallTo(() => logger.Warning(A<string>._, A<string>._, A<string>._, A<object[]>._))
            .MustHaveHappened();
    }

    [Fact]
    public void Multi_binding_tie_breaks_on_the_lowest_rtId()
    {
        // Same recency (both unstamped): the lowest binding rtId wins, ordinally — stable
        // between runs for unchanged data.
        var chosen = ResolveNotificationChannelNode.SelectBinding(
            [Phone("+2", "bbb"), Phone("+1", "aaa")]);

        Assert.Equal("aaa", chosen.BindingRtId);
    }

    [Fact]
    public async Task Unknown_legacy_preference_value_falls_back_to_email_and_never_guesses()
    {
        var (result, logger) = await RunAsync(new SubjectChannelRecord(
            SubjectId, "u@example.com", "u",
            PreferredChannelBindingId: null, LegacyPreferredChannel: "CARRIER_PIGEON",
            Identifiers: [Phone("+43660111", "b1")]));

        Assert.Equal("EMAIL", result!["effectiveChannel"]!.GetValue<string>());
        A.CallTo(() => logger.Warning(A<string>._, A<string>._, A<string>._, A<object[]>._))
            .MustHaveHappened();
    }

    // --- E-mail default -------------------------------------------------------------------------

    [Fact]
    public async Task No_preference_resolves_to_email_without_a_warning()
    {
        var (result, logger) = await RunAsync(new SubjectChannelRecord(
            SubjectId, "u@example.com", "u",
            PreferredChannelBindingId: null, LegacyPreferredChannel: null,
            Identifiers: [Phone("+43660111", "b1"), EntraId("oid-123", "b2")]));

        Assert.Equal("EMAIL", result!["effectiveChannel"]!.GetValue<string>());
        Assert.False(result.ContainsKey("preferredChannel"));
        Assert.False(result.ContainsKey("channelAddress"));
        A.CallTo(() => logger.Warning(A<string>._, A<string>._, A<string>._, A<object[]>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Both_identifier_kinds_are_surfaced_for_diagnostics()
    {
        var (result, _) = await RunAsync(new SubjectChannelRecord(
            SubjectId, "u@example.com", "u",
            PreferredChannelBindingId: null, LegacyPreferredChannel: null,
            Identifiers: [Phone("+43660111", "b1"), EntraId("oid-123", "b2")]));

        var identifiers = result!["identifiers"]!.AsArray();
        Assert.Equal(2, identifiers.Count);
        Assert.Equal("PhoneNumber", identifiers[0]!["kind"]!.GetValue<string>());
        Assert.Equal("+43660111", identifiers[0]!["value"]!.GetValue<string>());
        Assert.Equal("EntraIdObjectId", identifiers[1]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task Profile_fields_are_emitted_and_the_email_is_available_for_the_fallback()
    {
        var (result, _) = await RunAsync(new SubjectChannelRecord(
            SubjectId, "u@example.com", "u", null, null, []));

        Assert.Equal(SubjectId, result!["subjectId"]!.GetValue<string>());
        Assert.Equal("u@example.com", result["email"]!.GetValue<string>());
        Assert.Equal("u", result["name"]!.GetValue<string>());
    }

    // --- Unresolved subject ---------------------------------------------------------------------

    [Fact]
    public async Task Unresolved_subject_writes_not_found_with_email_fallback_and_a_warning()
    {
        var (result, logger) = await RunAsync(record: null);

        Assert.NotNull(result);
        Assert.False(result!["found"]!.GetValue<bool>());
        Assert.Equal("EMAIL", result["effectiveChannel"]!.GetValue<string>());
        A.CallTo(() => logger.Warning(A<string>._, A<string>._, A<string>._, A<object[]>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task Missing_subjectId_in_the_context_falls_back_to_email_without_a_lookup()
    {
        var (result, logger) = await RunAsync(record: null, subjectIdInContext: null);

        Assert.False(result!["found"]!.GetValue<bool>());
        Assert.Equal("EMAIL", result["effectiveChannel"]!.GetValue<string>());
        A.CallTo(() => _lookup.FindBySubjectIdAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => logger.Warning(A<string>._, A<string>._, A<string>._, A<object[]>._))
            .MustHaveHappened();
    }
}
