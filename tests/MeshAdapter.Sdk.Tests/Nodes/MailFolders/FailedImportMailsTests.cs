using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MailKit;
using MailKit.Search;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.MailFolders;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Xunit;

namespace MeshAdapter.Sdk.Tests.Nodes.MailFolders;

/// <summary>
/// AB#7087: listing and re-queueing the mails the e-mail import parked in the failed folder. The
/// regression behind it is AB#7068 — a mail moved back WITHOUT its <c>OctoMesh-Import-*</c> markers is
/// parked again by the very next poll, because the attempts are counted from them — so the order
/// (clear, then move) and the "no clear, no move" rule are pinned here for both channels.
/// </summary>
public class FailedImportMailsTests : NodeTestBase
{
    private const string Mailbox = "accounting@example.com";
    private const string FailedId = "folder-failed";
    private const string SourceId = "folder-inbox";

    // ------------------------------------------------------------------ scripted Graph

    /// <summary>Answers by method + URL fragment and records the order of every request.</summary>
    private sealed class GraphMailbox : HttpMessageHandler
    {
        private readonly List<(HttpMethod Method, string UrlPart, Func<string, HttpResponseMessage> Answer)> _routes = [];

        public List<(HttpMethod Method, string Url, string Body)> Requests { get; } = [];

