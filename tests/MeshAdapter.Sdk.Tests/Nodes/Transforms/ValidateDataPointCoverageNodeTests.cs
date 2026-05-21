using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

/// <summary>
/// Unit tests for the pure-function coverage evaluator on
/// <see cref="ValidateDataPointCoverageNode"/>. Verifies status calculation and
/// missing/present partitioning for the rule statuses ok / warning / error / info.
/// Full repository wiring is covered by end-to-end pipeline runs.
/// </summary>
public class ValidateDataPointCoverageNodeTests
{
    [Fact]
    public void EvaluateCoverage_NoRule_ReturnsInfoWithEmptyLists()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Temperature" };

        var result = ValidateDataPointCoverageNode.EvaluateCoverage(rule: null, present);

        Assert.Equal("info", result.Status);
        Assert.Empty(result.Required);
        Assert.Empty(result.Recommended);
        Assert.Empty(result.MissingRequired);
        Assert.Empty(result.MissingRecommended);
    }

    [Fact]
    public void EvaluateCoverage_AllRequiredAndRecommendedPresent_ReturnsOk()
    {
        var rule = new CoverageRule
        {
            CkTypeId = "EnergyIQ/Space",
            RequiredAttributes = new List<string> { "Temperature", "CO2Level", "Humidity" },
            RecommendedAttributes = new List<string> { "SetpointTemperature" },
        };
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Temperature", "CO2Level", "Humidity", "SetpointTemperature",
        };

        var result = ValidateDataPointCoverageNode.EvaluateCoverage(rule, present);

        Assert.Equal("ok", result.Status);
        Assert.Empty(result.MissingRequired);
        Assert.Empty(result.MissingRecommended);
    }

    [Fact]
    public void EvaluateCoverage_RequiredMissing_ReturnsErrorAndListsMissing()
    {
        var rule = new CoverageRule
        {
            CkTypeId = "EnergyIQ/Space",
            RequiredAttributes = new List<string> { "Temperature", "CO2Level", "Humidity" },
            RecommendedAttributes = new List<string> { "SetpointTemperature" },
        };
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Temperature" };

        var result = ValidateDataPointCoverageNode.EvaluateCoverage(rule, present);

        Assert.Equal("error", result.Status);
        Assert.Equal(new[] { "CO2Level", "Humidity" }, result.MissingRequired);
        Assert.Equal(new[] { "SetpointTemperature" }, result.MissingRecommended);
    }

    [Fact]
    public void EvaluateCoverage_OnlyRecommendedMissing_ReturnsWarning()
    {
        var rule = new CoverageRule
        {
            CkTypeId = "EnergyIQ/Space",
            RequiredAttributes = new List<string> { "Temperature", "Humidity" },
            RecommendedAttributes = new List<string> { "SetpointTemperature", "SetpointCO2" },
        };
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Temperature", "Humidity", "SetpointTemperature",
        };

        var result = ValidateDataPointCoverageNode.EvaluateCoverage(rule, present);

        Assert.Equal("warning", result.Status);
        Assert.Empty(result.MissingRequired);
        Assert.Equal(new[] { "SetpointCO2" }, result.MissingRecommended);
    }

    [Fact]
    public void EvaluateCoverage_PresentCheckIsCaseInsensitive()
    {
        // The caller (BuildReportAsync) builds the `present` set with
        // OrdinalIgnoreCase semantics. The evaluator must respect that contract,
        // otherwise mappings declared with inconsistent casing (e.g. "temperature"
        // vs "Temperature") would be flagged as missing.
        var rule = new CoverageRule
        {
            CkTypeId = "EnergyIQ/Space",
            RequiredAttributes = new List<string> { "Temperature" },
        };
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "temperature" };

        var result = ValidateDataPointCoverageNode.EvaluateCoverage(rule, present);

        Assert.Equal("ok", result.Status);
    }

    [Fact]
    public void EvaluateCoverage_EmptyRule_ReturnsOk()
    {
        // A rule with no required/recommended attrs is essentially "anything goes".
        var rule = new CoverageRule { CkTypeId = "Basic/TreeNode" };
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = ValidateDataPointCoverageNode.EvaluateCoverage(rule, present);

        Assert.Equal("ok", result.Status);
    }

    [Fact]
    public void EvaluateCoverage_RequiredAssociationPresent_ReturnsOk()
    {
        // v2 use case: EnergyIQ/Space requires an EnergyIQ/TemperatureSensor via
        // EnergyIQ/SpaceSensors. The association IS present → ok status.
        var rule = new CoverageRule
        {
            CkTypeId = "EnergyIQ/Space",
            RequiredAssociations = new List<RequiredAssociation>
            {
                new() { AssociationRoleId = "EnergyIQ/SpaceSensors", TargetCkTypeId = "EnergyIQ/TemperatureSensor" }
            }
        };
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var presentAssoc = new HashSet<string>(StringComparer.Ordinal)
        {
            "EnergyIQ/SpaceSensors→EnergyIQ/TemperatureSensor"
        };

        var result = ValidateDataPointCoverageNode.EvaluateCoverage(rule, present, presentAssoc);

        Assert.Equal("ok", result.Status);
        Assert.Empty(result.MissingRequiredAssociations);
        Assert.Single(result.PresentAssociations);
        Assert.Equal("EnergyIQ/SpaceSensors→EnergyIQ/TemperatureSensor", result.PresentAssociations[0]);
    }

    [Fact]
    public void EvaluateCoverage_RequiredAssociationMissing_ReturnsError()
    {
        var rule = new CoverageRule
        {
            CkTypeId = "EnergyIQ/Space",
            RequiredAssociations = new List<RequiredAssociation>
            {
                new() { AssociationRoleId = "EnergyIQ/SpaceSensors", TargetCkTypeId = "EnergyIQ/TemperatureSensor" },
                new() { AssociationRoleId = "EnergyIQ/SpaceSensors", TargetCkTypeId = "EnergyIQ/CO2Sensor" }
            }
        };
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var presentAssoc = new HashSet<string>(StringComparer.Ordinal)
        {
            "EnergyIQ/SpaceSensors→EnergyIQ/TemperatureSensor"   // CO2Sensor association is missing
        };

        var result = ValidateDataPointCoverageNode.EvaluateCoverage(rule, present, presentAssoc);

        Assert.Equal("error", result.Status);
        Assert.Single(result.MissingRequiredAssociations);
        Assert.Equal("EnergyIQ/SpaceSensors→EnergyIQ/CO2Sensor", result.MissingRequiredAssociations[0]);
    }

    [Fact]
    public void EvaluateCoverage_RequiredAttrPresent_RequiredAssocMissing_StillError()
    {
        // Mixing both kinds of requirements: attribute side is complete but
        // an association is missing — status is still error (associations are
        // weighted equally to attributes for error severity).
        var rule = new CoverageRule
        {
            CkTypeId = "EnergyIQ/Space",
            RequiredAttributes = new List<string> { "OperatingMode" },
            RequiredAssociations = new List<RequiredAssociation>
            {
                new() { AssociationRoleId = "EnergyIQ/SpaceSensors", TargetCkTypeId = "EnergyIQ/TemperatureSensor" }
            }
        };
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OperatingMode" };
        var presentAssoc = new HashSet<string>(StringComparer.Ordinal); // empty

        var result = ValidateDataPointCoverageNode.EvaluateCoverage(rule, present, presentAssoc);

        Assert.Equal("error", result.Status);
        Assert.Empty(result.MissingRequired);
        Assert.Single(result.MissingRequiredAssociations);
    }
}
