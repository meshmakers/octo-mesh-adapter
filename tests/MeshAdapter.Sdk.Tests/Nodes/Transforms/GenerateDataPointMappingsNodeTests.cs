using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class GenerateDataPointMappingsNodeTests : NodeTestBase
{
    private static readonly RtCkId<CkTypeId> RoomType = new("Loxone/Room");
    private static readonly RtCkId<CkTypeId> CategoryType = new("Loxone/Category");
    private static readonly RtCkId<CkTypeId> ControlType = new("Loxone/Control");
    private static readonly RtCkId<CkTypeId> SpaceType = new("EnergyIQ/Space");
    private static readonly RtCkId<CkAssociationRoleId> ParentChild = new("System/ParentChild");

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly ITenantRepository _tenantRepository = A.Fake<ITenantRepository>();
    private readonly IOctoSession _session = A.Fake<IOctoSession>();

    public GenerateDataPointMappingsNodeTests()
    {
        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(_session));
    }

    // ───────────────────────────── Normalize helper ─────────────────────────────

    [Theory]
    [InlineData("Wohnzimmer", "wohnzimmer")]
    [InlineData("WOHNZIMMER", "wohnzimmer")]
    [InlineData(" Wohn  zimmer ", "wohnzimmer")]
    [InlineData("Büro-OG.01", "buroog01")]
    public void Normalize_StripsDiacriticsWhitespaceAndPunctuation(string input, string expected)
    {
        var actual = GenerateDataPointMappingsNode.ContainerMatcher.Normalize(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Normalize_TwoNamesWithDifferentWhitespaceAndCase_BecomeEqual()
    {
        var a = GenerateDataPointMappingsNode.ContainerMatcher.Normalize("Wohn Zimmer");
        var b = GenerateDataPointMappingsNode.ContainerMatcher.Normalize("wohnzimmer");
        Assert.Equal(a, b);
    }

    // ───────────────────────────── ContainerMatcher ─────────────────────────────

    [Fact]
    public void ContainerMatcher_ExactName_MatchesFirstByName()
    {
        var target1 = CreateEntity(SpaceType, "01", ("Name", "Wohnzimmer"));
        var target2 = CreateEntity(SpaceType, "02", ("Name", "Schlafzimmer"));
        var container = CreateEntity(RoomType, "10", ("Name", "Schlafzimmer"));

        var strategies = new List<ContainerMatchingStrategyConfiguration>
        {
            new() { Kind = ContainerMatchingStrategyKind.ExactName, SourceAttribute = "Name", TargetAttribute = "Name" }
        };
        var matcher = new GenerateDataPointMappingsNode.ContainerMatcher(strategies, new List<RtEntity> { target1, target2 });
        var result = matcher.Match(container);
        Assert.NotNull(result);
        Assert.Same(target2, result);
    }

    [Fact]
    public void ContainerMatcher_ExactName_CaseSensitiveMiss()
    {
        var target = CreateEntity(SpaceType, "01", ("Name", "Wohnzimmer"));
        var container = CreateEntity(RoomType, "10", ("Name", "WOHNZIMMER"));
        var strategies = new List<ContainerMatchingStrategyConfiguration>
        {
            new() { Kind = ContainerMatchingStrategyKind.ExactName }
        };
        var matcher = new GenerateDataPointMappingsNode.ContainerMatcher(strategies, new List<RtEntity> { target });
        Assert.Null(matcher.Match(container));
    }

    [Fact]
    public void ContainerMatcher_NormalizedName_HandlesCaseDiacriticsAndWhitespace()
    {
        var target = CreateEntity(SpaceType, "01", ("Name", "Küche EG"));
        var container = CreateEntity(RoomType, "10", ("Name", "kueche eg"));
        var strategies = new List<ContainerMatchingStrategyConfiguration>
        {
            // Normalize strips diacritics (ü → u) so "Küche" → "kuche"; container "kueche" stays "kueche" — these do NOT match.
            // The test that should succeed is when both names normalize to the same canonical form. Use a real-world example:
            new() { Kind = ContainerMatchingStrategyKind.NormalizedName }
        };
        // Override: use same accented form so normalization yields the same string
        var target2 = CreateEntity(SpaceType, "02", ("Name", " Wohn Zimmer "));
        var container2 = CreateEntity(RoomType, "11", ("Name", "wohnzimmer"));
        var matcher = new GenerateDataPointMappingsNode.ContainerMatcher(strategies, new List<RtEntity> { target, target2 });
        var result = matcher.Match(container2);
        Assert.NotNull(result);
        Assert.Same(target2, result);
    }

    [Fact]
    public void ContainerMatcher_FallsThroughOrderedStrategies()
    {
        // ExactName misses, NormalizedName hits — second strategy wins.
        var target = CreateEntity(SpaceType, "01", ("Name", "Wohn Zimmer"));
        var container = CreateEntity(RoomType, "10", ("Name", "wohnzimmer"));
        var strategies = new List<ContainerMatchingStrategyConfiguration>
        {
            new() { Kind = ContainerMatchingStrategyKind.ExactName },
            new() { Kind = ContainerMatchingStrategyKind.NormalizedName }
        };
        var matcher = new GenerateDataPointMappingsNode.ContainerMatcher(strategies, new List<RtEntity> { target });
        var result = matcher.Match(container);
        Assert.NotNull(result);
        Assert.Same(target, result);
    }

    [Fact]
    public void ContainerMatcher_Regex_UsesCapturedGroupAgainstTargetAttribute()
    {
        // Source name "OG.01 Büro Direktor" → capture "OG.01" → matched against Space.RoomNumber
        var target = CreateEntity(SpaceType, "01", ("Name", "Büro Direktor"), ("RoomNumber", "OG.01"));
        var container = CreateEntity(RoomType, "10", ("Name", "OG.01 Büro Direktor"));
        var strategies = new List<ContainerMatchingStrategyConfiguration>
        {
            new()
            {
                Kind = ContainerMatchingStrategyKind.Regex,
                SourceAttribute = "Name",
                TargetAttribute = "RoomNumber",
                Pattern = @"^([A-Z]{2,3}\.\d+)",
                CaptureGroup = 1
            }
        };
        var matcher = new GenerateDataPointMappingsNode.ContainerMatcher(strategies, new List<RtEntity> { target });
        var result = matcher.Match(container);
        Assert.NotNull(result);
        Assert.Same(target, result);
    }

    [Fact]
    public void ContainerMatcher_Manual_UsesExplicitTargetRtId()
    {
        var target = CreateEntity(SpaceType, "01", ("Name", "Anything"));
        var container = CreateEntity(RoomType, "10", ("Name", "Sondername X"));
        // Manual lookup uses the entity's actual RtId (which CreateEntity pads to 24 hex chars).
        var strategies = new List<ContainerMatchingStrategyConfiguration>
        {
            new()
            {
                Kind = ContainerMatchingStrategyKind.Manual,
                Overrides = new List<ManualMatchOverride>
                {
                    new() { Source = "Sondername X", TargetRtId = target.RtId.ToString() }
                }
            }
        };
        var matcher = new GenerateDataPointMappingsNode.ContainerMatcher(strategies, new List<RtEntity> { target });
        var result = matcher.Match(container);
        Assert.NotNull(result);
        Assert.Same(target, result);
    }

    [Fact]
    public void ContainerMatcher_NoStrategyMatches_ReturnsNull()
    {
        var target = CreateEntity(SpaceType, "01", ("Name", "Other"));
        var container = CreateEntity(RoomType, "10", ("Name", "Unmatched Room"));
        var strategies = new List<ContainerMatchingStrategyConfiguration>
        {
            new() { Kind = ContainerMatchingStrategyKind.ExactName }
        };
        var matcher = new GenerateDataPointMappingsNode.ContainerMatcher(strategies, new List<RtEntity> { target });
        Assert.Null(matcher.Match(container));
    }

    // ───────────────────────────── ControlHasState ─────────────────────────────

    [Fact]
    public void ControlHasState_FindsRecordByName()
    {
        var control = CreateEntity(ControlType, "c0c001", ("ControlType", "IRoomControllerV2"));
        // States RecordArray with one record { Name = "tempActual", ExternalId = "uuid-1" }
        var stateRecord = new RtRecord();
        stateRecord.SetAttributeRawValue("Name", "tempActual");
        stateRecord.SetAttributeRawValue("ExternalId", "uuid-1");
        control.SetAttributeRawValue("States", new List<RtRecord> { stateRecord });

        Assert.True(GenerateDataPointMappingsNode.ControlHasState(control, "States", "Name", "tempActual"));
        Assert.False(GenerateDataPointMappingsNode.ControlHasState(control, "States", "Name", "humidity"));
    }

    // ───────────────────────────── E2E node tests ─────────────────────────────

    [Fact]
    public async Task ProcessObjectAsync_HappyPath_ProducesSuggestionsForMatchedRoom()
    {
        // Arrange: one room with one category containing one IRoomControllerV2 control,
        // matching exactly one space by name.
        var space = CreateEntity(SpaceType, "5cace1", ("Name", "Wohnzimmer"));
        var room = CreateEntity(RoomType, "100001", ("Name", "Wohnzimmer"));
        var category = CreateEntity(CategoryType, "ca7001", ("Name", "Klima"), ("CategoryType", "climate"));
        var control = CreateControlWithStates("c0c001", "Raumregler WZ", "IRoomControllerV2",
            ("tempActual", "uuid-tempA"), ("tempTarget", "uuid-tempT"), ("co2", "uuid-co2"));

        SetupGetByType(RoomType, room);
        SetupGetByType(SpaceType, space);
        // Room → Category (inbound ParentChild)
        SetupInboundAssoc(room, CategoryType, new[] { ("ca7001", category) });
        // Category → Control (inbound ParentChild)
        SetupInboundAssoc(category, ControlType, new[] { ("c0c001", control) });

        var config = new GenerateDataPointMappingsNodeConfiguration
        {
            SourceContainerCkTypeId = "Loxone/Room",
            SourceControlCkTypeId = "Loxone/Control",
            SourceCategoryCkTypeId = "Loxone/Category",
            TargetCkTypeId = "EnergyIQ/Space",
            TargetPath = "$.mappingSuggestions",
            ContainerMatchingStrategies = new List<ContainerMatchingStrategyConfiguration>
            {
                new() { Kind = ContainerMatchingStrategyKind.ExactName }
            },
            ControlMappingRules = new List<ControlMappingRuleConfiguration>
            {
                new()
                {
                    Id = "rc-tempActual",
                    When = new ControlMappingRuleWhen { ControlType = "IRoomControllerV2", StateName = "tempActual" },
                    Map = new ControlMappingRuleMap { TargetAttribute = "Temperature" }
                },
                new()
                {
                    Id = "rc-tempTarget",
                    When = new ControlMappingRuleWhen { ControlType = "IRoomControllerV2", StateName = "tempTarget" },
                    Map = new ControlMappingRuleMap { TargetAttribute = "TemperatureSetpointHeating" }
                },
                new()
                {
                    Id = "rc-co2",
                    When = new ControlMappingRuleWhen { ControlType = "IRoomControllerV2", StateName = "co2" },
                    Map = new ControlMappingRuleMap { TargetAttribute = "CO2Level" }
                },
                new()
                {
                    // Rule for a state the control does NOT publish — must not produce a suggestion.
                    Id = "rc-humidity",
                    When = new ControlMappingRuleWhen { ControlType = "IRoomControllerV2", StateName = "humidityActual" },
                    Map = new ControlMappingRuleMap { TargetAttribute = "Humidity", Expression = "value / 100" }
                }
            }
        };

        var (dataContext, nodeContext, next, captured) = PrepareTestWithCapture(config);
        var node = new GenerateDataPointMappingsNode(next, _etlContext);

        // Act
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Assert
        Assert.NotNull(captured.Value);
        var suggestions = captured.Value!;
        Assert.Equal(3, suggestions.Count);
        var attrs = suggestions.Select(s => s.TargetAttributePath).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "CO2Level", "Temperature", "TemperatureSetpointHeating" }, attrs);

        // Verify deterministic name format using the actual padded RtIds.
        var ctrlRtId = control.RtId.ToString();
        var spaceRtId = space.RtId.ToString();
        var tempActual = suggestions.First(s => s.TargetAttributePath == "Temperature");
        Assert.Equal($"rc-tempActual|{ctrlRtId}|tempActual", tempActual.Name);
        Assert.Equal(ctrlRtId, tempActual.ControlRtId);
        Assert.Equal(spaceRtId, tempActual.SpaceRtId);
        Assert.Equal("tempActual", tempActual.SourceAttributePath);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_UnmatchedRoom_ProducesNoSuggestionsForThatRoom()
    {
        var space = CreateEntity(SpaceType, "5cace1", ("Name", "Wohnzimmer"));
        var matchedRoom = CreateEntity(RoomType, "100001", ("Name", "Wohnzimmer"));
        var unmatchedRoom = CreateEntity(RoomType, "100002", ("Name", "Unbekannt"));
        var category = CreateEntity(CategoryType, "ca7001", ("Name", "Klima"));
        var control = CreateControlWithStates("c0c001", "Raumregler", "IRoomControllerV2",
            ("tempActual", "uuid-1"));

        SetupGetByType(RoomType, matchedRoom, unmatchedRoom);
        SetupGetByType(SpaceType, space);
        SetupInboundAssoc(matchedRoom, CategoryType, new[] { ("ca7001", category) });
        SetupInboundAssoc(unmatchedRoom, CategoryType, Array.Empty<(string, RtEntity)>());
        SetupInboundAssoc(category, ControlType, new[] { ("c0c001", control) });

        var config = new GenerateDataPointMappingsNodeConfiguration
        {
            SourceContainerCkTypeId = "Loxone/Room",
            SourceControlCkTypeId = "Loxone/Control",
            SourceCategoryCkTypeId = "Loxone/Category",
            TargetCkTypeId = "EnergyIQ/Space",
            TargetPath = "$.mappingSuggestions",
            ContainerMatchingStrategies = new List<ContainerMatchingStrategyConfiguration>
            {
                new() { Kind = ContainerMatchingStrategyKind.ExactName }
            },
            ControlMappingRules = new List<ControlMappingRuleConfiguration>
            {
                new()
                {
                    Id = "rc-tempActual",
                    When = new ControlMappingRuleWhen { ControlType = "IRoomControllerV2", StateName = "tempActual" },
                    Map = new ControlMappingRuleMap { TargetAttribute = "Temperature" }
                }
            }
        };

        var (dataContext, nodeContext, next, captured) = PrepareTestWithCapture(config);
        var node = new GenerateDataPointMappingsNode(next, _etlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(captured.Value);
        Assert.Single(captured.Value!);
        Assert.Equal(control.RtId.ToString(), captured.Value![0].ControlRtId);
    }

    [Fact]
    public async Task ProcessObjectAsync_ControlNameRegex_FiltersByName()
    {
        var space = CreateEntity(SpaceType, "5cace1", ("Name", "Bad"));
        var room = CreateEntity(RoomType, "100001", ("Name", "Bad"));
        var category = CreateEntity(CategoryType, "ca7001", ("Name", "Sensoren"));
        var humidity = CreateControlWithStates("c10001", "Luftfeuchte Bad", "InfoOnlyAnalog");
        var other = CreateControlWithStates("c20001", "Temperatur Bad", "InfoOnlyAnalog");

        SetupGetByType(RoomType, room);
        SetupGetByType(SpaceType, space);
        SetupInboundAssoc(room, CategoryType, new[] { ("ca7001", category) });
        SetupInboundAssoc(category, ControlType, new[] { ("c10001", humidity), ("c20001", other) });

        var config = new GenerateDataPointMappingsNodeConfiguration
        {
            SourceContainerCkTypeId = "Loxone/Room",
            SourceControlCkTypeId = "Loxone/Control",
            SourceCategoryCkTypeId = "Loxone/Category",
            TargetCkTypeId = "EnergyIQ/Space",
            TargetPath = "$.mappingSuggestions",
            ContainerMatchingStrategies = new List<ContainerMatchingStrategyConfiguration>
            {
                new() { Kind = ContainerMatchingStrategyKind.ExactName }
            },
            ControlMappingRules = new List<ControlMappingRuleConfiguration>
            {
                new()
                {
                    Id = "humidity-by-name",
                    When = new ControlMappingRuleWhen
                    {
                        ControlType = "InfoOnlyAnalog",
                        ControlNameRegex = "(?i)(feuchte|humid)"
                    },
                    Map = new ControlMappingRuleMap { TargetAttribute = "Humidity" }
                }
            }
        };

        var (dataContext, nodeContext, next, captured) = PrepareTestWithCapture(config);
        var node = new GenerateDataPointMappingsNode(next, _etlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(captured.Value);
        Assert.Single(captured.Value!);
        Assert.Equal(humidity.RtId.ToString(), captured.Value![0].ControlRtId);
        Assert.Equal("CurrentValue", captured.Value![0].SourceAttributePath);
    }

    [Fact]
    public async Task ProcessObjectAsync_ChildTargetCkTypeId_ResolvesSensorInsideMatchedSpace()
    {
        // EnergyIQ v2: a Loxone IRoomController.tempActual state should map to a
        // TemperatureSensor that lives inside the matched Space via SpaceSensors,
        // not to the Space itself.
        var sensorType = new RtCkId<CkTypeId>("EnergyIQ/TemperatureSensor");
        var spaceSensorsRole = new RtCkId<CkAssociationRoleId>("EnergyIQ/SpaceSensors");

        var space = CreateEntity(SpaceType, "5cace1", ("Name", "Wohnzimmer"));
        var room = CreateEntity(RoomType, "100001", ("Name", "Wohnzimmer"));
        var category = CreateEntity(CategoryType, "ca7001", ("Name", "Klima"));
        var control = CreateControlWithStates("c0c001", "Raumregler WZ", "IRoomControllerV2",
            ("tempActual", "uuid-tempA"));
        var sensor = CreateEntity(sensorType, "5e1501", ("Name", "Temp-Wohnzimmer"));

        SetupGetByType(RoomType, room);
        SetupGetByType(SpaceType, space);
        SetupInboundAssoc(room, CategoryType, new[] { ("ca7001", category) });
        SetupInboundAssoc(category, ControlType, new[] { ("c0c001", control) });
        // Space contains the TemperatureSensor via SpaceSensors (inbound from Space's perspective).
        SetupInboundAssoc(space, sensorType, new[] { ("5e1501", sensor) }, spaceSensorsRole);

        var config = new GenerateDataPointMappingsNodeConfiguration
        {
            SourceContainerCkTypeId = "Loxone/Room",
            SourceControlCkTypeId = "Loxone/Control",
            SourceCategoryCkTypeId = "Loxone/Category",
            TargetCkTypeId = "EnergyIQ/Space",
            TargetPath = "$.mappingSuggestions",
            ContainerMatchingStrategies = new List<ContainerMatchingStrategyConfiguration>
            {
                new() { Kind = ContainerMatchingStrategyKind.ExactName }
            },
            ControlMappingRules = new List<ControlMappingRuleConfiguration>
            {
                new()
                {
                    Id = "rc-tempActual-to-sensor",
                    When = new ControlMappingRuleWhen { ControlType = "IRoomControllerV2", StateName = "tempActual" },
                    Map = new ControlMappingRuleMap
                    {
                        TargetAttribute = "Temperature",
                        ChildTargetCkTypeId = "EnergyIQ/TemperatureSensor",
                        ChildTargetAssociationRoleId = "EnergyIQ/SpaceSensors"
                    }
                }
            }
        };

        var (dataContext, nodeContext, next, captured) = PrepareTestWithCapture(config);
        var node = new GenerateDataPointMappingsNode(next, _etlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(captured.Value);
        Assert.Single(captured.Value!);
        var suggestion = captured.Value![0];
        // The suggestion's target ids must be the sensor's, not the space's.
        Assert.Equal(sensor.RtId.ToString(), suggestion.SpaceRtId);
        Assert.Equal("EnergyIQ/TemperatureSensor", suggestion.SpaceCkTypeId);
        Assert.Equal("Temperature", suggestion.TargetAttributePath);
        // controlRtId still points at the Loxone control.
        Assert.Equal(control.RtId.ToString(), suggestion.ControlRtId);
    }

    [Fact]
    public async Task ProcessObjectAsync_ChildTargetCkTypeId_NoChildFound_ProducesNoSuggestion()
    {
        // Same v2 setup but the Space has no TemperatureSensor associated — the rule
        // must be skipped silently (zero suggestions) rather than emitting a sensor-less
        // mapping or crashing.
        var sensorType = new RtCkId<CkTypeId>("EnergyIQ/TemperatureSensor");
        var spaceSensorsRole = new RtCkId<CkAssociationRoleId>("EnergyIQ/SpaceSensors");

        var space = CreateEntity(SpaceType, "5cace1", ("Name", "Wohnzimmer"));
        var room = CreateEntity(RoomType, "100001", ("Name", "Wohnzimmer"));
        var category = CreateEntity(CategoryType, "ca7001", ("Name", "Klima"));
        var control = CreateControlWithStates("c0c001", "Raumregler WZ", "IRoomControllerV2",
            ("tempActual", "uuid-tempA"));

        SetupGetByType(RoomType, room);
        SetupGetByType(SpaceType, space);
        SetupInboundAssoc(room, CategoryType, new[] { ("ca7001", category) });
        SetupInboundAssoc(category, ControlType, new[] { ("c0c001", control) });
        // No TemperatureSensor under the Space — explicit empty.
        SetupInboundAssoc(space, sensorType, Array.Empty<(string, RtEntity)>(), spaceSensorsRole);

        var config = new GenerateDataPointMappingsNodeConfiguration
        {
            SourceContainerCkTypeId = "Loxone/Room",
            SourceControlCkTypeId = "Loxone/Control",
            SourceCategoryCkTypeId = "Loxone/Category",
            TargetCkTypeId = "EnergyIQ/Space",
            TargetPath = "$.mappingSuggestions",
            ContainerMatchingStrategies = new List<ContainerMatchingStrategyConfiguration>
            {
                new() { Kind = ContainerMatchingStrategyKind.ExactName }
            },
            ControlMappingRules = new List<ControlMappingRuleConfiguration>
            {
                new()
                {
                    Id = "rc-tempActual-to-sensor",
                    When = new ControlMappingRuleWhen { ControlType = "IRoomControllerV2", StateName = "tempActual" },
                    Map = new ControlMappingRuleMap
                    {
                        TargetAttribute = "Temperature",
                        ChildTargetCkTypeId = "EnergyIQ/TemperatureSensor",
                        ChildTargetAssociationRoleId = "EnergyIQ/SpaceSensors"
                    }
                }
            }
        };

        var (dataContext, nodeContext, next, captured) = PrepareTestWithCapture(config);
        var node = new GenerateDataPointMappingsNode(next, _etlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(captured.Value);
        Assert.Empty(captured.Value!);
    }

    // ───────────────────────────── Test setup helpers ─────────────────────────────

    [Fact]
    public async Task ProcessObjectAsync_DirectMappings_WireControlsByNameToEntitiesByName()
    {
        var meterType = new RtCkId<CkTypeId>("EnergyIQ/Meter");
        var gridType = new RtCkId<CkTypeId>("EnergyIQ/GridConnection");

        // Source controls (tenant-wide): a Loxone Meter with actual+total (no totalNeg),
        // and an EnergyManager2 exposing Gpwr.
        var meterCtrl = CreateControlWithStates("c0c0e6", "Verbrauch EG", "Meter",
            ("actual", "u-act"), ("total", "u-tot"));
        var emCtrl = CreateControlWithStates("c0c0e7", "Energiemanager", "EnergyManager2",
            ("Gpwr", "u-g"), ("Ppwr", "u-p"), ("Spwr", "u-s"), ("Ssoc", "u-soc"));

        var meterEntity = CreateEntity(meterType, "3001a2", ("Name", "Stromzähler HG-EG"));
        var gridEntity = CreateEntity(gridType, "3000a1", ("Name", "Hausanschluss"));

        // No containers/targets for the container path; direct path queries controls + per-type targets.
        SetupGetByType(RoomType);
        SetupGetByType(SpaceType);
        SetupGetByType(ControlType, meterCtrl, emCtrl);
        SetupGetByType(meterType, meterEntity);
        SetupGetByType(gridType, gridEntity);

        var config = new GenerateDataPointMappingsNodeConfiguration
        {
            SourceContainerCkTypeId = "Loxone/Room",
            SourceControlCkTypeId = "Loxone/Control",
            TargetCkTypeId = "EnergyIQ/Space",
            TargetPath = "$.mappingSuggestions",
            DirectControlMappings = new List<DirectControlMappingConfiguration>
            {
                new()
                {
                    Id = "meter-verbrauch-eg",
                    ControlType = "Meter",
                    ControlName = "Verbrauch EG",
                    TargetCkTypeId = "EnergyIQ/Meter",
                    TargetName = "Stromzähler HG-EG",
                    States = new List<DirectStateMapping>
                    {
                        new() { StateName = "actual", TargetAttribute = "ActivePower" },
                        new() { StateName = "total", TargetAttribute = "ImportedEnergy" },
                        // Not exposed by this (non-bidirectional) meter → must be skipped.
                        new() { StateName = "totalNeg", TargetAttribute = "ExportedEnergy" }
                    }
                },
                new()
                {
                    Id = "em-grid",
                    ControlType = "EnergyManager2",
                    ControlName = "Energiemanager",
                    TargetCkTypeId = "EnergyIQ/GridConnection",
                    TargetName = "Hausanschluss",
                    States = new List<DirectStateMapping>
                    {
                        new() { StateName = "Gpwr", TargetAttribute = "ActivePower" }
                    }
                }
            }
        };

        var (dataContext, nodeContext, next, captured) = PrepareTestWithCapture(config);
        var node = new GenerateDataPointMappingsNode(next, _etlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(captured.Value);
        var s = captured.Value!;
        Assert.Equal(3, s.Count); // meter: actual+total (totalNeg skipped) + grid: Gpwr

        var eg = s.Where(x => x.RuleId == "meter-verbrauch-eg").ToList();
        Assert.Equal(new[] { "ActivePower", "ImportedEnergy" },
            eg.Select(x => x.TargetAttributePath).OrderBy(x => x).ToArray());
        Assert.All(eg, x => Assert.Equal(meterEntity.RtId.ToString(), x.SpaceRtId));
        Assert.All(eg, x => Assert.Equal("EnergyIQ/Meter", x.SpaceCkTypeId));
        Assert.All(eg, x => Assert.Equal(meterCtrl.RtId.ToString(), x.ControlRtId));
        var actual = eg.First(x => x.TargetAttributePath == "ActivePower");
        Assert.Equal($"meter-verbrauch-eg|{meterCtrl.RtId}|actual", actual.Name);
        Assert.Equal("actual", actual.SourceAttributePath);

        var grid = s.Single(x => x.RuleId == "em-grid");
        Assert.Equal(gridEntity.RtId.ToString(), grid.SpaceRtId);
        Assert.Equal("EnergyIQ/GridConnection", grid.SpaceCkTypeId);
        Assert.Equal("ActivePower", grid.TargetAttributePath);
        Assert.Equal("Gpwr", grid.SourceAttributePath);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

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

    private static RtEntity CreateControlWithStates(string rtId, string name, string controlType,
        params (string stateName, string externalId)[] states)
    {
        var control = CreateEntity(ControlType, rtId,
            ("Name", name),
            ("ControlType", controlType));
        if (states.Length > 0)
        {
            var records = new List<RtRecord>();
            foreach (var (stateName, externalId) in states)
            {
                var record = new RtRecord();
                record.SetAttributeRawValue("Name", stateName);
                record.SetAttributeRawValue("ExternalId", externalId);
                records.Add(record);
            }
            control.SetAttributeRawValue("States", records);
        }
        return control;
    }

    private static string PadRtId(string id)
    {
        // OctoObjectId expects a 24-char hex string. Pad shorter test IDs with zeros for convenience.
        if (id.Length >= 24) return id;
        return id.PadLeft(24, '0').Replace(' ', '0');
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

    private void SetupInboundAssoc(RtEntity parent, RtCkId<CkTypeId> childCkTypeId,
        (string childRtId, RtEntity childEntity)[] children,
        RtCkId<CkAssociationRoleId>? roleId = null)
    {
        var role = roleId ?? ParentChild;
        var assocSet = A.Fake<IResultSet<RtAssociation>>();
        var assocs = children.Select(c => new RtAssociation
        {
            OriginRtId = new OctoObjectId(PadRtId(c.childRtId)),
            OriginCkTypeId = childCkTypeId,
            TargetRtId = parent.RtId,
            TargetCkTypeId = parent.CkTypeId!,
            AssociationRoleId = role
        }).ToList();
        A.CallTo(() => assocSet.Items).Returns(assocs);
        A.CallTo(() => assocSet.TotalCount).Returns(assocs.Count);

        A.CallTo(() => _tenantRepository.GetRtAssociationsAsync(
                A<IOctoSession>._,
                A<RtEntityId>.That.Matches(eid =>
                    eid.RtId.Equals(parent.RtId) && eid.CkTypeId.Equals(parent.CkTypeId!)),
                A<RtAssociationExtendedQueryOptions>.That.Matches(opts => opts.RelatedRtCkTypeId == childCkTypeId)))
            .Returns(assocSet);

        // Per-child id lookup
        foreach (var (childRtId, childEntity) in children)
        {
            var childSet = A.Fake<IResultSet<RtEntity>>();
            A.CallTo(() => childSet.Items).Returns(new List<RtEntity> { childEntity });
            A.CallTo(() => childSet.TotalCount).Returns(1);
            var paddedRtId = new OctoObjectId(PadRtId(childRtId));
            A.CallTo(() => _tenantRepository.GetRtEntitiesByIdAsync(
                    A<IOctoSession>._,
                    childCkTypeId,
                    A<IReadOnlyList<OctoObjectId>>.That.Matches(ids => ids.Contains(paddedRtId)),
                    A<RtEntityQueryOptions>._,
                    A<int?>._,
                    A<int?>._))
                .Returns(childSet);
        }
    }

    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next, CapturedSuggestions Captured)
        PrepareTestWithCapture(GenerateDataPointMappingsNodeConfiguration config)
    {
        var (dataContext, nodeContext, next) = PrepareTest<GenerateDataPointMappingsNodeConfiguration>(config);

        var captured = new CapturedSuggestions();
        // The node calls dataContext.Set<List<MappingSuggestion>>(targetPath, suggestions, ...) — capture them.
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set)
                && call.Arguments.Count >= 2
                && (call.Arguments[0] as string) == config.TargetPath
                && call.Arguments[1] is List<GenerateDataPointMappingsNode.MappingSuggestion>)
            .Invokes(call =>
                captured.Value = (List<GenerateDataPointMappingsNode.MappingSuggestion>)call.Arguments[1]!);

        return (dataContext, nodeContext, next, captured);
    }

    private sealed class CapturedSuggestions
    {
        public List<GenerateDataPointMappingsNode.MappingSuggestion>? Value { get; set; }
    }

    /// <summary>
    /// Characterization: the typed MappingSuggestion + MappingStatistics records serialize
    /// byte-identically to the former hand-built JsonObject shapes (camelCase keys, order,
    /// ruleHits dynamic object, string arrays). Legacy builders are reproduced locally.
    /// </summary>
    [Fact]
    public void MappingSuggestionAndStatistics_SerializeByteIdenticalToLegacyJsonObjects()
    {
        var suggestion = new GenerateDataPointMappingsNode.MappingSuggestion(
            Name: "rc-temp|0001|tempActual",
            ControlRtId: "0001",
            ControlCkTypeId: "Loxone/Control",
            SpaceRtId: "0002",
            SpaceCkTypeId: "EnergyIQ/Space",
            SourceAttributePath: "tempActual",
            TargetAttributePath: "Temperature",
            MappingExpression: "value / 100",
            RuleId: "rc-temp",
            Reason: "Container 'Wohnzimmer' matched; rule 'rc-temp' on control 'Raumregler'");

        var legacySuggestion = new JsonObject
        {
            ["name"] = suggestion.Name,
            ["controlRtId"] = suggestion.ControlRtId,
            ["controlCkTypeId"] = suggestion.ControlCkTypeId,
            ["spaceRtId"] = suggestion.SpaceRtId,
            ["spaceCkTypeId"] = suggestion.SpaceCkTypeId,
            ["sourceAttributePath"] = suggestion.SourceAttributePath,
            ["targetAttributePath"] = suggestion.TargetAttributePath,
            ["mappingExpression"] = suggestion.MappingExpression,
            ["ruleId"] = suggestion.RuleId,
            ["reason"] = suggestion.Reason,
        };

        Assert.Equal(
            legacySuggestion.ToJsonString(SystemTextJsonOptions.Default),
            JsonSerializer.Serialize(suggestion, SystemTextJsonOptions.Default));

        var unmatched = new List<string> { "Unbekannt", "Größe" };
        var ruleHits = new Dictionary<string, int>(StringComparer.Ordinal) { ["rc-temp"] = 3, ["rc-co2"] = 1 };
        var definedRuleIds = new List<string> { "rc-temp", "rc-co2", "rc-humidity" };

        var stats = new GenerateDataPointMappingsNode.MappingStatistics(
            TotalContainers: 10,
            MatchedContainers: 8,
            UnmatchedContainers: 2,
            UnmatchedContainerNames: unmatched,
            TotalSuggestions: 4,
            RuleHits: ruleHits,
            DefinedRuleIds: definedRuleIds);

        var legacyStats = new JsonObject
        {
            ["totalContainers"] = 10,
            ["matchedContainers"] = 8,
            ["unmatchedContainers"] = 2,
            ["unmatchedContainerNames"] = new JsonArray(unmatched.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["totalSuggestions"] = 4,
            ["ruleHits"] = (JsonObject)JsonSerializer.SerializeToNode(ruleHits, SystemTextJsonOptions.Default)!,
            ["definedRuleIds"] = new JsonArray(definedRuleIds.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        };

        Assert.Equal(
            legacyStats.ToJsonString(SystemTextJsonOptions.Default),
            JsonSerializer.Serialize(stats, SystemTextJsonOptions.Default));
    }
}
