using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
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

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

public class ToDiscordNodeTests : NodeTestBase
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var req = _handler.LastRequest!;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal($"https://discord.com/api/v10/channels/{ChannelId}/messages",
            req.RequestUri!.ToString());
        Assert.Equal("Bot", req.Headers.Authorization?.Scheme);
        Assert.Equal(BotToken, req.Headers.Authorization?.Parameter);

        var body = JsonNode.Parse(_handler.LastBody!)!.AsObject();
        Assert.Equal("hello world", body["content"]?.GetValue<string>());
        Assert.False(body.ContainsKey("embeds"));
        VerifyNextCalled(next, dataContext, nodeContext);
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var body = JsonNode.Parse(_handler.LastBody!)!.AsObject();
        Assert.False(body.ContainsKey("content"));
        var embeds = (JsonArray)body["embeds"]!;
        Assert.Single(embeds);
        Assert.Equal("Alert", embeds[0]!["title"]?.GetValue<string>());
        Assert.Equal("Something happened", embeds[0]!["description"]?.GetValue<string>());
        Assert.Equal(0xFF0000, embeds[0]!["color"]?.GetValue<int>());
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, "$.msg", "from-path");
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var body = JsonNode.Parse(_handler.LastBody!)!.AsObject();
        Assert.Equal("from-path", body["content"]?.GetValue<string>());
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, "$.channel", resolvedChannel);
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.Set(
            "$.discordResponse",
            A<JsonNode?>.That.Matches(t => t != null && t["id"]!.GetValue<string>() == "111"),
            config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode))
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.Set(
            A<string>._, A<JsonNode?>._, A<DocumentModes>._, A<ValueKinds>._, A<TargetValueWriteModes>._))
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, "$.color", raw);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var body = JsonNode.Parse(_handler.LastBody!)!.AsObject();
        Assert.Equal(expected, body["embeds"]![0]!["color"]!.GetValue<int>());
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, "$.color", "not-a-color");
        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    private static readonly RtCkId<CkTypeId> FileSystemItemCkTypeId =
        new("System.Reporting/FileSystemItem");

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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.StartsWith("multipart/form-data", _handler.LastContentType);
        Assert.Contains("\"content\":\"see attached\"", _handler.LastBody);
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, "$.desiredName", "inv-2025-09-30-abc.pdf");
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
            name: null,
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
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
        };
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var body = JsonNode.Parse(_handler.LastBody!)!.AsObject();
        var parse = body["allowed_mentions"]?["parse"]?.AsArray();
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var body = JsonNode.Parse(_handler.LastBody!)!.AsObject();
        Assert.False(body.ContainsKey("allowed_mentions"));
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var body = JsonNode.Parse(_handler.LastBody!)!.AsObject();
        var parse = body["allowed_mentions"]?["parse"]?.AsArray();
        Assert.NotNull(parse);
        Assert.Equal(expectedParse, parse!.Select(t => t!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task ProcessObjectAsync_MentionPolicyCustom_PassesThroughRawObject()
    {
        var custom = new JsonObject
        {
            ["parse"] = new JsonArray("users"),
            ["users"] = new JsonArray("111222333444555666"),
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
        A.CallTo(() => dataContext.Get<object?>("$.mentions")).Returns(custom);
        var node = CreateNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var body = JsonNode.Parse(_handler.LastBody!)!.AsObject();
        var am = body["allowed_mentions"]!;
        Assert.Equal(new[] { "users" }, am["parse"]!.AsArray().Select(t => t!.GetValue<string>()).ToArray());
        Assert.Equal("111222333444555666", am["users"]![0]!.GetValue<string>());
        Assert.True(am["replied_user"]!.GetValue<bool>());
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
        };
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
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
        var (dataContext, nodeContext, next) = PrepareTest<ToDiscordNodeConfiguration>(config);
        A.CallTo(() => dataContext.Get<object?>("$.missing")).Returns(null);
        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    // ── Byte-identity: DiscordPayload record vs the legacy hand-built JsonObject ──
    // Pins the Discord webhook wire format (key order + omit-when-null) so the typed-record
    // rewrite cannot silently drift from the pre-migration JsonObject output.

    [Fact]
    public void DiscordPayload_FullShape_SerializesByteIdenticalToLegacyJsonObject()
    {
        var legacy = new JsonObject
        {
            ["content"] = "hello",
            ["embeds"] = new JsonArray(new JsonObject
            {
                ["title"] = "T",
                ["description"] = "D",
                ["color"] = 123
            }),
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray(JsonValue.Create("users")) }
        };
        var payload = new ToDiscordNode.DiscordPayload(
            "hello",
            new[] { new ToDiscordNode.DiscordEmbed("T", "D", 123) },
            new ToDiscordNode.DiscordAllowedMentions(new[] { "users" }));

        Assert.Equal(
            legacy.ToJsonString(SystemTextJsonOptions.Default),
            JsonSerializer.Serialize(payload, SystemTextJsonOptions.Default));
    }

    [Fact]
    public void DiscordPayload_ContentOnly_OmitsEmbedsAndMentions()
    {
        var legacy = new JsonObject { ["content"] = "hi" };
        var payload = new ToDiscordNode.DiscordPayload("hi", null, null);

        Assert.Equal(
            legacy.ToJsonString(SystemTextJsonOptions.Default),
            JsonSerializer.Serialize(payload, SystemTextJsonOptions.Default));
    }

    [Fact]
    public void DiscordPayload_EmbedOnly_OmitsContentAndMentions_AndOmitsNullEmbedFields()
    {
        var legacy = new JsonObject
        {
            ["embeds"] = new JsonArray(new JsonObject { ["description"] = "D" })
        };
        var payload = new ToDiscordNode.DiscordPayload(
            null,
            new[] { new ToDiscordNode.DiscordEmbed(null, "D", null) },
            null);

        Assert.Equal(
            legacy.ToJsonString(SystemTextJsonOptions.Default),
            JsonSerializer.Serialize(payload, SystemTextJsonOptions.Default));
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
