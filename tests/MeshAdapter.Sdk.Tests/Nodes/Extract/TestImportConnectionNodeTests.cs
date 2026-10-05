using System.Net;
using System.Text.Json.Nodes;
using FakeItEasy;
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
/// The settings page's "Test connection" (AB#5370 follow-up): every check is a RESULT the page
/// shows in order, never a thrown node error, and a failure names what the operator has to fix.
/// Microsoft and Signal are driven over a scripted HTTP handler; IMAP needs a live server and is
/// covered through its pure parts (the source-folder verdict).
/// </summary>
public class TestImportConnectionNodeTests : NodeTestBase
{
    private const string GraphConfig = "MicrosoftGraphDocuments";

    // ------------------------------------------------------------------ pure parts

    [Theory]
    [InlineData("imap", "Imap")]
    [InlineData(" GRAPH ", "Graph")]
    [InlineData("teams", "Teams")]
    [InlineData("Signal", "Signal")]
    [InlineData("Discord", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Channel_IsNormalizedCaseInsensitively(string? raw, string? expected)
    {
        Assert.Equal(expected, TestImportConnectionNode.NormalizeChannel(raw));
    }

    [Fact]
    public void SourceFolder_ExactPath_Passes()
    {
        var check = TestImportConnectionNode.SourceFolderCheck("INBOX.Finanzen.Rechnungen",
            ["INBOX", "INBOX.Finanzen", "INBOX.Finanzen.Rechnungen"], "");

        Assert.True(check.Ok);
        Assert.Contains("'INBOX.Finanzen.Rechnungen' exists", check.Detail);
    }

    [Fact]
    public void SourceFolder_WrongDelimiter_FailsAndNamesTheServerSpelling()
    {
        // The AB#5385 shape: the operator typed the Graph syntax on a Dovecot server.
        var check = TestImportConnectionNode.SourceFolderCheck("INBOX/Finanzen/Rechnungen",
            ["INBOX", "INBOX.Finanzen", "INBOX.Finanzen.Rechnungen"], "");

        Assert.False(check.Ok);
        Assert.Contains("was not found", check.Detail);
        Assert.Contains("'INBOX.Finanzen.Rechnungen'", check.Detail);
    }

    [Fact]
    public void SourceFolder_Missing_FailsWithoutAGuess()
    {
        var check = TestImportConnectionNode.SourceFolderCheck("Archive/ToDo", ["INBOX", "Sent"], " (default)");

        Assert.False(check.Ok);
        Assert.Contains("(default)", check.Detail);
        Assert.Contains("Pick it from the folder list", check.Detail);
        Assert.DoesNotContain("The mailbox has", check.Detail);
    }

    [Fact]
    public void SignalAccount_IsMatchedByDigits_AndNamesTheOthersOnAMiss()
    {
        Assert.True(TestImportConnectionNode.AccountCheck("+43 664 1234567", """["+436641234567"]""").Ok);

        var miss = TestImportConnectionNode.AccountCheck("+436641234567", """["+491511111111"]""");
        Assert.False(miss.Ok);
        Assert.Contains("+491511111111", miss.Detail);

        var empty = TestImportConnectionNode.AccountCheck("+436641234567", "[]");
        Assert.False(empty.Ok);
        Assert.Contains("no account at all", empty.Detail);

        Assert.False(TestImportConnectionNode.AccountCheck("+436641234567", "<html>").Ok);
    }

    [Fact]
    public void Bridge_VersionAndMode_AreDescribedWhenPresent()
    {
        Assert.Equal(" (version 0.80, mode native)",
            TestImportConnectionNode.DescribeBridge("""{"version":"0.80","mode":"native","build":2}"""));
        Assert.Equal("", TestImportConnectionNode.DescribeBridge("not json"));
        Assert.Equal("", TestImportConnectionNode.DescribeBridge("[]"));
    }

    [Fact]
    public void Result_SummarizesTheFirstFailure_AndIsOkOnlyWhenEveryCheckPassed()
    {
        var ok = TestImportConnectionNode.ToJson("Imap",
            [new TestImportConnectionNode.Check("connection", true, "a"), new TestImportConnectionNode.Check("folders", true, "b")]);
        Assert.True(ok["ok"]!.GetValue<bool>());
        Assert.Equal("Imap: every check passed (2).", ok["summary"]!.GetValue<string>());
        Assert.Equal(2, ok["checks"]!.AsArray().Count);

        var failed = TestImportConnectionNode.ToJson("Graph",
            [new TestImportConnectionNode.Check("graphToken", true, "a"), new TestImportConnectionNode.Check("mailbox", false, "no such mailbox")]);
        Assert.False(failed["ok"]!.GetValue<bool>());
        Assert.Equal("Graph: no such mailbox", failed["summary"]!.GetValue<string>());

        Assert.False(TestImportConnectionNode.ToJson("Signal", []).Ok());
    }

    // ------------------------------------------------------------------ Microsoft over scripted HTTP

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
        A.CallTo(() => http.CreateClient(A<string>._))
            .ReturnsLazily(() => new HttpClient(handler, disposeHandler: false));
        return (etl, http);
    }

    private async Task<JsonObject> Run(string channel, TestImportConnectionNodeConfiguration config,
        (IMeshEtlContext Etl, IHttpClientFactory Http) ctx)
    {
        var (dataContext, nodeContext, next) = PrepareTest(config,
            new JsonObject { ["body"] = new JsonObject { ["channel"] = channel } });
        SetupGetSimpleValueByPath(dataContext, "$.body.channel", channel);
        JsonNode? written = null;
        A.CallTo(() => dataContext.Set(config.TargetPath, A<JsonNode?>._, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode))
            .Invokes(call => written = call.GetArgument<JsonNode>(1));

        var node = new TestImportConnectionNode(next, ctx.Etl, ctx.Http, NullLogger<TestImportConnectionNode>.Instance);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        return Assert.IsType<JsonObject>(written);
    }

    private static TestImportConnectionNodeConfiguration RouteConfig() => new()
    {
        TargetPath = "$.result",
        ChannelPath = "$.body.channel",
        GraphServerConfiguration = GraphConfig,
        GraphMailbox = "box@example.com"
    };

    [Fact]
    public async Task Teams_BothTokensAccepted_EveryCheckPasses()
    {
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json("""{"access_token":"graph"}"""),
            SequencedHttpMessageHandler.Json("""{"access_token":"bot"}"""));

        var result = await Run("Teams", RouteConfig(), GraphContext(handler));

        Assert.True(result["ok"]!.GetValue<bool>());
        var checks = result["checks"]!.AsArray();
        Assert.Equal(["graphToken", "botToken"], checks.Select(c => c!["name"]!.GetValue<string>()));
        Assert.All(checks, c => Assert.True(c!["ok"]!.GetValue<bool>()));
        // The bot token is asked of the tenant authority with the Bot Framework scope.
        Assert.Contains("tenant-guid", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task Teams_BotFrameworkRefuses_ReportsTheGraphPassAndTheBotFailure()
    {
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json("""{"access_token":"graph"}"""),
            SequencedHttpMessageHandler.Status(HttpStatusCode.Unauthorized,
                """{"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret provided."}"""));

        var result = await Run("Teams", RouteConfig(), GraphContext(handler));

        Assert.False(result["ok"]!.GetValue<bool>());
        var checks = result["checks"]!.AsArray();
        Assert.True(checks[0]!["ok"]!.GetValue<bool>());
        Assert.False(checks[1]!["ok"]!.GetValue<bool>());
        Assert.Contains("Invalid client secret", checks[1]!["detail"]!.GetValue<string>());
        Assert.StartsWith("Teams: The Bot Framework refused", result["summary"]!.GetValue<string>());
    }

    [Fact]
    public async Task Graph_TokenRefused_StopsAtTheFirstCheck()
    {
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.BadRequest,
                """{"error":"unauthorized_client","error_description":"AADSTS700016: Application not found."}"""));

