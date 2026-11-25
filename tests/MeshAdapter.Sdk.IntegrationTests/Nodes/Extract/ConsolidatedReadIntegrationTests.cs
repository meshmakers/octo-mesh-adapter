using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
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
///     AB#9771 acceptance criteria against a real tenant tree in a real MongoDB
///     (<see cref="TenantConsolidationFixture" />): a consolidation pipeline registered in the group reads the invoices
///     of its companies with GetRtEntitiesByType@1 <c>targetTenants</c> and sums them per company and in total with
///     SumAggregation@1, through the real orchestrator, the real guard, the real registry and the real repositories.
///     Only the identity service is scripted: it proves a right in a company only where a grant was given.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class ConsolidatedReadIntegrationTests(TenantConsolidationFixture fixture)
    : IClassFixture<TenantConsolidationFixture>
{
    private const string Generation = "gen-1";
    private const string StaleGeneration = "gen-0";

    /// <summary>Stands for the system tenant in theory data (its id comes from the configuration).</summary>
    private const string SystemTenantToken = "(system)";

    private const string Group = TenantSubtreeFixture.Group;
    private const string CompanyA = TenantSubtreeFixture.TenantA;
    private const string CompanyB = TenantSubtreeFixture.TenantB;
    private const string CompanyC = TenantConsolidationFixture.TenantC;

    /// <summary>(target tenant, subject kind) the scripted identity service grants.</summary>
    private readonly HashSet<(string Target, CrossTenantSubjectKind Kind)> _grants = [];

    /// <summary>Targets whose proof carries an earlier tenant generation (a deleted and re-created tenant).</summary>
    private readonly HashSet<string> _staleProofs = [];

    private string SystemTenantId => fixture.GetService<IOptions<OctoSystemConfiguration>>().Value.SystemTenantId;

    private ICrossTenantAccessGuard CreateGuard()
    {
        var provider = A.Fake<ICrossTenantIdentityProvider>();
        A.CallTo(() => provider.ProveAsync(A<CrossTenantProofRequest>._, A<CancellationToken>._))
            .ReturnsLazily((CrossTenantProofRequest r, CancellationToken _) => Task.FromResult(
                _grants.Contains((r.TargetTenantId.ToLowerInvariant(), r.SubjectKind))
                    ? new CrossTenantProof(r.TargetTenantId,
                        _staleProofs.Contains(r.TargetTenantId.ToLowerInvariant()) ? StaleGeneration : Generation,
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
        var id = new OctoObjectId("000000000000000000000079");
        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.GetNames()).Returns(Enumerable.Empty<string>());
        A.CallTo(() => globalConfig.IsDefined(A<string>._)).Returns(false);
        var resolver = A.Fake<IPipelineIdentityResolver>();
        A.CallTo(() => resolver.ResolveServiceAccountAsync(A<CancellationToken>._))
            .Returns(new ValueTask<RtSecurityContext>(RtSecurityContext.ForUser("sa-client", ["AdminOfSource"])));
        var caller = new VerifiedPrincipal($"controller-of-{sourceTenantId}", sourceTenantId, null, null,
            ["AdminOfSource"]);
        return new MeshEtlContext(sourceTenantId, repository, id, Guid.NewGuid(),
            new RtEntityId("System/RtDataPipeline", id), DateTime.UtcNow, null, globalConfig,
            new Dictionary<string, object?>(), caller, "caller-token", resolver,
            crossTenantAccessGuard: CreateGuard());
    }

    private async Task<JsonObject> RunAsync(string sourceTenantId, string yaml)
    {
        fixture.EnsureInitialized();
        var etlContext = await CreateContextAsync(sourceTenantId);
        var definition = await fixture.GetService<IPipelineConfigurationSerializer>().DeserializeAsync(yaml);
        var result = await fixture.GetService<IEtlDataOrchestrator>().ExecutePipelineAsync(definition, etlContext,
            value: new JsonObject());
        return (result as JsonNode ?? JsonSerializer.SerializeToNode(result))!.AsObject();
    }

    /// <summary>The consolidation pipeline of the group: read every company's invoices, sum per company and in total.</summary>
    private static string Consolidation(string onRefused = "Fail", string? tenantIds = null,
        string identity = "Caller") => $"""
        transformations:
          - type: GetRtEntitiesByType@1
            ckTypeId: MeshAdapterIntegrationTest/InterCompanyInvoice
            identity: {identity}
            targetTenants:
              relation: Child
              onRefused: {onRefused}{(tenantIds == null ? "" : $"\n      tenantIds: [{tenantIds}]")}
            targetPath: $.invoices
          - type: SumAggregation@1
            targetPath: $.figures.{CompanyA}
            aggregations:
              - path: $.invoices.Items[*]
                filterPath: $.SourceTenantId
                comparisonValue: {CompanyA}
                aggregationPath: $.Attributes.InvoiceAmount
                value: 1
          - type: SumAggregation@1
            targetPath: $.figures.{CompanyB}
            aggregations:
              - path: $.invoices.Items[*]
                filterPath: $.SourceTenantId
                comparisonValue: {CompanyB}
                aggregationPath: $.Attributes.InvoiceAmount
                value: 1
          - type: SumAggregation@1
            targetPath: $.figures.total
            aggregations:
              - path: $.invoices.Items[*]
                aggregationPath: $.Attributes.InvoiceAmount
                value: 1
        """;

    private static IReadOnlyList<string> SourceTenants(JsonObject output) =>
        output["invoices"]!["Items"]!.AsArray()
            .Select(i => i!["SourceTenantId"]!.GetValue<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<string> RefusedTenants(JsonObject output) =>
        output["invoices"]!["RefusedTenants"]!.AsArray()
            .Select(i => i!["TenantId"]!.GetValue<string>())
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

    private static double Figure(JsonObject output, string key) => output["figures"]![key]!.GetValue<double>();

    private void GrantAAndB()
    {
        _grants.Add((CompanyA, CrossTenantSubjectKind.User));
        _grants.Add((CompanyB, CrossTenantSubjectKind.User));
    }

    /// <summary>Runs and expects the execution to fail with the forbidden error; returns the refusal.</summary>
    private async Task<CrossTenantAccessDeniedException> RefusedAsync(string sourceTenantId, string yaml)
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(sourceTenantId, yaml));
        var chain = new List<Exception>();
        for (var e = exception; e != null; e = e.InnerException)
        {
            chain.Add(e);
        }

        var denied = Assert.Single(chain.OfType<CrossTenantAccessDeniedException>());
        var text = string.Join(" | ", chain.Select(e => e.Message));
        Assert.Contains(CrossTenantAccessCodes.Forbidden, text);
        // No invoice number of any company reaches the error text.
        Assert.DoesNotContain("INV-", text);
        return denied;
    }

    // ---- AC: the group's controller sees the consolidated figures ------------------------------------------

    [Fact]
    public async Task Group_ConsolidatesItsCompanies_PerCompanyAndInTotal()
    {
        GrantAAndB();

        var output = await RunAsync(Group, Consolidation("Fail", $"{CompanyA}, {CompanyB}"));

        Assert.Equal(150.0, Figure(output, CompanyA));
        Assert.Equal(70.0, Figure(output, CompanyB));
        Assert.Equal(220.0, Figure(output, "total"));
        Assert.Equal(new[] { CompanyA, CompanyB }, SourceTenants(output));
        Assert.Equal(3, output["invoices"]!["TotalCount"]!.GetValue<long>());
        Assert.Empty(RefusedTenants(output));
        var perTenant = output["invoices"]!["Tenants"]!.AsArray()
            .ToDictionary(t => t!["TenantId"]!.GetValue<string>(), t => t!["TotalCount"]!.GetValue<long>());
        Assert.Equal(2, perTenant[CompanyA]);
        Assert.Equal(1, perTenant[CompanyB]);
    }

    [Fact]
    public async Task EveryItem_NamesItsSourceTenant()
    {
        GrantAAndB();

        var output = await RunAsync(Group, Consolidation("Fail", $"{CompanyA}, {CompanyB}"));

        foreach (var item in output["invoices"]!["Items"]!.AsArray())
        {
            var source = item!["SourceTenantId"]!.GetValue<string>();
            var number = item["Attributes"]!["InvoiceNumber"]!.GetValue<string>();
            Assert.StartsWith($"INV-{source.ToUpperInvariant()}-", number);
        }
    }

    // ---- AC: a company without a right is refused (Fail and Report) ----------------------------------------

    [Fact]
    public async Task CompanyWithoutRight_Fail_FailsTheExecution_NamingIt_AndReadsNothing()
    {
        GrantAAndB();

        // All children of the group: A, B and C (C without a grant).
        var denied = await RefusedAsync(Group, Consolidation("Fail"));

        Assert.Equal(new[] { CompanyC }, denied.RefusedTenantIds);
        Assert.Contains($"'{CompanyC}'", denied.Message);
        Assert.Equal(CrossTenantRefusalReason.IdentityNotProvable, denied.ReasonCode);
    }

    [Fact]
    public async Task OnRefusedLeftOut_DefaultsToFail()
    {
        GrantAAndB();
        const string yaml = """
            transformations:
              - type: GetRtEntitiesByType@1
                ckTypeId: MeshAdapterIntegrationTest/InterCompanyInvoice
                targetTenants:
                  relation: Child
                targetPath: $.invoices
            """;

        var denied = await RefusedAsync(Group, yaml);

        Assert.Equal(new[] { CompanyC }, denied.RefusedTenantIds);
    }

    [Fact]
    public async Task CompanyWithoutRight_Report_ListsIt_ReturnsTheOthers_WithoutAnyDataOfIt()
    {
        GrantAAndB();

        var output = await RunAsync(Group, Consolidation("Report"));

        Assert.Equal(new[] { CompanyC }, RefusedTenants(output));
        Assert.Equal(CrossTenantAccessCodes.Forbidden,
            output["invoices"]!["RefusedTenants"]![0]!["ErrorCode"]!.GetValue<string>());
        Assert.Equal(new[] { CompanyA, CompanyB }, SourceTenants(output));
        Assert.Equal(220.0, Figure(output, "total"));
        Assert.DoesNotContain($"INV-{CompanyC.ToUpperInvariant()}", output.ToJsonString());
        Assert.DoesNotContain(TenantSubtreeFixture.MarkerOf(CompanyC), output.ToJsonString());
    }

    // ---- AC: a detached company is no longer part of the consolidation -------------------------------------

    [Fact]
    public async Task DetachedCompany_IsNotPartOfTheDefaultConsolidation()
    {
        GrantAAndB();
        _grants.Add((CompanyC, CrossTenantSubjectKind.User));
        _grants.Add((TenantSubtreeFixture.Detached, CrossTenantSubjectKind.User));

        var output = await RunAsync(Group, Consolidation("Fail"));

        Assert.Equal(new[] { CompanyA, CompanyB, CompanyC }, SourceTenants(output));
        Assert.DoesNotContain(output["invoices"]!["Tenants"]!.AsArray(),
            t => t!["TenantId"]!.GetValue<string>() == TenantSubtreeFixture.Detached);
        Assert.Equal(1219.0, Figure(output, "total"));
    }

    [Fact]
    public async Task DetachedCompany_NamedExplicitly_IsRefused()
    {
        GrantAAndB();
        _grants.Add((TenantSubtreeFixture.Detached, CrossTenantSubjectKind.User));

        var denied = await RefusedAsync(Group,
            Consolidation("Fail", $"{CompanyA}, {TenantSubtreeFixture.Detached}"));

        Assert.Equal(new[] { TenantSubtreeFixture.Detached }, denied.RefusedTenantIds);
        Assert.Equal(CrossTenantRefusalReason.TargetNotRegistered, denied.ReasonCode);
    }

    // ---- Abuse cases -----------------------------------------------------------------------------------------

    [Theory]
    // a grandchild is no child, even with a grant
    [InlineData(TenantSubtreeFixture.GrandChild)]
    // the system tenant is never a target
    [InlineData(SystemTenantToken)]
    // a tenant of another group
    [InlineData(TenantSubtreeFixture.Outsider)]
    // the other group itself (a top-level sibling of the group)
    [InlineData(TenantSubtreeFixture.OtherGroup)]
    // the group itself
    [InlineData(Group)]
    public async Task TenantIdsNamingANonChild_IsRefused_EvenWithAGrant(string target)
    {
        target = target == SystemTenantToken ? SystemTenantId : target;
        GrantAAndB();
        _grants.Add((target.ToLowerInvariant(), CrossTenantSubjectKind.User));

        var denied = await RefusedAsync(Group, Consolidation("Fail", $"{CompanyA}, {target}"));

        Assert.Equal(new[] { target }, denied.RefusedTenantIds);
    }

    [Fact]
    public async Task GrandChild_IsNeverPartOfTheDefaultConsolidation()
    {
        GrantAAndB();
        _grants.Add((CompanyC, CrossTenantSubjectKind.User));
        _grants.Add((TenantSubtreeFixture.GrandChild, CrossTenantSubjectKind.User));

        var output = await RunAsync(Group, Consolidation("Fail"));

        Assert.DoesNotContain(TenantSubtreeFixture.GrandChild, SourceTenants(output));
        Assert.DoesNotContain("INV-XTA1", output.ToJsonString());
    }

    [Fact]
    public async Task NonChild_Report_IsListed_AndReadsNothingOfIt()
    {
        GrantAAndB();
        _grants.Add((TenantSubtreeFixture.Outsider, CrossTenantSubjectKind.User));

        var output = await RunAsync(Group,
            Consolidation("Report", $"{CompanyA}, {TenantSubtreeFixture.Outsider}"));

        Assert.Equal(new[] { TenantSubtreeFixture.Outsider }, RefusedTenants(output));
        Assert.Equal(new[] { CompanyA }, SourceTenants(output));
        Assert.DoesNotContain(TenantSubtreeFixture.MarkerOf(TenantSubtreeFixture.Outsider), output.ToJsonString());
    }

    [Theory]
    [InlineData("Fail")]
    [InlineData("Report")]
    public async Task SystemIdentity_FailsTheExecution_EvenWithReport_AndGrants(string onRefused)
    {
        GrantAAndB();
        _grants.Add((CompanyA, CrossTenantSubjectKind.ServiceAccount));
        _grants.Add((CompanyB, CrossTenantSubjectKind.ServiceAccount));

        var denied = await RefusedAsync(Group, Consolidation(onRefused, $"{CompanyA}, {CompanyB}", "System"));

        Assert.Equal(CrossTenantRefusalReason.SystemIdentityRefused, denied.ReasonCode);
    }

    [Fact]
    public async Task SystemTenantAsSource_HasNoChildrenToConsolidate()
    {
        var output = await RunAsync(SystemTenantId, Consolidation("Fail"));

        Assert.Empty(output["invoices"]!["Items"]!.AsArray());
        Assert.Empty(output["invoices"]!["Tenants"]!.AsArray());
    }

    [Fact]
    public async Task StaleTenantGeneration_IsRefused()
    {
        GrantAAndB();
        _staleProofs.Add(CompanyB);

        var denied = await RefusedAsync(Group, Consolidation("Fail", $"{CompanyA}, {CompanyB}"));

        Assert.Equal(new[] { CompanyB }, denied.RefusedTenantIds);
        Assert.Equal(CrossTenantRefusalReason.TenantGenerationMismatch, denied.ReasonCode);
    }

    [Fact]
    public async Task ServiceAccount_ProvesItsRightPerCompany()
    {
        _grants.Add((CompanyA, CrossTenantSubjectKind.ServiceAccount));

        var output = await RunAsync(Group, Consolidation("Report", $"{CompanyA}, {CompanyB}", "ServiceAccount"));

        Assert.Equal(new[] { CompanyA }, SourceTenants(output));
        Assert.Equal(new[] { CompanyB }, RefusedTenants(output));
        Assert.Equal(150.0, Figure(output, "total"));
    }
}
