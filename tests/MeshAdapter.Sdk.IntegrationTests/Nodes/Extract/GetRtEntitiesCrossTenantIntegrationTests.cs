using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Extract;

/// <summary>
///     AB#9768 acceptance criteria against a real tenant tree in a real MongoDB (<see cref="TenantSubtreeFixture" />):
///     real pipeline YAML through the real orchestrator and the registered GetRtEntities nodes, the real guard on the
///     real tenant registry and repositories. Only the identity service is scripted: it proves a right in a target only
///     where a grant was given for that target. Every tenant holds a marker entity naming the tenant (the "contacts"
///     of the acceptance criteria), company B additionally a poison canary that a refused read must never return.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class GetRtEntitiesCrossTenantIntegrationTests(TenantSubtreePipelineFixture fixture)
    : IClassFixture<TenantSubtreePipelineFixture>
{
    private const string Generation = "gen-1";
    private const string SensorReading = "MeshAdapterIntegrationTest/SensorReading";

    /// <summary>(target tenant, subject kind) the scripted identity service grants.</summary>
    private readonly HashSet<(string Target, CrossTenantSubjectKind Kind)> _grants = [];

    private ICrossTenantAccessGuard CreateGuard()
    {
        var provider = A.Fake<ICrossTenantIdentityProvider>();
        A.CallTo(() => provider.ProveAsync(A<CrossTenantProofRequest>._, A<CancellationToken>._))
            .ReturnsLazily((CrossTenantProofRequest r, CancellationToken _) => Task.FromResult(
                _grants.Contains((r.TargetTenantId.ToLowerInvariant(), r.SubjectKind))
                    ? new CrossTenantProof(r.TargetTenantId, Generation,
                        RtSecurityContext.ForUser($"xt_{r.SourceTenantId}_{r.SourceIdentity.SubjectId}",
                            ["TenantDataReader"]))
                    : null));
        var generations = A.Fake<ITenantGenerationReader>();
        A.CallTo(() => generations.GetGenerationAsync(A<string>._, A<bool>._, A<CancellationToken>._))
            .Returns(new ValueTask<string?>(Generation));

        var systemContext = fixture.GetSystemContext();
        return new CrossTenantAccessGuard(systemContext, fixture.GetService<ICkCacheService>(),
            new TenantRegistryReader(systemContext, fixture.GetService<IOptions<OctoSystemConfiguration>>()),
            provider, NullLogger<CrossTenantAccessGuard>.Instance, generations);
    }

    private async Task<IMeshEtlContext> CreateContextAsync(string sourceTenantId)
    {
        var repository = await fixture.GetSystemContext().FindTenantRepositoryAsync(sourceTenantId);
        var id = new OctoObjectId("000000000000000000000078");
        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.GetNames()).Returns(Enumerable.Empty<string>());
        A.CallTo(() => globalConfig.IsDefined(A<string>._)).Returns(false);
        var resolver = A.Fake<IPipelineIdentityResolver>();
        A.CallTo(() => resolver.ResolveServiceAccountAsync(A<CancellationToken>._))
            .Returns(new ValueTask<RtSecurityContext>(RtSecurityContext.ForUser("sa-client", ["AdminOfSource"])));
        var caller = new VerifiedPrincipal($"user-of-{sourceTenantId}", sourceTenantId, null, null, ["AdminOfSource"]);
        return new MeshEtlContext(sourceTenantId, repository, id, Guid.NewGuid(),
            new RtEntityId("System/RtDataPipeline", id), DateTime.UtcNow, null, globalConfig,
            new Dictionary<string, object?>(), caller, "caller-token", resolver,
            crossTenantAccessGuard: CreateGuard());
    }

    private async Task<string> RunAsync(string sourceTenantId, string yaml, JsonObject? input = null)
    {
        fixture.EnsureInitialized();
        var etlContext = await CreateContextAsync(sourceTenantId);
        var definition = await fixture.GetService<IPipelineConfigurationSerializer>().DeserializeAsync(yaml);
        var result = await fixture.GetService<IEtlDataOrchestrator>().ExecutePipelineAsync(definition, etlContext,
            value: input ?? new JsonObject());
        return (result as JsonNode ?? JsonSerializer.SerializeToNode(result))!.ToJsonString();
    }

    private static string ByType(string target, string identity = "Caller") => $"""
        transformations:
          - type: GetRtEntitiesByType@1
            ckTypeId: {SensorReading}
            identity: {identity}
            targetTenant:
        {target}
            targetPath: $.contacts
        """;

    private static string Parent => "      relation: Parent";

    private static string Relation(string relation, string tenantId) =>
        $"      relation: {relation}\n      tenantId: {tenantId}";

    /// <summary>Runs and expects the execution to fail with the forbidden error; returns the error text.</summary>
    private async Task<string> RefusedAsync(string sourceTenantId, string yaml, JsonObject? input = null)
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(sourceTenantId, yaml, input));
        var chain = new List<Exception>();
        for (var e = exception; e != null; e = e.InnerException)
        {
            chain.Add(e);
        }

        var denied = Assert.Single(chain.OfType<CrossTenantAccessDeniedException>());
        Assert.Equal(CrossTenantAccessCodes.Forbidden, denied.ErrorCode);
        var text = string.Join(" | ", chain.Select(e => e.Message));
        Assert.Contains(CrossTenantAccessCodes.Forbidden, text);
        Assert.DoesNotContain(TenantSubtreeFixture.PoisonCanary, text);
        return text;
    }

    // ---- AC 1: A reads G (parent) with the identity granted in G --------------------------------------------

    [Fact]
    public async Task CompanyA_ReadsTheGroupsEntities_AsTheCallerGrantedInTheGroup()
    {
        _grants.Add((TenantSubtreeFixture.Group, CrossTenantSubjectKind.User));

        var output = await RunAsync(TenantSubtreeFixture.TenantA, ByType(Parent));

        Assert.Contains(TenantSubtreeFixture.MarkerOf(TenantSubtreeFixture.Group), output);
        Assert.DoesNotContain(TenantSubtreeFixture.MarkerOf(TenantSubtreeFixture.TenantA), output);
    }

    [Fact]
    public async Task CompanyA_ReadsTheGroup_AsTheServiceAccountGrantedInTheGroup()
    {
        _grants.Add((TenantSubtreeFixture.Group, CrossTenantSubjectKind.ServiceAccount));

        var output = await RunAsync(TenantSubtreeFixture.TenantA, ByType(Parent, "ServiceAccount"));

        Assert.Contains(TenantSubtreeFixture.MarkerOf(TenantSubtreeFixture.Group), output);
    }

    // ---- AC 2: sibling and child work, the other relations are refused -------------------------------------

    [Fact]
    public async Task CompanyA_ReadsSiblingB_WithAGrantInB()
    {
        _grants.Add((TenantSubtreeFixture.TenantB, CrossTenantSubjectKind.User));

        var output = await RunAsync(TenantSubtreeFixture.TenantA,
            ByType(Relation("Sibling", TenantSubtreeFixture.TenantB)));

        Assert.Contains(TenantSubtreeFixture.MarkerOf(TenantSubtreeFixture.TenantB), output);
        Assert.DoesNotContain(TenantSubtreeFixture.MarkerOf(TenantSubtreeFixture.TenantA), output);
    }

    [Fact]
    public async Task Group_ReadsChildA_WithAGrantInA()
    {
        _grants.Add((TenantSubtreeFixture.TenantA, CrossTenantSubjectKind.User));

        var output = await RunAsync(TenantSubtreeFixture.Group,
            ByType(Relation("Child", TenantSubtreeFixture.TenantA)));

        Assert.Contains(TenantSubtreeFixture.MarkerOf(TenantSubtreeFixture.TenantA), output);
        Assert.DoesNotContain(TenantSubtreeFixture.MarkerOf(TenantSubtreeFixture.Group), output);
    }

    [Theory]
    // a grandchild is no child
    [InlineData(TenantSubtreeFixture.Group, "Child", TenantSubtreeFixture.GrandChild)]
    // a tenant of another group is no sibling
    [InlineData(TenantSubtreeFixture.TenantA, "Sibling", TenantSubtreeFixture.Outsider)]
    // a detached child is no child
    [InlineData(TenantSubtreeFixture.Group, "Child", TenantSubtreeFixture.Detached)]
    // the sibling named as a child
    [InlineData(TenantSubtreeFixture.TenantA, "Child", TenantSubtreeFixture.TenantB)]
    // two groups below the system tenant are no siblings
    [InlineData(TenantSubtreeFixture.Group, "Sibling", TenantSubtreeFixture.OtherGroup)]
    public async Task OtherRelations_AreRefused_EvenWithAGrant(string source, string relation, string target)
    {
        _grants.Add((target, CrossTenantSubjectKind.User));

        await RefusedAsync(source, ByType(Relation(relation, target)));
    }

    [Fact]
    public async Task TopLevelGroup_HasNoParent_TheSystemTenantIsNeverRead()
    {
        await RefusedAsync(TenantSubtreeFixture.Group, ByType(Parent));
    }

    // ---- AC 4: a refused read fails the execution with the forbidden error ---------------------------------

    [Fact]
    public async Task SiblingWithoutAGrant_FailsTheExecution_WithTheForbiddenError_AndReadsNothingOfB()
    {
        await RefusedAsync(TenantSubtreeFixture.TenantA, ByType(Relation("Sibling", TenantSubtreeFixture.TenantB)));
    }

    [Fact]
    public async Task SystemIdentity_FailsTheExecution_EvenWithAGrant()
    {
        _grants.Add((TenantSubtreeFixture.Group, CrossTenantSubjectKind.User));
        _grants.Add((TenantSubtreeFixture.Group, CrossTenantSubjectKind.ServiceAccount));

        await RefusedAsync(TenantSubtreeFixture.TenantA, ByType(Parent, "System"));
    }

    [Fact]
    public async Task DataDrivenTargetOutsideTheRelation_IsRefused()
    {
        _grants.Add((TenantSubtreeFixture.Outsider, CrossTenantSubjectKind.User));
        const string yaml = """
            transformations:
              - type: GetRtEntitiesById@1
                ckTypeId: MeshAdapterIntegrationTest/SensorReading
                rtIds: [ "000000000000000000000001" ]
                targetTenant:
                  relation: Sibling
                  tenantIdPath: $.recipientTenantId
                targetPath: $.contact
            """;

        await RefusedAsync(TenantSubtreeFixture.TenantA, yaml,
            new JsonObject { ["recipientTenantId"] = TenantSubtreeFixture.Outsider });
    }

    // ---- AC 3: without the setting the node reads its own tenant as before ---------------------------------

    [Fact]
    public async Task WithoutTarget_TheNodeReadsItsOwnTenant_AndTheGuardIsNotAsked()
    {
        const string yaml = """
            transformations:
              - type: GetRtEntitiesByType@1
                ckTypeId: MeshAdapterIntegrationTest/SensorReading
                targetPath: $.contacts
            """;

        var output = await RunAsync(TenantSubtreeFixture.TenantA, yaml);

        Assert.Contains(TenantSubtreeFixture.MarkerOf(TenantSubtreeFixture.TenantA), output);
        Assert.DoesNotContain(TenantSubtreeFixture.MarkerOf(TenantSubtreeFixture.Group), output);
    }

    [Fact]
    public async Task WellKnownNameNode_ResolvesAgainstTheParent()
    {
        _grants.Add((TenantSubtreeFixture.Group, CrossTenantSubjectKind.User));
        // The marker has no well-known name: the node finds nothing in the group and writes nothing back, but the
        // read went to the group (a refusal would fail the execution).
        const string yaml = """
            transformations:
              - type: GetRtEntitiesByWellKnownName@1
                ckTypeId: MeshAdapterIntegrationTest/SensorReading
                path: $.items
                wellKnownNamePath: $.name
                targetTenant:
                  relation: Parent
            """;

        var output = await RunAsync(TenantSubtreeFixture.TenantA, yaml,
            new JsonObject { ["items"] = new JsonArray(new JsonObject { ["name"] = "unknown" }) });

        Assert.Contains("unknown", output);
        Assert.DoesNotContain("rtId", output);
    }
}
