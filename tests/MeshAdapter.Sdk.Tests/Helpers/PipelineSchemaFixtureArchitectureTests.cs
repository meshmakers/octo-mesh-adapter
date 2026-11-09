using System.Reflection;
using System.Text.RegularExpressions;

namespace MeshAdapter.Sdk.Tests.Helpers;

public class PipelineSchemaFixtureArchitectureTests
{
    // Both interfaces lead into the schema build of the SDK: the generator, and the node schema registry it reads.
    // The SDK types that reach that build on their own (AdapterPoolMemberService, AdapterExecutionService,
    // AdapterNodeDescriptorProjection, AdapterBuilder, WebAdapterBuilder) are refused by name as well.
    // PipelineSchemaFixture itself may appear in another test file only in a declaration without an initializer,
    // such as a constructor parameter (also alone on its line) or a field; every other mention is refused, apart from
    // the forms under "Not covered" below, because the usual ones (new, a target-typed new, IClassFixture<>,
    // ICollectionFixture<>, Lazy<>, Activator, a using alias) build or hand out a second schema outside the
    // collection. The rule reads a file as one text without its comment lines, so an initializer on the next line or
    // on a later declarator counts as well. A declaration with three or more declarators, a nullable declaration, a
    // property of that type and a verbatim identifier right after the type (PipelineSchemaFixture @schema) are refused
    // too, even without an initializer, which errs on the safe side. The rule is a tripwire for realistic mistakes,
    // not a C# parser.
    //
    // Not covered (CLAUDE.md and PipelineSchemaFixture point to this list instead of repeating it):
    // - AdapterPoolClient: its RegisterAsync and ResumeAsync build the schema, but other tests use its name
    //   legitimately (the leases of the integration tests, the singleton sweep).
    // - A file outside tests/ that a project includes with <Compile Include>.
    // - A type name put together from parts for reflection.
    // - A declaration without an initializer (a variable, a field or a parameter) that receives a target-typed new()
    //   somewhere else, for example a helper parameter called with new().
    // - A second declarator with a verbatim identifier and an initializer (_a, @b = new()).
    // - A block comment inside the declaration (between the declarators or before the =).
    // - An end-of-line comment after the comma.
    private static readonly Regex SchemaBuild = new(
        @"\b(IPipelineSchemaGenerator|INodeSchemaRegistry)\b|" +
        @"\b(AdapterPoolMemberService|AdapterExecutionService|AdapterNodeDescriptorProjection)\b|" +
        @"\b(AdapterBuilder|WebAdapterBuilder)\b|" +
        @"\bPipelineSchemaFixture\b(?!\s+\w+\s*([);]|,(?!\s*\w+\s*[=,])))",
        RegexOptions.Compiled);

    private const string FixtureFile = "tests/MeshAdapter.Sdk.Tests/Helpers/PipelineSchemaFixture.cs";

    // This file names what it searches for.
    private const string ThisFile = "tests/MeshAdapter.Sdk.Tests/Helpers/PipelineSchemaFixtureArchitectureTests.cs";

    private static readonly string[] SchemaTestClasses =
    [
        "tests/MeshAdapter.Sdk.Tests/Nodes/Extract/GetRtEntitiesByTypePipelineSchemaTests.cs",
        "tests/MeshAdapter.Sdk.Tests/Nodes/Load/SftpDeleteNodeIdentityConfigurationTests.cs",
        "tests/MeshAdapter.Sdk.Tests/Secrets/Captcha/VerifyCaptchaPipelineSchemaTests.cs"
    ];

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "src", "MeshAdapter.Sdk")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static bool BuildsTheSchema(string source)
    {
        var withoutCommentLines = source.Split('\n').Where(line => !line.TrimStart().StartsWith("//"));
        return SchemaBuild.IsMatch(string.Join("\n", withoutCommentLines));
    }

    [Fact]
    public void OnlyThePipelineSchemaFixture_BuildsTheSchema()
    {
        var root = RepositoryRoot();
        var read = new List<string>();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/") || relative.Contains("/bin/") || relative is FixtureFile or ThisFile)
            {
                continue;
            }

            read.Add(relative);
            if (BuildsTheSchema(File.ReadAllText(file)))
            {
                offenders.Add(relative);
            }
        }

        // A scan that reads nothing would pass as well.
        Assert.True(read.Count >= 200, $"Only {read.Count} test files were read below {root}/tests.");
        foreach (var schemaTestClass in SchemaTestClasses)
        {
            Assert.Contains(schemaTestClass, read);
        }

        Assert.True(offenders.Count == 0,
            "Test files that use IPipelineSchemaGenerator, INodeSchemaRegistry or an SDK type that reaches " +
            "the schema build (AdapterPoolMemberService, AdapterExecutionService, AdapterNodeDescriptorProjection, " +
            "AdapterBuilder, WebAdapterBuilder), or mention PipelineSchemaFixture other than in a declaration " +
            "without an initializer: " + string.Join("; ", offenders) +
            ". Building the schema on two threads at once can hang the test run: " +
            "join [Collection(PipelineSchemaCollection.Name)] and " +
            "take PipelineSchemaFixture in the constructor instead. " +
            "That collection exists only in MeshAdapter.Sdk.Tests; a test that needs the schema belongs there.");
    }

    [Theory]
    [InlineData("private readonly PipelineSchemaFixture _alias, _schema = new();", true)]
    [InlineData("private static readonly PipelineSchemaFixture Shared\n    = new();", true)]
    [InlineData("private readonly PipelineSchemaFixture _schema = new();", true)]
    [InlineData("public class A(PipelineSchemaFixture schema) : IClassFixture<PipelineSchemaFixture>", true)]
    [InlineData("using F = MeshAdapter.Sdk.Tests.Helpers.PipelineSchemaFixture;", true)]
    [InlineData("var fixture = new global::MeshAdapter.Sdk.Tests.Helpers.PipelineSchemaFixture();", true)]
    [InlineData("public A(INodeSchemaRegistry registry)", true)]
    [InlineData("var builder = new AdapterBuilder();", true)]
    [InlineData("private readonly PipelineSchemaFixture _a, _b, _schema = new();", true)]
    [InlineData("private readonly PipelineSchemaFixture @schema;", true)]
    [InlineData("public class A(PipelineSchemaFixture schema)", false)]
    [InlineData("public class A(\n    PipelineSchemaFixture schema\n)", false)]
    [InlineData("public class A(PipelineSchemaFixture schema, ITestOutputHelper output)", false)]
    [InlineData("public A(PipelineSchemaFixture schema, int x = 0)", false)]
    [InlineData("private readonly PipelineSchemaFixture _schema;", false)]
    [InlineData("private readonly PipelineSchemaFixture _a, _b;", false)]
    public void TheRule_RefusesWhatBuildsASecondSchema_AndAllowsADeclarationWithoutAnInitializer(string source,
        bool refused)
    {
        Assert.Equal(refused, BuildsTheSchema(source));
    }

    [Fact]
    public void ThePipelineSchemaCollection_RunsWithoutParallelism()
    {
        var definition = typeof(PipelineSchemaCollection).GetCustomAttribute<CollectionDefinitionAttribute>();

        Assert.NotNull(definition);
        Assert.True(definition.DisableParallelization,
            "PipelineSchemaCollection must keep DisableParallelization = true: its classes build the schema, " +
            "which must not happen on two threads at once.");
    }
}
