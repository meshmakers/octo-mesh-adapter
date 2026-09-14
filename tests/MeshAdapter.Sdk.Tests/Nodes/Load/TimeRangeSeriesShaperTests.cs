using System.Text.Json.Nodes;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

/// <summary>
/// Pins the shaping rules of <see cref="SaveTimeRangeSeriesInArchiveNode" /> — which anchor a series
/// belongs to, which value the anchor reflects, which values are unusable, and what ends up on a row.
/// </summary>
/// <remarks>
/// The claim the node exists to make is that N measured windows produce ONE anchor and N archive
/// rows, instead of N runtime entities filtered down to one. That claim lives entirely in here, which
/// is why this is a plain unit test with no repository, no CK model and no database: the parts that
/// need those (building the anchor's attributes through the model, the write itself) are covered
/// where they can be — against a real stack.
/// </remarks>
public class TimeRangeSeriesShaperTests
{
    private const string ParentRtId = "670000000000000000000001";
    private static readonly RtCkId<CkTypeId> CkType = new("Basic.Energy/EnergyMeasurement");

    private static SaveTimeRangeSeriesInArchiveNodeConfiguration Config() => new()
    {
        Path = "$.series",
        ArchiveRtId = "ec0000000000000000000a01",
        CkTypeId = CkType.ToString(),
        ValuesProperty = "EnergyQuantities",
        WellKnownNameFormat = "{MeteringPointRtId}_{MeterCode}",
        FromProperty = "From",
        ToProperty = "To",
        Columns =
        [
            new TimeRangeSeriesColumn { Name = "Amount.Value", ValueProperty = "Quantity" },
            new TimeRangeSeriesColumn
            {
                Name = "Amount.Unit",
                ValueProperty = "QuantityUnit",
                Scope = TimeRangeSeriesColumnScope.Series
            }
        ]
    };

    private static JsonArray Parse(string json) => (JsonArray)JsonNode.Parse(json)!;

    private static string SeriesJson(string meterCode, int slots, DateTime start) =>
        $$"""
          [ { "MeteringPointRtId": "{{ParentRtId}}", "MeterCode": "{{meterCode}}", "QuantityUnit": "kWh",
              "EnergyQuantities": [
          """
        + string.Join(",", Enumerable.Range(0, slots).Select(i =>
            $$"""
              { "From": "{{start.AddMinutes(15 * i):O}}", "To": "{{start.AddMinutes(15 * (i + 1)):O}}", "Quantity": {{i + 1}}.5 }
              """))
        + "] } ]";

    [Fact]
    public void ManyValuesProduceOneSeriesAndOneRowPerValue()
    {
        // 96 quarter-hours is one day of one register. The composition this replaces built 96
        // runtime entities in order to keep exactly one.
        var c = Config();
        var series = TimeRangeSeriesShaper.Shape(
            Parse(SeriesJson("1-1:2.9.0 G.03", 96, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))),
            c, out var unresolved);

        Assert.Single(series);
        Assert.Equal(96, series[0].Values.Count);
        Assert.Equal(0, unresolved);

        series[0].RtId = OctoObjectId.GenerateNewId();
        var rows = TimeRangeSeriesShaper.BuildRows(series, CkType, c, out var skipped);

