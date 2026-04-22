using System.Net;
using System.Net.Http;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

public class ToDiscordNodeTests
{
    private const string ServerConfig = "discord-1";
    private const string BotToken = "bot-token-xyz";
    private const string ChannelId = "123456789012345678";

    private readonly IMeshEtlContext _etlContext;
    private readonly IGlobalConfiguration _globalConfiguration;
    private readonly ITenantRepository _tenantRepository;
    private readonly Dictionary<string, object?> _properties;
    private readonly RecordingHandler _handler;
    private readonly IHttpClientFactory _httpClientFactory;

    public ToDiscordNodeTests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        _globalConfiguration = A.Fake<IGlobalConfiguration>();
        _tenantRepository = A.Fake<ITenantRepository>();
        _properties = new Dictionary<string, object?>();

        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);
        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _etlContext.Properties).Returns(_properties);

        _handler = new RecordingHandler(HttpStatusCode.OK,
            """{"id":"111","channel_id":"123456789012345678","content":"hi"}""");
        _httpClientFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => _httpClientFactory.CreateClient("Discord"))
            .Returns(new HttpClient(_handler, disposeHandler: false));

        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<ToDiscordNode.DiscordConfiguration>(ServerConfig))
            .Returns(new ToDiscordNode.DiscordConfiguration { BotToken = BotToken });
    }

    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest(
        ToDiscordNodeConfiguration config, JToken? testData = null)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.Current).Returns(testData ?? new JObject());

        var rootNodeContext = NodeContext.CreateRootNodeContext(
            services.BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode(
            "ToDiscord", 0, config, dataContext);
        var next = A.Fake<NodeDelegate>();
        return (dataContext, nodeContext, next);
    }

    private ToDiscordNode CreateNode(NodeDelegate next) =>
        new ToDiscordNode(next, _etlContext, _httpClientFactory);

    [Fact]
    public async Task ProcessObjectAsync_ContentOnly_PostsCorrectMessage()
    {
        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = ChannelId,
            Content = "hello world"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var req = _handler.LastRequest!;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal($"https://discord.com/api/v10/channels/{ChannelId}/messages",
            req.RequestUri!.ToString());
        Assert.Equal("Bot", req.Headers.Authorization?.Scheme);
        Assert.Equal(BotToken, req.Headers.Authorization?.Parameter);

        var body = JObject.Parse(_handler.LastBody!);
        Assert.Equal("hello world", body["content"]?.Value<string>());
        Assert.Null(body["embeds"]);
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_EmbedOnly_PostsEmbed()
    {
        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = ChannelId,
            EmbedTitle = "Alert",
            EmbedDescription = "Something happened",
            EmbedColor = 0xFF0000
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var body = JObject.Parse(_handler.LastBody!);
        Assert.Null(body["content"]);
        var embeds = (JArray)body["embeds"]!;
        Assert.Single(embeds);
        Assert.Equal("Alert", embeds[0]["title"]?.Value<string>());
        Assert.Equal("Something happened", embeds[0]["description"]?.Value<string>());
        Assert.Equal(0xFF0000, embeds[0]["color"]?.Value<int>());
    }

    [Fact]
    public async Task ProcessObjectAsync_ContentPathSet_ReadsFromDataContext()
    {
        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = ChannelId,
            Content = "literal",
            ContentPath = "$.msg"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config,
            new JObject { ["msg"] = "from-path" });
        A.CallTo(() => dataContext.GetSimpleValueByPath<string>("$.msg")).Returns("from-path");
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var body = JObject.Parse(_handler.LastBody!);
        Assert.Equal("from-path", body["content"]?.Value<string>());
    }

    [Fact]
    public async Task ProcessObjectAsync_ChannelIdPathSet_UsesResolvedId()
    {
        const string resolvedChannel = "222333444555666777";
        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelIdPath = "$.channel",
            Content = "hi"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetSimpleValueByPath<string>("$.channel")).Returns(resolvedChannel);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Contains($"/channels/{resolvedChannel}/messages",
            _handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task ProcessObjectAsync_TargetPathSet_WritesResponseToDataContext()
    {
        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = ChannelId,
            Content = "hi",
            TargetPath = "$.discordResponse"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.SetValueByPath(
            "$.discordResponse", config.DocumentMode, config.TargetValueKind,
            config.TargetValueWriteMode,
            A<JToken>.That.Matches(t => t["id"]!.Value<string>() == "111")))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_TargetPathEmpty_DoesNotWriteResponse()
    {
        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = ChannelId,
            Content = "hi",
            TargetPath = ""
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.SetValueByPath(
            A<string>._, A<DocumentModes>._, A<ValueKinds>._, A<TargetValueWriteModes>._, A<JToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_ServerConfigurationMissing_Throws()
    {
        A.CallTo(() => _globalConfiguration.IsDefined("missing-config")).Returns(false);

        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = "missing-config",
            ChannelId = ChannelId,
            Content = "hi"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = CreateNode(next);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("Global configuration parameter", ex.Message);
        Assert.Contains("missing-config", ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_DiscordReturns429_ThrowsWithBodyAndRetryAfter_TokenHidden()
    {
        var newHandler = new RecordingHandler((HttpStatusCode)429,
            """{"code":20028,"message":"The resource is being rate limited."}""");
        newHandler.ResponseHeaders["Retry-After"] = "5";
        A.CallTo(() => _httpClientFactory.CreateClient("Discord"))
            .Returns(new HttpClient(newHandler, disposeHandler: false));

        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = ChannelId,
            Content = "hi"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = CreateNode(next);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains("HTTP 429", ex.Message);
        Assert.Contains("rate limited", ex.Message);
        Assert.Contains("Retry-After=5", ex.Message);
        Assert.Contains(ChannelId, ex.Message);

        var full = ex.ToString();
        var inner = ex.InnerException?.ToString() ?? "";
        Assert.DoesNotContain(BotToken, full);
        Assert.DoesNotContain(BotToken, inner);
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_EmptyChannelId_Throws()
    {
        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = "",
            Content = "hi"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_NonNumericChannelId_Throws()
    {
        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = "not-a-snowflake",
            Content = "hi"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_NoMessageBody_Throws()
    {
        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = ChannelId
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Theory]
    [InlineData("0xFF00FF", 0xFF00FF)]
    [InlineData("#FF00FF", 0xFF00FF)]
    [InlineData("16711935", 16711935)]
    public async Task ProcessObjectAsync_EmbedColorPath_ParsesFormats(string raw, int expected)
    {
        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = ChannelId,
            EmbedTitle = "t",
            EmbedColorPath = "$.color"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetSimpleValueByPath<string>("$.color")).Returns(raw);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var body = JObject.Parse(_handler.LastBody!);
        Assert.Equal(expected, (int)body["embeds"]![0]!["color"]!);
    }

    [Fact]
    public async Task ProcessObjectAsync_EmbedColorPathGarbage_Throws()
    {
        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = ChannelId,
            EmbedTitle = "t",
            EmbedColorPath = "$.color"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetSimpleValueByPath<string>("$.color")).Returns("not-a-color");
        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    private static readonly RtCkId<CkTypeId> FileSystemItemCkTypeId =
        new("System.Reporting/FileSystemItem");

    /// <summary>
    /// Wires the repository fakes so that fetching <paramref name="fsItemRtId"/> returns a
    /// FileSystemItem whose <c>Content</c> points at <paramref name="binaryRtId"/> and whose
    /// <c>Name</c>/<c>Content.Filename</c> carry the supplied values; the subsequent binary
    /// download returns the given bytes.
    /// </summary>
    private void SetupFileSystemItem(
        string fsItemRtId,
        string binaryRtId,
        string? name,
        string? contentFilename,
        string contentType,
        byte[] bytes)
    {
        var session = A.Fake<IOctoSession>();
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(session));

        var fsItem = new RtEntity(FileSystemItemCkTypeId, new OctoObjectId(fsItemRtId));
        fsItem.SetAttributeValue("Content", AttributeValueTypesDto.BinaryLinked, new EntityBinaryInfo
        {
            BinaryId = new OctoObjectId(binaryRtId),
            Filename = contentFilename!,
            ContentType = contentType,
            Size = bytes.Length,
        });
        if (name != null)
        {
            fsItem.SetAttributeValue("Name", AttributeValueTypesDto.String, name);
        }

        var resultSet = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => resultSet.Items).Returns(new[] { fsItem });
        A.CallTo(() => resultSet.TotalCount).Returns(1);

        A.CallTo(() => _tenantRepository.GetRtEntitiesByIdAsync(
                A<IOctoSession>._,
                FileSystemItemCkTypeId,
                A<IReadOnlyList<OctoObjectId>>.That.Matches(ids =>
                    ids.Count == 1 && ids[0].ToString() == fsItemRtId),
                A<RtEntityQueryOptions>._,
                A<int?>._,
                A<int?>._))
            .Returns(Task.FromResult<IResultSet<RtEntity>>(resultSet));

        var downloadHandler = A.Fake<IDownloadStreamHandler>();
        A.CallTo(() => downloadHandler.Stream).Returns(new MemoryStream(bytes));
        A.CallTo(() => downloadHandler.Filename).Returns(contentFilename!);
        A.CallTo(() => downloadHandler.ContentType).Returns(contentType);

        A.CallTo(() => _tenantRepository.DownloadLargeBinaryAsync(
                A<IOctoSession>._,
                A<OctoObjectId>.That.Matches(id => id.ToString() == binaryRtId),
                A<CancellationToken>._))
            .Returns(Task.FromResult(downloadHandler));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithFileSystemItemAttachment_PostsMultipartUsingEntityName()
    {
        const string fsItemRtId = "000000000000000000000042";
        const string binaryRtId = "000000000000000000000099";
        SetupFileSystemItem(fsItemRtId, binaryRtId,
            name: "Invoice March.pdf",
            contentFilename: "aaaa-bbbb-cccc.pdf",
            contentType: "application/pdf",
            bytes: "hello pdf"u8.ToArray());

        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = ChannelId,
            Content = "see attached",
            AttachmentFileSystemItemRtId = fsItemRtId
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.StartsWith("multipart/form-data", _handler.LastContentType);
        Assert.Contains("\"content\":\"see attached\"", _handler.LastBody);
        // Name wins over content.filename when no override is set.
        Assert.Contains("filename=\"Invoice March.pdf\"", _handler.LastBody);
        Assert.DoesNotContain("aaaa-bbbb-cccc.pdf", _handler.LastBody);
    }

    [Fact]
    public async Task ProcessObjectAsync_AttachmentFilename_OverridesFileSystemItemName()
    {
        const string fsItemRtId = "000000000000000000000042";
        const string binaryRtId = "000000000000000000000099";
        SetupFileSystemItem(fsItemRtId, binaryRtId,
            name: "Invoice March.pdf",
            contentFilename: "aaaa-bbbb-cccc.pdf",
            contentType: "application/pdf",
            bytes: "hi"u8.ToArray());

        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = ChannelId,
            Content = "see attached",
            AttachmentFileSystemItemRtId = fsItemRtId,
            AttachmentFilename = "custom-override.pdf"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Contains("filename=custom-override.pdf", _handler.LastBody);
        Assert.DoesNotContain("Invoice March.pdf", _handler.LastBody);
    }

    [Fact]
    public async Task ProcessObjectAsync_AttachmentFilenamePath_OverridesFileSystemItemName()
    {
        const string fsItemRtId = "000000000000000000000042";
        const string binaryRtId = "000000000000000000000099";
        SetupFileSystemItem(fsItemRtId, binaryRtId,
            name: "Invoice March.pdf",
            contentFilename: "aaaa-bbbb-cccc.pdf",
            contentType: "application/pdf",
            bytes: "hi"u8.ToArray());

        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = ChannelId,
            Content = "see attached",
            AttachmentFileSystemItemRtId = fsItemRtId,
            AttachmentFilenamePath = "$.desiredName"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetSimpleValueByPath<string>("$.desiredName"))
            .Returns("inv-2025-09-30-abc.pdf");
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Contains("filename=inv-2025-09-30-abc.pdf", _handler.LastBody);
    }

    [Fact]
    public async Task ProcessObjectAsync_BlankFileSystemItemName_FallsBackToContentFilename()
    {
        const string fsItemRtId = "000000000000000000000042";
        const string binaryRtId = "000000000000000000000099";
        SetupFileSystemItem(fsItemRtId, binaryRtId,
            name: null, // no Name on the entity
            contentFilename: "fallback.pdf",
            contentType: "application/pdf",
            bytes: "hi"u8.ToArray());

        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = ChannelId,
            Content = "see attached",
            AttachmentFileSystemItemRtId = fsItemRtId
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Contains("filename=fallback.pdf", _handler.LastBody);
    }

    [Fact]
    public async Task ProcessObjectAsync_FileSystemItemNotFound_Throws()
    {
        const string fsItemRtId = "000000000000000000000042";
        var session = A.Fake<IOctoSession>();
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(session));

        var emptyResult = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => emptyResult.Items).Returns(Array.Empty<RtEntity>());
        A.CallTo(() => emptyResult.TotalCount).Returns(0);
        A.CallTo(() => _tenantRepository.GetRtEntitiesByIdAsync(
                A<IOctoSession>._, A<RtCkId<CkTypeId>>._, A<IReadOnlyList<OctoObjectId>>._,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(Task.FromResult<IResultSet<RtEntity>>(emptyResult));

        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = ChannelId,
            Content = "hi",
            AttachmentFileSystemItemRtId = fsItemRtId
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = CreateNode(next);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("FileSystemItem", ex.Message);
        Assert.Contains(fsItemRtId, ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_FileSystemItemWithoutBinary_Throws()
    {
        const string fsItemRtId = "000000000000000000000042";
        var session = A.Fake<IOctoSession>();
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(session));

        // Entity exists but has no Content attribute.
        var fsItem = new RtEntity(FileSystemItemCkTypeId, new OctoObjectId(fsItemRtId));
        fsItem.SetAttributeValue("Name", AttributeValueTypesDto.String, "orphan.pdf");

        var result = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => result.Items).Returns(new[] { fsItem });
        A.CallTo(() => result.TotalCount).Returns(1);
        A.CallTo(() => _tenantRepository.GetRtEntitiesByIdAsync(
                A<IOctoSession>._, A<RtCkId<CkTypeId>>._, A<IReadOnlyList<OctoObjectId>>._,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(Task.FromResult<IResultSet<RtEntity>>(result));

        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = ChannelId,
            Content = "hi",
            AttachmentFileSystemItemRtId = fsItemRtId
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = CreateNode(next);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("no Content.BinaryId", ex.Message);
    }

    [Fact]
    public async Task ProcessObjectAsync_DefaultMentionPolicy_SuppressesAllMentions()
    {
        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = ChannelId,
            Content = "hello @everyone"
            // MentionPolicy not set → defaults to None
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var body = JObject.Parse(_handler.LastBody!);
        var parse = (JArray?)body["allowed_mentions"]?["parse"];
        Assert.NotNull(parse);
        Assert.Empty(parse!);
    }

    [Fact]
    public async Task ProcessObjectAsync_MentionPolicyAll_OmitsAllowedMentions()
    {
        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = ChannelId,
            Content = "hello @everyone",
            MentionPolicy = MentionPolicy.All
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var body = JObject.Parse(_handler.LastBody!);
        Assert.Null(body["allowed_mentions"]);
    }

    [Theory]
    [InlineData(MentionPolicy.Users, new[] { "users" })]
    [InlineData(MentionPolicy.Roles, new[] { "roles" })]
    [InlineData(MentionPolicy.UsersAndRoles, new[] { "users", "roles" })]
    public async Task ProcessObjectAsync_MentionPolicy_SendsExpectedParseList(
        MentionPolicy policy, string[] expectedParse)
    {
        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = ChannelId,
            Content = "hi",
            MentionPolicy = policy
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var body = JObject.Parse(_handler.LastBody!);
        var parse = (JArray?)body["allowed_mentions"]?["parse"];
        Assert.NotNull(parse);
        Assert.Equal(expectedParse, parse!.Select(t => t.Value<string>()).ToArray());
    }

    [Fact]
    public async Task ProcessObjectAsync_MentionPolicyCustom_PassesThroughRawObject()
    {
        var custom = new JObject
        {
            ["parse"] = new JArray("users"),
            ["users"] = new JArray("111222333444555666"),
            ["replied_user"] = true
        };
        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = ChannelId,
            Content = "hi <@111222333444555666>",
            MentionPolicy = MentionPolicy.Custom,
            AllowedMentionsPath = "$.mentions"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetComplexObjectByPath<JToken>("$.mentions")).Returns(custom);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var body = JObject.Parse(_handler.LastBody!);
        var am = body["allowed_mentions"]!;
        Assert.Equal(new[] { "users" }, am["parse"]!.Select(t => t.Value<string>()).ToArray());
        Assert.Equal("111222333444555666", am["users"]![0]!.Value<string>());
        Assert.True(am["replied_user"]!.Value<bool>());
    }

    [Fact]
    public async Task ProcessObjectAsync_MentionPolicyCustom_MissingPath_Throws()
    {
        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = ChannelId,
            Content = "hi",
            MentionPolicy = MentionPolicy.Custom
            // AllowedMentionsPath deliberately unset
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_MentionPolicyCustom_PathResolvesNull_Throws()
    {
        var config = new ToDiscordNodeConfiguration
        {
            ServerConfiguration = ServerConfig,
            ChannelId = ChannelId,
            Content = "hi",
            MentionPolicy = MentionPolicy.Custom,
            AllowedMentionsPath = "$.missing"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetComplexObjectByPath<JToken>("$.missing")).Returns(null);
        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }
}

internal class RecordingHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }
    public string? LastContentType { get; private set; }
    private int _callCount;
    public int CallCount => _callCount;

    public Dictionary<string, string> ResponseHeaders { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _callCount++;
        LastRequest = request;
        LastContentType = request.Content?.Headers.ContentType?.MediaType;
        LastBody = request.Content == null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        var resp = new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json")
        };
        foreach (var (k, v) in ResponseHeaders)
        {
            resp.Headers.TryAddWithoutValidation(k, v);
        }
        return resp;
    }
}
