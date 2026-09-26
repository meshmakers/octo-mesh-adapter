using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using FakeItEasy;
using MailKit.Security;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.MailFolders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

/// <summary>
/// AB#5370: the folder picker's contract is that <c>path</c> is EXACTLY the string the channel's
/// trigger accepts. For IMAP that is the server-reported full name with the server's own delimiter
/// (Dovecot's <c>.</c>, others' <c>/</c>), untouched; for Microsoft Graph it is the display names
/// joined with <c>/</c>, a <c>/</c> inside a name escaped as <c>\/</c> — the rule AB#5385 part 3
/// adopts on the trigger side. These tests pin both spellings without a live mailbox, plus the
/// walk order, the cap, and the operator-facing wording of each failure.
/// </summary>
public class ListMailFoldersNodeTests : NodeTestBase
{
    // ------------------------------------------------------------------ Graph path syntax

    [Fact]
    public void GraphPath_SlashInsideAName_IsEscaped()
    {
        Assert.Equal(@"02_Steuern \/ Finanzen", MailFolderPathSyntax.EscapeGraphSegment("02_Steuern / Finanzen"));
        Assert.Equal("Rechnungen", MailFolderPathSyntax.EscapeGraphSegment("Rechnungen"));
    }

    [Fact]
    public void GraphPath_NestedNames_AreJoinedWithSlash()
    {
        var path = MailFolderPathSyntax.JoinGraphPath(["Inbox", "02_Steuern / Finanzen", "1_Tecob GmbH"]);

        Assert.Equal(@"Inbox/02_Steuern \/ Finanzen/1_Tecob GmbH", path);
    }

    [Fact]
    public void GraphPath_Split_IsTheInverseOfJoin()
    {
        string[] names = ["Inbox", "02_Steuern / Finanzen", "1_Tecob GmbH"];

        var split = MailFolderPathSyntax.SplitGraphPath(MailFolderPathSyntax.JoinGraphPath(names));

        Assert.Equal(names, split);
    }

    [Fact]
    public void GraphPath_Split_KeepsPreExistingPathsReadable()
    {
        // What every operator stored before the rule existed: plain '/'-separated names, trimmed,
        // empty segments dropped — exactly the trigger's historical split.
        Assert.Equal(["Archive", "Rechnungen_Verträge", "ToDo"],
            MailFolderPathSyntax.SplitGraphPath(" Archive / Rechnungen_Verträge//ToDo "));
        // A backslash that is not followed by '/' is an ordinary character.
        Assert.Equal([@"A\B", "C"], MailFolderPathSyntax.SplitGraphPath(@"A\B/C"));
        Assert.Empty(MailFolderPathSyntax.SplitGraphPath("   "));
        Assert.Empty(MailFolderPathSyntax.SplitGraphPath(null));
    }

    // ------------------------------------------------------------------ Tree walk

    private sealed record FakeFolder(string Name, string FullName, List<FakeFolder> Children);

    private static FakeFolder F(string name, string fullName, params FakeFolder[] children) =>
        new(name, fullName, children.ToList());

    private static Task<MailFolderTree.Listing> WalkImap(IReadOnlyList<FakeFolder> roots, int max = 500) =>
        MailFolderTree.WalkDepthFirstAsync<FakeFolder>(
            roots,
            f => Task.FromResult<IReadOnlyList<FakeFolder>>(f.Children),
            f => f.Name,
            (f, _) => f.FullName,
            max);

    [Fact]
    public async Task ImapWalk_DotDelimiter_ReportsServerNamesVerbatim()
    {
        // The prod-1/gastroacker shape (Dovecot): a real INBOX beneath INBOX, delimiter '.'.
        var roots = new List<FakeFolder>
        {
            F("INBOX", "INBOX",
                F("INBOX", "INBOX.INBOX",
                    F("Finanzen", "INBOX.INBOX.Finanzen",
                        F("Rechnungen", "INBOX.INBOX.Finanzen.Rechnungen"))),
                F("Sent", "INBOX.Sent")),
            F("Archiv", "Archiv")
        };

        var listing = await WalkImap(roots);

        Assert.False(listing.Truncated);
        Assert.Equal(
        [
            ("INBOX", "INBOX", 0),
            ("INBOX.INBOX", "INBOX", 1),
            ("INBOX.INBOX.Finanzen", "Finanzen", 2),
            ("INBOX.INBOX.Finanzen.Rechnungen", "Rechnungen", 3),
            ("INBOX.Sent", "Sent", 1),
            ("Archiv", "Archiv", 0)
        ], listing.Folders.Select(e => (e.Path, e.DisplayName, e.Depth)).ToList());
    }

