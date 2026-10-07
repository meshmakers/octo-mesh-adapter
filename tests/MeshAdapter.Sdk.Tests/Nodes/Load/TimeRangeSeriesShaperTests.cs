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

    /// <summary>Identity conversion — the node supplies the CK-model-backed one in production.</summary>
    private static object? PassThrough(string column, object? raw) => raw;

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
        var rows = TimeRangeSeriesShaper.BuildRows(series, CkType, c, PassThrough, out var skipped);

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
    public void SeriesScopedValuesComeFromTheObjectEachValueCameFrom()
    {
        // One batch carrying two documents for the same register: an original and a same-quality
        // correction of the same window. The archive's ConflictPrecedence decides between them by the
        // document date, a series-scoped column — so each row must carry its OWN document's date. With
        // both rows stamped with the first document's date the dates tie and position decides.
        var c = Config();
        c.Columns.Add(new TimeRangeSeriesColumn
        {
            Name = "SourceDocumentDate", ValueProperty = "CreationTime", Scope = TimeRangeSeriesColumnScope.Series
        });
        var series = TimeRangeSeriesShaper.Shape(Parse(
            $$"""
              [ { "MeteringPointRtId": "{{ParentRtId}}", "MeterCode": "G.03", "QuantityUnit": "kWh", "CreationTime": "2026-01-02T08:00:00Z",
                  "EnergyQuantities": [ { "From": "2026-01-01T00:00:00Z", "To": "2026-01-01T00:15:00Z", "Quantity": 1.0 } ] },
                { "MeteringPointRtId": "{{ParentRtId}}", "MeterCode": "G.03", "QuantityUnit": "Wh", "CreationTime": "2026-01-05T08:00:00Z",
                  "EnergyQuantities": [ { "From": "2026-01-01T00:00:00Z", "To": "2026-01-01T00:15:00Z", "Quantity": 1.2 } ] } ]
              """), c, out _);
        var rows = TimeRangeSeriesShaper.BuildRows(series, CkType, c, PassThrough, out _);

        Assert.Single(series);
        Assert.Equal(
            [new DateTime(2026, 1, 2, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 5, 8, 0, 0, DateTimeKind.Utc)],
            rows.Select(r => TimeRangeSeriesShaper.NormaliseToUtc((DateTime)r.Attributes["SourceDocumentDate"]!)).ToArray());
        Assert.Equal(["kWh", "Wh"], rows.Select(r => r.Attributes["Amount.Unit"]).ToArray());
    }

    [Fact]
    public void DateColumnsAreStoredAsUtcWhateverOffsetTheSourceCarries()
    {
        // Some grid operators write DocumentCreationDateTime with an offset ("+02:00") where the
        // others write "Z". The window boundaries were always normalised to UTC; a date COLUMN was
        // passed through, and a date string carrying an offset parses to a LOCAL DateTime - so the
        // archive stored the host's wall clock as UTC, off by the host's offset (two hours on a CEST
        // host, none in a UTC container, which is why it only showed on a laptop).
        var c = Config();
        c.Columns.Add(new TimeRangeSeriesColumn
        {
            Name = "SourceDocumentDate", ValueProperty = "CreationTime", Scope = TimeRangeSeriesColumnScope.Series
        });
        var series = TimeRangeSeriesShaper.Shape(Parse(
            $$"""
              [ { "MeteringPointRtId": "{{ParentRtId}}", "MeterCode": "G.01", "QuantityUnit": "kWh", "CreationTime": "2026-07-15T09:54:05+02:00",
                  "EnergyQuantities": [ { "From": "2026-07-14T22:00:00Z", "To": "2026-07-14T22:15:00Z", "Quantity": 1.0 } ] },
                { "MeteringPointRtId": "{{ParentRtId}}", "MeterCode": "G.02", "QuantityUnit": "kWh", "CreationTime": "2026-07-15T07:54:05Z",
                  "EnergyQuantities": [ { "From": "2026-07-14T22:00:00Z", "To": "2026-07-14T22:15:00Z", "Quantity": 2.0 } ] } ]
              """), c, out _);

        var rows = TimeRangeSeriesShaper.BuildRows(series, CkType, c, PassThrough, out _);

        var expected = new DateTime(2026, 7, 15, 7, 54, 5, DateTimeKind.Utc);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            var stored = Assert.IsType<DateTime>(r.Attributes["SourceDocumentDate"]);
            Assert.Equal(DateTimeKind.Utc, stored.Kind);
            Assert.Equal(expected, stored);
        });
    }

    [Fact]
    public void TheWinningValueNamesItsOwnSourceObject()
    {
        // The anchor reflects the latest window; its series-scoped attributes must come from the
        // document that delivered that window, not from whichever document came first.
        var c = Config();
        var series = TimeRangeSeriesShaper.Shape(Parse(
            $$"""
              [ { "MeteringPointRtId": "{{ParentRtId}}", "MeterCode": "G.03", "QuantityUnit": "kWh",
                  "EnergyQuantities": [ { "From": "2026-01-01T00:15:00Z", "To": "2026-01-01T00:30:00Z", "Quantity": 2.0 } ] },
                { "MeteringPointRtId": "{{ParentRtId}}", "MeterCode": "G.03", "QuantityUnit": "Wh",
                  "EnergyQuantities": [ { "From": "2026-01-01T00:30:00Z", "To": "2026-01-01T00:45:00Z", "Quantity": 3.0 } ] } ]
              """), c, out _);

        var winner = TimeRangeSeriesShaper.SelectAnchorValue(series[0].Values, c);

        Assert.Equal("Wh", series[0].SourceOf(winner)["QuantityUnit"]!.GetValue<string>());
        Assert.Equal("kWh", series[0].Series["QuantityUnit"]!.GetValue<string>());
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
        var rows = TimeRangeSeriesShaper.BuildRows(series, CkType, c, PassThrough, out _);

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

        var rows = TimeRangeSeriesShaper.BuildRows(series, CkType, c, PassThrough, out var skipped);

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

        var rows = TimeRangeSeriesShaper.BuildRows(series, CkType, c, PassThrough, out _);

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

    [Fact]
    public void EveryColumnValueGoesThroughTheConverter()
    {
        // Load-bearing, and the reason this is a parameter rather than a pass-through: the archive
        // stores an Enum attribute as its integer KEY, so the raw "L1" in the document has to become
        // 1 before it reaches an integer CrateDB column. The composition this node replaces got that
        // for free by routing every value through an RtEntity — exactly the round trip being removed
        // — so without the converter every write to the archive fails on a cast.
        var c = Config();
        var series = TimeRangeSeriesShaper.Shape(Parse(
            $$"""
              [ { "MeteringPointRtId": "{{ParentRtId}}", "MeterCode": "G.03", "QuantityUnit": "KWh",
                  "EnergyQuantities": [
                    { "From": "2026-01-01T00:00:00Z", "To": "2026-01-01T00:15:00Z", "Quantity": 1.0 } ] } ]
              """), c, out _);

        var seen = new List<(string Column, object? Raw)>();
        var rows = TimeRangeSeriesShaper.BuildRows(series, CkType, c,
            (column, raw) =>
            {
                seen.Add((column, raw));
                return raw is string s ? 1 : raw;   // stand-in for the enum name-to-key mapping
            },
            out _);

        Assert.Contains(("Amount.Unit", (object?)"KWh"), seen);
        Assert.Contains(("Amount.Value", (object?)1.0), seen);
        Assert.Equal(1, rows[0].Attributes["Amount.Unit"]);
        Assert.Equal(1.0, rows[0].Attributes["Amount.Value"]);
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
