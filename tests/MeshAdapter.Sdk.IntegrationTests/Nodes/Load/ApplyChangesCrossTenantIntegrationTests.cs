using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Load;

/// <summary>
///     AB#9769 acceptance criteria against a real tenant tree in a real MongoDB (<see cref="TenantSubtreeFixture" />): a
///     pipeline of company A (real YAML, real orchestrator, the registered nodes) reads a released inter-company invoice
///     in its own tenant and writes an incoming document into company B with ApplyChanges@2 and its target-tenant setting,
///     through the real guard on the real tenant registry and repositories. Only the identity service is scripted: it
///     proves a right in a target only where a grant was given for that target (and can be told to answer a proof for the
///     wrong tenant or an earlier generation). Every refusal leaves company B unchanged.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class ApplyChangesCrossTenantIntegrationTests(TenantSubtreePipelineFixture fixture)
    : IClassFixture<TenantSubtreePipelineFixture>
{
    private const string Generation = "gen-1";

    /// <summary>Placeholder of the system tenant's id in test data (read from the configuration).</summary>
    private const string SystemTenant = "(system tenant)";
    private const string Model = "MeshAdapterIntegrationTest";
    private static readonly RtCkId<CkTypeId> InvoiceType = new($"{Model}/InterCompanyInvoice");
    private static readonly RtCkId<CkTypeId> DocumentType = new($"{Model}/IncomingDocument");

    /// <summary>(target tenant, subject kind) the scripted identity service grants.</summary>
    private readonly HashSet<(string Target, CrossTenantSubjectKind Kind)> _grants = [];

    /// <summary>Abuse cases: the proof names the source tenant instead of the target.</summary>
    private bool _proofForTheSourceTenant;

    /// <summary>Abuse cases: the proof carries an earlier generation of the target.</summary>
    private bool _staleGeneration;

    private ICrossTenantAccessGuard CreateGuard()
    {
        var provider = A.Fake<ICrossTenantIdentityProvider>();
        A.CallTo(() => provider.ProveAsync(A<CrossTenantProofRequest>._, A<CancellationToken>._))
            .ReturnsLazily((CrossTenantProofRequest r, CancellationToken _) => Task.FromResult(
                _grants.Contains((r.TargetTenantId.ToLowerInvariant(), r.SubjectKind))
                    ? new CrossTenantProof(_proofForTheSourceTenant ? r.SourceTenantId : r.TargetTenantId,
                        _staleGeneration ? "gen-0" : Generation,
                        RtSecurityContext.ForUser($"xt_{r.SourceTenantId}_{r.SourceIdentity.SubjectId}",
                            ["TenantDataWriter"]))
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
        var caller = new VerifiedPrincipal($"user-of-{sourceTenantId}", sourceTenantId, null, null, ["AdminOfSource"]);
        return new MeshEtlContext(sourceTenantId, repository, id, Guid.NewGuid(),
            new RtEntityId("System/RtDataPipeline", id), DateTime.UtcNow, null, globalConfig,
            new Dictionary<string, object?>(), caller, "caller-token", resolver,
            crossTenantAccessGuard: CreateGuard());
    }

    private async Task RunAsync(string sourceTenantId, string yaml, JsonObject input)
    {
        fixture.EnsureInitialized();
        var etlContext = await CreateContextAsync(sourceTenantId);
        var definition = await fixture.GetService<IPipelineConfigurationSerializer>().DeserializeAsync(yaml);
        await fixture.GetService<IEtlDataOrchestrator>().ExecutePipelineAsync(definition, etlContext, value: input);
    }

    /// <summary>Runs and expects the execution to fail with the forbidden error.</summary>
    private async Task RefusedAsync(string sourceTenantId, string yaml, JsonObject input)
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(sourceTenantId, yaml, input));
        var chain = new List<Exception>();
        for (var e = exception; e != null; e = e.InnerException)
        {
            chain.Add(e);
        }

        var denied = Assert.Single(chain.OfType<CrossTenantAccessDeniedException>());
        Assert.Equal(CrossTenantAccessCodes.Forbidden, denied.ErrorCode);
        Assert.Contains(CrossTenantAccessCodes.Forbidden, string.Join(" | ", chain.Select(e => e.Message)));
    }

    // ---- data -----------------------------------------------------------------------------------------------

    private async Task<OctoObjectId> SeedAsync(string tenantId, RtCkId<CkTypeId> type,
        params (string Name, object Value)[] attributes)
    {
        fixture.EnsureInitialized();
        var repository = await fixture.GetSystemContext().FindTenantRepositoryAsync(tenantId);
        var entity = new RtEntity(type, OctoObjectId.GenerateNewId());
        foreach (var (name, value) in attributes)
        {
            entity.SetAttributeRawValue(name, value);
        }

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var result = new OperationResult();
        await repository.ApplyChangesAsync(session, [EntityUpdateInfo<RtEntity>.CreateInsert(entity)], result);
        Assert.False(result.HasErrors || result.HasFatalErrors, result.GetMessages());
        await session.CommitTransactionAsync();
        return entity.RtId;
    }

    private Task<OctoObjectId> SeedInvoiceInAAsync(string number, string recipient, string status = "Released") =>
        SeedAsync(TenantSubtreeFixture.TenantA, InvoiceType, ("InvoiceNumber", number), ("InvoiceAmount", 1250.5),
            ("RecipientTenantId", recipient), ("InvoiceStatus", status));

    private Task<OctoObjectId> SeedDocumentInBAsync(string number) =>
        SeedAsync(TenantSubtreeFixture.TenantB, DocumentType, ("InvoiceNumber", number), ("InvoiceAmount", 10.0),
            ("SourceTenantId", TenantSubtreeFixture.TenantA), ("SourceInvoiceRtId", "seeded"));

    /// <summary>Every entity of <paramref name="type" /> in <paramref name="tenantId" />, read directly (system session).</summary>
    private async Task<List<RtEntity>> AllAsync(string tenantId, RtCkId<CkTypeId> type)
    {
        var repository = await fixture.GetSystemContext().FindTenantRepositoryAsync(tenantId);
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var result = await repository.GetRtEntitiesByTypeAsync(session, type, RtEntityQueryOptions.Create());
        await session.CommitTransactionAsync();
        return result.Items.ToList();
    }

    private async Task<List<RtEntity>> DocumentsInAsync(string tenantId, string number) =>
        (await AllAsync(tenantId, DocumentType))
        .Where(d => Equals(Text(d, "InvoiceNumber"), number)).ToList();

    /// <summary>A snapshot of company B's incoming documents (id, number, amount) to prove a refusal changed nothing.</summary>
    private async Task<string> SnapshotOfBAsync() =>
        string.Join(";", (await AllAsync(TenantSubtreeFixture.TenantB, DocumentType))
            .OrderBy(d => d.RtId.ToString())
            .Select(d => $"{d.RtId}|{Text(d, "InvoiceNumber")}|" +
                         $"{d.GetAttributeValueOrDefault<double>("InvoiceAmount")}"));

    private static string NewNumber() => $"IC-{Guid.NewGuid():N}";

    /// <summary>A string attribute as stored (its string form; <c>null</c> when absent).</summary>
    private static string? Text(RtEntity entity, string attributeName) =>
        entity.Attributes.TryGetValue(attributeName, out var value) ? value?.ToString() : null;

    // ---- pipelines ------------------------------------------------------------------------------------------

    /// <summary>
    ///     Company A's release pipeline: reads its released invoice by number (own tenant) and creates the incoming
    ///     document in the recipient the invoice names (Sibling, tenant id from the data), with the invoice data and a
    ///     reference to A's invoice.
    /// </summary>
    private const string RecipientFromTheInvoice =
        "targetTenant:\n  relation: Sibling\n  tenantIdPath: $.invoice.Items[0].Attributes.RecipientTenantId";

    private static string ReleasePipeline(string identity = "Caller", string target = RecipientFromTheInvoice) => $"""
        transformations:
          - type: GetRtEntitiesByType@1
            ckTypeId: {Model}/InterCompanyInvoice
            targetPath: $.invoice
            fieldFilters:
              - attributePath: InvoiceNumber
                operator: Equals
                comparisonValuePath: $.invoiceNumber
              - attributePath: InvoiceStatus
                operator: Equals
                comparisonValue: Released
          - type: CreateUpdateInfo@1
            targetPath: $._inserts
            targetValueKind: Array
            updateKind: INSERT
            ckTypeId: {Model}/IncomingDocument
            generateRtId: true
            attributeUpdates:
              - attributeName: InvoiceNumber
                attributeValueType: String
                valuePath: $.invoice.Items[0].Attributes.InvoiceNumber
              - attributeName: InvoiceAmount
                attributeValueType: Double
                valuePath: $.invoice.Items[0].Attributes.InvoiceAmount
              - attributeName: SourceTenantId
                attributeValueType: String
                valuePath: $.sourceTenantId
              - attributeName: SourceInvoiceRtId
                attributeValueType: String
                valuePath: $.invoice.Items[0].RtId
          - type: ApplyChanges@2
            entityUpdatesPath: $._inserts
            identity: {identity}
        {Indent(target)}
        """;

    /// <summary>An update (new amount) or a delete of an incoming document in <paramref name="target" />.</summary>
    private static string ChangePipeline(string updateKind, string target, string identity = "Caller") => $"""
        transformations:
          - type: CreateUpdateInfo@1
            targetPath: $._changes
            targetValueKind: Array
            updateKind: {updateKind}
            ckTypeId: {Model}/IncomingDocument
            rtIdPath: $.documentRtId
            attributeUpdates:
              - attributeName: InvoiceAmount
                attributeValueType: Double
                valuePath: $.amount
          - type: ApplyChanges@2
            entityUpdatesPath: $._changes
            identity: {identity}
        {Indent(target)}
        """;

    /// <summary>Indents a target-tenant block to the level of the ApplyChanges@2 properties.</summary>
    private static string Indent(string block) =>
        string.Join("\n", block.Split('\n').Select(l => l.Length == 0 ? l : "    " + l));

    private static string SiblingB => $"targetTenant:\n  relation: Sibling\n  tenantId: {TenantSubtreeFixture.TenantB}";

    private static string Target(string relation, string? tenantId) =>
        tenantId == null
            ? $"targetTenant:\n  relation: {relation}"
            : $"targetTenant:\n  relation: {relation}\n  tenantId: {tenantId}";

    private static JsonObject ReleaseInput(string number) => new()
    {
        ["invoiceNumber"] = number, ["sourceTenantId"] = TenantSubtreeFixture.TenantA
    };

    private static JsonObject ChangeInput(OctoObjectId rtId, double amount = 99.5) => new()
    {
        ["documentRtId"] = rtId.ToString(), ["amount"] = amount
    };

    // ---- AC 1: released invoice in A -> incoming document in B; update and delete visible in B -----------------

    [Theory]
    [InlineData("Caller", CrossTenantSubjectKind.User)]
    [InlineData("ServiceAccount", CrossTenantSubjectKind.ServiceAccount)]
    public async Task ReleasedInvoiceInA_AppearsAsIncomingDocumentInB_ThenUpdateAndDeleteAreVisibleInB(
        string identity, CrossTenantSubjectKind kind)
    {
        _grants.Add((TenantSubtreeFixture.TenantB, kind));
        var number = NewNumber();
        var invoiceRtId = await SeedInvoiceInAAsync(number, TenantSubtreeFixture.TenantB);

        // A service account names its target statically; only a verified caller may take it from the invoice (AB#9767).
        var target = identity == "ServiceAccount" ? SiblingB : RecipientFromTheInvoice;
        await RunAsync(TenantSubtreeFixture.TenantA, ReleasePipeline(identity, target), ReleaseInput(number));

        var document = Assert.Single(await DocumentsInAsync(TenantSubtreeFixture.TenantB, number));
        Assert.Equal(1250.5, document.GetAttributeValueOrDefault<double>("InvoiceAmount"));
        Assert.Equal(TenantSubtreeFixture.TenantA, Text(document, "SourceTenantId"));
        Assert.Equal(invoiceRtId.ToString(), Text(document, "SourceInvoiceRtId"));
        // Written in B only: company A has no incoming document of its own invoice.
        Assert.Empty(await DocumentsInAsync(TenantSubtreeFixture.TenantA, number));

        await RunAsync(TenantSubtreeFixture.TenantA, ChangePipeline("UPDATE", SiblingB, identity),
            ChangeInput(document.RtId, 980.25));
        var updated = Assert.Single(await DocumentsInAsync(TenantSubtreeFixture.TenantB, number));
        Assert.Equal(980.25, updated.GetAttributeValueOrDefault<double>("InvoiceAmount"));

        await RunAsync(TenantSubtreeFixture.TenantA, ChangePipeline("DELETE", SiblingB, identity),
            ChangeInput(document.RtId));
        Assert.Empty(await DocumentsInAsync(TenantSubtreeFixture.TenantB, number));
    }

    [Fact]
    public async Task DraftInvoice_IsNotReleased_NothingIsWritten()
    {
        _grants.Add((TenantSubtreeFixture.TenantB, CrossTenantSubjectKind.User));
        var number = NewNumber();
        await SeedInvoiceInAAsync(number, TenantSubtreeFixture.TenantB, status: "Draft");
        var before = await SnapshotOfBAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            RunAsync(TenantSubtreeFixture.TenantA, ReleasePipeline(), ReleaseInput(number)));

        Assert.Equal(before, await SnapshotOfBAsync());
    }

    // ---- AC 2: without a grant in B, create, update and delete are refused and B stays unchanged ---------------

    [Fact]
    public async Task WithoutAGrantInB_CreateUpdateAndDelete_AreRefused_AndBIsUnchanged()
    {
        var number = NewNumber();
        await SeedInvoiceInAAsync(number, TenantSubtreeFixture.TenantB);
        var existing = await SeedDocumentInBAsync(NewNumber());
        var before = await SnapshotOfBAsync();

        await RefusedAsync(TenantSubtreeFixture.TenantA, ReleasePipeline(), ReleaseInput(number));
        await RefusedAsync(TenantSubtreeFixture.TenantA, ChangePipeline("UPDATE", SiblingB), ChangeInput(existing));
        await RefusedAsync(TenantSubtreeFixture.TenantA, ChangePipeline("DELETE", SiblingB), ChangeInput(existing));

        Assert.Equal(before, await SnapshotOfBAsync());
    }

    [Fact]
    public async Task ServiceAccount_WithTheRecipientFromTheInvoice_IsRefused_EvenWithAGrant()
    {
        // AB#9767: the data must not choose the tenant a service account acts in.
        _grants.Add((TenantSubtreeFixture.TenantB, CrossTenantSubjectKind.ServiceAccount));
        var number = NewNumber();
        await SeedInvoiceInAAsync(number, TenantSubtreeFixture.TenantB);
        var before = await SnapshotOfBAsync();

        await RefusedAsync(TenantSubtreeFixture.TenantA, ReleasePipeline("ServiceAccount"), ReleaseInput(number));

        Assert.Equal(before, await SnapshotOfBAsync());
    }

    [Fact]
    public async Task AGrantForTheServiceAccount_DoesNotLetTheCallerWrite()
    {
        _grants.Add((TenantSubtreeFixture.TenantB, CrossTenantSubjectKind.ServiceAccount));
        var number = NewNumber();
        await SeedInvoiceInAAsync(number, TenantSubtreeFixture.TenantB);
        var before = await SnapshotOfBAsync();

        await RefusedAsync(TenantSubtreeFixture.TenantA, ReleasePipeline("Caller"), ReleaseInput(number));

        Assert.Equal(before, await SnapshotOfBAsync());
    }

    // ---- AC 3: without the setting the node writes its own tenant as before ------------------------------------

    [Fact]
    public async Task WithoutTarget_ApplyChangesV2_WritesItsOwnTenant()
    {
        var number = NewNumber();
        await SeedInvoiceInAAsync(number, TenantSubtreeFixture.TenantB);
        var before = await SnapshotOfBAsync();

        await RunAsync(TenantSubtreeFixture.TenantA, ReleasePipeline(target: ""), ReleaseInput(number));

        Assert.Single(await DocumentsInAsync(TenantSubtreeFixture.TenantA, number));
        Assert.Equal(before, await SnapshotOfBAsync());
    }

    // ---- B's own write guards hold cross-tenant at least as strictly -------------------------------------------

    private static string InsertPipeline(string type, string attribute, string target) => $"""
        transformations:
          - type: CreateUpdateInfo@1
            targetPath: $._inserts
            targetValueKind: Array
            updateKind: INSERT
            ckTypeId: {Model}/{type}
            generateRtId: true
            attributeUpdates:
              - attributeName: {attribute}
                attributeValueType: String
                valuePath: $.value
          - type: ApplyChanges@2
            entityUpdatesPath: $._inserts
        {Indent(target)}
        """;

    /// <summary>Runs and expects the execution to fail with <paramref name="refusal" /> somewhere in the chain.</summary>
    private async Task RefusedByAsync(string refusal, string sourceTenantId, string yaml)
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            RunAsync(sourceTenantId, yaml, new JsonObject { ["value"] = "written-from-outside" }));
        var chain = new List<string>();
        for (var e = exception; e != null; e = e.InnerException)
        {
            chain.Add(e.GetType().Name);
        }

        Assert.Contains(refusal, chain);
    }

    [Theory]
    // a Hidden attribute of B's CK model (SensorReading.ReadingSecret): no write outside a method run
    [InlineData("SensorReading", "ReadingSecret", nameof(RtAttributeWriteRestrictedException))]
    // a type whose generic create B's CK model replaces by a method (ReplacedAccount, Provision-1)
    [InlineData("ReplacedAccount", "AccountName", nameof(GenericCreateReplacedByMethodException))]
    public async Task AWriteBsOwnGuardsRefuse_IsRefusedCrossTenantAsWell_AndBIsUnchanged(string type,
        string attribute, string refusal)
    {
        _grants.Add((TenantSubtreeFixture.TenantB, CrossTenantSubjectKind.User));
        var ckTypeId = new RtCkId<CkTypeId>($"{Model}/{type}");
        var before = (await AllAsync(TenantSubtreeFixture.TenantB, ckTypeId)).Count;

        // B's own pipeline (no target, no method run) is refused ...
        await RefusedByAsync(refusal, TenantSubtreeFixture.TenantB, InsertPipeline(type, attribute, ""));
        // ... and so is A's pipeline writing the same into B, with a valid grant in B.
        await RefusedByAsync(refusal, TenantSubtreeFixture.TenantA, InsertPipeline(type, attribute, SiblingB));

        Assert.Equal(before, (await AllAsync(TenantSubtreeFixture.TenantB, ckTypeId)).Count);
    }

    // ---- abuse cases ---------------------------------------------------------------------------------------------

    [Theory]
    // a grandchild is no child
    [InlineData(TenantSubtreeFixture.Group, "Child", TenantSubtreeFixture.GrandChild)]
    // a cousin (a tenant of another group) is no sibling
    [InlineData(TenantSubtreeFixture.TenantA, "Sibling", TenantSubtreeFixture.Outsider)]
    // the sibling named as a child (wrong relation)
    [InlineData(TenantSubtreeFixture.TenantA, "Child", TenantSubtreeFixture.TenantB)]
    // the parent named as a sibling (wrong relation)
    [InlineData(TenantSubtreeFixture.TenantA, "Sibling", TenantSubtreeFixture.Group)]
    // a detached child is no child
    [InlineData(TenantSubtreeFixture.Group, "Child", TenantSubtreeFixture.Detached)]
    // the system tenant is never a target
    [InlineData(TenantSubtreeFixture.Group, "Parent", null)]
    [InlineData(TenantSubtreeFixture.TenantA, "Sibling", SystemTenant)]
    public async Task WritesOutsideTheRelation_AreRefused_EvenWithAGrant(string source, string relation,
        string? target)
    {
        if (target == SystemTenant)
        {
            target = fixture.GetService<IOptions<OctoSystemConfiguration>>().Value.SystemTenantId;
        }

        if (target != null)
        {
            _grants.Add((target, CrossTenantSubjectKind.User));
        }

        _grants.Add((TenantSubtreeFixture.Group, CrossTenantSubjectKind.User));
        var existing = await SeedDocumentInBAsync(NewNumber());
        var before = await SnapshotOfBAsync();

        await RefusedAsync(source, ChangePipeline("UPDATE", Target(relation, target)), ChangeInput(existing));

        Assert.Equal(before, await SnapshotOfBAsync());
    }

    [Fact]
    public async Task MissingRelation_IsRefused_AsAConfigurationError()
    {
        _grants.Add((TenantSubtreeFixture.TenantB, CrossTenantSubjectKind.User));
        var existing = await SeedDocumentInBAsync(NewNumber());
        var before = await SnapshotOfBAsync();

        await RefusedAsync(TenantSubtreeFixture.TenantA,
            ChangePipeline("UPDATE", $"targetTenant:\n  tenantId: {TenantSubtreeFixture.TenantB}"),
            ChangeInput(existing));

        Assert.Equal(before, await SnapshotOfBAsync());
    }

    [Fact]
    public async Task IdentityProvenOnlyInTheSourceTenant_IsRefused()
    {
        _grants.Add((TenantSubtreeFixture.TenantB, CrossTenantSubjectKind.User));
        _proofForTheSourceTenant = true;
        var existing = await SeedDocumentInBAsync(NewNumber());
        var before = await SnapshotOfBAsync();

        await RefusedAsync(TenantSubtreeFixture.TenantA, ChangePipeline("UPDATE", SiblingB), ChangeInput(existing));

        Assert.Equal(before, await SnapshotOfBAsync());
    }

    [Fact]
    public async Task ProofOfAnEarlierGenerationOfB_IsRefused()
    {
        _grants.Add((TenantSubtreeFixture.TenantB, CrossTenantSubjectKind.User));
        _staleGeneration = true;
        var existing = await SeedDocumentInBAsync(NewNumber());
        var before = await SnapshotOfBAsync();

        await RefusedAsync(TenantSubtreeFixture.TenantA, ChangePipeline("DELETE", SiblingB), ChangeInput(existing));

        Assert.Equal(before, await SnapshotOfBAsync());
    }

    [Fact]
    public async Task SystemIdentity_IsRefused_EvenWithAGrant()
    {
        _grants.Add((TenantSubtreeFixture.TenantB, CrossTenantSubjectKind.User));
        _grants.Add((TenantSubtreeFixture.TenantB, CrossTenantSubjectKind.ServiceAccount));
        var number = NewNumber();
        await SeedInvoiceInAAsync(number, TenantSubtreeFixture.TenantB);
        var before = await SnapshotOfBAsync();

        await RefusedAsync(TenantSubtreeFixture.TenantA, ReleasePipeline("System"), ReleaseInput(number));

        Assert.Equal(before, await SnapshotOfBAsync());
    }

    [Fact]
    public async Task ADataDrivenRecipientOutsideTheRelation_IsRefused()
    {
        _grants.Add((TenantSubtreeFixture.Outsider, CrossTenantSubjectKind.User));
        var number = NewNumber();
        await SeedInvoiceInAAsync(number, TenantSubtreeFixture.Outsider);
        var outsiderBefore = (await AllAsync(TenantSubtreeFixture.Outsider, DocumentType)).Count;

        await RefusedAsync(TenantSubtreeFixture.TenantA, ReleasePipeline(), ReleaseInput(number));

        Assert.Equal(outsiderBefore, (await AllAsync(TenantSubtreeFixture.Outsider, DocumentType)).Count);
    }

    [Fact]
    public async Task ApplyChangesV1_WithATarget_DoesNotLoad_AndNothingIsWritten()
    {
        _grants.Add((TenantSubtreeFixture.TenantB, CrossTenantSubjectKind.User));
        var before = await SnapshotOfBAsync();
        var yaml = $"""
            transformations:
              - type: ApplyChanges@1
                path: $.updates
                targetTenant:
                  relation: Sibling
                  tenantId: {TenantSubtreeFixture.TenantB}
            """;

        await Assert.ThrowsAsync<PipelineSerializationException>(() =>
            RunAsync(TenantSubtreeFixture.TenantA, yaml, new JsonObject()));

        Assert.Equal(before, await SnapshotOfBAsync());
    }
}
