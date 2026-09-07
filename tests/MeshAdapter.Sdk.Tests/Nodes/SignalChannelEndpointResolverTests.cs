using FakeItEasy;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Xunit;

namespace MeshAdapter.Sdk.Tests.Nodes;

/// <summary>
/// AB#5145: FromSignal@1 / SignalSender@1 resolve the bridge number + api URL from the tenant's
/// System.Communication/SignalChannel singleton — the entry the communication controller now
/// ALWAYS projects into every pipeline's GlobalConfiguration under the key "signal-channel" —
/// before falling back to the deprecated settingsConfiguration/node-property mechanism. These
/// tests pin the resolution matrix: registered channel wins over legacy; a present-but-not-
/// registered channel warns and falls through; legacy still works (with a deprecation warning);
/// neither resolves to None (the trigger's idle case). The wire shape mirrors the controller's
/// Serialize(): the whole entity with CK attributes nested under "attributes" (PascalCase) and
/// the RegistrationState enum as a JSON number.
/// </summary>
public class SignalChannelEndpointResolverTests
{
    private const string LegacySettingsName = "SignalImportSettings";

    private const string RegisteredChannelJson =
        """
        {"rtId":"aa0000000000000000000401","ckTypeId":{"fullName":"System.Communication/SignalChannel-1"},
         "rtWellKnownName":"signal-channel",
         "attributes":{"Number":"+43677111111111","ApiUrl":"http://bridge.signal:8080",
         "RegistrationState":2,"RegisteredAt":"2026-09-07T10:00:00Z"}}
        """;

    private const string CodePendingChannelJson =
        """
        {"attributes":{"Number":"+43677111111111","ApiUrl":"http://bridge.signal:8080",
         "RegistrationState":1}}
        """;

    private const string LegacySettingsJson =
        """
        {"attributes":{"SignalNumber":"+43677222222222","SignalApiUrl":"http://legacy.signal:8080"}}
        """;

    private static IGlobalConfiguration GlobalConfigWith(
        string? signalChannelJson = null, string? legacySettingsJson = null)
    {
        var g = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => g.IsDefined(A<string>._)).Returns(false);
        if (signalChannelJson != null)
        {
            A.CallTo(() => g.IsDefined("signal-channel")).Returns(true);
            A.CallTo(() => g.GetRawJson("signal-channel")).Returns(signalChannelJson);
        }

        if (legacySettingsJson != null)
        {
            A.CallTo(() => g.IsDefined(LegacySettingsName)).Returns(true);
            A.CallTo(() => g.GetRawJson(LegacySettingsName)).Returns(legacySettingsJson);
        }