        var result = await Run("Graph", RouteConfig(), GraphContext(handler));

        Assert.False(result["ok"]!.GetValue<bool>());
        var checks = result["checks"]!.AsArray();
        Assert.Single(checks);
        Assert.Equal("graphToken", checks[0]!["name"]!.GetValue<string>());
        Assert.Contains("Application not found", checks[0]!["detail"]!.GetValue<string>());
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Graph_MailboxListed_ButNoSourceFolderConfigured_FailsOnTheFolder()
    {
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json("""{"access_token":"tok"}"""),
            SequencedHttpMessageHandler.Json(
                """{"value":[{"id":"1","displayName":"Inbox","childFolderCount":0},{"id":"2","displayName":"Archive","childFolderCount":0}]}"""));

        var result = await Run("Graph", RouteConfig(), GraphContext(handler));

        Assert.False(result["ok"]!.GetValue<bool>());
        var names = result["checks"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["graphToken", "mailbox", "sourceFolder"], names);
        Assert.True(result["checks"]![1]!["ok"]!.GetValue<bool>());
        Assert.Contains("2 folder(s)", result["checks"]![1]!["detail"]!.GetValue<string>());
        Assert.Contains("No source folder is configured", result["checks"]![2]!["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Graph_NoMailbox_FailsBeforeAnyRequest()
    {
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Throws(new HttpRequestException("must not be called")));
        var config = RouteConfig();
        config.GraphMailbox = null;

        var result = await Run("Graph", config, GraphContext(handler));

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal("mailbox", result["checks"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Signal_WithoutARegisteredChannel_FailsOnTheChannelCheck()
    {
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Throws(new HttpRequestException("must not be called")));
        var ctx = GraphContext(handler);
        A.CallTo(() => ctx.Etl.GlobalConfiguration.IsDefined("signal-channel")).Returns(false);

        var result = await Run("Signal", RouteConfig(), ctx);

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal("channel", result["checks"]![0]!["name"]!.GetValue<string>());
        Assert.Contains("Refinery Studio", result["checks"]![0]!["detail"]!.GetValue<string>());
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task UnknownChannel_ThrowsAConfigurationError()
    {
        var (dataContext, nodeContext, next) = PrepareTest(RouteConfig(),
            new JsonObject { ["body"] = new JsonObject { ["channel"] = "Discord" } });
        SetupGetSimpleValueByPath(dataContext, "$.body.channel", "Discord");
        var ctx = GraphContext(new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Throws(new HttpRequestException("must not be called"))));
        var node = new TestImportConnectionNode(next, ctx.Etl, ctx.Http, NullLogger<TestImportConnectionNode>.Instance);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains("'Discord'", ex.Message);
        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }
}

file static class JsonObjectExtensions
{
    public static bool Ok(this JsonObject o) => o["ok"]!.GetValue<bool>();
}
