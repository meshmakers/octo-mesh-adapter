using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

public class SftpContentDecoderTests : NodeTestBase
{
    private (INodeContext Context, IPipelineLogger Logger) Prepare()
    {
        var config = new SftpListNodeConfiguration
        {
            ServerConfiguration = "LkvSftp",
            RemoteDirectory = "/",
            FilePattern = "AR*TXT",
            TargetPath = "$.files"
        };
        var (_, nodeContext, _, logger) = PrepareTestWithLogger<SftpListNodeConfiguration>(config);
        return (nodeContext, logger);
    }

    [Fact]
    public void Decode_Latin1Bytes_ReadsUmlautAsOneByte()
    {
        var (nodeContext, _) = Prepare();
        // 0xFC is 'ü' in ISO-8859-1 and an invalid sequence in UTF-8.
        var bytes = new byte[] { 0x47, 0x72, 0xFC, 0x73, 0x73, 0x65 };

        var text = SftpContentDecoder.Decode(bytes, "iso-8859-1", EncodingErrorHandling.Replace, nodeContext);

        Assert.Equal("Grüsse", text);
    }

    [Fact]
    public void Decode_InvalidUtf8WithFail_Throws()
    {
        var (nodeContext, _) = Prepare();
        var bytes = new byte[] { 0x47, 0xFC, 0x73 };

        var ex = Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => SftpContentDecoder.Decode(bytes, "utf-8", EncodingErrorHandling.Fail, nodeContext));
        Assert.Contains("utf-8", ex.Message);
    }

    [Fact]
    public void Decode_InvalidUtf8WithReplace_ReplacesAndWarns()
    {
        var (nodeContext, logger) = Prepare();
        var bytes = new byte[] { 0x47, 0xFC, 0x73 };

        var text = SftpContentDecoder.Decode(bytes, "utf-8", EncodingErrorHandling.Replace, nodeContext);

        Assert.Contains("�", text);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._, A<string>._, A<object[]>._))
            .MustHaveHappened();
    }

    [Fact]
    public void Decode_Utf8WithByteOrderMark_DropsTheMark()
    {
        var (nodeContext, logger) = Prepare();
        // A BOM is valid UTF-8, so neither the strict pass nor OnEncodingError sees anything
        // wrong with it: kept, it would ride along as an invisible first character and turn a
        // downstream header comparison or split into a silent mismatch.
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat("Name;Value"u8.ToArray()).ToArray();

        var text = SftpContentDecoder.Decode(bytes, "utf-8", EncodingErrorHandling.Fail, nodeContext);

        Assert.Equal("Name;Value", text);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._, A<string>._, A<object[]>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public void Decode_Utf8WithByteOrderMarkAndUndecodableBytes_DropsTheMarkOnTheLenientPass()
    {
        var (nodeContext, _) = Prepare();
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, 0x47, 0xFC, 0x73 };

        var text = SftpContentDecoder.Decode(bytes, "utf-8", EncodingErrorHandling.Replace, nodeContext);

        // The lenient pass decodes a second time, so it has to strip the mark as well.
        Assert.Equal("G�s", text);
    }

    [Fact]
    public void Decode_ContentWithNoMark_KeepsItsFirstCharacter()
    {
        var (nodeContext, _) = Prepare();

        var text = SftpContentDecoder.Decode("Name;Value"u8.ToArray(), "utf-8", EncodingErrorHandling.Fail,
            nodeContext);

        Assert.Equal("Name;Value", text);
    }

    [Fact]
    public void Decode_MarkBytesUnderASingleByteCodePage_AreLeftAlone()
    {
        var (nodeContext, _) = Prepare();
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, 0x41 };

        var text = SftpContentDecoder.Decode(bytes, "iso-8859-1", EncodingErrorHandling.Fail, nodeContext);

        // Under a single-byte code page those three bytes are three ordinary characters, not a
        // mark. Reading them as one would be guessing that the operator picked the wrong
        // encoding, which is a decision only they can make.
        Assert.Equal("ï»¿A", text);
    }

    [Fact]
    public void Decode_ValidUtf8_NeedsNoReplacement()
    {
        var (nodeContext, logger) = Prepare();
        var bytes = "Grüsse"u8.ToArray();

        var text = SftpContentDecoder.Decode(bytes, "utf-8", EncodingErrorHandling.Replace, nodeContext);

        Assert.Equal("Grüsse", text);
        A.CallTo(() => logger.Warning(A<string>._, A<string>._, A<string>._, A<object[]>._))
            .MustNotHaveHappened();
    }
}
