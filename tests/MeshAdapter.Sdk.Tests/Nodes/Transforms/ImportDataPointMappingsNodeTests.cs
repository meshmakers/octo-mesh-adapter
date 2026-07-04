using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class ImportDataPointMappingsNodeTests : NodeTestBase
{
    private static readonly RtCkId<CkTypeId> ControlType = new("Loxone/Control");
    private static readonly RtCkId<CkTypeId> SpaceType = new("EnergyIQ/Space");

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly ITenantRepository _tenantRepository = A.Fake<ITenantRepository>();
    private readonly IOctoSession _session = A.Fake<IOctoSession>();

    public ImportDataPointMappingsNodeTests()
    {
        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(_session));
    }

    [Fact]
    public async Task ProcessObjectAsync_ResolvesViaIdentityAttributeAfterTenantReInitialisation()
    {
        // The export was taken on a tenant whose control had rtId ...dead01. After
        // om_initialize_tenant, the control was re-imported with a NEW rtId but the
        // same LoxoneUuid — the identity attribute must win.
        var control = CreateEntity(ControlType, "aaaa01",
            ("Name", "Raumregler WZ"), ("LoxoneUuid", "0f2e-uuid"));
        var space = CreateEntity(SpaceType, "5cace1",
            ("Name", "Wohnzimmer"), ("GlobalId", "space-eg-wz"));

        SetupGetByType(ControlType, control);
        SetupGetByType(SpaceType, space);

        var document = Document(
            ExportedMappingEntry(
                "WZ temp", enabled: false,
                source: new ExportedEntityRef("Loxone/Control", Pad("dead01"), "Raumregler WZ", "LoxoneUuid", "0f2e-uuid"),
                target: new ExportedEntityRef("EnergyIQ/Space", Pad("dead02"), "Wohnzimmer", "GlobalId", "space-eg-wz")));

        var config = new ImportDataPointMappingsNodeConfiguration
        {
            Path = "$.importDocument",
            TargetPath = "$.mappingSuggestions",
            StatisticsTargetPath = "$.importStatistics",
        };

        var (dataContext, nodeContext, next, captured) = PrepareTestWithCapture(config, document);
        var node = new ImportDataPointMappingsNode(next, _etlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var suggestion = Assert.Single(captured.Suggestions!);
        Assert.Equal("WZ temp", suggestion.Name);
        Assert.Equal(control.RtId.ToString(), suggestion.ControlRtId);
        Assert.Equal("Loxone/Control", suggestion.ControlCkTypeId);
        Assert.Equal(space.RtId.ToString(), suggestion.SpaceRtId);
        Assert.Equal("EnergyIQ/Space", suggestion.SpaceCkTypeId);
        Assert.Equal("import", suggestion.RuleId);
        Assert.False(suggestion.Enabled);
        Assert.Contains("LoxoneUuid", suggestion.Reason);

        Assert.NotNull(captured.Statistics);
        Assert.Equal(1, captured.Statistics!.Total);
        Assert.Equal(1, captured.Statistics.Resolved);
        Assert.Equal(0, captured.Statistics.Unresolved);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_PrefersRtIdWhenStillValid()
    {
        var control = CreateEntity(ControlType, "aaaa01", ("Name", "Raumregler WZ"));
        var space = CreateEntity(SpaceType, "5cace1", ("Name", "Wohnzimmer"));

        SetupGetByType(ControlType, control);
        SetupGetByType(SpaceType, space);

        var document = Document(
            ExportedMappingEntry(
                "WZ temp", enabled: true,
                source: new ExportedEntityRef("Loxone/Control", control.RtId.ToString(), null, null, null),
                target: new ExportedEntityRef("EnergyIQ/Space", space.RtId.ToString(), null, null, null)));

        var config = new ImportDataPointMappingsNodeConfiguration
        {
            Path = "$.importDocument",
            TargetPath = "$.mappingSuggestions",
        };

        var (dataContext, nodeContext, next, captured) = PrepareTestWithCapture(config, document);
        var node = new ImportDataPointMappingsNode(next, _etlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var suggestion = Assert.Single(captured.Suggestions!);
        Assert.Contains("rtId", suggestion.Reason);
    }

    [Fact]
    public async Task ProcessObjectAsync_FallsBackToUniqueName()
    {
        var control = CreateEntity(ControlType, "aaaa01", ("Name", "Raumregler WZ"));
        var space = CreateEntity(SpaceType, "5cace1", ("Name", "Wohnzimmer"));

        SetupGetByType(ControlType, control);
        SetupGetByType(SpaceType, space);

        var document = Document(
            ExportedMappingEntry(
                "WZ temp", enabled: true,
                source: new ExportedEntityRef("Loxone/Control", Pad("dead01"), "Raumregler WZ", null, null),
                target: new ExportedEntityRef("EnergyIQ/Space", Pad("dead02"), "Wohnzimmer", null, null)));

        var config = new ImportDataPointMappingsNodeConfiguration
        {
            Path = "$.importDocument",
            TargetPath = "$.mappingSuggestions",
        };

        var (dataContext, nodeContext, next, captured) = PrepareTestWithCapture(config, document);
        var node = new ImportDataPointMappingsNode(next, _etlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var suggestion = Assert.Single(captured.Suggestions!);
        Assert.Contains("name='Raumregler WZ'", suggestion.Reason);
    }

    [Fact]
    public async Task ProcessObjectAsync_AmbiguousName_ReportsUnresolvedInsteadOfGuessing()
    {
        var control1 = CreateEntity(ControlType, "aaaa01", ("Name", "Meter"));
        var control2 = CreateEntity(ControlType, "aaaa02", ("Name", "Meter"));
        var space = CreateEntity(SpaceType, "5cace1", ("Name", "Wohnzimmer"));

        SetupGetByType(ControlType, control1, control2);
        SetupGetByType(SpaceType, space);

        var document = Document(
            ExportedMappingEntry(
                "Meter mapping", enabled: true,
                source: new ExportedEntityRef("Loxone/Control", Pad("dead01"), "Meter", null, null),
                target: new ExportedEntityRef("EnergyIQ/Space", Pad("dead02"), "Wohnzimmer", null, null)));

        var config = new ImportDataPointMappingsNodeConfiguration
        {
            Path = "$.importDocument",
            TargetPath = "$.mappingSuggestions",
            StatisticsTargetPath = "$.importStatistics",
        };

        var (dataContext, nodeContext, next, captured) = PrepareTestWithCapture(config, document);
        var node = new ImportDataPointMappingsNode(next, _etlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Empty(captured.Suggestions!);
        Assert.NotNull(captured.Statistics);
        Assert.Equal(1, captured.Statistics!.Unresolved);
        var entry = Assert.Single(captured.Statistics.UnresolvedEntries);
        Assert.Equal("Meter mapping", entry.Name);
        Assert.Contains("ambiguous", entry.Reason);
    }

    [Fact]
    public async Task ProcessObjectAsync_MissingDocument_WritesEmptyResultAndContinues()
    {
        var config = new ImportDataPointMappingsNodeConfiguration
        {
            Path = "$.importDocument",
            TargetPath = "$.mappingSuggestions",
            StatisticsTargetPath = "$.importStatistics",
        };

        var (dataContext, nodeContext, next, captured) = PrepareTestWithCapture(config, null);
        var node = new ImportDataPointMappingsNode(next, _etlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(captured.Suggestions);
        Assert.Empty(captured.Suggestions!);
        Assert.Equal(0, captured.Statistics!.Total);
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    // ───────────────────────────── Test setup helpers ─────────────────────────────

    private static DataPointMappingExportDocument Document(params ExportedMapping[] mappings) =>
        new(1, "System.Communication/DataPointMapping", mappings);

    private static ExportedMapping ExportedMappingEntry(string name, bool enabled,
        ExportedEntityRef? source, ExportedEntityRef? target) =>
        new(name, enabled, "tempActual", "Temperature", "value", source, target);

    private static RtEntity CreateEntity(RtCkId<CkTypeId> ckTypeId, string rtId,
        params (string name, object? value)[] attributes)
    {
        var entity = new RtEntity(ckTypeId, new OctoObjectId(Pad(rtId)));
        foreach (var (name, value) in attributes)
        {
            entity.SetAttributeRawValue(name, value);
        }
        return entity;
    }

    private static string Pad(string id)
    {
        return id.Length >= 24 ? id : id.PadLeft(24, '0');
    }

    private void SetupGetByType(RtCkId<CkTypeId> ckTypeId, params RtEntity[] entities)
    {
        var resultSet = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => resultSet.Items).Returns(entities.ToList());
        A.CallTo(() => resultSet.TotalCount).Returns(entities.Length);
        A.CallTo(() => _tenantRepository.GetRtEntitiesByTypeAsync(
                A<IOctoSession>._,
                ckTypeId,
                A<RtEntityQueryOptions>._,
                A<int?>._,
                A<int?>._))
            .Returns(resultSet);
    }

    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next, CapturedResults Captured)
        PrepareTestWithCapture(ImportDataPointMappingsNodeConfiguration config,
            DataPointMappingExportDocument? document)
    {
        var (dataContext, nodeContext, next) = PrepareTest<ImportDataPointMappingsNodeConfiguration>(config);

        A.CallTo(() => dataContext.Get<DataPointMappingExportDocument>(config.Path))
            .Returns(document);

        var captured = new CapturedResults();
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set)
                && call.Arguments.Count >= 2
                && (call.Arguments[0] as string) == config.TargetPath
                && call.Arguments[1] is List<ImportDataPointMappingsNode.ImportedMappingSuggestion>)
            .Invokes(call =>
                captured.Suggestions =
                    (List<ImportDataPointMappingsNode.ImportedMappingSuggestion>)call.Arguments[1]!);
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set)
                && call.Arguments.Count >= 2
                && (call.Arguments[0] as string) == config.StatisticsTargetPath
                && call.Arguments[1] is ImportDataPointMappingsNode.ImportStatistics)
            .Invokes(call =>
                captured.Statistics = (ImportDataPointMappingsNode.ImportStatistics)call.Arguments[1]!);

        return (dataContext, nodeContext, next, captured);
    }

    private sealed class CapturedResults
    {
        public List<ImportDataPointMappingsNode.ImportedMappingSuggestion>? Suggestions { get; set; }
        public ImportDataPointMappingsNode.ImportStatistics? Statistics { get; set; }
    }
}
