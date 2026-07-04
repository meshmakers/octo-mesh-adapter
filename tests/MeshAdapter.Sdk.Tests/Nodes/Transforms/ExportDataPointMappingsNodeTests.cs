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

public class ExportDataPointMappingsNodeTests : NodeTestBase
{
    private static readonly RtCkId<CkTypeId> MappingType = new("System.Communication/DataPointMapping");
    private static readonly RtCkId<CkTypeId> ControlType = new("Loxone/Control");
    private static readonly RtCkId<CkTypeId> SpaceType = new("EnergyIQ/Space");
    private static readonly RtCkId<CkAssociationRoleId> MapsFrom = new("System.Communication/MapsFrom");
    private static readonly RtCkId<CkAssociationRoleId> MapsTo = new("System.Communication/MapsTo");

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly ITenantRepository _tenantRepository = A.Fake<ITenantRepository>();
    private readonly IOctoSession _session = A.Fake<IOctoSession>();

    public ExportDataPointMappingsNodeTests()
    {
        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(_session));
    }

    [Fact]
    public async Task ProcessObjectAsync_ExportsMappingWithIdentityAttributesAndNames()
    {
        var control = CreateEntity(ControlType, "c0c001",
            ("Name", "Raumregler WZ"), ("LoxoneUuid", "0f2e-uuid"));
        var space = CreateEntity(SpaceType, "5cace1",
            ("Name", "Wohnzimmer"), ("GlobalId", "space-eg-wz"));
        var mapping = CreateEntity(MappingType, "3a9001",
            ("Name", "WZ temp"), ("Enabled", true),
            ("SourceAttributePath", "tempActual"),
            ("TargetAttributePath", "Temperature"),
            ("MappingExpression", "value"));

        SetupGetByType(MappingType, mapping);
        SetupOutboundAssoc(mapping, MapsFrom, control);
        SetupOutboundAssoc(mapping, MapsTo, space);

        var config = new ExportDataPointMappingsNodeConfiguration
        {
            TargetPath = "$.export",
            IdentityAttributes = new List<EntityIdentityAttributeConfiguration>
            {
                new() { CkTypeId = "Loxone/Control", Attribute = "LoxoneUuid" },
                new() { CkTypeId = "EnergyIQ/Space", Attribute = "GlobalId" },
            },
        };

        var (dataContext, nodeContext, next, captured) = PrepareTestWithCapture(config);
        var node = new ExportDataPointMappingsNode(next, _etlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(captured.Value);
        var document = captured.Value!;
        Assert.Equal(1, document.Version);
        var exported = Assert.Single(document.Mappings);
        Assert.Equal("WZ temp", exported.Name);
        Assert.True(exported.Enabled);
        Assert.Equal("tempActual", exported.SourceAttributePath);
        Assert.Equal("Temperature", exported.TargetAttributePath);
        Assert.Equal("value", exported.MappingExpression);

        Assert.NotNull(exported.Source);
        Assert.Equal("Loxone/Control", exported.Source!.CkTypeId);
        Assert.Equal(control.RtId.ToString(), exported.Source.RtId);
        Assert.Equal("Raumregler WZ", exported.Source.Name);
        Assert.Equal("LoxoneUuid", exported.Source.IdentityAttribute);
        Assert.Equal("0f2e-uuid", exported.Source.IdentityValue);

        Assert.NotNull(exported.Target);
        Assert.Equal("EnergyIQ/Space", exported.Target!.CkTypeId);
        Assert.Equal("GlobalId", exported.Target.IdentityAttribute);
        Assert.Equal("space-eg-wz", exported.Target.IdentityValue);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_ExportsNameOnlyIdentityWhenTypeHasNoConfiguredAttribute()
    {
        var control = CreateEntity(ControlType, "c0c001", ("Name", "Raumregler WZ"));
        var mapping = CreateEntity(MappingType, "3a9001", ("Name", "WZ temp"), ("Enabled", true));

        SetupGetByType(MappingType, mapping);
        SetupOutboundAssoc(mapping, MapsFrom, control);
        SetupNoOutboundAssoc(mapping, MapsTo);

        var config = new ExportDataPointMappingsNodeConfiguration { TargetPath = "$.export" };

        var (dataContext, nodeContext, next, captured) = PrepareTestWithCapture(config);
        var node = new ExportDataPointMappingsNode(next, _etlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var exported = Assert.Single(captured.Value!.Mappings);
        Assert.NotNull(exported.Source);
        Assert.Null(exported.Source!.IdentityAttribute);
        Assert.Null(exported.Source.IdentityValue);
        Assert.Equal("Raumregler WZ", exported.Source.Name);
        // Missing MapsTo association is exported as null so the import can report it.
        Assert.Null(exported.Target);
    }

    [Fact]
    public async Task ProcessObjectAsync_ExcludeNameRegex_SkipsRuleGeneratedMappings()
    {
        var manual = CreateEntity(MappingType, "3a9001", ("Name", "Handgemacht"), ("Enabled", true));
        var generated = CreateEntity(MappingType, "3a9002",
            ("Name", "rc-tempActual|0000000000000000000c0c01|tempActual"), ("Enabled", true));

        SetupGetByType(MappingType, manual, generated);
        SetupNoOutboundAssoc(manual, MapsFrom);
        SetupNoOutboundAssoc(manual, MapsTo);

        var config = new ExportDataPointMappingsNodeConfiguration
        {
            TargetPath = "$.export",
            ExcludeNameRegex = @"^[\w-]+\|[0-9a-f]{24}\|",
        };

        var (dataContext, nodeContext, next, captured) = PrepareTestWithCapture(config);
        var node = new ExportDataPointMappingsNode(next, _etlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var exported = Assert.Single(captured.Value!.Mappings);
        Assert.Equal("Handgemacht", exported.Name);
    }

    [Fact]
    public async Task ProcessObjectAsync_IncludeDisabledFalse_SkipsDisabledMappings()
    {
        var enabled = CreateEntity(MappingType, "3a9001", ("Name", "On"), ("Enabled", true));
        var disabled = CreateEntity(MappingType, "3a9002", ("Name", "Off"), ("Enabled", false));

        SetupGetByType(MappingType, enabled, disabled);
        SetupNoOutboundAssoc(enabled, MapsFrom);
        SetupNoOutboundAssoc(enabled, MapsTo);

        var config = new ExportDataPointMappingsNodeConfiguration
        {
            TargetPath = "$.export",
            IncludeDisabled = false,
        };

        var (dataContext, nodeContext, next, captured) = PrepareTestWithCapture(config);
        var node = new ExportDataPointMappingsNode(next, _etlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var exported = Assert.Single(captured.Value!.Mappings);
        Assert.Equal("On", exported.Name);
    }

    // ───────────────────────────── Test setup helpers ─────────────────────────────

    private static RtEntity CreateEntity(RtCkId<CkTypeId> ckTypeId, string rtId,
        params (string name, object? value)[] attributes)
    {
        var entity = new RtEntity(ckTypeId, new OctoObjectId(PadRtId(rtId)));
        foreach (var (name, value) in attributes)
        {
            entity.SetAttributeRawValue(name, value);
        }
        return entity;
    }

    private static string PadRtId(string id)
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

    private void SetupOutboundAssoc(RtEntity mapping, RtCkId<CkAssociationRoleId> roleId, RtEntity endpoint)
    {
        var assocSet = A.Fake<IResultSet<RtAssociation>>();
        var assocs = new List<RtAssociation>
        {
            new()
            {
                OriginRtId = mapping.RtId,
                OriginCkTypeId = mapping.CkTypeId!,
                TargetRtId = endpoint.RtId,
                TargetCkTypeId = endpoint.CkTypeId!,
                AssociationRoleId = roleId,
            },
        };
        A.CallTo(() => assocSet.Items).Returns(assocs);
        A.CallTo(() => assocSet.TotalCount).Returns(1);

        A.CallTo(() => _tenantRepository.GetRtAssociationsAsync(
                A<IOctoSession>._,
                A<RtEntityId>.That.Matches(eid => eid.RtId.Equals(mapping.RtId)),
                A<RtAssociationExtendedQueryOptions>.That.Matches(opts => Equals(opts.RoleId, roleId))))
            .Returns(assocSet);

        var endpointSet = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => endpointSet.Items).Returns(new List<RtEntity> { endpoint });
        A.CallTo(() => endpointSet.TotalCount).Returns(1);
        A.CallTo(() => _tenantRepository.GetRtEntitiesByIdAsync(
                A<IOctoSession>._,
                endpoint.CkTypeId!,
                A<IReadOnlyList<OctoObjectId>>.That.Matches(ids => ids.Contains(endpoint.RtId)),
                A<RtEntityQueryOptions>._,
                A<int?>._,
                A<int?>._))
            .Returns(endpointSet);
    }

    private void SetupNoOutboundAssoc(RtEntity mapping, RtCkId<CkAssociationRoleId> roleId)
    {
        var assocSet = A.Fake<IResultSet<RtAssociation>>();
        A.CallTo(() => assocSet.Items).Returns(new List<RtAssociation>());
        A.CallTo(() => assocSet.TotalCount).Returns(0);
        A.CallTo(() => _tenantRepository.GetRtAssociationsAsync(
                A<IOctoSession>._,
                A<RtEntityId>.That.Matches(eid => eid.RtId.Equals(mapping.RtId)),
                A<RtAssociationExtendedQueryOptions>.That.Matches(opts => Equals(opts.RoleId, roleId))))
            .Returns(assocSet);
    }

    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next, CapturedDocument Captured)
        PrepareTestWithCapture(ExportDataPointMappingsNodeConfiguration config)
    {
        var (dataContext, nodeContext, next) = PrepareTest<ExportDataPointMappingsNodeConfiguration>(config);

        var captured = new CapturedDocument();
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set)
                && call.Arguments.Count >= 2
                && (call.Arguments[0] as string) == config.TargetPath
                && call.Arguments[1] is DataPointMappingExportDocument)
            .Invokes(call =>
                captured.Value = (DataPointMappingExportDocument)call.Arguments[1]!);

        return (dataContext, nodeContext, next, captured);
    }

    private sealed class CapturedDocument
    {
        public DataPointMappingExportDocument? Value { get; set; }
    }
}