        return g;
    }

    private static string ChannelJsonWithState(string stateJson) =>
        """
        {"attributes":{"Number":"+43677111111111","ApiUrl":"http://bridge.signal:8080",
         "RegistrationState":__STATE__}}
        """.Replace("__STATE__", stateJson);

    private static SignalEndpointResolution ResolveWithLegacyConfigured(IGlobalConfiguration g) =>
        SignalChannelEndpointResolver.Resolve(g,
            LegacySettingsName, "SignalNumber", "SignalApiUrl",
            legacyNumber: null, legacyApiUrl: null);

    [Fact]
    public void RegisteredChannel_WinsOverLegacySettings()
    {
        var g = GlobalConfigWith(RegisteredChannelJson, LegacySettingsJson);

        var r = ResolveWithLegacyConfigured(g);

        Assert.Equal(SignalEndpointSource.SignalChannel, r.Source);
        Assert.Equal("+43677111111111", r.Number);
        Assert.Equal("http://bridge.signal:8080", r.ApiUrl);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void RegisteredChannel_ResolvesWithoutAnyLegacyConfiguration()
    {
        // New-style pipelines omit settingsConfiguration/numberAttribute/apiUrlAttribute entirely.
        var g = GlobalConfigWith(RegisteredChannelJson);

        var r = SignalChannelEndpointResolver.Resolve(g, null, null, null, null, null);

        Assert.Equal(SignalEndpointSource.SignalChannel, r.Source);
        Assert.True(r.IsConfigured);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void NotRegisteredChannel_WarnsAndFallsBackToLegacy()
    {
        var g = GlobalConfigWith(CodePendingChannelJson, LegacySettingsJson);

        var r = ResolveWithLegacyConfigured(g);

        Assert.Equal(SignalEndpointSource.LegacySettings, r.Source);
        Assert.Equal("+43677222222222", r.Number);
        Assert.Equal("http://legacy.signal:8080", r.ApiUrl);
        Assert.Contains(r.Warnings, w => w.Contains("not Registered"));
        Assert.Contains(r.Warnings, w => w.Contains("deprecated"));
    }

    [Fact]
    public void NotRegisteredChannel_WithoutLegacy_IsUnconfiguredWithWarning()
    {
        var g = GlobalConfigWith(CodePendingChannelJson);

        var r = SignalChannelEndpointResolver.Resolve(g, null, null, null, null, null);

        Assert.Equal(SignalEndpointSource.None, r.Source);
        Assert.False(r.IsConfigured);
        Assert.Contains(r.Warnings, w => w.Contains("not Registered"));
    }

    [Fact]
    public void LegacySettingsEntity_ResolvesWithDeprecationWarning()
    {
        var g = GlobalConfigWith(legacySettingsJson: LegacySettingsJson);

        var r = ResolveWithLegacyConfigured(g);

        Assert.Equal(SignalEndpointSource.LegacySettings, r.Source);
        Assert.Equal("+43677222222222", r.Number);
        Assert.Equal("http://legacy.signal:8080", r.ApiUrl);
        Assert.Contains(r.Warnings, w => w.Contains("deprecated"));
    }

    [Fact]
    public void LegacyNodeProperties_ResolveWithDeprecationWarning()
    {
        var g = GlobalConfigWith();

        var r = SignalChannelEndpointResolver.Resolve(g, null, null, null,
            legacyNumber: "+43677333333333", legacyApiUrl: "http://literal.signal:8080");

        Assert.Equal(SignalEndpointSource.LegacySettings, r.Source);
        Assert.Equal("+43677333333333", r.Number);
        Assert.Equal("http://literal.signal:8080", r.ApiUrl);
        Assert.Contains(r.Warnings, w => w.Contains("deprecated"));
    }

    [Fact]
    public void LegacySettingsValue_WinsOverNodeProperty()
    {
        // Unchanged legacy precedence: a settings-entity value overrides the literal.
        var g = GlobalConfigWith(legacySettingsJson: LegacySettingsJson);

        var r = SignalChannelEndpointResolver.Resolve(g,
            LegacySettingsName, "SignalNumber", "SignalApiUrl",
            legacyNumber: "+43677999999999", legacyApiUrl: "http://stale.signal:8080");

        Assert.Equal("+43677222222222", r.Number);
        Assert.Equal("http://legacy.signal:8080", r.ApiUrl);
    }

    [Fact]
    public void Neither_IsUnconfiguredWithoutWarnings()
    {
        var g = GlobalConfigWith();

        var r = SignalChannelEndpointResolver.Resolve(g, null, null, null, null, null);

        Assert.Equal(SignalEndpointSource.None, r.Source);
        Assert.False(r.IsConfigured);
        // No channel and no legacy settings is the ordinary state of a tenant without Signal —
        // nothing to warn about; the node emits its single idle line instead.
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void HalfConfiguredLegacy_IsUnconfiguredWithWarning()
    {
        var g = GlobalConfigWith();

        var r = SignalChannelEndpointResolver.Resolve(g, null, null, null,
            legacyNumber: "+43677333333333", legacyApiUrl: null);

        Assert.Equal(SignalEndpointSource.None, r.Source);
        Assert.Contains(r.Warnings, w => w.Contains("only part"));
    }

    [Theory]
    [InlineData("\"2\"")] // numeric string
    [InlineData("\"Registered\"")] // enum name
    public void RegistrationState_IsReadLiberally(string stateJson)
    {
        var json = ChannelJsonWithState(stateJson);
        var g = GlobalConfigWith(json);

        var r = SignalChannelEndpointResolver.Resolve(g, null, null, null, null, null);

        Assert.Equal(SignalEndpointSource.SignalChannel, r.Source);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("\"CodePending\"")]
    [InlineData("null")]
    public void NonRegisteredStates_AreTreatedAsUnconfigured(string stateJson)
    {
        var json = ChannelJsonWithState(stateJson);
        var g = GlobalConfigWith(json);

        var r = SignalChannelEndpointResolver.Resolve(g, null, null, null, null, null);

        Assert.Equal(SignalEndpointSource.None, r.Source);
        Assert.Contains(r.Warnings, w => w.Contains("not Registered"));
    }

    [Fact]
    public void MissingRegistrationState_FailsClosed()
    {
        // Fail closed: without a readable state the channel must not be used — an unregistered
        // number cannot send, and acting on it would poll/post against a dead account.
        const string json =
            """{"attributes":{"Number":"+43677111111111","ApiUrl":"http://bridge.signal:8080"}}""";
        var g = GlobalConfigWith(json);

        var r = SignalChannelEndpointResolver.Resolve(g, null, null, null, null, null);

        Assert.Equal(SignalEndpointSource.None, r.Source);
    }

    [Fact]
    public void RegisteredChannelWithoutNumber_WarnsAndFallsBackToLegacy()
    {
        const string json =
            """{"attributes":{"ApiUrl":"http://bridge.signal:8080","RegistrationState":2}}""";
        var g = GlobalConfigWith(json, LegacySettingsJson);

        var r = ResolveWithLegacyConfigured(g);

        Assert.Equal(SignalEndpointSource.LegacySettings, r.Source);
        Assert.Contains(r.Warnings, w => w.Contains("no Number/ApiUrl"));
    }

    [Fact]
    public void MalformedChannelEntry_WarnsAndFallsBackToLegacy()
    {
        var g = GlobalConfigWith("{not json", LegacySettingsJson);

        var r = ResolveWithLegacyConfigured(g);

        Assert.Equal(SignalEndpointSource.LegacySettings, r.Source);
        Assert.Contains(r.Warnings, w => w.Contains("could not be parsed"));
    }
}
