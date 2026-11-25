using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Common;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshAdapter.Sdk.Tests.Services.CrossTenant;

/// <summary>
///     One test per abuse case of the cross-tenant access rules (docs/cross-tenant-access.md, section 5), against the
///     guard with a fake registry, a scripted identity provider and a fake generation store.
/// </summary>
/// <remarks>
///     Tree used by every test:
///     <code>
///     system
///     ├── g          (group)  ── a ── a1 (grandchild of g)
///     │                       └─ b
///     ├── h          (other group) ── x (outsider for g's subtree)
///     └── solo       (top-level tenant without children)
///     d              detached: not in the registry
///     </code>
///     Invariant under test: a pipeline of T reaches U ≠ T only when U is in the named relation AND the identity
///     service proved a right in U (tenant and generation checked); the system identity never; every refusal happens
///     before U's repository is opened.
/// </remarks>
public class CrossTenantAccessGuardTests
{
    private const string System = "octosystem";
    private const string Generation = "gen-current";

    private static readonly TenantRegistrySnapshot Registry = new(System,
    [
        ("g", null), ("a", "g"), ("b", "g"), ("a1", "a"),
        ("h", System), ("x", "h"),
        ("solo", null)
    ]);

    private static readonly VerifiedPrincipal Caller =
        new("user-1", "a", null, null, ["AdminOfA"]);

    private static readonly RtSecurityContext ServiceAccountOfSource =
        RtSecurityContext.ForUser("sa-of-source", ["AdminOfSource"]);

    private readonly ITenantRegistryReader _registryReader = A.Fake<ITenantRegistryReader>();
    private readonly ICrossTenantIdentityProvider _provider = A.Fake<ICrossTenantIdentityProvider>();
    private readonly ITenantGenerationReader _generationReader = A.Fake<ITenantGenerationReader>();
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly IPipelineIdentityResolver _resolver = A.Fake<IPipelineIdentityResolver>();
    private readonly ITenantRepository _targetRepository =
        A.Fake<ITenantRepository>(o => o.Implements<ISecureSessionFactory>());

    /// <summary>Grants the scripted identity service holds: (target, subject kind) → roles in the target.</summary>
    private readonly Dictionary<(string Target, CrossTenantSubjectKind Kind), string[]> _grants = new();

    public CrossTenantAccessGuardTests()
    {
        A.CallTo(() => _registryReader.GetAsync(A<bool>._, A<CancellationToken>._)).Returns(Registry);
        A.CallTo(() => _generationReader.GetGenerationAsync(A<string>._, A<bool>._, A<CancellationToken>._))
            .Returns(new ValueTask<string?>(Generation));
        A.CallTo(() => _resolver.ResolveServiceAccountAsync(A<CancellationToken>._))
            .Returns(new ValueTask<RtSecurityContext>(ServiceAccountOfSource));
        A.CallTo(() => _systemContext.FindTenantRepositoryAsync(A<string>._)).Returns(_targetRepository);
        A.CallTo(() => ((ISecureSessionFactory)_targetRepository).GetSessionAsync(A<RtSecurityContext>._))
            .Returns(Task.FromResult(A.Fake<IOctoSession>()));
        A.CallTo(() => _provider.ProveAsync(A<CrossTenantProofRequest>._, A<CancellationToken>._))
            .ReturnsLazily((CrossTenantProofRequest r, CancellationToken _) =>
                Task.FromResult(_grants.TryGetValue((r.TargetTenantId, r.SubjectKind), out var roles)
                    ? new CrossTenantProof(r.TargetTenantId, Generation,
                        RtSecurityContext.ForUser($"xt_{r.SourceTenantId}_{r.SourceIdentity.SubjectId}", roles))
                    : null));
    }

