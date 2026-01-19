using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class PlaceholderReplaceNodeTests
{
    private (IDataContext DataContext, INodeContext NodeContext) PrepareTest(
        PlaceholderReplaceNodeConfiguration config,
        JToken? testData,
        Dictionary<string, string?>? pathValues = null)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var dataContext = A.Fake<IDataContext>();

        A.CallTo(() => dataContext.Current).Returns(testData ?? new JObject());

        // Setup GetSimpleValueByPath for template value
        A.CallTo(() => dataContext.GetSimpleValueByPath<string>(config.Path))
            .ReturnsLazily(() =>
            {
                if (testData == null) return null;
                var token = testData.SelectToken(config.Path.TrimStart('$', '.'));
                return token?.Value<string>();
            });

        // Setup GetSimpleValueByPath for each replace rule path
        if (pathValues != null)
        {
            foreach (var (path, value) in pathValues)
            {
                A.CallTo(() => dataContext.GetSimpleValueByPath<string>(path)).Returns(value);
            }
        }

        var rootNodeContext = NodeContext.CreateRootNodeContext(services.BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("PlaceholderReplace", 0, config, dataContext);

        return (dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_SinglePlaceholder_ReplacesCorrectly()
    {
        var config = new PlaceholderReplaceNodeConfiguration
        {
            Path = "$.template",
            TargetPath = "$.result",
            ReplaceRules = new List<PlaceholderRule>
            {
                new() { Placeholder = "name", Path = "$.name" }
            }
        };

        var testData = new JObject
        {
            ["template"] = "Hello ${name}!",
            ["name"] = "World"
        };

        var pathValues = new Dictionary<string, string?>
        {
            ["$.name"] = "World"
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData, pathValues);
        var next = A.Fake<NodeDelegate>();
        var node = new PlaceholderReplaceNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath<string>(
            config.TargetPath,
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode,
            "Hello World!")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_MultiplePlaceholders_ReplacesAll()
    {
        var config = new PlaceholderReplaceNodeConfiguration
        {
            Path = "$.template",
            TargetPath = "$.result",
            ReplaceRules = new List<PlaceholderRule>
            {
                new() { Placeholder = "firstName", Path = "$.firstName" },
                new() { Placeholder = "lastName", Path = "$.lastName" }
            }
        };

        var testData = new JObject
        {
            ["template"] = "Hello ${firstName} ${lastName}!",
            ["firstName"] = "John",
            ["lastName"] = "Doe"
        };

        var pathValues = new Dictionary<string, string?>
        {
            ["$.firstName"] = "John",
            ["$.lastName"] = "Doe"
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData, pathValues);
        var next = A.Fake<NodeDelegate>();
        var node = new PlaceholderReplaceNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath<string>(
            config.TargetPath,
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode,
            "Hello John Doe!")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_CaseInsensitivePlaceholder_ReplacesCorrectly()
    {
        var config = new PlaceholderReplaceNodeConfiguration
        {
            Path = "$.template",
            TargetPath = "$.result",
            ReplaceRules = new List<PlaceholderRule>
            {
                new() { Placeholder = "NAME", Path = "$.name" }
            }
        };

        var testData = new JObject
        {
            ["template"] = "Hello ${name}!",
            ["name"] = "World"
        };

        var pathValues = new Dictionary<string, string?>
        {
            ["$.name"] = "World"
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData, pathValues);
        var next = A.Fake<NodeDelegate>();
        var node = new PlaceholderReplaceNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath<string>(
            config.TargetPath,
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode,
            "Hello World!")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_NullValue_DoesNotCallNext()
    {
        var config = new PlaceholderReplaceNodeConfiguration
        {
            Path = "$.template",
            TargetPath = "$.result",
            ReplaceRules = new List<PlaceholderRule>
            {
                new() { Placeholder = "name", Path = "$.name" }
            }
        };

        var testData = new JObject
        {
            ["name"] = "World"
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        var next = A.Fake<NodeDelegate>();
        var node = new PlaceholderReplaceNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_EmptyValue_DoesNotCallNext()
    {
        var config = new PlaceholderReplaceNodeConfiguration
        {
            Path = "$.template",
            TargetPath = "$.result",
            ReplaceRules = new List<PlaceholderRule>
            {
                new() { Placeholder = "name", Path = "$.name" }
            }
        };

        var testData = new JObject
        {
            ["template"] = "",
            ["name"] = "World"
        };

        var pathValues = new Dictionary<string, string?>
        {
            ["$.name"] = "World"
        };

        // Override the default return for empty string
        var (dataContext, nodeContext) = PrepareTest(config, testData, pathValues);
        A.CallTo(() => dataContext.GetSimpleValueByPath<string>(config.Path)).Returns("");

        var next = A.Fake<NodeDelegate>();
        var node = new PlaceholderReplaceNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WhitespaceValue_DoesNotCallNext()
    {
        var config = new PlaceholderReplaceNodeConfiguration
        {
            Path = "$.template",
            TargetPath = "$.result",
            ReplaceRules = new List<PlaceholderRule>
            {
                new() { Placeholder = "name", Path = "$.name" }
            }
        };

        var testData = new JObject
        {
            ["template"] = "   ",
            ["name"] = "World"
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData);
        A.CallTo(() => dataContext.GetSimpleValueByPath<string>(config.Path)).Returns("   ");

        var next = A.Fake<NodeDelegate>();
        var node = new PlaceholderReplaceNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_NoPlaceholders_ReturnsOriginalValue()
    {
        var config = new PlaceholderReplaceNodeConfiguration
        {
            Path = "$.template",
            TargetPath = "$.result",
            ReplaceRules = new List<PlaceholderRule>
            {
                new() { Placeholder = "name", Path = "$.name" }
            }
        };

        var testData = new JObject
        {
            ["template"] = "Hello World!",
            ["name"] = "Test"
        };

        var pathValues = new Dictionary<string, string?>
        {
            ["$.name"] = "Test"
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData, pathValues);
        var next = A.Fake<NodeDelegate>();
        var node = new PlaceholderReplaceNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath<string>(
            config.TargetPath,
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode,
            "Hello World!")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_NullReplacementValue_ReplacesWithEmpty()
    {
        var config = new PlaceholderReplaceNodeConfiguration
        {
            Path = "$.template",
            TargetPath = "$.result",
            ReplaceRules = new List<PlaceholderRule>
            {
                new() { Placeholder = "name", Path = "$.name" }
            }
        };

        var testData = new JObject
        {
            ["template"] = "Hello ${name}!"
        };

        var pathValues = new Dictionary<string, string?>
        {
            ["$.name"] = null
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData, pathValues);
        var next = A.Fake<NodeDelegate>();
        var node = new PlaceholderReplaceNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath<string>(
            config.TargetPath,
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode,
            "Hello !")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_MultipleSamePlaceholder_ReplacesAll()
    {
        var config = new PlaceholderReplaceNodeConfiguration
        {
            Path = "$.template",
            TargetPath = "$.result",
            ReplaceRules = new List<PlaceholderRule>
            {
                new() { Placeholder = "name", Path = "$.name" }
            }
        };

        var testData = new JObject
        {
            ["template"] = "Hello ${name}, goodbye ${name}!",
            ["name"] = "World"
        };

        var pathValues = new Dictionary<string, string?>
        {
            ["$.name"] = "World"
        };

        var (dataContext, nodeContext) = PrepareTest(config, testData, pathValues);
        var next = A.Fake<NodeDelegate>();
        var node = new PlaceholderReplaceNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.SetValueByPath<string>(
            config.TargetPath,
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode,
            "Hello World, goodbye World!")).MustHaveHappenedOnceExactly();
    }
}
