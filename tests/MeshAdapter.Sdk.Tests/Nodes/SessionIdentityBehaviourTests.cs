using System.Text;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.ExcelImport;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;

namespace MeshAdapter.Sdk.Tests.Nodes;

/// <summary>
///     Proves the effective identity actually reaches the repository for the call sites that had no
///     driving test of their own before AB#5028. The nodes that already had one assert it in their
///     own suite; <see cref="SessionIdentityClassificationTests" /> guarantees the two together leave
///     nothing unclassified.
/// </summary>
/// <remarks>
///     Each test drives the node only as far as the session, then asserts which
///     <see cref="RtSecurityContext" /> the repository was asked for. Whatever the node does
///     afterwards is another suite's business, so the run is wrapped in
///     <see cref="Record.ExceptionAsync" /> — the assertion is about the identity, not the outcome.
/// </remarks>
public class SessionIdentityBehaviourTests : SessionNodeTestBase
{
    private static readonly RtCkId<CkTypeId> TestCkTypeId = new("TestModel/TestType");
    private static readonly OctoObjectId TestRtId = new("000000000000000000000001");

    // ---------------------------------------------------------------- Extract

    [Fact]
    public async Task GetFileSystemContent_DownloadsAsTheSystemContext()
    {
        GivenSystemSessionIsExpected();

        var config = new GetFileSystemContentNodeConfiguration
        {
            RtIdPath = "$.rtId",
            TargetPath = "$.content"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        SetupGetSimpleValueByPath(dataContext, "$.rtId", TestRtId.ToString());

        var node = new GetFileSystemContentNode(next, EtlContext);
        await Record.ExceptionAsync(() => node.ProcessObjectAsync(dataContext, nodeContext));

        AssertSystemSessionOpened();
    }

    [Fact]
    public async Task GetNotificationTemplate_ReadsPlatformConfigurationAsTheSystemContext()
    {
        GivenSystemSessionIsExpected();

        var config = new GetNotificationTemplateNodeConfiguration
        {
            NotificationTemplateName = "welcome",
            SubjectTargetPath = "$.subject",
            TargetPath = "$.body"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var node = new GetNotificationTemplateNode(next, EtlContext);
        await Record.ExceptionAsync(() => node.ProcessObjectAsync(dataContext, nodeContext));

        AssertSystemSessionOpened();
    }

    [Fact]
    public async Task GetOrCreateRtEntitiesByType_ActsAsTheExecutionIdentity()
    {
        var config = new GetOrCreateRtEntitiesByTypeNodeConfiguration
        {
            CkTypeId = TestCkTypeId,
            FieldFilters =
            [
                new FieldFilterWithPathDto
                {
                    AttributePath = nameof(RtEntity.RtWellKnownName),
                    Operator = FieldFilterOperatorDto.Equals,
                    ComparisonValue = "anything"
                }
            ]
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var node = new GetOrCreateRtEntitiesByTypeNode(next, EtlContext);
        await Record.ExceptionAsync(() => node.ProcessObjectAsync(dataContext, nodeContext));

        AssertScopedSessionOpened();
    }

    // ------------------------------------------------------------------- Load

    [Fact]
    public async Task DeployPipeline_ActsAsTheServiceIdentity()
    {
        GivenSystemSessionIsExpected();

        A.CallTo(() => EtlContext.PipelineRtEntityId)
            .Returns(new RtEntityId(TestCkTypeId, new OctoObjectId("0000000000000000000000ff")));

        var config = new DeployPipelineNodeConfiguration { PipelineRtId = TestRtId };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var node = new DeployPipelineNode(next, EtlContext,
            A.Fake<ICommunicationServicesClient>(), A.Fake<IServiceAccountTokenService>());
        await Record.ExceptionAsync(() => node.ProcessObjectAsync(dataContext, nodeContext));

        AssertSystemSessionOpened();
    }

    [Fact]
    public async Task SendEMail_DownloadsTheAttachmentAsTheSystemContext()
    {
        GivenSystemSessionIsExpected();

        var globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => EtlContext.GlobalConfiguration).Returns(globalConfiguration);
        A.CallTo(() => EtlContext.Properties).Returns(new Dictionary<string, object?>());
        A.CallTo(() => globalConfiguration.IsDefined("smtp")).Returns(true);
        A.CallTo(() => globalConfiguration.GetRawJson("smtp")).Returns(
            """{"host":"localhost","port":25,"username":"u","password":"p","isSslEnabled":false}""");

        // A handler comes back, but the configuration names neither file name nor content type — so
        // the node throws right AFTER the download and never opens an SMTP connection.
        A.CallTo(() => TenantRepository.DownloadLargeBinaryAsync(
                A<IOctoSession>._, A<OctoObjectId>._, A<CancellationToken>._))
            .Returns(Task.FromResult(A.Fake<IDownloadStreamHandler>()));

        var config = new EMailSenderNodeConfiguration
        {
            ServerConfiguration = "smtp",
            SubjectPath = "$.subject",
            ToPath = "$.to",
            Path = "$.body",
            AttachmentRtIdPath = "$.attachmentRtId",
            AttachmentFileName = null,
            AttachmentContentType = null
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => dataContext.GetArray<string>("$.to")).Returns(new[] { "someone@example.com" });
        SetupGetSimpleValueByPath(dataContext, "$.subject", "hello");
        SetupGetSimpleValueByPath(dataContext, "$.body", "body");
        SetupGetSimpleValueByPath(dataContext, "$.attachmentRtId", TestRtId.ToString());

        var node = new EMailSenderNode(next, EtlContext);
        await Record.ExceptionAsync(() => node.ProcessObjectAsync(dataContext, nodeContext));

        AssertSystemSessionOpened();
    }

    // -------------------------------------------------------------- Transform

    [Fact]
    public async Task BuildMappingTargets_ActsAsTheExecutionIdentity()
    {
        var config = new BuildMappingTargetsNodeConfiguration
        {
            SourceCkTypeId = TestCkTypeId.SemanticVersionedFullName,
            SourceIdentifierAttribute = "Uuid",
            TargetPath = "$.targets"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var node = new BuildMappingTargetsNode(next, EtlContext);
        await Record.ExceptionAsync(() => node.ProcessObjectAsync(dataContext, nodeContext));

        AssertScopedSessionOpened();
    }

    [Fact]
    public async Task ValidateDataPointCoverage_ActsAsTheExecutionIdentity()
    {
        var config = new ValidateDataPointCoverageNodeConfiguration
        {
            RootRtId = TestRtId.ToString(),
            RootCkTypeId = TestCkTypeId.SemanticVersionedFullName,
            TargetPath = "$.report"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var node = new ValidateDataPointCoverageNode(next, EtlContext);
        await Record.ExceptionAsync(() => node.ProcessObjectAsync(dataContext, nodeContext));

        AssertScopedSessionOpened();
    }

    [Fact]
    public async Task ApplyDataPointMappings_ActsAsTheExecutionIdentity()
    {
        var config = new ApplyDataPointMappingsNodeConfiguration
        {
            SourceRtIdPath = "$.rtId",
            SourceCkTypeIdPath = "$.ckTypeId",
            SourceValuePath = "$.value",
            TargetPath = "$.updates"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        SetupGetSimpleValueByPath<OctoObjectId?>(dataContext, "$.rtId", TestRtId);
        SetupGetSimpleValueByPath<RtCkId<CkTypeId>?>(dataContext, "$.ckTypeId", TestCkTypeId);

        var node = new ApplyDataPointMappingsNode(next, EtlContext,
            A.Fake<ICkCacheService>(), A.Fake<IFormulaEngine>());
        await Record.ExceptionAsync(() => node.ProcessObjectAsync(dataContext, nodeContext));

        AssertScopedSessionOpened();
    }

    [Fact]
    public async Task CreateFileSystemUpdate_WritesScopedButResolvesTheFolderRootAsSystem()
    {
        GivenSystemSessionIsExpected();
        GivenFolderRootResolves();

        var config = new CreateFileSystemUpdateNodeConfiguration
        {
            Path = "$.content",
            TargetPath = "$.item",
            RootFolderWellKnownName = "reports",
            FileName = "a.txt",
            ContentType = "text/plain",
            ContentLength = 3,
            GenerateRtId = true
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        SetupGetSimpleValueByPath(dataContext, "$.content",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("abc")));

        var node = new CreateFileSystemItemUpdateNode(next, EtlContext);
        await Record.ExceptionAsync(() => node.ProcessObjectAsync(dataContext, nodeContext));

        // Both halves of the split, in one node.
        AssertSystemSessionOpened();
        AssertScopedSessionOpened();
    }

    [Fact]
    public async Task CreateZipArchive_WritesScopedButResolvesTheFolderRootAsSystem()
    {
        GivenSystemSessionIsExpected();
        GivenFolderRootResolves();

        var config = new CreateZipArchiveNodeConfiguration
        {
            Path = "$.entries",
            TargetPath = "$.zip",
            PersistAsFileSystemItem = true,
            RootFolderWellKnownName = "reports",
            FileName = "bundle.zip"
        };
        var entries = new JsonArray(new JsonObject
        {
            ["fileName"] = "a.txt",
            ["contentBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("abc"))
        });
        var scratchSpace = new InMemoryScratchSpace();
        var (dataContext, nodeContext, next) = PrepareTest(config, scratchSpace: scratchSpace);
        A.CallTo(() => dataContext.Get<JsonNode>("$.entries")).Returns(entries);

        var node = new CreateZipArchiveNode(next, EtlContext);
        await Record.ExceptionAsync(() => node.ProcessObjectAsync(dataContext, nodeContext));

        AssertSystemSessionOpened();
        AssertScopedSessionOpened();
    }

    [Fact]
    public async Task WellKnownNameLoader_LoadsSynchronouslyAsTheSystemContext()
    {
        GivenSystemSessionIsExpected();

        var loader = new WellKnownNameLoader(EtlContext);
        await Record.ExceptionAsync(() => loader.LoadAsync(["a"], TestCkTypeId));

        // The synchronous face — a search for GetSessionAsync alone would miss this call site.
        AssertSystemSessionOpenedSynchronously();
    }

    /// <summary>
    ///     Minimal in-memory <see cref="IPipelineScratchSpace" />: CreateZipArchive@1 streams the
    ///     archive through one before it ever reaches the repository.
    /// </summary>
    private sealed class InMemoryScratchSpace : IPipelineScratchSpace
    {
        private readonly Dictionary<string, MemoryStream> _files = new(StringComparer.Ordinal);

        public string CreateFile(string? extension = null)
        {
            var token = Guid.NewGuid().ToString("N");
            _files[token] = new MemoryStream();
            return token;
        }

        public Stream OpenWrite(string token) => new NonClosingStream(_files[token]);

        public Stream OpenRead(string token) => new MemoryStream(_files[token].ToArray(), writable: false);

        public long GetLength(string token) => _files[token].Length;

        public bool Exists(string token) => _files.ContainsKey(token);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>The ZipArchive disposes the stream it wrote to; the buffer has to survive that.</summary>
        private sealed class NonClosingStream(MemoryStream inner) : Stream
        {
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => inner.CanSeek;
            public override bool CanWrite => inner.CanWrite;
            public override long Length => inner.Length;

            public override long Position
            {
                get => inner.Position;
                set => inner.Position = value;
            }

            public override void Flush() => inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
            public override void SetLength(long value) => inner.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        }
    }

    /// <summary>
    ///     Makes the <c>System.Reporting/FolderRoot</c> lookup resolve to exactly one entity, which is
    ///     what lets the two split-identity nodes get past it and reach their scoped write.
    /// </summary>
    private void GivenFolderRootResolves()
    {
        var folderRoot = new RtEntity(new RtCkId<CkTypeId>("System.Reporting/FolderRoot"),
            new OctoObjectId("0000000000000000000000aa"));
        var resultSet = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => resultSet.Items).Returns(new List<RtEntity> { folderRoot });
        A.CallTo(() => resultSet.TotalCount).Returns(1);

        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(
                A<IOctoSession>._, A<RtCkId<CkTypeId>>._, A<RtEntityQueryOptions>._,
                A<int?>._, A<int?>._))
            .Returns(Task.FromResult(resultSet));

        A.CallTo(() => TenantRepository.CreateTransientRtEntityByRtCkIdAsync(A<RtCkId<CkTypeId>>._))
            .ReturnsLazily((RtCkId<CkTypeId> ckTypeId) =>
                Task.FromResult(new RtEntity(ckTypeId, OctoObjectId.GenerateNewId())));
    }
}