    [Fact]
    public async Task ImapWalk_SlashDelimiter_ReportsServerNamesVerbatim()
    {
        var roots = new List<FakeFolder>
        {
            F("INBOX", "INBOX",
                F("Finanzen", "INBOX/Finanzen",
                    F("Rechnungen", "INBOX/Finanzen/Rechnungen")))
        };

        var listing = await WalkImap(roots);

        Assert.Equal(["INBOX", "INBOX/Finanzen", "INBOX/Finanzen/Rechnungen"],
            listing.Folders.Select(e => e.Path).ToList());
        Assert.Equal([0, 1, 2], listing.Folders.Select(e => e.Depth).ToList());
    }

    [Fact]
    public async Task Walk_StopsAtTheCap_AndSaysSo()
    {
        var roots = new List<FakeFolder>
        {
            F("A", "A", F("A1", "A.A1"), F("A2", "A.A2")),
            F("B", "B")
        };

        var capped = await WalkImap(roots, max: 3);
        Assert.True(capped.Truncated);
        Assert.Equal(["A", "A.A1", "A.A2"], capped.Folders.Select(e => e.Path).ToList());

        // Exactly the cap is not truncation — nothing was left out.
        var exact = await WalkImap(roots, max: 4);
        Assert.False(exact.Truncated);
        Assert.Equal(4, exact.Folders.Count);
    }

    [Fact]
    public async Task Walk_DoesNotFetchChildrenBeyondTheCap()
    {
        var fetched = new List<string>();
        var roots = new List<FakeFolder> { F("A", "A", F("A1", "A.A1")), F("B", "B", F("B1", "B.B1")) };

        await MailFolderTree.WalkDepthFirstAsync<FakeFolder>(
            roots,
            f =>
            {
                fetched.Add(f.Name);
                return Task.FromResult<IReadOnlyList<FakeFolder>>(f.Children);
            },
            f => f.Name,
            (f, _) => f.FullName,
            maxFolders: 2);

        Assert.Equal(["A", "A1"], fetched);
    }

    [Fact]
    public async Task GraphWalk_BuildsEscapedPathsFromDisplayNames()
    {
        // The prod-1/tecob shape: a folder whose display name contains a '/'.
        var roots = new List<FakeFolder>
        {
            F("Inbox", "",
                F("02_Steuern / Finanzen", "",
                    F("1_Tecob GmbH", "")))
        };

        var listing = await MailFolderTree.WalkDepthFirstAsync<FakeFolder>(
            roots,
            f => Task.FromResult<IReadOnlyList<FakeFolder>>(f.Children),
            f => f.Name,
            (f, parent) => MailFolderPathSyntax.JoinGraphPath(parent?.Path, f.Name),
            500);

        Assert.Equal(
        [
            ("Inbox", "Inbox", 0),
            (@"Inbox/02_Steuern \/ Finanzen", "02_Steuern / Finanzen", 1),
            (@"Inbox/02_Steuern \/ Finanzen/1_Tecob GmbH", "1_Tecob GmbH", 2)
        ], listing.Folders.Select(e => (e.Path, e.DisplayName, e.Depth)).ToList());

        // And every one of them resolves back to the display names the trigger will look up.
        Assert.Equal(["Inbox", "02_Steuern / Finanzen", "1_Tecob GmbH"],
            MailFolderPathSyntax.SplitGraphPath(listing.Folders[2].Path));
    }

    // ------------------------------------------------------------------ Resolution

