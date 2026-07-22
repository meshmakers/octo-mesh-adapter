using System.Text.Json;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

public class SftpUploadNodeConfigurationTests
{
    private static SftpUploadNodeConfiguration CreateConfig()
    {
        return new SftpUploadNodeConfiguration
        {
            ServerConfiguration = "sftp-server-1",
            RemoteDirectory = "/upload/test",
        };
    }

    [Fact]
    public void Defaults_AreUtf8AndReplace()
    {
        var config = CreateConfig();

        Assert.Equal("utf-8", config.Encoding);
        Assert.Equal(EncodingErrorHandling.Replace, config.OnEncodingError);
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("UTF-8")]
    [InlineData("windows-1252")]
    [InlineData("iso-8859-1")]
    public void Encoding_KnownName_IsAccepted(string encodingName)
    {
        var config = CreateConfig();

        config.Encoding = encodingName;

        Assert.Equal(encodingName, config.Encoding);
    }

    [Fact]
    public void Encoding_UnknownName_ThrowsNamingTheValue()
    {
        var config = CreateConfig();

        var ex = Assert.Throws<ArgumentException>(() => config.Encoding = "utf-99");

        Assert.Contains("utf-99", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Encoding_EmptyOrWhitespace_Throws(string encodingName)
    {
        var config = CreateConfig();

        Assert.Throws<ArgumentException>(() => config.Encoding = encodingName);
    }

    [Theory]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    [InlineData("utf-32")]
    public void Encoding_ByteOrderSensitiveEncoding_IsRejected(string encodingName)
    {
        var config = CreateConfig();

        var ex = Assert.Throws<ArgumentException>(() => config.Encoding = encodingName);

        Assert.Contains(encodingName, ex.Message);
    }

    [Fact]
    public void OnEncodingError_UndefinedEnumValue_Throws()
    {
        var config = CreateConfig();

        var ex = Assert.Throws<ArgumentException>(() => config.OnEncodingError = (EncodingErrorHandling)5);

        Assert.Contains("5", ex.Message);
    }

    [Fact]
    public void Deserialize_WithoutEncodingProperties_KeepsBackwardCompatibleDefaults()
    {
        const string json =
            """
            {"serverConfiguration":"sftp-server-1","remoteDirectory":"/upload/test","fileName":"report.csv","path":"$.content"}
            """;

        var config = JsonSerializer.Deserialize<SftpUploadNodeConfiguration>(json, SystemTextJsonOptions.Default);

        Assert.NotNull(config);
        Assert.Equal("utf-8", config.Encoding);
        Assert.Equal(EncodingErrorHandling.Replace, config.OnEncodingError);
    }

    [Fact]
    public void Deserialize_KnownEncodingAndErrorMode_Binds()
    {
        const string json =
            """
            {"serverConfiguration":"sftp-server-1","remoteDirectory":"/upload/test","fileName":"report.csv","path":"$.content","encoding":"windows-1252","onEncodingError":"Fail"}
            """;

        var config = JsonSerializer.Deserialize<SftpUploadNodeConfiguration>(json, SystemTextJsonOptions.Default);

        Assert.NotNull(config);
        Assert.Equal("windows-1252", config.Encoding);
        Assert.Equal(EncodingErrorHandling.Fail, config.OnEncodingError);
    }

    [Fact]
    public void Deserialize_UnknownEncoding_FailsAtBindTime()
    {
        const string json =
            """
            {"serverConfiguration":"sftp-server-1","remoteDirectory":"/upload/test","fileName":"report.csv","path":"$.content","encoding":"utf-99"}
            """;

        var ex = Assert.ThrowsAny<Exception>(
            () => JsonSerializer.Deserialize<SftpUploadNodeConfiguration>(json, SystemTextJsonOptions.Default));

        Assert.Contains("utf-99", Flatten(ex));
    }

    private static string Flatten(Exception exception)
    {
        var message = exception.Message;
        for (var inner = exception.InnerException; inner != null; inner = inner.InnerException)
        {
            message += " | " + inner.Message;
        }

        return message;
    }
}