        public GraphMailbox On(HttpMethod method, string urlPart, string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _routes.Add((method, urlPart, _ => new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            }));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, url, body));
            foreach (var (method, part, answer) in _routes)
            {
                if (method == request.Method && url.Contains(part, StringComparison.Ordinal))
                {
                    return answer(body);
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotImplemented)
            {
                Content = new StringContent($"no route for {request.Method} {url}")
            };
        }

        public HttpClient Client() => new(this, disposeHandler: false);
    }

    private static string Message(string id, string subject, string[] categories, string parent = FailedId,
        string from = "Max Muster <max@example.com>") =>
        JsonSerializer.Serialize(new
        {
            id,
            subject,
            parentFolderId = parent,
            receivedDateTime = "2026-10-18T08:30:00Z",
            from = new { emailAddress = new { name = from.Split(" <")[0], address = from.Split('<')[1].TrimEnd('>') } },
            categories
        });

    // ------------------------------------------------------------------ Graph: list

    [Fact]
    public async Task Graph_List_ReadsAttemptsParkedFlagAndSender_FollowsPaging_AndCaps()
    {
        var page1 = $$"""
            {"value":[{{Message("m1", "Rechnung 1", ["OctoMesh-Import-Failed", "Steuer"])}},
                      {{Message("m2", "Rechnung 2", ["OctoMesh-Import-Attempt-2"])}}],
             "@odata.nextLink":"https://graph.microsoft.com/v1.0/next-page"}
            """;
        var page2 = $$"""{"value":[{{Message("m3", "Rechnung 3", [])}}]}""";
        var graph = new GraphMailbox()
            .On(HttpMethod.Get, "next-page", page2)
            .On(HttpMethod.Get, $"mailFolders/{FailedId}/messages", page1);

        var all = await GraphFailedMailAccess.ListAsync(graph.Client(), Mailbox, FailedId, 10, CancellationToken.None);

        Assert.False(all.Truncated);
        Assert.Equal(["m1", "m2", "m3"], all.Mails.Select(m => m.Id));
        var parked = all.Mails[0];
        Assert.True(parked.Parked);
        Assert.Null(parked.Attempts); // the parked marker replaces the attempt markers
        Assert.Equal("Max Muster <max@example.com>", parked.From);
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 8, 30, 0, TimeSpan.Zero), parked.ReceivedAt);
        Assert.Null(parked.FailureReason); // neither trigger records one
        Assert.Equal(2, all.Mails[1].Attempts);
        Assert.False(all.Mails[1].Parked);

        var capped = await GraphFailedMailAccess.ListAsync(graph.Client(), Mailbox, FailedId, 2, CancellationToken.None);
        Assert.True(capped.Truncated);
        Assert.Equal(2, capped.Mails.Count);
    }

    // ------------------------------------------------------------------ Graph: re-queue

    [Fact]
    public async Task Graph_Requeue_ClearsOurMarkersKeepsForeignCategories_ThenMovesToTheSourceFolder()
    {
        var graph = new GraphMailbox()
            .On(HttpMethod.Get, "/messages/m1", Message("m1", "Rechnung", [
                "OctoMesh-Import-Failed", "OctoMesh-Import-Attempt-3", "Steuer", "OctoMesh-Import-Attempt-x"]))
            .On(HttpMethod.Patch, "/messages/m1", "{}")
            .On(HttpMethod.Post, "/messages/m1/move", """{"id":"moved"}""");

        var outcome = await GraphFailedMailAccess.RequeueAsync(graph.Client(), Mailbox, FailedId, SourceId, "m1",
            CancellationToken.None);

        Assert.Equal(RequeueOutcome.Requeued, outcome.Status);
        Assert.Equal("Rechnung", outcome.Subject);

        // Clear BEFORE move.
        Assert.Equal([HttpMethod.Get, HttpMethod.Patch, HttpMethod.Post], graph.Requests.Select(r => r.Method));
        var patch = JsonDocument.Parse(graph.Requests[1].Body).RootElement.GetProperty("categories")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        // The user's category survives, and so does a category that merely shares the prefix without
        // a numeric suffix — only well-formed markers are ours.
        Assert.Equal(["Steuer", "OctoMesh-Import-Attempt-x"], patch);
        Assert.Contains(SourceId, graph.Requests[2].Body);
    }

    [Fact]
    public void Regression_AB7068_AMailMovedBackWithItsMarkers_WouldBeParkedAgainByTheNextPoll()
    {
        // The trigger parks a mail whose counted attempts reach the budget. The count is READ FROM
        // THE CATEGORIES — so a mail carried back with 'OctoMesh-Import-Attempt-3' on it fails again
        // at once, which is exactly what happened on prod-1 on 2026-10-19.
        const int maxAttemptsPerMessage = 3;
        string[] movedBackAlone = ["OctoMesh-Import-Attempt-3", "Steuer"];
        Assert.True(FromMicrosoftGraphEmailNode.ResolveAttemptCount(movedBackAlone, null, out _) >= maxAttemptsPerMessage);

        // What the re-queue leaves behind starts over with a full budget.
        var afterRequeue = FromMicrosoftGraphEmailNode.WithoutImportMarkers(movedBackAlone);
        Assert.Equal(["Steuer"], afterRequeue);
        Assert.Equal(0, FromMicrosoftGraphEmailNode.ResolveAttemptCount(afterRequeue, null, out _));
    }

    [Fact]
    public async Task Graph_Requeue_IfTheMarkersCannotBeCleared_TheMailIsNotMoved()
    {
        var graph = new GraphMailbox()
            .On(HttpMethod.Get, "/messages/m1", Message("m1", "Rechnung", ["OctoMesh-Import-Attempt-3"]))
            .On(HttpMethod.Patch, "/messages/m1", """{"error":{"code":"ErrorAccessDenied","message":"denied"}}""",
                HttpStatusCode.Forbidden)
            .On(HttpMethod.Post, "/move", "{}");

        var outcome = await GraphFailedMailAccess.RequeueAsync(graph.Client(), Mailbox, FailedId, SourceId, "m1",
            CancellationToken.None);

        Assert.Equal(RequeueOutcome.Failed, outcome.Status);
        Assert.Contains("NOT moved", outcome.Reason);
        Assert.DoesNotContain(graph.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Graph_Requeue_IfTheMoveFails_TheReasonSaysTheMarkersAreAlreadyCleared()
    {
        var graph = new GraphMailbox()
            .On(HttpMethod.Get, "/messages/m1", Message("m1", "Rechnung", ["OctoMesh-Import-Failed"]))
            .On(HttpMethod.Patch, "/messages/m1", "{}")
            .On(HttpMethod.Post, "/move", "{}", HttpStatusCode.InternalServerError);

        var outcome = await GraphFailedMailAccess.RequeueAsync(graph.Client(), Mailbox, FailedId, SourceId, "m1",
            CancellationToken.None);

        Assert.Equal(RequeueOutcome.Failed, outcome.Status);
        Assert.Contains("markers were cleared", outcome.Reason);
        Assert.Contains("still in the failed folder", outcome.Reason);
    }

    [Fact]
    public async Task Graph_Requeue_NothingToClear_MovesWithoutAPatch()
    {
        var graph = new GraphMailbox()
            .On(HttpMethod.Get, "/messages/m1", Message("m1", "Rechnung", ["Steuer"]))
            .On(HttpMethod.Post, "/move", "{}");

        var outcome = await GraphFailedMailAccess.RequeueAsync(graph.Client(), Mailbox, FailedId, SourceId, "m1",
            CancellationToken.None);

        Assert.Equal(RequeueOutcome.Requeued, outcome.Status);
        Assert.DoesNotContain(graph.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task Graph_Requeue_AMailThatIsGoneOrNotInTheFailedFolder_IsNotFoundAndNothingChanges()
    {
        var gone = new GraphMailbox().On(HttpMethod.Get, "/messages/m1", "{}", HttpStatusCode.NotFound);
        var goneOutcome = await GraphFailedMailAccess.RequeueAsync(gone.Client(), Mailbox, FailedId, SourceId, "m1",
            CancellationToken.None);
        Assert.Equal(RequeueOutcome.NotFound, goneOutcome.Status);
        Assert.Single(gone.Requests);

        // Someone already moved it to the inbox: the id still resolves, but not in the failed folder —
        // a caller must not be able to move an arbitrary mail of the mailbox through this route.
        var elsewhere = new GraphMailbox()
            .On(HttpMethod.Get, "/messages/m2", Message("m2", "Inbox mail", ["Steuer"], parent: SourceId));
        var elsewhereOutcome = await GraphFailedMailAccess.RequeueAsync(elsewhere.Client(), Mailbox, FailedId,
            SourceId, "m2", CancellationToken.None);
        Assert.Equal(RequeueOutcome.NotFound, elsewhereOutcome.Status);
        Assert.Single(elsewhere.Requests);
    }

    [Fact]
    public async Task Graph_ResolveFolder_FindsNestedPaths_AndDoesNotCreateMissingOnes()
    {
        var graph = new GraphMailbox()
            .On(HttpMethod.Get, "mailFolders?$filter=displayName eq 'Archive'", """{"value":[{"id":"a"}]}""")
            .On(HttpMethod.Get, "mailFolders/a/childFolders?$filter=displayName eq 'Failed'", """{"value":[{"id":"f"}]}""")
            .On(HttpMethod.Get, "mailFolders/a/childFolders?$filter=displayName eq 'Nope'", """{"value":[]}""");

        Assert.Equal("f", await GraphFailedMailAccess.ResolveFolderIdAsync(graph.Client(), Mailbox, "Archive/Failed", CancellationToken.None));
        Assert.Null(await GraphFailedMailAccess.ResolveFolderIdAsync(graph.Client(), Mailbox, "Archive/Nope", CancellationToken.None));
        Assert.DoesNotContain(graph.Requests, r => r.Method == HttpMethod.Post);
    }

    // ------------------------------------------------------------------ IMAP

    private static IMessageSummary Summary(uint uid, string subject, MessageFlags flags = MessageFlags.None,
        params string[] keywords)
    {
        var summary = A.Fake<IMessageSummary>();
        A.CallTo(() => summary.UniqueId).Returns(new UniqueId(uid));
        A.CallTo(() => summary.Envelope).Returns(new Envelope
        {
            Subject = subject,
            From = { new MailboxAddress("Max Muster", "max@example.com") }
        });
        A.CallTo(() => summary.Flags).Returns(flags);
        A.CallTo(() => summary.Keywords).Returns(new HashSet<string>(keywords));
        A.CallTo(() => summary.InternalDate).Returns(new DateTimeOffset(2026, 10, 18, 8, 30, 0, TimeSpan.Zero));
        return summary;
    }

    private static IMailFolder FailedFolder(uint validity = 77, params IMessageSummary[] present)
    {
        var folder = A.Fake<IMailFolder>();
        A.CallTo(() => folder.UidValidity).Returns(validity);
        A.CallTo(() => folder.FetchAsync(A<IList<UniqueId>>._, A<IFetchRequest>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                var asked = call.GetArgument<IList<UniqueId>>(0)!;
                return Task.FromResult<IList<IMessageSummary>>(present.Where(p => asked.Contains(p.UniqueId)).ToList());
            });
        return folder;
    }

    [Fact]
    public async Task Imap_Requeue_ClearsSeenDeletedAndMarkerKeywords_BeforeTheMove_KeepingForeignKeywords()
    {
        var failed = FailedFolder(77, Summary(5, "Rechnung", MessageFlags.Seen | MessageFlags.Deleted,
            "OctoMesh-Import-Failed", "OctoMesh-Import-Attempt-2", "Steuer"));
        var source = A.Fake<IMailFolder>();
        IStoreFlagsRequest? stored = null;
        A.CallTo(() => failed.StoreAsync(A<UniqueId>._, A<IStoreFlagsRequest>._, A<CancellationToken>._))
            .Invokes(call => stored = call.GetArgument<IStoreFlagsRequest>(1))
            .Returns(Task.FromResult(true));

        var outcome = await ImapFailedMailAccess.RequeueAsync(failed, source, "77:5", CancellationToken.None);

        Assert.Equal(RequeueOutcome.Requeued, outcome.Status);
        Assert.Equal("Rechnung", outcome.Subject);
        // Seen and Deleted off (a read mail is never offered again with onlyUnread), our markers off,
        // "Steuer" stays.
        A.CallTo(() => failed.StoreAsync(new UniqueId(5), A<IStoreFlagsRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly()
            .Then(A.CallTo(() => failed.MoveToAsync(new UniqueId(5), source, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly());
        Assert.NotNull(stored);
        Assert.Equal(StoreAction.Remove, stored!.Action);
        Assert.Equal(MessageFlags.Seen | MessageFlags.Deleted, stored.Flags);
        Assert.True(stored.Silent);
        Assert.Equal(["OctoMesh-Import-Attempt-2", "OctoMesh-Import-Failed"], stored.Keywords!.OrderBy(k => k));
        Assert.DoesNotContain("Steuer", stored.Keywords!);
    }

    [Fact]
    public async Task Imap_Requeue_IfTheFlagsCannotBeCleared_TheMailIsNotMoved()
    {
        var failed = FailedFolder(77, Summary(5, "Rechnung", MessageFlags.Seen));
        var source = A.Fake<IMailFolder>();
        A.CallTo(() => failed.StoreAsync(A<UniqueId>._, A<IStoreFlagsRequest>._, A<CancellationToken>._))
            .Throws(new ServiceNotConnectedException("gone"));

        var outcome = await ImapFailedMailAccess.RequeueAsync(failed, source, "77:5", CancellationToken.None);

        Assert.Equal(RequeueOutcome.Failed, outcome.Status);
        Assert.Contains("NOT moved", outcome.Reason);
        A.CallTo(() => failed.MoveToAsync(A<UniqueId>._, A<IMailFolder>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData("76:5")]   // the server renumbered the folder since the list was made
    [InlineData("77:6")]   // not (any more) in the failed folder
    [InlineData("garbage")]
    [InlineData("77:0")]
    [InlineData("")]
    public async Task Imap_Requeue_AnIdThatDoesNotMatch_IsNotFoundAndNothingChanges(string id)
    {
        var failed = FailedFolder(77, Summary(5, "Rechnung"));
        var source = A.Fake<IMailFolder>();

        var outcome = await ImapFailedMailAccess.RequeueAsync(failed, source, id, CancellationToken.None);

        Assert.Equal(RequeueOutcome.NotFound, outcome.Status);
        A.CallTo(() => failed.StoreAsync(A<UniqueId>._, A<IStoreFlagsRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => failed.MoveToAsync(A<UniqueId>._, A<IMailFolder>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Imap_List_ReportsNewestFirst_WithoutAttemptCounts_AndCaps()
    {
        var failed = FailedFolder(77, Summary(3, "old"), Summary(9, "new"), Summary(5, "mid"));
        A.CallTo(() => failed.SearchAsync(A<SearchQuery>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IList<UniqueId>>([new UniqueId(3), new UniqueId(9), new UniqueId(5)]));

        var listing = await ImapFailedMailAccess.ListAsync(failed, 2, CancellationToken.None);

        Assert.True(listing.Truncated);
        Assert.Equal(["77:9", "77:5"], listing.Mails.Select(m => m.Id));
        Assert.All(listing.Mails, m => Assert.Null(m.Attempts));
        Assert.Equal("Max Muster <max@example.com>", listing.Mails[0].From);
    }

    // ------------------------------------------------------------------ the node

    private sealed class FakeChannel(string? failedFolder = "Archive/Failed") : IFailedMailChannel
    {
        public string Mailbox => Mailbox_;
        private const string Mailbox_ = "box@example.com";
        public string? FailedFolder { get; } = failedFolder;
        public string? SourceFolder => "Inbox";
        public bool FailedFolderFound => true;
        public List<FailedMail> Mails { get; } = [];
        public bool Truncated { get; set; }
        public HashSet<string> Throwing { get; } = [];
        public HashSet<string> Missing { get; } = [];
        public List<string> Processed { get; } = [];
        public bool Prepared { get; private set; }

        public Task<FailedMailListing> ListAsync(int max, CancellationToken cancellationToken) =>
            Task.FromResult(new FailedMailListing(Mails.Take(max).ToList(), Truncated || Mails.Count > max));

        public Task PrepareRequeueAsync(CancellationToken cancellationToken)
        {
            Prepared = true;
            return Task.CompletedTask;
        }

        public Task<RequeueOutcome> RequeueAsync(string id, CancellationToken cancellationToken)
        {
            Processed.Add(id);
            if (Throwing.Contains(id))
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult(Missing.Contains(id) ? RequeueOutcome.Missing(id) : RequeueOutcome.Ok(id, "S-" + id));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static FailedMail Mail(string id) => new(id, "S-" + id, "a@b.c", DateTimeOffset.UnixEpoch, null, true, null);

    [Fact]
    public async Task Node_Requeue_OneFailingMailDoesNotStopTheOthers_AndEveryMailGetsItsOwnResult()
    {
        var channel = new FakeChannel();
        channel.Throwing.Add("b");
        channel.Missing.Add("c");

        var result = await RequeueFailedImportMailsNode.RunAsync("Graph", channel, ["a", "b", "c", "d"], 200,
            NullLogger.Instance, CancellationToken.None);

        Assert.True(channel.Prepared);
        Assert.Equal(["a", "b", "c", "d"], channel.Processed);
        Assert.Equal(4, result["requested"]!.GetValue<int>());
        Assert.Equal(2, result["requeued"]!.GetValue<int>());
        Assert.Equal(1, result["failed"]!.GetValue<int>());
        Assert.Equal(1, result["notFound"]!.GetValue<int>());
        var results = result["results"]!.AsArray();
        Assert.Equal(["Requeued", "Failed", "NotFound", "Requeued"], results.Select(r => r!["status"]!.GetValue<string>()));
        Assert.Contains("boom", results[1]!["reason"]!.GetValue<string>());
        Assert.Equal("Archive/Failed", result["failedFolder"]!.GetValue<string>());
        Assert.Equal("Inbox", result["sourceFolder"]!.GetValue<string>());
    }

    [Fact]
    public async Task Node_Requeue_All_ListsTheFailedFolderAndProcessesEveryMail_ReportingTruncation()
    {
        var channel = new FakeChannel();
        channel.Mails.AddRange([Mail("a"), Mail("b"), Mail("c")]);

        var result = await RequeueFailedImportMailsNode.RunAsync("Imap", channel, null, 2, NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(["a", "b"], channel.Processed);
        Assert.True(result["truncated"]!.GetValue<bool>());
        Assert.Equal("S-a", result["results"]!.AsArray()[0]!["subject"]!.GetValue<string>());
    }

    [Fact]
    public async Task Node_Requeue_WithoutAFailedFolder_FailsWithAnOperatorSentence()
    {
        var ex = await Assert.ThrowsAsync<MailFolderListingException>(() =>
            RequeueFailedImportMailsNode.RunAsync("Graph", new FakeChannel(failedFolder: null), ["a"], 200,
                NullLogger.Instance, CancellationToken.None));
        Assert.Contains("No failed folder is configured", ex.Message);
    }

    [Fact]
    public async Task Node_Requeue_WhenTheTimeBudgetRunsOut_TheRestIsReportedAsNotProcessed()
    {
        using var cts = new CancellationTokenSource();
        var channel = new CancellingChannel(cts);

        var result = await RequeueFailedImportMailsNode.RunAsync("Graph", channel, ["a", "b"], 200,
            NullLogger.Instance, cts.Token);

        var results = result["results"]!.AsArray();
        Assert.All(results, r => Assert.Equal("Failed", r!["status"]!.GetValue<string>()));
        Assert.Contains("time budget", results[1]!["reason"]!.GetValue<string>());
    }

    private sealed class CancellingChannel(CancellationTokenSource cts) : IFailedMailChannel
    {
        public string Mailbox => "m";
        public string? FailedFolder => "F";
        public string? SourceFolder => "S";
        public bool FailedFolderFound => true;
        public Task<FailedMailListing> ListAsync(int max, CancellationToken ct) => Task.FromResult(new FailedMailListing([], false));
        public Task PrepareRequeueAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<RequeueOutcome> RequeueAsync(string id, CancellationToken ct)
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(RequeueOutcome.Ok(id, null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Node_List_ANotConfiguredFailedFolder_IsAnAnswerNotAnError()
    {
        var json = await ListFailedImportMailsNode.BuildResultAsync("Graph", new FakeChannel(failedFolder: " "), 200,
            CancellationToken.None);

        Assert.False(json["configured"]!.GetValue<bool>());
        Assert.Empty(json["mails"]!.AsArray());
    }

    [Fact]
    public async Task Node_List_ShapesEveryMail()
    {
        var channel = new FakeChannel();
        channel.Mails.Add(new FailedMail("x", "Subj", "a@b.c", new DateTimeOffset(2026, 10, 18, 8, 30, 0, TimeSpan.Zero), 2, false, null));

        var json = await ListFailedImportMailsNode.BuildResultAsync("Graph", channel, 200, CancellationToken.None);

        Assert.True(json["configured"]!.GetValue<bool>());
        var mail = json["mails"]!.AsArray()[0]!;
        Assert.Equal("x", mail["id"]!.GetValue<string>());
        Assert.Equal(2, mail["attempts"]!.GetValue<int>());
        Assert.Equal("2026-10-18T08:30:00.0000000Z", mail["receivedAt"]!.GetValue<string>());
        Assert.Null(mail["failureReason"]);
    }

    // ------------------------------------------------------------------ the node, wired end to end (Graph)

    private static (IMeshEtlContext Etl, IHttpClientFactory Http, GraphMailbox Graph) GraphWorld(GraphMailbox graph)
    {
        graph.On(HttpMethod.Post, "login.microsoftonline.com", """{"access_token":"tok"}""");
        var etl = A.Fake<IMeshEtlContext>();
        var global = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => etl.GlobalConfiguration).Returns(global);
        A.CallTo(() => global.IsDefined("Graph")).Returns(true);
        A.CallTo(() => global.GetValue<GraphMailboxAccess.GraphAppCredentials>("Graph")).Returns(
            new GraphMailboxAccess.GraphAppCredentials
            {
                AzureTenantId = "t", ClientId = "c", ClientSecret = "s3cret-value"
            });
        A.CallTo(() => global.IsDefined("Settings")).Returns(true);
        A.CallTo(() => global.GetRawJson("Settings")).Returns("""
            {"attributes":{"EmailImportMailbox":"accounting@example.com","EmailImportSourceFolder":"Inbox","EmailImportFailedFolder":"Archive/Failed"}}
            """);
        var http = A.Fake<IHttpClientFactory>();
        A.CallTo(() => http.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(graph, disposeHandler: false));
        return (etl, http, graph);
    }

    [Fact]
    public async Task Node_Requeue_Graph_UsesOnlyTheStoredMailbox_IgnoresMailboxInTheRequest_AndLeaksNoSecret()
    {
        var graph = new GraphMailbox()
            .On(HttpMethod.Get, "mailFolders?$filter=displayName eq 'Archive'", """{"value":[{"id":"arch"}]}""")
            .On(HttpMethod.Get, "mailFolders/arch/childFolders?$filter=displayName eq 'Failed'",
                $$"""{"value":[{"id":"{{FailedId}}"}]}""")
            .On(HttpMethod.Get, "mailFolders?$filter=displayName eq 'Inbox'", $$"""{"value":[{"id":"{{SourceId}}"}]}""")
            .On(HttpMethod.Get, "/messages/m1", Message("m1", "Rechnung", ["OctoMesh-Import-Failed"]))
            .On(HttpMethod.Patch, "/messages/m1", "{}")
            .On(HttpMethod.Post, "/messages/m1/move", "{}");
        var (etl, http, _) = GraphWorld(graph);
        var config = new RequeueFailedImportMailsNodeConfiguration
        {
            TargetPath = "$.result",
            ChannelPath = "$.body.channel",
            MessageIdsPath = "$.body.messageIds",
            GraphServerConfiguration = "Graph",
            GraphSettingsConfiguration = "Settings",
            GraphMailboxAttribute = "EmailImportMailbox",
            GraphSourceFolderAttribute = "EmailImportSourceFolder",
            GraphFailedFolderAttribute = "EmailImportFailedFolder"
        };
        // A hostile body names another mailbox and folders; none of it may be read.
        var body = new JsonObject
        {
            ["channel"] = "Graph",
            ["messageIds"] = new JsonArray("m1"),
            ["mailbox"] = "victim@other-tenant.example",
            ["sourceFolder"] = "Elsewhere"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config, new JsonObject { ["body"] = body });
        SetupGetSimpleValueByPath(dataContext, "$.body.channel", "Graph");
        A.CallTo(() => dataContext.Get<JsonArray>("$.body.messageIds")).Returns((JsonArray)body["messageIds"]!);
        JsonNode? written = null;
        A.CallTo(() => dataContext.Set(config.TargetPath, A<JsonNode?>._, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode))
            .Invokes(call => written = call.GetArgument<JsonNode>(1));

        var node = new RequeueFailedImportMailsNode(next, etl, http, NullLogger<RequeueFailedImportMailsNode>.Instance);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        var result = Assert.IsType<JsonObject>(written);
        Assert.Equal(1, result["requeued"]!.GetValue<int>());
        Assert.Equal("accounting@example.com", result["mailbox"]!.GetValue<string>());
        Assert.All(graph.Requests.Where(r => r.Url.Contains("/users/")),
            r => Assert.Contains("/users/accounting@example.com/", r.Url));
        Assert.DoesNotContain(graph.Requests, r => r.Url.Contains("victim") || r.Url.Contains("Elsewhere"));
        Assert.DoesNotContain("s3cret-value", result.ToJsonString());
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Node_Requeue_NothingSelected_IsRefused()
    {
        var (etl, http, _) = GraphWorld(new GraphMailbox());
        var config = new RequeueFailedImportMailsNodeConfiguration
        {
            TargetPath = "$.result", Channel = "Graph", MessageIdsPath = "$.body.messageIds", AllPath = "$.body.all",
            GraphServerConfiguration = "Graph"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config, new JsonObject());
        A.CallTo(() => dataContext.Get<JsonArray>("$.body.messageIds")).Returns(new JsonArray());

        var node = new RequeueFailedImportMailsNode(next, etl, http, NullLogger<RequeueFailedImportMailsNode>.Instance);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(() =>
            node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("Nothing to re-queue", ex.Message);
        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }
}
