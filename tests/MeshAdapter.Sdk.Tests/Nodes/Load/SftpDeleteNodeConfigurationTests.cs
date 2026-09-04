using Meshmakers.Octo.MeshAdapter.Nodes.Load;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

public class SftpDeleteNodeConfigurationTests
{
    private static SftpDeleteNodeConfiguration CreateConfig()
    {
        return new SftpDeleteNodeConfiguration
        {
            ServerConfiguration = "LkvSftp",
            RemotePath = "/out/AR00001.TXT"
        };
    }

    [Fact]
    public void MissingFileHandling_DefaultsToFail()
    {
        // Fail is also the zero member, so default(MissingFileHandling) is the strict value:
        // a definition that leaves the key out cannot end up on the lenient one by accident.
        Assert.Equal(MissingFileHandling.Fail, CreateConfig().MissingFileHandling);
        Assert.Equal(0, (int)MissingFileHandling.Fail);
        Assert.Equal(MissingFileHandling.Fail, default(MissingFileHandling));
    }

    [Theory]
    [InlineData(MissingFileHandling.Fail)]
    [InlineData(MissingFileHandling.Ignore)]
    public void MissingFileHandling_DefinedValue_IsAccepted(MissingFileHandling value)
    {
        var config = CreateConfig();

        config.MissingFileHandling = value;

        Assert.Equal(value, config.MissingFileHandling);
    }

    [Fact]
    public void MissingFileHandling_UndefinedValue_IsRejected()
    {
        var config = CreateConfig();

        var ex = Assert.Throws<ArgumentException>(() => config.MissingFileHandling = (MissingFileHandling)42);
        Assert.Contains("Fail or Ignore", ex.Message);
    }
}