    private CrossTenantAccessGuard CreateGuard(ITenantGenerationReader? generationReader = null,
        bool withGenerationReader = true)
    {
        return new CrossTenantAccessGuard(_systemContext, A.Fake<ICkCacheService>(), _registryReader, _provider,
            NullLogger<CrossTenantAccessGuard>.Instance,
            withGenerationReader ? generationReader ?? _generationReader : null);
    }

    private CrossTenantAccessRequest Request(string source, TenantTargetConfiguration target,
        NodeExecutionIdentity identity = NodeExecutionIdentity.Caller, VerifiedPrincipal? caller = null,
        string? callerToken = "caller-token", bool withResolver = true, IDataContext? dataContext = null,
        CrossTenantProofCache? cache = null)
    {
        return new CrossTenantAccessRequest(source, target, identity, dataContext ?? A.Fake<IDataContext>(),
            "transformations/0", caller, caller == null ? null : callerToken, withResolver ? _resolver : null,
            A.Fake<IGlobalConfiguration>(), cache ?? new CrossTenantProofCache());
    }

    private static TenantTargetConfiguration Parent() => new() { Relation = TenantRelation.Parent };

    private static TenantTargetConfiguration Sibling(string id) =>
        new() { Relation = TenantRelation.Sibling, TenantId = id };

    private static TenantTargetConfiguration Child(string id) =>
        new() { Relation = TenantRelation.Child, TenantId = id };

