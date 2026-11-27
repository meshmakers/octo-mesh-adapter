using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Common;
using Meshmakers.Octo.MeshAdapter.Nodes.Configuration;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes;

/// <summary>
/// The <c>connectRetry</c> option of the four SFTP nodes: optional, one attempt unless configured,
/// and a value out of range is refused when the definition is bound.
/// </summary>
public class SftpConnectRetryOptionsTests
{
    [Fact]
    public void Defaults_AreOneAttemptAndOneSecond()
    {
        var options = new SftpConnectRetryOptions();

        Assert.Equal(1, options.MaxAttempts);
        Assert.Equal(1, options.BackoffBaseSeconds);
        Assert.Equal(10, SftpConnectRetryOptions.MaxAllowedAttempts);
        Assert.Equal(60, SftpConnectRetryOptions.MaxBackoffSeconds);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(11, false)]
    [InlineData(int.MaxValue, false)]
    public void MaxAttempts_OneToTen_AreAccepted_OthersRefusedNamingTheOption(int value, bool accepted)
    {
        var options = new SftpConnectRetryOptions();

        if (accepted)
        {
            options.MaxAttempts = value;
            Assert.Equal(value, options.MaxAttempts);
            return;
        }

        var ex = Assert.Throws<ArgumentException>(() => options.MaxAttempts = value);
        Assert.Contains(FormattableString.Invariant($"connectRetry.maxAttempts is {value}. Use 1 (a single attempt, no retry) to 10."),
            ex.Message);
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(10.0, true)]
    [InlineData(double.MaxValue, true)]
    [InlineData(-0.001, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(double.NegativeInfinity, false)]
    public void BackoffBaseSeconds_ZeroOrPositive_AreAccepted_NegativeOrNotFiniteRefused(double value, bool accepted)
    {
        var options = new SftpConnectRetryOptions();

        if (accepted)
        {
            options.BackoffBaseSeconds = value;
            Assert.Equal(value, options.BackoffBaseSeconds);
            return;
        }

        var ex = Assert.Throws<ArgumentException>(() => options.BackoffBaseSeconds = value);
        Assert.Contains("connectRetry.backoffBaseSeconds is ", ex.Message);
    }

    private const string ListYaml =
        "type: SftpList@1\n    serverConfiguration: s\n    remoteDirectory: /out\n    filePattern: '*'\n    targetPath: $.files";

    public static TheoryData<string> NodeYaml =>
    [
        ListYaml,
        "type: SftpDownload@1\n    serverConfiguration: s\n    remotePathPath: $.key.fullPath\n    targetPath: $.key.content",
        "type: SftpUpload@1\n    serverConfiguration: s\n    remoteDirectory: /in\n    fileName: a.txt\n    path: $.content",
        "type: SftpDelete@1\n    serverConfiguration: s\n    remotePathPath: $.key.fullPath"
    ];

    [Theory]
    [MemberData(nameof(NodeYaml))]
    public async Task Definition_WithoutTheOption_LoadsWithoutIt_AndWithItAsWritten(string node)
    {
        var without = await LoadAsync(node);
        var withOption = await LoadAsync(node + "\n    connectRetry:\n      maxAttempts: 3\n      backoffBaseSeconds: 10");

        Assert.Null(ConnectRetryOf(without));
        Assert.Equal(3, ConnectRetryOf(withOption)!.MaxAttempts);
        Assert.Equal(10, ConnectRetryOf(withOption)!.BackoffBaseSeconds);
    }

    [Theory]
    [InlineData("maxAttempts: 0", "connectRetry.maxAttempts")]
    [InlineData("maxAttempts: 11", "connectRetry.maxAttempts")]
    [InlineData("backoffBaseSeconds: -5", "connectRetry.backoffBaseSeconds")]
    [InlineData("maxAttemps: 3", "maxAttemps")]
    public async Task Definition_WithAValueOutOfRangeOrAMisspelledMember_FailsWhenThePipelineLoads(string line,
        string expected)
    {
        // A mistake fails the deployment instead of the first connection; a misspelled member
        // dropped silently would leave one attempt behind a definition that reads as retrying.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => LoadAsync(ListYaml + "\n    connectRetry:\n      " + line));

        // ToString holds the message of every inner exception.
        Assert.Contains(expected, ex.ToString());
    }

    private static async Task<INodeConfiguration> LoadAsync(string node)
    {
        var services = new ServiceCollection();
        services.AddDataPipelineSerializer().AddMeshDataPipelineNodes();
        var serializer = services.BuildServiceProvider().GetRequiredService<IPipelineConfigurationSerializer>();
        var root = await serializer.DeserializeAsync("transformations:\n  - " + node);
        return Assert.Single(root.Transformations!);
    }

    private static SftpConnectRetryOptions? ConnectRetryOf(INodeConfiguration configuration) => configuration switch
    {
        SftpListNodeConfiguration c => c.ConnectRetry,
        SftpDownloadNodeConfiguration c => c.ConnectRetry,
        SftpUploadNodeConfiguration c => c.ConnectRetry,
        SftpDeleteNodeConfiguration c => c.ConnectRetry,
        _ => throw new InvalidOperationException(configuration.GetType().Name)
    };
}

/// <summary>The schema offers <c>connectRetry</c> on all four SFTP nodes as an optional property.</summary>
[Collection(PipelineSchemaCollection.Name)]
public class SftpConnectRetrySchemaTests(PipelineSchemaFixture schema)
{
    [Theory]
    [InlineData("SftpList@1")]
    [InlineData("SftpDownload@1")]
    [InlineData("SftpUpload@1")]
    [InlineData("SftpDelete@1")]
    public void Schema_OffersConnectRetry_AsAnOptionalProperty(string nodeName)
    {
        var node = schema.NodeSchema(nodeName);

        Assert.Contains("connectRetry", node["properties"]!.AsObject().Select(p => p.Key));
        Assert.DoesNotContain("connectRetry",
            node["required"]?.AsArray().Select(r => r!.GetValue<string>()) ?? []);
    }
}
