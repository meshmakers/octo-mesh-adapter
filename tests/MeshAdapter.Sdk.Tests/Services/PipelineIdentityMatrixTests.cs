using System.Runtime.CompilerServices;
using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshAdapter.Sdk.Tests.Services;

/// <summary>
///     The connected proof of the delegation semantics (AB#5029): the same rules hold for
///     <b>every</b> trigger kind and every identity situation, and they hold <i>fail-closed</i>.
/// </summary>
/// <remarks>
///     <para>
///         The pieces exist already — <c>PipelineIdentityResolverTests</c> covers the resolution,
///         <c>ServiceAccountTokenServiceTests</c> the two grants, <c>FromHttpRequestNode2Tests</c> the
///         one trigger that carries a caller, <c>AnthropicAiQueryNodeDelegationTests</c> the AI path,
///         and <c>SessionIdentityIntegrationTests</c> the real session. What was missing is the join:
///         a single place that says which trigger produces which identity situation, and what the
///         effective identity is in each. That is what fails when someone adds a trigger, changes a
///         precedence, or "repairs" the empty-role case.
///     </para>
///     <para><b>The rules pinned here</b></para>
///     <list type="number">
///         <item>Precedence: verified caller ▶ service account ▶ system.</item>
///         <item>
///             The intersection is over <b>role names</b>, and the <b>subject is the caller</b> — so an
///             owner-scoped check (<c>RtCreatedBy</c>, <c>ownerAttributePath</c>, AB#4978) is about the
///             human, never about the service account acting for them.
///         </item>
///         <item>
///             An <b>empty intersection is fail-closed and, identity-side, a SUCCESS</b>: a token is
///             issued, it simply carries no roles. It is not an error anywhere, and its only symptom is
///             that nothing protected comes back.
///         </item>
///         <item>A caller with no roles sees nothing on a protected type — same shape, different cause.</item>
///         <item>No service account configured ▶ the system path, byte for byte as before AB#5027.</item>
///         <item>A configured service account whose token cannot be had ▶ abort, never a system fallback.</item>
///     </list>
/// </remarks>
public class PipelineIdentityMatrixTests
{
    private const string TenantId = "test-tenant";
    private const string ServiceAccountClientId = "octo-pipeline-sa-abc";
    private const string CallerSubjectId = "user-42";

    private readonly IGlobalConfiguration _globalConfiguration = A.Fake<IGlobalConfiguration>();
    private readonly IServiceAccountTokenService _tokenService = A.Fake<IServiceAccountTokenService>();

    /// <summary>
    ///     How a trigger starts an execution — the only axis of the matrix that is not about the
    ///     identity service. Every value names the triggers that produce it.
    /// </summary>
    public enum TriggerKind
    {
        /// <summary><c>FromHttpRequest@2</c> on a secured route: principal AND raw token.</summary>
        HttpWithVerifiedCaller,

        /// <summary><c>FromHttpRequest@2</c> with <c>allowAnonymous</c>, and <c>FromHttpRequest@1</c>.</summary>
        HttpAnonymous,

        /// <summary><c>FromPipelineTriggerEvent@1</c> — cron / scheduled.</summary>
        CronPipelineTrigger,

        /// <summary><c>FromPipelineDataEvent@1</c> — a chained pipeline (AB#5045).</summary>
        PipelineDataEvent,

        /// <summary><c>FromExecutePipelineCommand@1</c> — Studio "Execute" and the ExecutePipeline API.</summary>
        ExecutePipelineCommand,

        /// <summary>
        ///     The channel triggers: <c>FromEmail@1</c>, <c>FromMicrosoftGraph@1</c>,
        ///     <c>FromMicrosoftGraphEmail@1</c>, <c>FromSignal@1</c>, <c>FromTeamsBot@1</c>,
        ///     <c>FromWatchRtEntity@1</c>, <c>FromSendNotification@1</c>.
        /// </summary>
        Channel
    }

    /// <summary>
    ///     Builds the <see cref="ExecutePipelineOptions" /> the named trigger kind produces. Only
    ///     <see cref="TriggerKind.HttpWithVerifiedCaller" /> carries anything;
    ///     <see cref="OnlyTheHttpTriggerCarriesACallerIdentity" /> is what keeps that claim true against
    ///     the actual trigger sources rather than against this method.
    /// </summary>
    private static ExecutePipelineOptions OptionsFor(TriggerKind kind, IReadOnlyList<string>? callerRoles = null)
    {
        return kind switch
        {
            TriggerKind.HttpWithVerifiedCaller => new ExecutePipelineOptions(DateTime.UtcNow)
            {
                VerifiedPrincipal = new VerifiedPrincipal(CallerSubjectId, TenantId, "u@example.com", "U",
                    callerRoles ?? ["Accounting", "Reader"]),
                CallerAccessToken = "ey.the.callers.token"
            },
            _ => new ExecutePipelineOptions(DateTime.UtcNow)
        };
    }