    [Theory]
    [InlineData("Imap", "Imap")]
    [InlineData("imap", "Imap")]
    [InlineData(" GRAPH ", "Graph")]
    [InlineData("Graph", "Graph")]
    [InlineData("Exchange", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeChannel_IsCaseInsensitiveAndStrict(string? input, string? expected)
    {
        Assert.Equal(expected, ListMailFoldersNode.NormalizeChannel(input));
    }

    [Fact]
    public void GraphMailbox_SettingsAttributeWins_NodePropertyIsTheFallback()
    {
        var global = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => global.IsDefined("EmailImportSettings")).Returns(true);
        A.CallTo(() => global.GetRawJson("EmailImportSettings")).Returns(
            """{"rtId":"aa0000000000000000000330","attributes":{"EmailImportMailbox":"box@example.com"}}""");

        var withSettings = new ListMailFoldersNodeConfiguration
        {
            TargetPath = "$.result",
            GraphMailbox = "stale@in-definition",
            GraphSettingsConfiguration = "EmailImportSettings",
            GraphMailboxAttribute = "emailImportMailbox"
        };
        Assert.Equal("box@example.com", ListMailFoldersNode.ResolveGraphMailbox(global, withSettings));

        var withoutSettings = withSettings with { GraphSettingsConfiguration = null };
        Assert.Equal("stale@in-definition", ListMailFoldersNode.ResolveGraphMailbox(global, withoutSettings));
    }

    // ------------------------------------------------------------------ Failure wording

    private static readonly ImapMailboxAccess.ImapServerSettings Imap = new()
    {
        Host = "imap.example.com", Port = 993, Username = "buchhaltung@example.com", Password = "x",
        IsSslEnabled = true
    };

    [Fact]
    public void ImapFailure_NamesItsCause()
    {
        var timeout = TimeSpan.FromSeconds(60);

        var auth = ImapMailboxAccess.DescribeConnectFailure(new AuthenticationException(), Imap, timeout);
        Assert.Contains("authentication failed", auth);
        Assert.Contains("buchhaltung@example.com", auth);
        Assert.Contains("imap.example.com:993", auth);

        var tls = ImapMailboxAccess.DescribeConnectFailure(new SslHandshakeException("certificate rejected"), Imap, timeout);
        Assert.Contains("TLS handshake", tls);
        Assert.Contains("993", tls);

        var unreachable = ImapMailboxAccess.DescribeConnectFailure(
            new SocketException((int)SocketError.HostNotFound), Imap, timeout);
        Assert.Contains("not reachable", unreachable);

        var wrapped = ImapMailboxAccess.DescribeConnectFailure(
            new IOException("read failed", new SocketException((int)SocketError.TimedOut)), Imap, timeout);
        Assert.Contains("not reachable", wrapped);

        var timedOut = ImapMailboxAccess.DescribeConnectFailure(new OperationCanceledException(), Imap, timeout);
        Assert.Contains("did not answer within 60 seconds", timedOut);
    }

    [Fact]
    public void GraphFailure_NamesCredentialsConsentOrMailbox()
    {
        var token = GraphMailboxAccess.DescribeTokenFailure(401,
            """{"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret provided.\r\nTrace ID: abc"}""");
        Assert.Contains("invalid_client", token);
        Assert.Contains("AADSTS7000215: Invalid client secret provided.", token);
        Assert.DoesNotContain("Trace ID", token);
        Assert.Contains("client secret", token);

        var forbidden = GraphMailboxAccess.DescribeGraphFailure(403,
            """{"error":{"code":"ErrorAccessDenied","message":"Access is denied. Check credentials and try again."}}""",
            "box@example.com");
        Assert.Contains("Mail.Read", forbidden);
        Assert.Contains("box@example.com", forbidden);
        Assert.Contains("ErrorAccessDenied", forbidden);

        var unauthorized = GraphMailboxAccess.DescribeGraphFailure(401, "", "box@example.com");
        Assert.Contains("(401)", unauthorized);
        Assert.Contains("client secret", unauthorized);

        var notFound = GraphMailboxAccess.DescribeGraphFailure(404,
            """{"error":{"code":"ErrorInvalidUser","message":"The requested user 'x' is invalid."}}""",
            "box@example.com");
        Assert.Contains("was not found", notFound);
        Assert.Contains("ErrorInvalidUser", notFound);
    }

    [Fact]
    public void GraphPage_ReadsFoldersAndNextLink_UnknownChildCountMeansLook()
    {
        var folders = new List<GraphMailboxAccess.GraphFolder>();

        var next = GraphMailboxAccess.ParseFolderPage(
            """
            {"value":[
              {"id":"a","displayName":"Inbox","childFolderCount":2},
              {"id":"b","displayName":"Sent Items","childFolderCount":0},
              {"id":"c","displayName":"NoCount"}
            ],"@odata.nextLink":"https://graph.microsoft.com/v1.0/next"}
            """, folders);

        Assert.Equal("https://graph.microsoft.com/v1.0/next", next);
        Assert.Equal([("a", "Inbox", 2), ("b", "Sent Items", 0), ("c", "NoCount", 1)],
            folders.Select(f => (f.Id, f.DisplayName, f.ChildFolderCount)).ToList());
        Assert.Null(GraphMailboxAccess.ParseFolderPage("""{"value":[]}""", folders));
    }

    // ------------------------------------------------------------------ Node, Graph channel end to end

    private const string GraphConfig = "MicrosoftGraphDocuments";

    private static (IMeshEtlContext Etl, IHttpClientFactory Http) GraphContext(SequencedHttpMessageHandler handler)
    {
        var etl = A.Fake<IMeshEtlContext>();
        var global = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => etl.GlobalConfiguration).Returns(global);
        A.CallTo(() => global.IsDefined(GraphConfig)).Returns(true);
        A.CallTo(() => global.GetValue<GraphMailboxAccess.GraphAppCredentials>(GraphConfig))
            .Returns(new GraphMailboxAccess.GraphAppCredentials
            {
                AzureTenantId = "tenant-guid", ClientId = "client-guid", ClientSecret = "secret"
            });
        var http = A.Fake<IHttpClientFactory>();
        A.CallTo(() => http.CreateClient(string.Empty))
            .ReturnsLazily(() => new HttpClient(handler, disposeHandler: false));
        return (etl, http);
    }

