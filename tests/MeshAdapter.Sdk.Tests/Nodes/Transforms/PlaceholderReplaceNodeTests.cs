using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class PlaceholderReplaceNodeTests : NodeTestBase
{
    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest(
        PlaceholderReplaceNodeConfiguration config,
        JsonNode? testData,
        Dictionary<string, string?>? pathValues = null)
    {
        var setup = PrepareTest<PlaceholderReplaceNodeConfiguration>(config, testData);

        // Setup Get<string> for the template path, sourcing from testData when provided.
        A.CallTo(() => setup.DataContext.Get<string>(config.Path))
            .ReturnsLazily(() =>
            {
                if (testData is null) return null;
                var node = ResolvePath(testData, config.Path);
                return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            });

        if (pathValues != null)
        {
            foreach (var (path, value) in pathValues)
            {
                A.CallTo(() => setup.DataContext.Get<string>(path))
                    .Returns(value);
            }
        }

        return setup;
    }

    private static JsonNode? ResolvePath(JsonNode? root, string path)
    {
        if (root is null) return null;
        var trimmed = path.StartsWith("$.") ? path[2..] : path.StartsWith('$') ? path[1..] : path;
        if (string.IsNullOrEmpty(trimmed)) return root;
        JsonNode? cursor = root;
        foreach (var segment in trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cursor is JsonObject obj && obj.TryGetPropertyValue(segment, out var next))
            {
                cursor = next;
            }
            else
            {
                return null;
            }
        }
        return cursor;
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

        var testData = new JsonObject
        {
            ["template"] = "Hello ${name}!",
            ["name"] = "World"
        };

        var pathValues = new Dictionary<string, string?>
        {
            ["$.name"] = "World"
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData, pathValues);
        var node = new PlaceholderReplaceNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        A.CallTo(() => dataContext.Set<string>(
            config.TargetPath,
            "Hello World!",
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
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

        var testData = new JsonObject
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

        var (dataContext, nodeContext, next) = PrepareTest(config, testData, pathValues);
        var node = new PlaceholderReplaceNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        A.CallTo(() => dataContext.Set<string>(
            config.TargetPath,
            "Hello John Doe!",
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
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

        var testData = new JsonObject
        {
            ["template"] = "Hello ${name}!",
            ["name"] = "World"
        };

        var pathValues = new Dictionary<string, string?>
        {
            ["$.name"] = "World"
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData, pathValues);
        var node = new PlaceholderReplaceNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        A.CallTo(() => dataContext.Set<string>(
            config.TargetPath,
            "Hello World!",
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
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

        var testData = new JsonObject
        {
            ["name"] = "World"
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);
        var node = new PlaceholderReplaceNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextNotCalled(next, dataContext, nodeContext);
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

        var testData = new JsonObject
        {
            ["template"] = "",
            ["name"] = "World"
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);
        var node = new PlaceholderReplaceNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextNotCalled(next, dataContext, nodeContext);
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

        var testData = new JsonObject
        {
            ["template"] = "   ",
            ["name"] = "World"
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);
        var node = new PlaceholderReplaceNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextNotCalled(next, dataContext, nodeContext);
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

        var testData = new JsonObject
        {
            ["template"] = "Hello World!",
            ["name"] = "Test"
        };

        var pathValues = new Dictionary<string, string?>
        {
            ["$.name"] = "Test"
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData, pathValues);
        var node = new PlaceholderReplaceNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        A.CallTo(() => dataContext.Set<string>(
            config.TargetPath,
            "Hello World!",
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
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

        var testData = new JsonObject
        {
            ["template"] = "Hello ${name}!"
        };

        var pathValues = new Dictionary<string, string?>
        {
            ["$.name"] = null
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData, pathValues);
        var node = new PlaceholderReplaceNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        A.CallTo(() => dataContext.Set<string>(
            config.TargetPath,
            "Hello !",
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
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

        var testData = new JsonObject
        {
            ["template"] = "Hello ${name}, goodbye ${name}!",
            ["name"] = "World"
        };

        var pathValues = new Dictionary<string, string?>
        {
            ["$.name"] = "World"
        };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData, pathValues);
        var node = new PlaceholderReplaceNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        A.CallTo(() => dataContext.Set<string>(
            config.TargetPath,
            "Hello World, goodbye World!",
            config.DocumentMode,
            config.TargetValueKind,
            config.TargetValueWriteMode)).MustHaveHappenedOnceExactly();
    }
}