    private PipelineIdentityResolver ResolverFor(TriggerKind kind, IReadOnlyList<string>? callerRoles = null)
    {
        var options = OptionsFor(kind, callerRoles);
        return new PipelineIdentityResolver(TenantId, options.VerifiedPrincipal, _globalConfiguration,
            _tokenService, NullLogger.Instance);
    }

    private void GivenAServiceAccountIsConfigured(params string[] roles)
    {
        A.CallTo(() => _globalConfiguration.GetAllRawJsonByCkTypeId(
                PipelineIdentityResolver.ServiceAccountConfigurationCkTypeId))
            .Returns([
                $$"""
                  {
                    "issuerUri": "https://identity.example.com",
                    "clientId": "{{ServiceAccountClientId}}",
                    "clientSecret": "s3cr3t",
                    "tenantId": "{{TenantId}}"
                  }
                  """
            ]);

        A.CallTo(() => _tokenService.AcquireServiceAccountIdentityAsync(
                A<ServiceAccountCredentials>._, A<CancellationToken>._))
            .Returns(Task.FromResult<ServiceAccountIdentity?>(
                new ServiceAccountIdentity(ServiceAccountClientId, roles, DateTime.UtcNow.AddMinutes(5))));
    }

    private void GivenNoServiceAccountIsConfigured()
    {
        A.CallTo(() => _globalConfiguration.GetAllRawJsonByCkTypeId(
            PipelineIdentityResolver.ServiceAccountConfigurationCkTypeId)).Returns([]);
    }

    private void GivenTheServiceAccountTokenCannotBeAcquired()
    {
        A.CallTo(() => _globalConfiguration.GetAllRawJsonByCkTypeId(
                PipelineIdentityResolver.ServiceAccountConfigurationCkTypeId))
            .Returns([
                $$"""
                  {
                    "issuerUri": "https://identity.example.com",
                    "clientId": "{{ServiceAccountClientId}}",
                    "clientSecret": "s3cr3t",
                    "tenantId": "{{TenantId}}"
                  }
                  """
            ]);

        A.CallTo(() => _tokenService.AcquireServiceAccountIdentityAsync(
                A<ServiceAccountCredentials>._, A<CancellationToken>._))
            .Returns(Task.FromResult<ServiceAccountIdentity?>(null));
    }

    // =============================================================================================
    // Rule 1 + 5 — precedence, across every trigger kind, with and without a service account.
    // =============================================================================================

    public static TheoryData<TriggerKind> AllTriggerKinds =>
    [
        TriggerKind.HttpWithVerifiedCaller, TriggerKind.HttpAnonymous, TriggerKind.CronPipelineTrigger,
        TriggerKind.PipelineDataEvent, TriggerKind.ExecutePipelineCommand, TriggerKind.Channel
    ];

    public static TheoryData<TriggerKind> TriggerKindsWithoutACaller =>
    [
        TriggerKind.HttpAnonymous, TriggerKind.CronPipelineTrigger, TriggerKind.PipelineDataEvent,
        TriggerKind.ExecutePipelineCommand, TriggerKind.Channel
    ];

    [Theory]
    [MemberData(nameof(AllTriggerKinds))]
    public async Task WithAServiceAccount_TheCallerWinsWhereverThereIsOne(TriggerKind kind)
    {
        GivenAServiceAccountIsConfigured("CommunicationManagement", "Accounting", "Reader");

        var context = await ResolverFor(kind).ResolveAsync();

        if (kind == TriggerKind.HttpWithVerifiedCaller)
        {
            // Rule 2: the subject is the CALLER, not the account acting for them — an owner-scoped
            // read (RtCreatedBy / ownerAttributePath, AB#4978) is about the human.
            Assert.Equal(CallerSubjectId, context.SubjectId);
            Assert.Equal(["Accounting", "Reader"], context.Roles);
        }
        else
        {
            Assert.Equal(ServiceAccountClientId, context.SubjectId);
            Assert.Equal(["CommunicationManagement", "Accounting", "Reader"], context.Roles);
        }

        Assert.False(context.IsSystem);
    }

    [Theory]
    [MemberData(nameof(TriggerKindsWithoutACaller))]
    public async Task WithoutAServiceAccount_ATriggerWithoutACallerKeepsTheSystemPath(TriggerKind kind)
    {
        // Rule 5. This is the fleet before provisioning has run, and every tenant that never gets a
        // service account. Changing it would take every one of them down at once, so it is pinned for
        // each trigger kind rather than once.
        GivenNoServiceAccountIsConfigured();

        var context = await ResolverFor(kind).ResolveAsync();

        Assert.True(context.IsSystem);
    }

