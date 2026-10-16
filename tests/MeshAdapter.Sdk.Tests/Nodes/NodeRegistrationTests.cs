using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MeshAdapter.Sdk.Tests.Nodes;

public class NodeRegistrationTests
{
    private static readonly Regex NodeClass = new(
        @"\[NodeConfiguration\(typeof\(\w+\)\)\][\s\S]*?\bclass\s+(?<name>\w+)", RegexOptions.Compiled);

    private static readonly Regex Registration = new(
        @"\.Register(?:Trigger)?Node<(?<name>\w+)>\(\)", RegexOptions.Compiled);

    [Fact]
    public void EveryNodeIsRegistered()
    {
        var root = SdkDirectory();
        var nodes = Directory.EnumerateFiles(Path.Combine(root, "Nodes"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => NodeClass.Matches(File.ReadAllText(file)).Select(m => m.Groups["name"].Value))
            .ToHashSet(StringComparer.Ordinal);
        var registered = Registration
            .Matches(File.ReadAllText(Path.Combine(root, "Configuration", "DependencyInjection",
                "ServiceCollectionExtensions.cs")))
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(nodes);
        Assert.Empty(nodes.Except(registered).Order());
    }

    private static string SdkDirectory([CallerFilePath] string thisFile = "")
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", ".."));
        return Path.Combine(repositoryRoot, "src", "MeshAdapter.Sdk");
    }
}
