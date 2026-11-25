using System.Text.RegularExpressions;

namespace MeshAdapter.Sdk.Tests.Services.CrossTenant;

/// <summary>
///     "One door": the cross-tenant guard is the only adapter code that opens <b>another</b> tenant's repository or
///     context. Every call of <c>ISystemContext.(Try)Find(TenantRepository|TenantContext)Async</c> and every child-tenant
///     lookup in <c>src/</c> is listed here; a new one fails this test until it is reviewed and added with its reason.
/// </summary>
public class CrossTenantCallSiteArchitectureTests
{
    private static readonly Regex TenantOpenCall = new(
        @"\.\s*(TryFindTenantRepositoryAsync|FindTenantRepositoryAsync|TryFindTenantContextAsync|FindTenantContextAsync|GetChildTenantContextAsync|TryGetChildTenantContextAsync)\s*\(",
        RegexOptions.Compiled);

    /// <summary>File → number of call sites, with the reason they may open a tenant.</summary>
    private static readonly IReadOnlyDictionary<string, int> AllowList = new Dictionary<string, int>
    {
        // The guard: the one place that opens another tenant, after relation and identity proof.
        ["src/MeshAdapter.Sdk/Services/CrossTenant/CrossTenantAccessGuard.cs"] = 1,

        // Own tenant only: the registration's tenant of the execution, trigger or lease.
        ["src/MeshAdapter.Sdk/Services/MeshContextCreatorService.cs"] = 1,
        ["src/MeshAdapter.Sdk/Nodes/Trigger/FromWatchRtEntityNode.cs"] = 1,
        ["src/MeshAdapter.Sdk/Leasing/CkModelCacheLeaseParticipant.cs"] = 1,
        ["src/MeshAdapter.Sdk/Services/CkModelWarmupService.cs"] = 1,
        ["src/MeshAdapter.Sdk/Nodes/Load/SaveTimeRangeStreamDataInArchive.cs"] = 1,
        ["src/MeshAdapter.Sdk/Nodes/Load/SaveStreamDataInArchive.cs"] = 1,
        ["src/MeshAdapter.Sdk/Nodes/Load/SaveTimeRangeSeriesInArchiveNode.cs"] = 2,
        ["src/MeshAdapter.Sdk/Nodes/Extract/BackfillFromRtEntityNode.cs"] = 1,
        ["src/MeshAdapter.Sdk/Nodes/Extract/DetectStreamDataGapsNode.cs"] = 1,
        ["src/MeshAdapter.Sdk/Nodes/Extract/GetStreamDataNode.cs"] = 1,
        ["src/MeshAdapter.Sdk/Nodes/Extract/GetQueryByIdNode.cs"] = 1,
        ["src/MeshAdapter.Sdk/Nodes/Extract/AggregateStreamDataNode.cs"] = 1,
        ["src/MeshAdapter.Sdk/Nodes/Transform/CommunityAllocation/CommunityInputs.cs"] = 1,

        // Own tenant only: caller-binding directories resolve a sender in the trigger's tenant.
        ["src/MeshAdapter.Sdk/Services/CallerBinding/CkSubjectChannelLookup.cs"] = 1,
        ["src/MeshAdapter.Sdk/Services/CallerBinding/CkEntraIdUserLookup.cs"] = 1,
        ["src/MeshAdapter.Sdk/Services/CallerBinding/CkEmailUserLookup.cs"] = 1,
        ["src/MeshAdapter.Sdk/Services/CallerBinding/CkPhoneUserLookup.cs"] = 1
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
                .Count(line => TenantOpenCall.IsMatch(line));
            if (count > 0)
            {
                found[relative] = count;
            }
        }

        return found;
    }

    [Fact]
    public void EveryTenantOpeningCallSite_IsOnTheAllowList()
    {
        var unexpected = FindCallSites()
            .Where(site => !AllowList.TryGetValue(site.Key, out var allowed) || site.Value > allowed)
            .Select(site => $"{site.Key} ({site.Value} call sites)")
            .ToList();

        Assert.True(unexpected.Count == 0,
            "New call sites that open a tenant outside the cross-tenant guard: " + string.Join("; ", unexpected) +
            ". Another tenant is reached only through IMeshEtlContext.OpenTenantAsync.");
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
    [InlineData("var r = await systemContext.FindTenantRepositoryAsync(other);")]
    [InlineData("var c = await _systemContext . TryFindTenantContextAsync(id);")]
    [InlineData("var c = await tenantContext.GetChildTenantContextAsync(adminSession, id);")]
    public void ThePattern_MatchesTheCallShapes(string line)
    {
        Assert.Matches(TenantOpenCall, line);
    }
}