        Assert.Equal(96, rows.Count);
        Assert.Equal(0, skipped);
        Assert.All(rows, r => Assert.Equal(series[0].RtId, r.RtId));
    }

    [Fact]
    public void TheAnchorKeyIsBuiltFromTheSeriesProperties()
    {
        var series = TimeRangeSeriesShaper.Shape(
            Parse(SeriesJson("1-1:2.9.0 G.03", 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))),
            Config(), out _);

        Assert.Equal($"{ParentRtId}_1-1:2.9.0 G.03", series[0].WellKnownName);
    }

    [Fact]
    public void TwoSeriesSharingAKeyShareOneAnchor()
    {
        // A source that splits one logical series across blocks must not create two anchors for it —
        // that is how an archive ends up with rows pointing at two RtIds for one register.
        var series = TimeRangeSeriesShaper.Shape(Parse(
            $$"""
              [ { "MeteringPointRtId": "{{ParentRtId}}", "MeterCode": "G.03", "QuantityUnit": "kWh",
                  "EnergyQuantities": [ { "From": "2026-01-01T00:00:00Z", "To": "2026-01-01T00:15:00Z", "Quantity": 1.0 } ] },
                { "MeteringPointRtId": "{{ParentRtId}}", "MeterCode": "G.03", "QuantityUnit": "kWh",
                  "EnergyQuantities": [ { "From": "2026-01-01T00:15:00Z", "To": "2026-01-01T00:30:00Z", "Quantity": 2.0 } ] } ]
              """), Config(), out _);

        Assert.Single(series);
        Assert.Equal(2, series[0].Values.Count);
    }

    [Fact]
    public void TwoRegistersOfOneMeteringPointGetTheirOwnAnchors()
    {
        var series = TimeRangeSeriesShaper.Shape(Parse(
            $$"""
              [ { "MeteringPointRtId": "{{ParentRtId}}", "MeterCode": "G.03", "QuantityUnit": "kWh",
                  "EnergyQuantities": [ { "From": "2026-01-01T00:00:00Z", "To": "2026-01-01T00:15:00Z", "Quantity": 1.0 } ] },
                { "MeteringPointRtId": "{{ParentRtId}}", "MeterCode": "P.01T", "QuantityUnit": "kWh",
                  "EnergyQuantities": [ { "From": "2026-01-01T00:00:00Z", "To": "2026-01-01T00:15:00Z", "Quantity": 2.0 } ] } ]
              """), Config(), out _);

        Assert.Equal(2, series.Count);
        Assert.Equal([$"{ParentRtId}_G.03", $"{ParentRtId}_P.01T"],
            series.Select(s => s.WellKnownName).ToArray());
    }

    [Fact]
    public void ASeriesWhoseKeyDoesNotResolveIsReportedNotMerged()
    {
        // A half-built key would silently merge unrelated series into one anchor, so an unresolved
        // one is dropped — and counted, because a series that vanishes without a word is exactly
        // the failure mode this node must not have.
        var series = TimeRangeSeriesShaper.Shape(Parse(
            $$"""
              [ { "MeteringPointRtId": "{{ParentRtId}}", "QuantityUnit": "kWh",
                  "EnergyQuantities": [ { "From": "2026-01-01T00:00:00Z", "To": "2026-01-01T00:15:00Z", "Quantity": 1.0 } ] },
                { "MeteringPointRtId": "{{ParentRtId}}", "MeterCode": "G.03", "QuantityUnit": "kWh",
                  "EnergyQuantities": [ { "From": "2026-01-01T00:00:00Z", "To": "2026-01-01T00:15:00Z", "Quantity": 2.0 } ] } ]
              """), Config(), out var unresolved);

        Assert.Single(series);
        Assert.Equal(1, unresolved);
    }

    [Fact]
    public void ASeriesWithNoValuesProducesNoAnchor()
    {
        // An anchor with nothing behind it is a placeholder entity nobody asked for.
        var series = TimeRangeSeriesShaper.Shape(Parse(
            $$"""
              [ { "MeteringPointRtId": "{{ParentRtId}}", "MeterCode": "G.03", "QuantityUnit": "kWh",
                  "EnergyQuantities": [] } ]
              """), Config(), out _);

        Assert.Empty(series);
    }

    [Fact]
    public void SeriesScopedColumnsLandOnEveryRowAndValueScopedOnesVary()
    {
        var c = Config();
        var series = TimeRangeSeriesShaper.Shape(
            Parse(SeriesJson("G.03", 3, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))), c, out _);
        var rows = TimeRangeSeriesShaper.BuildRows(series, CkType, c, out _);

        Assert.All(rows, r => Assert.Equal("kWh", r.Attributes["Amount.Unit"]));
        Assert.Equal([1.5, 2.5, 3.5], rows.Select(r => r.Attributes["Amount.Value"]).ToArray());
    }

    [Fact]
    public void AValueWithoutAUsableWindowIsSkippedRatherThanFailingTheBatch()
    {
        // One malformed slot in a bulk replay must not cost the other three hundred thousand rows.
        var c = Config();
        var series = TimeRangeSeriesShaper.Shape(Parse(
            $$"""
              [ { "MeteringPointRtId": "{{ParentRtId}}", "MeterCode": "G.03", "QuantityUnit": "kWh",
                  "EnergyQuantities": [
                    { "From": "2026-01-01T00:00:00Z", "To": "2026-01-01T00:15:00Z", "Quantity": 1.0 },
                    { "From": "2026-01-01T00:15:00Z", "Quantity": 2.0 },
                    { "From": "2026-01-01T00:45:00Z", "To": "2026-01-01T00:30:00Z", "Quantity": 3.0 },
                    { "From": "2026-01-01T00:30:00Z", "To": "2026-01-01T00:45:00Z", "Quantity": 4.0 } ] } ]
              """), c, out _);

        var rows = TimeRangeSeriesShaper.BuildRows(series, CkType, c, out var skipped);

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, skipped);
        Assert.Equal([1.0, 4.0], rows.Select(r => r.Attributes["Amount.Value"]).ToArray());
    }

    [Fact]
    public void WindowBoundariesComeBackAsUtcWhateverTheDocumentCarried()
    {
        // The document round-trips dates as ISO strings; read as local they would shift the whole
        // series by the host's offset and land in the wrong bucket of every rollup above.
        var c = Config();
        var series = TimeRangeSeriesShaper.Shape(Parse(
            $$"""
              [ { "MeteringPointRtId": "{{ParentRtId}}", "MeterCode": "G.03", "QuantityUnit": "kWh",
                  "EnergyQuantities": [
                    { "From": "2026-01-01T00:00:00+02:00", "To": "2026-01-01T00:15:00+02:00", "Quantity": 1.0 } ] } ]
              """), c, out _);

        var rows = TimeRangeSeriesShaper.BuildRows(series, CkType, c, out _);

        Assert.Equal(new DateTime(2025, 12, 31, 22, 0, 0, DateTimeKind.Utc), rows[0].From);
        Assert.Equal(DateTimeKind.Utc, rows[0].From.Kind);
    }

    [Fact]
    public void TheAnchorReflectsTheLatestWindowNotTheLastInDocumentOrder()
    {
        // Lesart D: the runtime entity holds the most recent reading. A corrected block that arrives
        // with its slots out of order must not leave the anchor on whichever one happened to be last.
        var c = Config() with { AnchorWindowToAttribute = "TimeRange.To" };
        var series = TimeRangeSeriesShaper.Shape(Parse(
            $$"""
              [ { "MeteringPointRtId": "{{ParentRtId}}", "MeterCode": "G.03", "QuantityUnit": "kWh",
                  "EnergyQuantities": [
                    { "From": "2026-01-01T00:30:00Z", "To": "2026-01-01T00:45:00Z", "Quantity": 3.0 },
                    { "From": "2026-01-01T00:00:00Z", "To": "2026-01-01T00:15:00Z", "Quantity": 1.0 } ] } ]
              """), c, out _);

        var winner = TimeRangeSeriesShaper.SelectAnchorValue(series[0].Values, c);

        Assert.Equal(3.0, TimeRangeSeriesShaper.ToScalar(winner["Quantity"]));
    }

    [Fact]
    public void WithoutAnAnchorWindowTheLastValueInDocumentOrderWins()
    {
        var c = Config();
        var series = TimeRangeSeriesShaper.Shape(
            Parse(SeriesJson("G.03", 3, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))), c, out _);

        var winner = TimeRangeSeriesShaper.SelectAnchorValue(series[0].Values, c);

        Assert.Equal(3.5, TimeRangeSeriesShaper.ToScalar(winner["Quantity"]));
    }

    [Theory]
    [InlineData("{A}_{B}", "x_y")]
    [InlineData("{A}", "x")]
    [InlineData("plain", "plain")]
    [InlineData("{A}-mid-{B}-end", "x-mid-y-end")]
    public void KeyFormattingSubstitutesEveryPlaceholder(string format, string expected)
    {
        var series = (JsonObject)JsonNode.Parse("""{ "A": "x", "B": "y" }""")!;

        Assert.Equal(expected, TimeRangeSeriesShaper.FormatWellKnownName(format, series));
    }

    [Theory]
    [InlineData("{Missing}")]
    [InlineData("{A}_{Missing}")]
    [InlineData("{Nested}")]
    public void KeyFormattingRefusesToProduceAPartialKey(string format)
    {
        var series = (JsonObject)JsonNode.Parse("""{ "A": "x", "Nested": { "B": "y" } }""")!;

        Assert.Equal(string.Empty, TimeRangeSeriesShaper.FormatWellKnownName(format, series));
    }

    [Fact]
    public void KeyFormattingRendersNumbersWithTheInvariantCulture()
    {
        // A key built under a de-AT ambient culture would read "1,5" where the same tenant on a
        // different host reads "1.5" — two anchors for one series, discovered much later.
        var series = (JsonObject)JsonNode.Parse("""{ "A": 1.5 }""")!;
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-AT");
            Assert.Equal("1.5", TimeRangeSeriesShaper.FormatWellKnownName("{A}", series));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
