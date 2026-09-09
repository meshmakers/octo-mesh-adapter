using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Xunit;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
/// FromMicrosoftGraphEmail can read its mailbox / folders / poll interval from a
/// configuration entity (SettingsConfiguration + configurable attribute names)
/// instead of the pipeline definition, so a redeploy never overwrites operator
/// settings and nothing tenant-specific leaks into the seed. These tests pin the
/// resolution rules — including the load-bearing detail that the serialized config's
/// CK attributes live in a nested "attributes" object, not at the JSON root.
/// </summary>
public class FromMicrosoftGraphEmailNodeSettingsResolutionTests
{
    private const string SettingsName = "EmailImportSettings";

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

    private static FromMicrosoftGraphEmailNodeConfiguration ConfigWithSettingsSource(
        string? mailboxProp = null, string? folderProp = null) => new()
    {
        ServerConfiguration = "graph",
        Mailbox = mailboxProp!,
        FolderPath = folderProp!,
        PollingIntervalSeconds = 120,
        SettingsConfiguration = SettingsName,
        MailboxAttribute = "EmailImportMailbox",
        SourceFolderAttribute = "EmailImportSourceFolder",
        DoneFolderAttribute = "EmailImportDoneFolder",
        PollingSecondsAttribute = "EmailImportPollingSeconds",
    };

    // The runtime serializes the whole entity; attributes are nested (PascalCase).
    private const string RealShapeJson =
        """
        {"rtId":"aa0000000000000000000330","ckTypeId":{"fullName":"Meshmakers.Accounting/EmailImportSettings-1"},
         "attributes":{"EmailImportEnabled":true,"EmailImportMailbox":"box@example.com",
         "EmailImportSourceFolder":"Archive/Invoices/ToDo","EmailImportDoneFolder":"Archive/Invoices/Done",
         "EmailImportPollingSeconds":300}}
        """;

    [Fact]
    public void ResolvesMailboxFoldersAndPollingFromNestedAttributes()
    {
        var result = FromMicrosoftGraphEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith(RealShapeJson), ConfigWithSettingsSource());

        Assert.Equal("box@example.com", result.Mailbox);
        Assert.Equal("Archive/Invoices/ToDo", result.FolderPath);
        Assert.Equal("Archive/Invoices/Done", result.MoveToFolderPathOnSuccess);
        Assert.Equal(300, result.PollingIntervalSeconds);
    }

    [Fact]
    public void SettingsValueWinsOverNodeProperty()
    {
        var result = FromMicrosoftGraphEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith(RealShapeJson),
            ConfigWithSettingsSource(mailboxProp: "stale@in-definition", folderProp: "Stale/Folder"));

        Assert.Equal("box@example.com", result.Mailbox);
        Assert.Equal("Archive/Invoices/ToDo", result.FolderPath);
    }

    [Fact]
    public void MissingAttributeFallsBackToNodeProperty()
    {
        // Source folder absent in the config → keep the node property; mailbox still resolves.
        const string json =
            """{"attributes":{"EmailImportMailbox":"box@example.com"}}""";

        var result = FromMicrosoftGraphEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith(json), ConfigWithSettingsSource(folderProp: "Fallback/Folder"));

        Assert.Equal("box@example.com", result.Mailbox);
        Assert.Equal("Fallback/Folder", result.FolderPath);
        Assert.Equal(120, result.PollingIntervalSeconds); // no polling attr → node default
    }

    [Fact]
    public void NoSettingsConfiguration_ReturnsNodePropsUnchanged()
    {
        var c = new FromMicrosoftGraphEmailNodeConfiguration
        {
            ServerConfiguration = "graph",
            Mailbox = "box@example.com",
            FolderPath = "Archive/Invoices/ToDo",
        };

        var result = FromMicrosoftGraphEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith(RealShapeJson), c);

        Assert.Same(c, result);
    }

    [Fact]
    public void SettingsNotDefined_ReturnsNodePropsUnchanged()
    {
        var c = ConfigWithSettingsSource(mailboxProp: "box@example.com", folderProp: "F");

        var result = FromMicrosoftGraphEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith(null), c);

        Assert.Same(c, result);
    }

    [Fact]
    public void MalformedJson_FallsBackToNodeProps()
    {
        var c = ConfigWithSettingsSource(mailboxProp: "box@example.com", folderProp: "F");

        var result = FromMicrosoftGraphEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith("{ this is not json"), c);

        Assert.Same(c, result);
    }
}
