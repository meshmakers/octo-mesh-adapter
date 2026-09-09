using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MeshAdapter.Sdk.Tests.Nodes;

/// <summary>
///     Pins the session-identity classification of every node in the SDK (AB#5028).
/// </summary>
/// <remarks>
///     <para>
///         The behavioural tests prove that the nodes they drive open the session they should. This
///         one closes the gap they cannot: it proves that <b>every</b> session in <c>Nodes/</c> is
///         classified at all, that none reaches the repository directly, and that the classification
///         is written down next to the call site. A new node that opens a session without deciding —
///         or a migration that quietly flips one — fails here with the table it has to update.
///     </para>
///     <para>
///         The source is located through <see cref="CallerFilePathAttribute" />, i.e. the path the
///         compiler recorded, so the test finds the repository it was built from on a developer
///         machine and on a build agent alike.
///     </para>
/// </remarks>
public class SessionIdentityClassificationTests
{
    private static readonly Regex ScopedAsync = new(@"GetScopedSessionAsync\(\)", RegexOptions.Compiled);
    private static readonly Regex ScopedSync = new(@"GetScopedSession\(\)", RegexOptions.Compiled);
    private static readonly Regex SystemAsync = new(@"GetSystemSessionAsync\(\)", RegexOptions.Compiled);
    private static readonly Regex SystemSync = new(@"GetSystemSession\(\)", RegexOptions.Compiled);

    // AB#5127: the caller-scoped data nodes now select their session from their configuration's
    // `identity` value via GetSessionForAsync(...). The default is Caller, so a configurable call site
    // is classified as scoped here — it is a caller-scoped node that also accepts an opt-in elevation.
    private static readonly Regex Configurable = new(@"GetSessionForAsync\(", RegexOptions.Compiled);

    /// <summary>
    ///     The classification, file by file: how many scoped and how many system sessions each node
    ///     opens. Adding a node means adding a row; changing a node's identity means changing one —
    ///     both are exactly the reviewable moments this table exists for.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (int Scoped, int System)> ExpectedClassification =
        new Dictionary<string, (int, int)>(StringComparer.Ordinal)
        {
            // --- Extract -----------------------------------------------------------------------
            ["Extract/BackfillFromRtEntityNode.cs"] = (0, 1),
            ["Extract/GetAssociationTargetsNode.cs"] = (1, 0),
            ["Extract/GetFileSystemContentNode.cs"] = (0, 1),
            ["Extract/GetNotificationTemplateNode.cs"] = (0, 1),
            ["Extract/GetOrCreateRtEntitiesByTypeNode.cs"] = (1, 0),
            ["Extract/GetQueryByIdNode.cs"] = (1, 0),
            ["Extract/GetRtEntitiesByIdNode.cs"] = (1, 0),
            ["Extract/GetRtEntitiesByTypeNode.cs"] = (1, 0),
            ["Extract/GetRtEntitiesByWellKnownNameTypeNode.cs"] = (1, 0),

            // --- Load --------------------------------------------------------------------------
            ["Load/ApplyChangesNode.cs"] = (0, 1),
            ["Load/ApplyChangesNode2.cs"] = (1, 0),
            ["Load/DeployPipelineNode.cs"] = (0, 1),
            ["Load/EMailSenderNode.cs"] = (0, 1),
            ["Load/SaveTimeRangeStreamDataInArchive.cs"] = (0, 1),
            ["Load/SftpUploadNode.cs"] = (0, 1),
            ["Load/ToDiscordNode.cs"] = (0, 2),
            ["Load/UpdateRtEntityIfNewerNode.cs"] = (1, 0),

            // --- Transform ---------------------------------------------------------------------
            ["Transform/ApplyDataPointMappingsNode.cs"] = (1, 0),
            ["Transform/BuildMappingTargetsNode.cs"] = (1, 0),
            ["Transform/CheckDuplicateNode.cs"] = (0, 1),
            // Two identities in one node: the artefact is the caller's, the FolderRoot is the
            // platform's.
            ["Transform/CreateFileSystemItemUpdateNode.cs"] = (1, 1),
            ["Transform/CreateZipArchiveNode.cs"] = (1, 1),
            ["Transform/ExportDataPointMappingsNode.cs"] = (0, 1),
            ["Transform/GenerateDataPointMappingsNode.cs"] = (1, 0),
            ["Transform/ImportDataPointMappingsNode.cs"] = (0, 1),
            ["Transform/ImportFromExcelNode.cs"] = (0, 1),
            ["Transform/SimulateEnergyMeasurementsNode.cs"] = (1, 0),
            ["Transform/ValidateDataPointCoverageNode.cs"] = (1, 0),
            ["Transform/ExcelImport/WellKnownNameLoader.cs"] = (0, 1)
        };

    [Fact]
    public void EveryNodeSessionIsClassifiedExactlyAsTheTableSays()
    {
        var actual = ScanNodes()
            .Where(e => e.Value.Scoped > 0 || e.Value.System > 0)
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

        var unexpected = actual.Keys.Except(ExpectedClassification.Keys, StringComparer.Ordinal).ToList();
        Assert.True(unexpected.Count == 0,
            "These node files open a session but are not in the AB#5028 classification table — decide "
            + "whether they are scoped or system, say so in a code comment and add them here: "
            + string.Join(", ", unexpected));

        var gone = ExpectedClassification.Keys.Except(actual.Keys, StringComparer.Ordinal).ToList();
        Assert.True(gone.Count == 0,
            "These files no longer open a session; remove them from the classification table: "
            + string.Join(", ", gone));

        foreach (var (file, expected) in ExpectedClassification)
        {
            Assert.Equal(expected, actual[file]);
        }
    }