    private static ListMailFoldersNodeConfiguration GraphRouteConfig() => new()
    {
        TargetPath = "$.result",
        ChannelPath = "$.body.channel",
        GraphServerConfiguration = GraphConfig,
        GraphMailbox = "box@example.com",
        MaxFolders = 50
    };

    [Fact]
    public async Task Graph_ListsTheTreeDepthFirst_FollowingPagingAndChildFolders()
    {
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json("""{"access_token":"tok"}"""),
            // Root page 1 → page 2 (paging), then the children of "Inbox", then of the '/' folder.
            SequencedHttpMessageHandler.Json(
                """{"value":[{"id":"inbox","displayName":"Inbox","childFolderCount":1}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users/box%40example.com/mailFolders?$skip=1"}"""),
            SequencedHttpMessageHandler.Json(
                """{"value":[{"id":"sent","displayName":"Sent Items","childFolderCount":0}]}"""),
            SequencedHttpMessageHandler.Json(
                """{"value":[{"id":"st","displayName":"02_Steuern / Finanzen","childFolderCount":1}]}"""),
            SequencedHttpMessageHandler.Json(
                """{"value":[{"id":"tc","displayName":"1_Tecob GmbH","childFolderCount":0}]}"""));
        var (etl, http) = GraphContext(handler);
        var config = GraphRouteConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);
        SetupGetSimpleValueByPath(dataContext, "$.body.channel", "graph");

        var node = new ListMailFoldersNode(next, etl, http, NullLogger<ListMailFoldersNode>.Instance);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.Set("$.result",
                A<JsonNode?>.That.Matches(n => n != null &&
                                              n["channel"]!.GetValue<string>() == "Graph" &&
                                              n["delimiter"]!.GetValue<string>() == "/" &&
                                              n["mailbox"]!.GetValue<string>() == "box@example.com" &&
                                              n["truncated"]!.GetValue<bool>() == false &&
                                              n["folders"]!.AsArray().Count == 4 &&
                                              n["folders"]![0]!["path"]!.GetValue<string>() == "Inbox" &&
                                              n["folders"]![1]!["path"]!.GetValue<string>() == @"Inbox/02_Steuern \/ Finanzen" &&
                                              n["folders"]![1]!["displayName"]!.GetValue<string>() == "02_Steuern / Finanzen" &&
                                              n["folders"]![1]!["depth"]!.GetValue<int>() == 1 &&
                                              n["folders"]![2]!["path"]!.GetValue<string>() == @"Inbox/02_Steuern \/ Finanzen/1_Tecob GmbH" &&
                                              n["folders"]![2]!["depth"]!.GetValue<int>() == 2 &&
                                              n["folders"]![3]!["path"]!.GetValue<string>() == "Sent Items" &&
                                              n["folders"]![3]!["depth"]!.GetValue<int>() == 0),
                config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();

        // Token, two root pages, one child listing per folder that has children — leaves are never asked.
        Assert.Equal(5, handler.CallCount);
        Assert.Contains("login.microsoftonline.com/tenant-guid/oauth2/v2.0/token", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("/users/box%40example.com/mailFolders/inbox/childFolders", handler.Requests[3].RequestUri!.ToString());
        Assert.Contains("/mailFolders/st/childFolders", handler.Requests[4].RequestUri!.ToString());
    }

    [Fact]
    public async Task Graph_MissingConsent_ThrowsTheOperatorMessage()
    {
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json("""{"access_token":"tok"}"""),
            SequencedHttpMessageHandler.Json(
                """{"error":{"code":"ErrorAccessDenied","message":"Access is denied."}}""", HttpStatusCode.Forbidden));
        var (etl, http) = GraphContext(handler);
        var config = GraphRouteConfig() with { ChannelPath = null, Channel = "Graph" };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var node = new ListMailFoldersNode(next, etl, http, NullLogger<ListMailFoldersNode>.Instance);
        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.StartsWith("Microsoft 365: ", ex.Message);
        Assert.Contains("Mail.Read", ex.Message);
        Assert.Contains("box@example.com", ex.Message);
        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task UnknownChannel_ThrowsAConfigurationError()
    {
        var (etl, http) = GraphContext(new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Status(HttpStatusCode.OK)));
        var config = GraphRouteConfig();
        var (dataContext, nodeContext, next) = PrepareTest(config);
        SetupGetSimpleValueByPath(dataContext, "$.body.channel", "Exchange");

        var node = new ListMailFoldersNode(next, etl, http, NullLogger<ListMailFoldersNode>.Instance);
        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains("must be 'Imap' or 'Graph'", ex.Message);
        Assert.Contains("'Exchange'", ex.Message);
    }
}
