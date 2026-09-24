using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Xunit;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
/// AB#5341 — the import WINDOW, as opposed to the cut-off inside it.
///
/// <para>
/// The accounting channel derives its cut-off from the open fiscal years, and the case that has no
/// date at all — no fiscal year is open — has to mean "import nothing". An absent
/// <c>sinceDate</c>/<c>sinceDaysBack</c> cannot say that: it means "no date filter", and paired with
/// <c>onlyUnread: false</c> that is the unbounded <c>SearchQuery.All</c> which killed the adapter on
/// prod-1 with 1697 mails (AB#5336). So the empty window is its own value, and these tests pin the
/// two properties that make it safe:
/// </para>
/// <list type="bullet">
/// <item><description>unset reads as OPEN, so every already deployed pipeline keeps polling;</description></item>
/// <item><description>the settings entity wins over the definition, like every other setting (AB#5345).</description></item>
/// </list>
/// </summary>
public class FromEmailNodeImportWindowTests
{
    private const string SettingsName = "ImapImportSettings";

    private static IGlobalConfiguration GlobalConfigWith(string? rawJson)
    {
        var g = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => g.IsDefined(SettingsName)).Returns(rawJson != null);
        if (rawJson != null)
        {
            A.CallTo(() => g.GetRawJson(SettingsName)).Returns(rawJson);
        }

        return g;
    }

    private static string SettingsJson(string attributes) =>
        "{\"rtId\":\"aa0000000000000000000332\"," +
        "\"ckTypeId\":{\"fullName\":\"Meshmakers.Accounting/EmailImportSettings-2\"}," +
        "\"attributes\":{" + attributes + "}}";

    private static FromEmailNodeConfiguration DefinitionConfig(bool? windowOpen = null) => new()
    {
        ServerConfiguration = "EmailAccountingAssistant",
        PollingIntervalSeconds = 60,
        OnlyUnread = true,
        SettingsConfiguration = SettingsName,
        SinceDateAttribute = "EmailImportSinceDate",
        SinceDaysBackAttribute = "EmailImportSinceDaysBack",
        WindowOpen = windowOpen,
        WindowOpenAttribute = "EmailImportWindowOpen",
    };

    // ---- Unset is OPEN ---------------------------------------------------------------

    /// <summary>
    /// The migration property. Every <c>FromEmail@1</c> deployed before AB#5341 names neither the
    /// node property nor the attribute, and none of them may stop fetching because of it.
    /// </summary>
    [Fact]
    public void NeitherDefinitionNorSettingsSayAnything_WindowIsOpen()
    {
        var result = FromEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith(SettingsJson("\"EmailImportSinceDaysBack\":14")), DefinitionConfig());

        Assert.Null(result.WindowOpen);
        Assert.True(FromEmailNode.ResolveWindowOpen(result));
    }

    /// <summary>
    /// A <c>windowOpen:</c> key that is PRESENT and null is the YamlDotNet trap the property is
    /// nullable for: on a non-nullable bool it would deserialize to <c>false</c> and switch a
    /// working mailbox off.
    /// </summary>
    [Fact]
    public void NoSettingsEntityAtAll_WindowIsOpen()
    {
        var result = FromEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith(null), DefinitionConfig());

        Assert.True(FromEmailNode.ResolveWindowOpen(result));
    }

    // ---- The settings win ------------------------------------------------------------

    [Fact]
    public void SettingsCloseTheWindow_AndOverrideAnOpenDefinition()
    {
        var result = FromEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith(SettingsJson("\"EmailImportWindowOpen\":false")),
            DefinitionConfig(windowOpen: true));

        Assert.False(FromEmailNode.ResolveWindowOpen(result));
    }

    [Fact]
    public void SettingsReopenTheWindow_AndOverrideAClosedDefinition()
    {
        var result = FromEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith(SettingsJson("\"EmailImportWindowOpen\":true")),
            DefinitionConfig(windowOpen: false));

        Assert.True(FromEmailNode.ResolveWindowOpen(result));
    }

    /// <summary>
    /// The definition stays the fallback: a channel may close its window without any configuration
    /// entity at all.
    /// </summary>
    [Fact]
    public void DefinitionClosesTheWindow_AndTheSettingsAreSilent()
    {
        var result = FromEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith(SettingsJson("\"EmailImportSinceDaysBack\":14")),
            DefinitionConfig(windowOpen: false));

        Assert.False(FromEmailNode.ResolveWindowOpen(result));
    }

    /// <summary>
    /// A definition that names no attribute opts out of the setting entirely — the configuration
    /// may carry the value, and it is not read.
    /// </summary>
    [Fact]
    public void DefinitionNamesNoAttribute_TheConfiguredValueIsIgnored()
    {
        var definition = DefinitionConfig(windowOpen: true) with { WindowOpenAttribute = null };

        var result = FromEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith(SettingsJson("\"EmailImportWindowOpen\":false")), definition);

        Assert.True(FromEmailNode.ResolveWindowOpen(result));
    }

    // ---- The window is not the cut-off -----------------------------------------------

    /// <summary>
    /// A closed window leaves the cut-off exactly as it stands. It is not cleared and not
    /// overwritten: the moment the window reopens, the recompute writes the new cut-off, and until
    /// then the stale one is simply not used by anything.
    /// </summary>
    [Fact]
    public void AClosedWindowDoesNotTouchTheCutOff()
    {
        var result = FromEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith(SettingsJson(
                "\"EmailImportWindowOpen\":false,\"EmailImportSinceDate\":\"2026-01-01T00:00:00Z\"")),
            DefinitionConfig());

        Assert.False(FromEmailNode.ResolveWindowOpen(result));
        Assert.Equal(new DateTime(2026, 1, 1), FromEmailNode.ResolveSinceDate(result));
    }
}