    [Fact]
    public void TheTotalsMatchTheWorkItem()
    {
        var actual = ScanNodes().Values.ToList();

        // 32 sites, not the 31 the work item estimated — ToDiscord@1 opens two (the entity lookup
        // and the binary download) and both had to be decided separately. AB#5127 did not add or
        // remove a site: it turned all 15 scoped ones into config-selected ones (counted as scoped,
        // default Caller), so the totals are unchanged.
        Assert.Equal(15, actual.Sum(v => v.Scoped));
        Assert.Equal(17, actual.Sum(v => v.System));
    }

    [Fact]
    public void NoNodeReachesTheRepositorySessionFactoryDirectly()
    {
        // The whole point of IMeshEtlContext.Get*Session* is that TenantRepositorySecurityExtensions
        // degrades into the parameterless system session SILENTLY. A node that still calls the
        // repository is a node whose identity nobody decided.
        var offenders = EnumerateNodeSources()
            .Where(f => CodeLines(File.ReadAllLines(f.Path))
                .Any(l => l.Contains("TenantRepository.GetSessionAsync()", StringComparison.Ordinal)
                          || l.Contains("TenantRepository.GetSession()", StringComparison.Ordinal)))
            .Select(f => f.RelativePath)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These node files open a session on the repository instead of through IMeshEtlContext "
            + "(AB#5028): " + string.Join(", ", offenders));
    }

    [Fact]
    public void EverySessionCallSiteCarriesItsReasoning()
    {
        var undocumented = new List<string>();

        foreach (var (relativePath, path) in EnumerateNodeSources())
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i]) || CountSessions(lines[i]) == 0)
                {
                    continue;
                }

                // The reasoning sits directly above the call, or — for the two split-identity helper
                // methods — in the method's own XML remarks a few lines further up.
                var windowStart = Math.Max(0, i - 14);
                var documented = false;
                for (var j = windowStart; j < i; j++)
                {
                    // AB#5028 classified every site originally; AB#5127 turned the caller-scoped ones
                    // into config-selected ones — either marker documents the decision.
                    if (lines[j].Contains("AB#5028", StringComparison.Ordinal)
                        || lines[j].Contains("AB#5127", StringComparison.Ordinal))
                    {
                        documented = true;
                        break;
                    }
                }

                if (!documented)
                {
                    undocumented.Add($"{relativePath}:{i + 1}");
                }
            }
        }

        Assert.True(undocumented.Count == 0,
            "These session call sites carry no AB#5028 / AB#5127 comment saying which identity they "
            + "use and what breaks if it were the other one: " + string.Join(", ", undocumented));
    }

    [Fact]
    public void OnlyTheTwoKnownCallSitesAreSynchronous()
    {
        var synchronous = new List<string>();

        foreach (var (relativePath, path) in EnumerateNodeSources())
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i]))
                {
                    continue;
                }

                if (ScopedSync.IsMatch(lines[i]) || SystemSync.IsMatch(lines[i]))
                {
                    synchronous.Add(relativePath);
                }
            }
        }

        // The work item calls these out because a search for "GetSessionAsync" alone misses them.
        Assert.Equal(
            new[] { "Transform/ExcelImport/WellKnownNameLoader.cs", "Transform/ImportFromExcelNode.cs" },
            synchronous.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    private static Dictionary<string, (int Scoped, int System)> ScanNodes()
    {
        var result = new Dictionary<string, (int Scoped, int System)>(StringComparer.Ordinal);

        foreach (var (relativePath, path) in EnumerateNodeSources())
        {
            var scoped = 0;
            var system = 0;
            foreach (var line in CodeLines(File.ReadAllLines(path)))
            {
                scoped += ScopedAsync.Matches(line).Count + ScopedSync.Matches(line).Count
                          + Configurable.Matches(line).Count;
                system += SystemAsync.Matches(line).Count + SystemSync.Matches(line).Count;
            }

            result[relativePath] = (scoped, system);
        }

        return result;
    }

    private static IEnumerable<(string RelativePath, string Path)> EnumerateNodeSources()
    {
        var root = NodesDirectory();
        Assert.True(Directory.Exists(root), $"Node sources not found at '{root}'.");

        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(p => (Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'), p))
            .OrderBy(t => t.Item1, StringComparer.Ordinal);
    }

    private static IEnumerable<string> CodeLines(IEnumerable<string> lines)
    {
        return lines.Where(l => !IsComment(l));
    }

    private static bool IsComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
               || trimmed.StartsWith("*", StringComparison.Ordinal);
    }

    private static int CountSessions(string line)
    {
        return ScopedAsync.Matches(line).Count + ScopedSync.Matches(line).Count
               + SystemAsync.Matches(line).Count + SystemSync.Matches(line).Count
               + Configurable.Matches(line).Count;
    }

    /// <summary>
    ///     <c>&lt;repo&gt;/src/MeshAdapter.Sdk/Nodes</c>, derived from this file's own compile-time
    ///     path (<c>&lt;repo&gt;/tests/MeshAdapter.Sdk.Tests/Nodes/…</c>).
    /// </summary>
    private static string NodesDirectory([CallerFilePath] string thisFile = "")
    {
        var repositoryRoot = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", ".."));
        return Path.Combine(repositoryRoot, "src", "MeshAdapter.Sdk", "Nodes");
    }
}
