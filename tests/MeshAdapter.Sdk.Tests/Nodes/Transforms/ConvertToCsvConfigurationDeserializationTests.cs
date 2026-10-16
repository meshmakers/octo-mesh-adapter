using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

/// <summary>
/// What a real pipeline definition produces, which a C# object initializer cannot reach: an
/// initializer applies only when the key is <em>absent</em>, so a definition carrying an explicit
/// null hands the node a value nobody wrote. Every option with a default is therefore nullable and
/// resolved where it is read; these tests pin that the YAML route reaches the same behaviour.
/// </summary>
public class ConvertToCsvConfigurationDeserializationTests
{
    private static async Task<ConvertToCsvNodeConfiguration> DeserializeAsync(string yaml)
    {
        var services = new ServiceCollection();
        var builder = services.AddDataPipelineSerializer();
        builder.RegisterNode(typeof(ConvertToCsvNode));
        var serializer = services.BuildServiceProvider().GetRequiredService<IPipelineConfigurationSerializer>();

        var root = await serializer.DeserializeAsync("transformations:\n" + yaml);
        return root.Transformations!.OfType<ConvertToCsvNodeConfiguration>().Single();
    }

    [Fact]
    public async Task Deserialize_OnlyPathsGiven_LeavesEveryOptionUnset()
    {
        var config = await DeserializeAsync("""
              - type: ConvertToCsv@1
                path: $.rows
                targetPath: $.csv
            """);

        Assert.Equal("$.rows", config.Path);
        Assert.Equal("$.csv", config.TargetPath);
        Assert.Null(config.Columns);
        Assert.Null(config.Delimiter);
        Assert.Null(config.Culture);
        Assert.Null(config.LineEnding);
        Assert.Null(config.Quoting);
        Assert.Null(config.IncludeHeader);
        Assert.Null(config.ByteOrderMark);
        Assert.Null(config.OutputAsBase64);
    }

    [Fact]
    public async Task Deserialize_FullDefinition_BindsEveryProperty()
    {
        var config = await DeserializeAsync("""
              - type: ConvertToCsv@1
                path: $.rows
                targetPath: $.csv
                delimiter: ";"
                culture: de-AT
                lineEnding: Lf
                includeHeader: true
                quoting: Text
                trailingDelimiter: true
                trailingNewLine: true
                byteOrderMark: true
                outputAsBase64: true
                contentLengthTargetPath: $.length
                columns:
                  - header: Datum
                    valuePath: $.from
                    dataType: DateTime
                    format: dd.MM.yyyy HH:mm
                    timeZone: Europe/Vienna
                  - header: kW
                    valuePath: $.kwh
                    factor: 4
                    culture: ""
                    decimals: 3
                    format: "0.###"
                    required: true
                  - header: Status
                    value: VALID
            """);

        Assert.Equal("de-AT", config.Culture);
        Assert.Equal(CsvLineEnding.Lf, config.LineEnding);
        Assert.Equal(CsvQuoting.Text, config.Quoting);
        Assert.True(config.TrailingDelimiter);
        Assert.True(config.ByteOrderMark);
        Assert.True(config.OutputAsBase64);
        Assert.Equal("$.length", config.ContentLengthTargetPath);

        var columns = config.Columns!.ToList();
        Assert.Equal(3, columns.Count);
        Assert.Equal(CsvValueType.DateTime, columns[0].DataType);
        Assert.Equal("Europe/Vienna", columns[0].TimeZone);
        Assert.Equal(4m, columns[1].Factor);
        Assert.Equal("", columns[1].Culture);
        Assert.Null(columns[0].Culture);
        Assert.Equal(3, columns[1].Decimals);
        Assert.True(columns[1].Required);
        Assert.Equal("VALID", columns[2].Value);
    }

    [Fact]
    public async Task Deserialize_ExplicitNulls_AreTheSameAsOmitted()
    {
        var config = await DeserializeAsync("""
              - type: ConvertToCsv@1
                path: $.rows
                targetPath: $.csv
                delimiter:
                culture:
                lineEnding:
                includeHeader:
                quoting:
                trailingDelimiter:
                trailingNewLine:
                byteOrderMark:
                outputAsBase64:
            """);

        Assert.Null(config.Delimiter);
        Assert.Null(config.Culture);
        Assert.Null(config.LineEnding);
        Assert.Null(config.IncludeHeader);
        Assert.Null(config.Quoting);
        Assert.Null(config.TrailingDelimiter);
        Assert.Null(config.TrailingNewLine);
        Assert.Null(config.ByteOrderMark);
        Assert.Null(config.OutputAsBase64);
    }
}
