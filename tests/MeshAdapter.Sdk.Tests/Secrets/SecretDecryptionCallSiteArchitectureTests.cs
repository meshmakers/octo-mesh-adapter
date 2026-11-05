using System.Text.RegularExpressions;

namespace MeshAdapter.Sdk.Tests.Secrets;

public class SecretDecryptionCallSiteArchitectureTests
{
    private static readonly Regex DecryptCall = new(@"\.\s*(Unprotect|RevealOrNull|GetSecretPlaintext)\s*\(", RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, int> AllowList = new Dictionary<string, int>
    {
        ["src/MeshAdapter.Sdk/Nodes/Extract/RevealSecretNode.cs"] = 1,
        ["src/MeshAdapter.Sdk/Services/ServiceAccountTokenService.cs"] = 1
    };

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "src", "MeshAdapter.Sdk")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static Dictionary<string, int> FindCallSites()
    {
        var root = RepositoryRoot();
        var found = new Dictionary<string, int>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/") || relative.Contains("/bin/"))
            {
                continue;
            }

            var count = File.ReadLines(file)
                .Where(line => !line.TrimStart().StartsWith("//"))
                .Count(line => DecryptCall.IsMatch(line));
            if (count > 0)
            {
                found[relative] = count;
            }
        }

        return found;
    }

    [Fact]
    public void EveryDecryptionCallSite_IsOnTheAllowList()
    {
        var found = FindCallSites();

        var unexpected = found
            .Where(site => !AllowList.TryGetValue(site.Key, out var allowed) || site.Value > allowed)
            .Select(site => $"{site.Key} ({site.Value} call sites)")
            .ToList();

        Assert.True(unexpected.Count == 0,
            "New decryption call sites of Unprotect/RevealOrNull/GetSecretPlaintext outside the allow-list: " +
            string.Join("; ", unexpected));
    }

    [Fact]
    public void TheAllowList_HasNoStaleEntries()
    {
        var found = FindCallSites();

        var stale = AllowList.Where(entry => !found.TryGetValue(entry.Key, out var count) || count != entry.Value)
            .Select(entry => entry.Key)
            .ToList();

        Assert.True(stale.Count == 0, "Allow-list entries without a matching call site: " + string.Join("; ", stale));
    }

    [Theory]
    [InlineData("var plain = protector.Unprotect(value, context);")]
    [InlineData("var plain = protector.RevealOrNull(value, context);")]
    [InlineData("var plain = entity.GetSecretPlaintext(\"Password\", protector, tenantId);")]
    [InlineData("    .GetSecretPlaintext (\"Password\", protector);")]
    public void TheDetection_FindsEveryDecryptingCall(string line)
    {
        Assert.Matches(DecryptCall, line);
    }

    [Fact]
    public void RevealSecretNode_ConsultsThePlatformOnlyRuleBeforeItDecrypts()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(),
            "src/MeshAdapter.Sdk/Nodes/Extract/RevealSecretNode.cs"));

        var ruleIndex = source.IndexOf("PlatformOnlySecrets.", StringComparison.Ordinal);
        var decryptIndex = source.IndexOf("secretAttributeProtector.Unprotect(", StringComparison.Ordinal);

        Assert.True(ruleIndex >= 0, "RevealSecretNode does not use PlatformOnlySecrets.");
        Assert.True(ruleIndex < decryptIndex, "RevealSecretNode decrypts before it consults PlatformOnlySecrets.");
    }
}