    [Fact]
    public async Task WithoutAServiceAccount_AVerifiedCallerIsStillTheIdentity()
    {
        // The caller does not depend on the service account existing: it is on the options already.
        GivenNoServiceAccountIsConfigured();

        var context = await ResolverFor(TriggerKind.HttpWithVerifiedCaller).ResolveAsync();

        Assert.False(context.IsSystem);
        Assert.Equal(CallerSubjectId, context.SubjectId);
    }

    // =============================================================================================
    // Rule 6 — a configured account whose token cannot be had aborts. Never a system fallback.
    // =============================================================================================

    [Theory]
    [MemberData(nameof(TriggerKindsWithoutACaller))]
    public async Task AConfiguredAccountWhoseTokenIsUnavailableAbortsTheExecution(TriggerKind kind)
    {
        // 🔴 RtSecurityContext.System bypasses data-level permissions entirely (AB#4969), so falling
        // back would fail OPEN — an identity-service outage would silently widen every read and leave
        // every write unstamped, and nothing downstream could tell that apart from a correct run.
        GivenTheServiceAccountTokenCannotBeAcquired();

        var exception = await Assert.ThrowsAnyAsync<PipelineExecutionException>(
            async () => await ResolverFor(kind).ResolveAsync());

        Assert.Contains(ServiceAccountClientId, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVerifiedCallerIsNotAffectedByAnUnavailableServiceAccountToken()
    {
        // The caller is resolved before the account is ever consulted, so an identity-service outage
        // must not fail an HTTP request that carried its own proof of identity.
        GivenTheServiceAccountTokenCannotBeAcquired();

        var context = await ResolverFor(TriggerKind.HttpWithVerifiedCaller).ResolveAsync();

        Assert.Equal(CallerSubjectId, context.SubjectId);
        A.CallTo(() => _tokenService.AcquireServiceAccountIdentityAsync(
            A<ServiceAccountCredentials>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // =============================================================================================
    // Rules 3 + 4 — the role-less identities. Both are legitimate, neither is an error, and the only
    // symptom of either is that nothing protected is delivered.
    // =============================================================================================

    [Fact]
    public async Task ACallerWithoutRolesIsAnIdentityWithNoRoles_NotTheSystemContext()
    {
        // Rule 4. ForUser(sub, []) is NOT the system context — it is subject to data permissions and
        // stamps RtCreatedBy — so on a protected type it sees only what needs no role. A check on
        // IsSystem alone would wave it through, which is why the assertion is on the roles too.
        GivenAServiceAccountIsConfigured("CommunicationManagement");

        var context = await ResolverFor(TriggerKind.HttpWithVerifiedCaller, callerRoles: []).ResolveAsync();

        Assert.False(context.IsSystem);
        Assert.Equal(CallerSubjectId, context.SubjectId);
        Assert.Empty(context.Roles);

        // Explicitly NOT "fall back to the service account because the caller has no roles": that
        // would hand a role-less user the account's full reach.
        Assert.NotEqual(ServiceAccountClientId, context.SubjectId);
    }

    [Fact]
    public async Task AnEmptyRoleIntersectionResolvesQuietlyToAnIdentityWithNoRoles()
    {
        // Rule 3, on the execution-identity side: a service account that carries no roles at all is
        // the degenerate intersection. The resolver must NOT treat it as a failure — an issued token
        // with no roles is a successful answer, and inventing an error here would turn a correctly
        // restricted (if useless) configuration into an outage. What it must also NOT do is fall
        // through to the system context, which is the "repair" that would silently grant everything.
        GivenAServiceAccountIsConfigured();

        var context = await ResolverFor(TriggerKind.CronPipelineTrigger).ResolveAsync();

        Assert.False(context.IsSystem);
        Assert.Equal(ServiceAccountClientId, context.SubjectId);
        Assert.Empty(context.Roles);
    }

    // =============================================================================================
    // Rule 1, the cheap half — a caller costs no token round trip, on any trigger.
    // =============================================================================================

    [Fact]
    public async Task ResolvingACallerCostsNoTokenRequest()
    {
        GivenAServiceAccountIsConfigured("Accounting");

        await ResolverFor(TriggerKind.HttpWithVerifiedCaller).ResolveAsync();

        A.CallTo(() => _tokenService.AcquireServiceAccountIdentityAsync(
            A<ServiceAccountCredentials>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _globalConfiguration.GetAllRawJsonByCkTypeId(A<string>._)).MustNotHaveHappened();
    }

    // =============================================================================================
    // Which triggers carry a caller identity. Before AB#5126 only FromHttpRequestNode2 did; Strang B
    // (AB#5126/5124/5123/5125) resolves a caller on every channel trigger too. This pins that set.
    // =============================================================================================

    /// <summary>
    ///     Pins, against the trigger sources themselves, which triggers put a caller identity on
    ///     <see cref="ExecutePipelineOptions" />.
    /// </summary>
    /// <remarks>
    ///     Without this the matrix would only be a restatement of what the test author believed. A new
    ///     trigger that starts forwarding a principal — or a channel trigger that grows one — changes
    ///     the identity every pipeline behind it runs as, and does so invisibly: nothing fails, the
    ///     execution just sees different data. Modelled on <c>SessionIdentityClassificationTests</c>,
    ///     which does the same for the session call sites. Since AB#5126 the set is HTTP plus the
    ///     channel triggers whose sender the caller-binding seam resolves; a change to this list must
    ///     be a deliberate edit here.
    /// </remarks>
    [Fact]
    public void TheTriggersThatCarryACallerIdentityArePinned()
    {
        var carriers = EnumerateTriggerSources()
            .Where(t =>
            {
                var code = CodeLines(File.ReadAllLines(t.Path)).ToList();
                return code.Any(l => l.Contains("VerifiedPrincipal =", StringComparison.Ordinal))
                       || code.Any(l => l.Contains("CallerAccessToken =", StringComparison.Ordinal));
            })
            .Select(t => t.RelativePath)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([
            "FromEmailNode.cs",
            "FromHttpRequestNode2.cs",
            "FromMicrosoftGraphEmailNode.cs",
            "FromMicrosoftGraphNode.cs",
            "FromSignalNode.cs",
            "FromTeamsBotNode.cs"
        ], carriers);
    }

    /// <summary>
    ///     Every trigger that starts an execution is accounted for by a row of
    ///     <see cref="TriggerKind" />.
    /// </summary>
    /// <remarks>
    ///     A trigger nobody classified is a trigger whose identity nobody decided — the exact gap
    ///     AB#5028 closed for the session call sites and this closes for the entry points.
    /// </remarks>
    [Fact]
    public void EveryTriggerThatStartsAnExecutionIsInTheMatrix()
    {
        var starters = EnumerateTriggerSources()
            .Where(t => CodeLines(File.ReadAllLines(t.Path))
                .Any(l => l.Contains("new ExecutePipelineOptions", StringComparison.Ordinal)))
            .Select(t => t.RelativePath)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        // Kept as an explicit list rather than a count so a rename shows what changed. The two
        // pipeline-bus triggers (FromPipelineDataEvent@1, FromExecutePipelineCommand@1) live in
        // octo-communication-sdk and are pinned by their own tests there.
        string[] expected =
        [
            "FromEmailNode.cs",              // Channel
            "FromHttpRequestNode.cs",        // HttpAnonymous (deprecated @1: no caller at all)
            "FromHttpRequestNode2.cs",       // HttpWithVerifiedCaller / HttpAnonymous
            "FromMicrosoftGraphEmailNode.cs",// Channel
            "FromMicrosoftGraphNode.cs",     // Channel
            "FromPipelineTriggerEventNode.cs", // CronPipelineTrigger
            "FromSendNotificationNode.cs",   // Channel
            "FromSignalNode.cs",             // Channel
            "FromTeamsBotNode.cs",           // Channel
            "FromWatchRtEntityNode.cs"       // Channel
        ];

        Assert.Equal(expected, starters);
    }

    private static IEnumerable<(string RelativePath, string Path)> EnumerateTriggerSources()
    {
        var root = TriggerDirectory();
        Assert.True(Directory.Exists(root), $"Trigger sources not found at '{root}'.");

        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(p => (Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'), p))
            .OrderBy(t => t.Item1, StringComparer.Ordinal);
    }

    private static IEnumerable<string> CodeLines(IEnumerable<string> lines)
    {
        return lines.Where(l =>
        {
            var trimmed = l.TrimStart();
            return !trimmed.StartsWith("//", StringComparison.Ordinal)
                   && !trimmed.StartsWith("*", StringComparison.Ordinal);
        });
    }

    /// <summary>
    ///     <c>&lt;repo&gt;/src/MeshAdapter.Sdk/Nodes/Trigger</c>, derived from this file's own
    ///     compile-time path (<c>&lt;repo&gt;/tests/MeshAdapter.Sdk.Tests/Services/…</c>).
    /// </summary>
    private static string TriggerDirectory([CallerFilePath] string thisFile = "")
    {
        var repositoryRoot = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", ".."));
        return Path.Combine(repositoryRoot, "src", "MeshAdapter.Sdk", "Nodes", "Trigger");
    }
}