    private async Task<CrossTenantAccessDeniedException> RefusedAsync(CrossTenantAccessRequest request,
        CrossTenantAccessGuard? guard = null)
    {
        var exception = await Assert.ThrowsAsync<CrossTenantAccessDeniedException>(() =>
            (guard ?? CreateGuard()).OpenAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(CrossTenantAccessCodes.Forbidden, exception.ErrorCode);
        Assert.Contains(CrossTenantAccessCodes.Forbidden, exception.Message);
        return exception;
    }

    private void AssertTargetNeverOpened() =>
        A.CallTo(() => _systemContext.FindTenantRepositoryAsync(A<string>._)).MustNotHaveHappened();

    // ---- allowed paths --------------------------------------------------------------------------------------

    [Fact]
    public async Task CallerWithGrant_ReachesParent_WithTheContextProvenForTheTarget()
    {
        _grants[("g", CrossTenantSubjectKind.User)] = ["ReaderInG"];

        var access = await CreateGuard().OpenAsync(Request("a", Parent(), caller: Caller),
            TestContext.Current.CancellationToken);
        await access.OpenSessionAsync();

        Assert.Equal("g", access.TenantId);
        A.CallTo(() => _systemContext.FindTenantRepositoryAsync("g")).MustHaveHappenedOnceExactly();
        // Abuse case 10 (role-name carry-over): the session in g uses the shadow subject with g's roles, never the
        // caller's context of a.
        A.CallTo(() => ((ISecureSessionFactory)_targetRepository).GetSessionAsync(
                A<RtSecurityContext>.That.Matches(c =>
                    c.SubjectId == "xt_a_user-1" && c.Roles.SequenceEqual(new[] { "ReaderInG" }))))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => ((ISecureSessionFactory)_targetRepository).GetSessionAsync(
                A<RtSecurityContext>.That.Matches(c => c.SubjectId == "user-1" || c.IsSystem)))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task CallerProof_SendsTheCallerToken_ServiceAccountProof_SendsNone()
    {
        _grants[("b", CrossTenantSubjectKind.User)] = ["R"];
        _grants[("b", CrossTenantSubjectKind.ServiceAccount)] = ["R"];
        var requests = new List<CrossTenantProofRequest>();
        A.CallTo(() => _provider.ProveAsync(A<CrossTenantProofRequest>._, A<CancellationToken>._))
            .Invokes((CrossTenantProofRequest r, CancellationToken _) => requests.Add(r))
            .ReturnsLazily((CrossTenantProofRequest r, CancellationToken _) => Task.FromResult<CrossTenantProof?>(
                new CrossTenantProof("b", Generation, RtSecurityContext.ForUser("shadow", ["R"]))));

        await CreateGuard().OpenAsync(Request("a", Sibling("b"), caller: Caller), TestContext.Current.CancellationToken);
        await CreateGuard().OpenAsync(Request("a", Sibling("b"), NodeExecutionIdentity.ServiceAccount, caller: Caller),
            TestContext.Current.CancellationToken);

        Assert.Equal(CrossTenantSubjectKind.User, requests[0].SubjectKind);
        Assert.Equal("caller-token", requests[0].CallerAccessToken);
        Assert.Equal(CrossTenantSubjectKind.ServiceAccount, requests[1].SubjectKind);
        Assert.Null(requests[1].CallerAccessToken);
        Assert.Equal("sa-of-source", requests[1].SourceIdentity.SubjectId);
        Assert.DoesNotContain("caller-token", requests[0].ToString());
    }

    [Fact]
    public async Task ServiceAccountWithGrant_ReachesChild()
    {
        _grants[("a", CrossTenantSubjectKind.ServiceAccount)] = ["WriterInA"];

        var access = await CreateGuard().OpenAsync(Request("g", Child("a"), NodeExecutionIdentity.ServiceAccount),
            TestContext.Current.CancellationToken);

        Assert.Equal("a", access.TenantId);
    }

    [Fact]
    public async Task TargetIds_AreTrimmedAndCaseInsensitive()
    {
        _grants[("B", CrossTenantSubjectKind.User)] = ["R"];

        var access = await CreateGuard().OpenAsync(Request("A", Sibling("  B "), caller: Caller),
            TestContext.Current.CancellationToken);

        Assert.Equal("B", access.TenantId, ignoreCase: true);
    }

    // ---- abuse cases ----------------------------------------------------------------------------------------

    [Fact]
    public async Task AbuseCase01_TargetOutsideTheSubtree_IsRefused()
    {
        _grants[("x", CrossTenantSubjectKind.User)] = ["R"]; // even a grant does not widen the relation

        var refused = await RefusedAsync(Request("a", Sibling("x"), caller: Caller));

        Assert.Equal(CrossTenantRefusalReason.RelationNotAllowed, refused.ReasonCode);
        AssertTargetNeverOpened();
        A.CallTo(() => _provider.ProveAsync(A<CrossTenantProofRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AbuseCase02_DetachedChild_IsRefused_AfterOneRegistryRefresh()
    {
        var refused = await RefusedAsync(Request("g", Child("d"), caller: Caller));

        Assert.Equal(CrossTenantRefusalReason.TargetNotRegistered, refused.ReasonCode);
        A.CallTo(() => _registryReader.GetAsync(true, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        AssertTargetNeverOpened();
    }

    [Theory]
    [InlineData("x", CrossTenantRefusalReason.RelationNotAllowed)]
    [InlineData("d", CrossTenantRefusalReason.TargetNotRegistered)]
    [InlineData("octosystem", CrossTenantRefusalReason.RelationNotAllowed)]
    [InlineData("a", CrossTenantRefusalReason.RelationNotAllowed)]
    [InlineData("", CrossTenantRefusalReason.InvalidTargetConfiguration)]
    public async Task AbuseCase03_TargetIdFromInputData_IsCheckedLikeAnyOther(string dataValue, string reason)
    {
        _grants[("x", CrossTenantSubjectKind.User)] = ["R"];
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.Get<string>("$.body.tenant")).Returns(dataValue);

        var refused = await RefusedAsync(Request("a",
            new TenantTargetConfiguration { Relation = TenantRelation.Sibling, TenantIdPath = "$.body.tenant" },
            caller: Caller, dataContext: dataContext));

        Assert.Equal(reason, refused.ReasonCode);
        AssertTargetNeverOpened();
    }

    [Fact]
    public async Task AbuseCase03_TargetIdFromInputData_WithRelationAndRight_IsAllowed()
    {
        _grants[("b", CrossTenantSubjectKind.User)] = ["R"];
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.Get<string>("$.invoice.recipient")).Returns("b");

        var access = await CreateGuard().OpenAsync(Request("a",
            new TenantTargetConfiguration { Relation = TenantRelation.Sibling, TenantIdPath = "$.invoice.recipient" },
            caller: Caller, dataContext: dataContext), TestContext.Current.CancellationToken);

        Assert.Equal("b", access.TenantId);
    }

    [Fact]
    public async Task AbuseCase03_TargetIdFromInputData_NotAString_IsRefused()
    {
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.Get<string>("$.body.tenant")).Throws(new InvalidOperationException("not a string"));

        var refused = await RefusedAsync(Request("a",
            new TenantTargetConfiguration { Relation = TenantRelation.Sibling, TenantIdPath = "$.body.tenant" },
            caller: Caller, dataContext: dataContext));

        Assert.Equal(CrossTenantRefusalReason.InvalidTargetConfiguration, refused.ReasonCode);
    }

    // Abuse case 23 (AB#9767): a data-driven target only for a verified caller. A service account (configured, or the
    // fallback of a Caller node without a verified caller: lease queue, cron, event) or the system identity must not
    // let the data pick the tenant — the run-time counterpart of the controller's deploy rule "service-account and
    // system nodes name a target tenant only statically".

    private static TenantTargetConfiguration SiblingFromData() =>
        new() { Relation = TenantRelation.Sibling, TenantIdPath = "$.body.tenant" };

    private static IDataContext DataNaming(string tenantId)
    {
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.Get<string>("$.body.tenant")).Returns(tenantId);
        return dataContext;
    }

    [Fact]
    public async Task AbuseCase23_ServiceAccount_WithTargetFromData_IsRefused()
    {
        _grants[("b", CrossTenantSubjectKind.ServiceAccount)] = ["R"]; // even a grant does not allow it

        var refused = await RefusedAsync(Request("a", SiblingFromData(), NodeExecutionIdentity.ServiceAccount,
            caller: Caller, dataContext: DataNaming("b")));

        Assert.Equal(CrossTenantRefusalReason.DataDrivenTargetNeedsCaller, refused.ReasonCode);
        AssertTargetNeverOpened();
        A.CallTo(() => _registryReader.GetAsync(A<bool>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _provider.ProveAsync(A<CrossTenantProofRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AbuseCase23_CallerFallingBackToTheServiceAccount_WithTargetFromData_IsRefused()
    {
        _grants[("b", CrossTenantSubjectKind.ServiceAccount)] = ["R"];

        var refused = await RefusedAsync(Request("a", SiblingFromData(), NodeExecutionIdentity.Caller, caller: null,
            dataContext: DataNaming("b")));

        Assert.Equal(CrossTenantRefusalReason.DataDrivenTargetNeedsCaller, refused.ReasonCode);
        AssertTargetNeverOpened();
        A.CallTo(() => _resolver.ResolveServiceAccountAsync(A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _provider.ProveAsync(A<CrossTenantProofRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AbuseCase23_SystemIdentity_WithTargetFromData_IsRefused()
    {
        var refused = await RefusedAsync(Request("a", SiblingFromData(), NodeExecutionIdentity.System, caller: Caller,
            dataContext: DataNaming("b")));

        Assert.Equal(CrossTenantRefusalReason.SystemIdentityRefused, refused.ReasonCode);
        AssertTargetNeverOpened();
    }

    [Fact]
    public async Task AbuseCase23_VerifiedCaller_WithTargetFromData_IsAllowed()
    {
        _grants[("b", CrossTenantSubjectKind.User)] = ["R"];

        var access = await CreateGuard().OpenAsync(Request("a", SiblingFromData(), NodeExecutionIdentity.Caller,
            caller: Caller, dataContext: DataNaming("b")), TestContext.Current.CancellationToken);

        Assert.Equal("b", access.TenantId);
    }

    [Fact]
    public async Task AbuseCase23_ServiceAccount_WithStaticTarget_IsAllowed()
    {
        _grants[("b", CrossTenantSubjectKind.ServiceAccount)] = ["R"];

        var access = await CreateGuard().OpenAsync(Request("a", Sibling("b"), NodeExecutionIdentity.ServiceAccount),
            TestContext.Current.CancellationToken);

        Assert.Equal("b", access.TenantId);
    }

    [Fact]
    public async Task AbuseCase04_SiblingWithoutGrant_IsRefused()
    {
        var refused = await RefusedAsync(Request("a", Sibling("b"), caller: Caller));

        Assert.Equal(CrossTenantRefusalReason.IdentityNotProvable, refused.ReasonCode);
        AssertTargetNeverOpened();
    }

    [Theory]
    [InlineData(TenantRelation.Parent)]
    [InlineData(TenantRelation.Sibling)]
    [InlineData(TenantRelation.Child)]
    public async Task AbuseCase05_SystemIdentity_IsAlwaysRefused(TenantRelation relation)
    {
        _grants[("g", CrossTenantSubjectKind.User)] = ["R"];
        _grants[("g", CrossTenantSubjectKind.ServiceAccount)] = ["R"];
        var target = relation == TenantRelation.Parent
            ? Parent()
            : new TenantTargetConfiguration { Relation = relation, TenantId = relation == TenantRelation.Child ? "a1" : "b" };

        var refused = await RefusedAsync(Request("a", target, NodeExecutionIdentity.System, caller: Caller));

        Assert.Equal(CrossTenantRefusalReason.SystemIdentityRefused, refused.ReasonCode);
        AssertTargetNeverOpened();
        A.CallTo(() => _provider.ProveAsync(A<CrossTenantProofRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AbuseCase06_CallerWithoutCaller_AndNoServiceAccount_IsRefusedInsteadOfSystem()
    {
        _grants[("g", CrossTenantSubjectKind.ServiceAccount)] = ["R"];
        A.CallTo(() => _resolver.ResolveServiceAccountAsync(A<CancellationToken>._))
            .Returns(new ValueTask<RtSecurityContext>(RtSecurityContext.System));

        var refused = await RefusedAsync(Request("a", Parent()));

        Assert.Equal(CrossTenantRefusalReason.SystemIdentityRefused, refused.ReasonCode);
        AssertTargetNeverOpened();
    }

    [Fact]
    public async Task AbuseCase06_NoIdentityResolver_IsRefused()
    {
        var refused = await RefusedAsync(Request("a", Parent(), NodeExecutionIdentity.ServiceAccount,
            withResolver: false));

        Assert.Equal(CrossTenantRefusalReason.SystemIdentityRefused, refused.ReasonCode);
    }

    [Fact]
    public async Task AbuseCase06_ServiceAccountUnavailable_IsRefused()
    {
        A.CallTo(() => _resolver.ResolveServiceAccountAsync(A<CancellationToken>._))
            .Throws(new InvalidOperationException("token endpoint down"));

        var refused = await RefusedAsync(Request("a", Parent(), NodeExecutionIdentity.ServiceAccount));

        Assert.Equal(CrossTenantRefusalReason.IdentityNotProvable, refused.ReasonCode);
    }

    [Fact]
    public async Task CallerWithoutAccessToken_IsRefused_AndNeverFallsBackToTheServiceAccount()
    {
        _grants[("g", CrossTenantSubjectKind.ServiceAccount)] = ["R"];

        var refused = await RefusedAsync(Request("a", Parent(), caller: Caller, callerToken: null));

        Assert.Equal(CrossTenantRefusalReason.IdentityNotProvable, refused.ReasonCode);
        A.CallTo(() => _resolver.ResolveServiceAccountAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AbuseCase07_ProofForAnotherTenant_IsRefused()
    {
        A.CallTo(() => _provider.ProveAsync(A<CrossTenantProofRequest>._, A<CancellationToken>._))
            .Returns(new CrossTenantProof("a", Generation, RtSecurityContext.ForUser("sa", ["Admin"])));

        var refused = await RefusedAsync(Request("a", Sibling("b"), NodeExecutionIdentity.ServiceAccount));

        Assert.Equal(CrossTenantRefusalReason.IdentityNotProvable, refused.ReasonCode);
        AssertTargetNeverOpened();
        A.CallTo(() => _systemContext.FindTenantRepositoryAsync("a")).MustNotHaveHappened();
    }

    [Fact]
    public async Task AbuseCase07_SystemContextReturnedAsProof_IsRefused()
    {
        A.CallTo(() => _provider.ProveAsync(A<CrossTenantProofRequest>._, A<CancellationToken>._))
            .Returns(new CrossTenantProof("b", Generation, RtSecurityContext.System));

        var refused = await RefusedAsync(Request("a", Sibling("b"), caller: Caller));

        Assert.Equal(CrossTenantRefusalReason.IdentityNotProvable, refused.ReasonCode);
        AssertTargetNeverOpened();
    }

    [Fact]
    public async Task AbuseCase08_RecreatedTenantId_ProofOfAnEarlierGeneration_IsRefused()
    {
        A.CallTo(() => _provider.ProveAsync(A<CrossTenantProofRequest>._, A<CancellationToken>._))
            .Returns(new CrossTenantProof("b", "gen-of-the-deleted-tenant", RtSecurityContext.ForUser("s", ["R"])));

        var refused = await RefusedAsync(Request("a", Sibling("b"), caller: Caller));

        Assert.Equal(CrossTenantRefusalReason.TenantGenerationMismatch, refused.ReasonCode);
        // The cached generation is read once more before refusing.
        A.CallTo(() => _generationReader.GetGenerationAsync("b", true, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        AssertTargetNeverOpened();
    }

    [Fact]
    public async Task AbuseCase08_ProofWithoutGeneration_IsRefused()
    {
        A.CallTo(() => _provider.ProveAsync(A<CrossTenantProofRequest>._, A<CancellationToken>._))
            .Returns(new CrossTenantProof("b", null, RtSecurityContext.ForUser("s", ["R"])));

        var refused = await RefusedAsync(Request("a", Sibling("b"), caller: Caller));

        Assert.Equal(CrossTenantRefusalReason.TargetUnverifiable, refused.ReasonCode);
    }

    [Fact]
    public async Task AbuseCase11_SystemTenantAsTarget_IsRefused()
    {
        _grants[("octosystem", CrossTenantSubjectKind.User)] = ["R"];

        // The parent of a top-level tenant is the system tenant.
        var refused = await RefusedAsync(Request("solo", Parent(), caller: Caller));

        Assert.Equal(CrossTenantRefusalReason.RelationNotAllowed, refused.ReasonCode);
        AssertTargetNeverOpened();
    }

    [Fact]
    public async Task AbuseCase11_SystemTenantAsSource_IsRefused()
    {
        _grants[("g", CrossTenantSubjectKind.ServiceAccount)] = ["R"];

        var refused = await RefusedAsync(Request(System, Child("g"), NodeExecutionIdentity.ServiceAccount));

        Assert.Equal(CrossTenantRefusalReason.RelationNotAllowed, refused.ReasonCode);
    }

    [Fact]
    public async Task AbuseCase11_TwoTopLevelTenants_AreNoSiblings()
    {
        _grants[("g", CrossTenantSubjectKind.User)] = ["R"];

        var refused = await RefusedAsync(Request("solo", Sibling("g"), caller: Caller));

        Assert.Equal(CrossTenantRefusalReason.RelationNotAllowed, refused.ReasonCode);
    }

    [Theory]
    [InlineData("a1", TenantRelation.Sibling, "g")] // ancestor above the parent
    [InlineData("a1", TenantRelation.Sibling, "b")] // nephew → aunt
    [InlineData("b", TenantRelation.Sibling, "a1")] // aunt → nephew
    [InlineData("a1", TenantRelation.Sibling, "x")] // cousin in another group
    [InlineData("g", TenantRelation.Child, "a1")] // grandchild
    [InlineData("a", TenantRelation.Child, "b")] // sibling named as child
    [InlineData("g", TenantRelation.Sibling, "h")] // top-level groups
    public async Task AbuseCase12_AncestorsNephewsCousinsAndGrandchildren_AreRefused(string source,
        TenantRelation relation, string target)
    {
        _grants[(target, CrossTenantSubjectKind.User)] = ["R"];

        var refused = await RefusedAsync(Request(source,
            new TenantTargetConfiguration { Relation = relation, TenantId = target }, caller: Caller));

        Assert.Equal(CrossTenantRefusalReason.RelationNotAllowed, refused.ReasonCode);
        AssertTargetNeverOpened();
    }

    [Theory]
    [InlineData(TenantRelation.Parent, "g", null)]
    [InlineData(TenantRelation.Parent, null, "$.parent")]
    [InlineData(TenantRelation.Sibling, null, null)]
    [InlineData(TenantRelation.Child, "a", "$.child")]
    [InlineData((TenantRelation)42, "a", null)]
    public async Task AbuseCase13_InconsistentConfiguration_IsRefusedBeforeAnyLookup(TenantRelation relation,
        string? tenantId, string? tenantIdPath)
    {
        var refused = await RefusedAsync(Request("a",
            new TenantTargetConfiguration { Relation = relation, TenantId = tenantId, TenantIdPath = tenantIdPath },
            caller: Caller));

        Assert.Equal(CrossTenantRefusalReason.InvalidTargetConfiguration, refused.ReasonCode);
        Assert.Contains("invalid target tenant configuration", refused.Message);
        A.CallTo(() => _registryReader.GetAsync(A<bool>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AbuseCase14_TheAccessNamesTheTargetTenant_ForTheAttributeGuards()
    {
        // Nodes run the Hidden/MethodOnly guards and CK lookups with ICrossTenantAccess.TenantId; the target's CK
        // cache is loaded by the guard before the access is handed out.
        _grants[("g", CrossTenantSubjectKind.User)] = ["R"];

        var access = await CreateGuard().OpenAsync(Request("a", Parent(), caller: Caller),
            TestContext.Current.CancellationToken);

        Assert.Equal("g", access.TenantId);
        Assert.Same(_targetRepository, access.Repository);
    }

    [Fact]
    public async Task AbuseCase20_UnreadableRegistry_IsRefused()
    {
        A.CallTo(() => _registryReader.GetAsync(A<bool>._, A<CancellationToken>._))
            .Throws(new TimeoutException("mongo"));

        var refused = await RefusedAsync(Request("a", Parent(), caller: Caller));

        Assert.Equal(CrossTenantRefusalReason.TargetUnverifiable, refused.ReasonCode);
    }

    [Fact]
    public async Task AbuseCase20_UnreachableIdentityService_IsRefused()
    {
        A.CallTo(() => _provider.ProveAsync(A<CrossTenantProofRequest>._, A<CancellationToken>._))
            .Throws(new CrossTenantIdentityUnavailableException("identity down"));

        var refused = await RefusedAsync(Request("a", Parent(), caller: Caller));

        Assert.Equal(CrossTenantRefusalReason.TargetUnverifiable, refused.ReasonCode);
        AssertTargetNeverOpened();
    }

    [Fact]
    public async Task AbuseCase20_NoGenerationStore_IsRefused()
    {
        _grants[("g", CrossTenantSubjectKind.User)] = ["R"];

        var refused = await RefusedAsync(Request("a", Parent(), caller: Caller),
            CreateGuard(withGenerationReader: false));

        Assert.Equal(CrossTenantRefusalReason.TargetUnverifiable, refused.ReasonCode);
    }

    [Fact]
    public async Task AbuseCase20_UnreadableGeneration_IsRefused()
    {
        _grants[("g", CrossTenantSubjectKind.User)] = ["R"];
        A.CallTo(() => _generationReader.GetGenerationAsync(A<string>._, A<bool>._, A<CancellationToken>._))
            .Throws(new TimeoutException("store"));

        var refused = await RefusedAsync(Request("a", Parent(), caller: Caller));

        Assert.Equal(CrossTenantRefusalReason.TargetUnverifiable, refused.ReasonCode);
    }

    [Fact]
    public async Task AbuseCase20_TargetRepositoryCannotBeOpened_IsRefused()
    {
        _grants[("g", CrossTenantSubjectKind.User)] = ["R"];
        A.CallTo(() => _systemContext.FindTenantRepositoryAsync(A<string>._)).Throws(new TimeoutException("db"));

        var refused = await RefusedAsync(Request("a", Parent(), caller: Caller));

        Assert.Equal(CrossTenantRefusalReason.TargetUnverifiable, refused.ReasonCode);
    }

    [Fact]
    public async Task AbuseCase21_NotRegistered_OutsideRelation_AndNoRight_GiveTheSameMessage()
    {
        var notRegistered = await RefusedAsync(Request("a", Sibling("d"), caller: Caller));
        var outsideRelation = await RefusedAsync(Request("a", Sibling("x"), caller: Caller));
        var noRight = await RefusedAsync(Request("a", Sibling("b"), caller: Caller));

        Assert.Equal(notRegistered.Message, outsideRelation.Message);
        Assert.Equal(notRegistered.Message, noRight.Message);
        // The message names no tenant id at all, so it cannot confirm that a probed id exists.
        Assert.DoesNotContain("'d'", notRegistered.Message);
        Assert.DoesNotContain("'b'", noRight.Message);
        Assert.Equal(3, new[] { notRegistered.ReasonCode, outsideRelation.ReasonCode, noRight.ReasonCode }
            .Distinct().Count());
    }

    [Fact]
    public async Task AbuseCase22_ProofsAreReusedWithinOneExecution_AndProvenAgainInTheNext()
    {
        _grants[("g", CrossTenantSubjectKind.User)] = ["R"];
        var guard = CreateGuard();
        var execution1 = new CrossTenantProofCache();
        var execution2 = new CrossTenantProofCache();

        await guard.OpenAsync(Request("a", Parent(), caller: Caller, cache: execution1),
            TestContext.Current.CancellationToken);
        await guard.OpenAsync(Request("a", Parent(), caller: Caller, cache: execution1),
            TestContext.Current.CancellationToken);
        await guard.OpenAsync(Request("a", Parent(), caller: Caller, cache: execution2),
            TestContext.Current.CancellationToken);

        A.CallTo(() => _provider.ProveAsync(A<CrossTenantProofRequest>._, A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task AbuseCase22_ACachedProofIsStillCheckedForItsGeneration()
    {
        _grants[("g", CrossTenantSubjectKind.User)] = ["R"];
        var guard = CreateGuard();
        var execution = new CrossTenantProofCache();
        await guard.OpenAsync(Request("a", Parent(), caller: Caller, cache: execution),
            TestContext.Current.CancellationToken);

        // g is deleted and created again while the execution runs.
        A.CallTo(() => _generationReader.GetGenerationAsync(A<string>._, A<bool>._, A<CancellationToken>._))
            .Returns(new ValueTask<string?>("gen-new"));

        var refused = await RefusedAsync(Request("a", Parent(), caller: Caller, cache: execution), guard);
        Assert.Equal(CrossTenantRefusalReason.TenantGenerationMismatch, refused.ReasonCode);
    }
}
