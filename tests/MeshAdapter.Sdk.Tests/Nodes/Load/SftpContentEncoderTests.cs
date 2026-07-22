using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

public class SftpContentEncoderTests
{
    private readonly INodeContext _nodeContext;
    private readonly List<string> _warnings = [];

    public SftpContentEncoderTests()
    {
        _nodeContext = A.Fake<INodeContext>();
        A.CallTo(() => _nodeContext.Warning(A<string>._, A<object[]>._))
            .Invokes(call =>
            {
                var message = call.GetArgument<string>(0)!;
                var args = call.GetArgument<object[]>(1) ?? [];
                _warnings.Add(string.Format(message, args));
            });
    }

    [Fact]
    public void Encode_Utf8Default_EncodesUmlautAsUtf8Bytes()
    {
        var bytes = SftpContentEncoder.Encode("ö", "utf-8", EncodingErrorHandling.Replace, _nodeContext);

        Assert.Equal(new byte[] { 0xC3, 0xB6 }, bytes);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void Encode_Windows1252_EncodesUmlautsAndEuroAsSingleBytes()
    {
        var bytes = SftpContentEncoder.Encode("WeißGröße€", "windows-1252", EncodingErrorHandling.Replace, _nodeContext);

        Assert.Equal(
            new byte[] { 0x57, 0x65, 0x69, 0xDF, 0x47, 0x72, 0xF6, 0xDF, 0x65, 0x80 },
            bytes);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void Encode_Iso88591Replace_ReplacesEuroWithSingleQuestionMarkAndWarns()
    {
        var bytes = SftpContentEncoder.Encode("Grö€", "iso-8859-1", EncodingErrorHandling.Replace, _nodeContext);

        Assert.Equal(new byte[] { 0x47, 0x72, 0xF6, 0x3F }, bytes);
        var warning = Assert.Single(_warnings);
        Assert.Contains("U+20AC", warning);
        Assert.Contains("iso-8859-1", warning);
    }

    [Fact]
    public void Encode_Windows1252Replace_SurrogatePairCollapsesToOneQuestionMark()
    {
        var bytes = SftpContentEncoder.Encode("a\U0001D11Eb", "windows-1252", EncodingErrorHandling.Replace, _nodeContext);

        Assert.Equal(new byte[] { 0x61, 0x3F, 0x62 }, bytes);
        var warning = Assert.Single(_warnings);
        Assert.Contains("U+1D11E", warning);
    }

    [Fact]
    public void Encode_Windows1252Replace_LoneSurrogateBecomesOneQuestionMark()
    {
        var bytes = SftpContentEncoder.Encode("a\uD800b", "windows-1252", EncodingErrorHandling.Replace, _nodeContext);

        Assert.Equal(new byte[] { 0x61, 0x3F, 0x62 }, bytes);
        var warning = Assert.Single(_warnings);
        Assert.Contains("U+D800", warning);
    }

    [Fact]
    public void Encode_Utf8Replace_LoneSurrogateBecomesOneQuestionMark()
    {
        var bytes = SftpContentEncoder.Encode("a\uD800b", "utf-8", EncodingErrorHandling.Replace, _nodeContext);

        Assert.Equal(new byte[] { 0x61, 0x3F, 0x62 }, bytes);
        var warning = Assert.Single(_warnings);
        Assert.Contains("U+D800", warning);
    }

    [Fact]
    public void Encode_Iso88591Replace_RepeatedBadCharacterCountsEveryOccurrence()
    {
        var bytes = SftpContentEncoder.Encode("€€", "iso-8859-1", EncodingErrorHandling.Replace, _nodeContext);

        Assert.Equal(new byte[] { 0x3F, 0x3F }, bytes);
        var warning = Assert.Single(_warnings);
        Assert.Contains("2 character", warning);
        Assert.Contains("U+20AC", warning);
    }

    [Fact]
    public void Encode_Windows1252Fail_ThrowsListingCodePointsWithoutWarning()
    {
        var ex = Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => SftpContentEncoder.Encode("a\U0001D11Eb", "windows-1252", EncodingErrorHandling.Fail, _nodeContext));

        Assert.Contains("U+1D11E", ex.Message);
        Assert.Contains("windows-1252", ex.Message);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void Encode_ReplaceCleanContent_DoesNotWarn()
    {
        var bytes = SftpContentEncoder.Encode("plain ascii", "windows-1252", EncodingErrorHandling.Replace, _nodeContext);

        Assert.Equal("plain ascii".Length, bytes.Length);
        Assert.Empty(_warnings);
    }
}
