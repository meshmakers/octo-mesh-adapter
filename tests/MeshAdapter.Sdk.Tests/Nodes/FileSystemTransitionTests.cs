using System.Text;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes;

/// <summary>
///     AB#6177 — the file nodes resolve <c>System.Files</c> first and fall back to <c>System.Reporting</c>
///     (concept §8.6). The unmigrated cases are the contract of the transition: a tenant that still owns
///     its roots and files as <c>System.Reporting</c> — with or without the <c>System.Files</c> model
///     imported (empty <c>Files</c> root) — must behave exactly as before the change.
/// </summary>
public class FileSystemTransitionTests : SessionNodeTestBase
{
    private const string NewRoot = "System.Files/FolderRoot";
    private const string OldRoot = "System.Reporting/FolderRoot";
    private const string NewItem = "System.Files/FileSystemItem";
    private const string OldItem = "System.Reporting/FileSystemItem";

    private static readonly OctoObjectId RootRtId = new("0000000000000000000000aa");
    private static readonly OctoObjectId ItemRtId = new("0000000000000000000000bb");
    private static readonly OctoObjectId BinaryRtId = new("0000000000000000000000cc");

    /// <summary>How the tenant answers a query for a type.</summary>
    private enum TypeState
    {
        /// <summary>Type known, entity present.</summary>
        Hit,

        /// <summary>Type known, no entity (e.g. imported System.Files with an empty root).</summary>
        Empty,

        /// <summary>Type unknown to the tenant (model not imported or already dropped).</summary>
        Unknown,

        /// <summary>The lookup fails for another reason (timeout, database error).</summary>
        Fails,

        /// <summary>Type known, more than one root with the well-known name (misconfiguration).</summary>
        Ambiguous
    }

    public FileSystemTransitionTests()
    {
        GivenSystemSessionIsExpected();
        A.CallTo(() => TenantRepository.CreateTransientRtEntityByRtCkIdAsync(A<RtCkId<CkTypeId>>._))
            .ReturnsLazily((RtCkId<CkTypeId> ckTypeId) =>
                Task.FromResult(new RtEntity(ckTypeId, OctoObjectId.GenerateNewId())));
    }

    // ---------------------------------------------------------------- stubs

    private void GivenRoots(TypeState newRoot, TypeState oldRoot)
    {
        StubRoot(NewRoot, newRoot);
        StubRoot(OldRoot, oldRoot);
    }

    private void StubRoot(string ckTypeId, TypeState state)
    {
        var call = A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(
            A<IOctoSession>._, new RtCkId<CkTypeId>(ckTypeId), A<RtEntityQueryOptions>._, A<int?>._, A<int?>._));
        switch (state)
        {
            case TypeState.Unknown:
                call.Throws(() => new CkCacheException($"Unknown CK type '{ckTypeId}'"));
                break;
            default:
                var set = A.Fake<IResultSet<RtEntity>>();
                var items = state switch
                {
                    TypeState.Hit => new List<RtEntity> { new(new RtCkId<CkTypeId>(ckTypeId), RootRtId) },
                    TypeState.Ambiguous => new List<RtEntity>
                    {
                        new(new RtCkId<CkTypeId>(ckTypeId), RootRtId),
                        new(new RtCkId<CkTypeId>(ckTypeId), OctoObjectId.GenerateNewId())
                    },
                    _ => new List<RtEntity>()
                };
                A.CallTo(() => set.Items).Returns(items);
                call.Returns(Task.FromResult(set));
                break;
        }
    }

    private (List<RtCkId<CkTypeId>> InsertedTypes, List<AssociationUpdateInfo> Assocs) CaptureApplyChanges()
    {
        var types = new List<RtCkId<CkTypeId>>();
        var assocs = new List<AssociationUpdateInfo>();
        A.CallTo(() => TenantRepository.ApplyChangesAsync(A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> entities,
                IReadOnlyList<AssociationUpdateInfo> a, OperationResult _) =>
            {
                types.AddRange(entities.Select(e => e.RtEntity!.CkTypeId!));
                assocs.AddRange(a);
            })
            .Returns(Task.CompletedTask);
        return (types, assocs);
    }

    private void GivenItem(string ckTypeId, TypeState state)
    {
        var id = new RtCkId<CkTypeId>(ckTypeId);
        var byRtId = A.CallTo(() => TenantRepository.GetRtEntityByRtIdAsync(A<IOctoSession>._,
            A<RtEntityId>.That.Matches(e => e.CkTypeId == id)));
        if (state == TypeState.Unknown)
        {
            byRtId.Throws(() => new CkCacheException($"Unknown CK type '{ckTypeId}'"));
            return;
        }

        if (state == TypeState.Fails)
        {
            byRtId.Throws(() => new TimeoutException($"Lookup of '{ckTypeId}' timed out"));
            return;
        }

        RtEntity? entity = null;
        if (state == TypeState.Hit)
        {
            entity = new RtEntity(id, ItemRtId);
            entity.SetAttributeValue("Content", AttributeValueTypesDto.BinaryLinked, new EntityBinaryInfo
            {
                BinaryId = BinaryRtId, Filename = "a.txt", ContentType = "text/plain", Size = 3
            });
        }

        byRtId.Returns(Task.FromResult(entity));
    }

    private void GivenBinary(string text)
    {
        var handler = A.Fake<IDownloadStreamHandler>();
        A.CallTo(() => handler.Stream).Returns(new MemoryStream(Encoding.UTF8.GetBytes(text)));
        A.CallTo(() => TenantRepository.DownloadLargeBinaryAsync(A<IOctoSession>._, A<OctoObjectId>._,
                A<CancellationToken>._))
            .Returns(Task.FromResult(handler));
    }

    // ---------------------------------------------------------------- CreateFileSystemUpdate@1

    private async Task<(List<RtCkId<CkTypeId>> Types, List<AssociationUpdateInfo> Assocs, IDataContext Data)>
        RunCreateFileSystemUpdateAsync()
    {
        var captured = CaptureApplyChanges();
        var config = new CreateFileSystemUpdateNodeConfiguration
        {
            Path = "$.content", TargetPath = "$.item", RootFolderWellKnownName = "Documents",
            FileName = "a.txt", ContentType = "text/plain", ContentLength = 3, GenerateRtId = true
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        SetupGetSimpleValueByPath(dataContext, "$.content", Convert.ToBase64String(Encoding.UTF8.GetBytes("abc")));

        await new CreateFileSystemItemUpdateNode(next, EtlContext).ProcessObjectAsync(dataContext, nodeContext);
        return (captured.InsertedTypes, captured.Assocs, dataContext);
    }

    [Fact]
    public async Task CreateFileSystemUpdate_RootInSystemFiles_WritesSystemFilesItem()
    {
        GivenRoots(TypeState.Hit, TypeState.Empty);

        var (types, assocs, _) = await RunCreateFileSystemUpdateAsync();

        Assert.Equal([new RtCkId<CkTypeId>(NewItem)], types);
        Assert.Single(assocs);
    }

    [Fact]
    public async Task CreateFileSystemUpdate_UnmigratedTenantWithEmptySystemFiles_KeepsWritingSystemReporting()
    {
        // System.Files imported (empty `Files` root), "Documents" still a System.Reporting root.
        GivenRoots(TypeState.Empty, TypeState.Hit);

        var (types, assocs, _) = await RunCreateFileSystemUpdateAsync();

        Assert.Equal([new RtCkId<CkTypeId>(OldItem)], types);
        Assert.Single(assocs);
    }

    [Fact]
    public async Task CreateFileSystemUpdate_UnmigratedTenantWithoutSystemFilesModel_KeepsWritingSystemReporting()
    {
        GivenRoots(TypeState.Unknown, TypeState.Hit);

        var (types, _, _) = await RunCreateFileSystemUpdateAsync();

        Assert.Equal([new RtCkId<CkTypeId>(OldItem)], types);
    }

    [Fact]
    public async Task CreateFileSystemUpdate_MigratedTenantWithoutSystemReporting_WritesSystemFilesItem()
    {
        GivenRoots(TypeState.Hit, TypeState.Unknown);

        var (types, _, _) = await RunCreateFileSystemUpdateAsync();

        Assert.Equal([new RtCkId<CkTypeId>(NewItem)], types);
    }

    [Fact]
    public async Task CreateFileSystemUpdate_RootInBothModels_NewTypeWins()
    {
        GivenRoots(TypeState.Hit, TypeState.Hit);

        var (types, _, _) = await RunCreateFileSystemUpdateAsync();

        Assert.Equal([new RtCkId<CkTypeId>(NewItem)], types);
    }

    [Fact]
    public async Task CreateFileSystemUpdate_RootInNeitherModel_Fails()
    {
        GivenRoots(TypeState.Empty, TypeState.Empty);
        CaptureApplyChanges();
        var config = new CreateFileSystemUpdateNodeConfiguration
        {
            Path = "$.content", TargetPath = "$.item", RootFolderWellKnownName = "Documents",
            FileName = "a.txt", ContentType = "text/plain", ContentLength = 3
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        SetupGetSimpleValueByPath(dataContext, "$.content", Convert.ToBase64String("abc"u8.ToArray()));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new CreateFileSystemItemUpdateNode(next, EtlContext).ProcessObjectAsync(dataContext, nodeContext));
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task CreateFileSystemUpdate_AmbiguousSystemFilesRoot_FailsWithoutFallingBack()
    {
        // Review finding (CodeRabbit, PR #68): a duplicate System.Files root must fail like before instead of
        // silently writing a System.Reporting item under the legacy root.
        GivenRoots(TypeState.Ambiguous, TypeState.Hit);
        var (types, _) = CaptureApplyChanges();
        var config = new CreateFileSystemUpdateNodeConfiguration
        {
            Path = "$.content", TargetPath = "$.item", RootFolderWellKnownName = "Documents",
            FileName = "a.txt", ContentType = "text/plain", ContentLength = 3
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        SetupGetSimpleValueByPath(dataContext, "$.content", Convert.ToBase64String("abc"u8.ToArray()));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new CreateFileSystemItemUpdateNode(next, EtlContext).ProcessObjectAsync(dataContext, nodeContext));
        Assert.Empty(types);
    }

    // ---------------------------------------------------------------- CreateZipArchive@1 (persist)

    private async Task<List<RtCkId<CkTypeId>>> RunPersistZipAsync()
    {
        var captured = CaptureApplyChanges();
        var config = new CreateZipArchiveNodeConfiguration
        {
            Path = "$.entries", TargetPath = "$.zip", PersistAsFileSystemItem = true,
            RootFolderWellKnownName = "Documents", FileName = "bundle.zip"
        };
        var entries = new JsonArray(new JsonObject
        {
            ["fileName"] = "a.txt", ["contentBase64"] = Convert.ToBase64String("abc"u8.ToArray())
        });
        var (dataContext, nodeContext, next) =
            PrepareTest(config, scratchSpace: new SessionIdentityBehaviourTests.InMemoryScratchSpace());
        A.CallTo(() => dataContext.Get<JsonNode>("$.entries")).Returns(entries);

        await new CreateZipArchiveNode(next, EtlContext).ProcessObjectAsync(dataContext, nodeContext);
        return captured.InsertedTypes;
    }

    [Fact]
    public async Task CreateZipArchive_Persist_RootInSystemFiles_WritesSystemFilesItem()
    {
        GivenRoots(TypeState.Hit, TypeState.Empty);

        Assert.Equal([new RtCkId<CkTypeId>(NewItem)], await RunPersistZipAsync());
    }

    [Fact]
    public async Task CreateZipArchive_Persist_UnmigratedTenant_KeepsWritingSystemReporting()
    {
        GivenRoots(TypeState.Empty, TypeState.Hit);

        Assert.Equal([new RtCkId<CkTypeId>(OldItem)], await RunPersistZipAsync());
    }

    [Fact]
    public async Task CreateZipArchive_Persist_UnmigratedTenantWithoutSystemFilesModel_KeepsWritingSystemReporting()
    {
        GivenRoots(TypeState.Unknown, TypeState.Hit);

        Assert.Equal([new RtCkId<CkTypeId>(OldItem)], await RunPersistZipAsync());
    }

    // ---------------------------------------------------------------- GetFileSystemContent@1

    private async Task<string?> RunGetContentAsync()
    {
        GivenBinary("abc");
        var config = new GetFileSystemContentNodeConfiguration { RtIdPath = "$.id", TargetPath = "$.content" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        SetupGetSimpleValueByPath(dataContext, "$.id", ItemRtId.ToString());

        await new GetFileSystemContentNode(next, EtlContext).ProcessObjectAsync(dataContext, nodeContext);

        var call = Fake.GetCalls(dataContext).FirstOrDefault(c => c.Method.Name == "Set"
                                                                  && (string?)c.Arguments[0] == "$.content");
        return call?.Arguments[1] as string;
    }

    [Fact]
    public async Task GetFileSystemContent_ItemInSystemFiles_ReadsIt()
    {
        GivenItem(NewItem, TypeState.Hit);
        GivenItem(OldItem, TypeState.Empty);

        Assert.Equal(Convert.ToBase64String("abc"u8.ToArray()), await RunGetContentAsync());
    }

    [Fact]
    public async Task GetFileSystemContent_UnmigratedItem_FallsBackToSystemReporting()
    {
        GivenItem(NewItem, TypeState.Empty);
        GivenItem(OldItem, TypeState.Hit);

        Assert.Equal(Convert.ToBase64String("abc"u8.ToArray()), await RunGetContentAsync());
    }

    [Fact]
    public async Task GetFileSystemContent_UnmigratedTenantWithoutSystemFilesModel_FallsBackToSystemReporting()
    {
        GivenItem(NewItem, TypeState.Unknown);
        GivenItem(OldItem, TypeState.Hit);

        Assert.Equal(Convert.ToBase64String("abc"u8.ToArray()), await RunGetContentAsync());
    }

    [Fact]
    public async Task GetFileSystemContent_ItemInNeitherType_Throws()
    {
        GivenItem(NewItem, TypeState.Empty);
        GivenItem(OldItem, TypeState.Empty);

        await Assert.ThrowsAnyAsync<Exception>(RunGetContentAsync);
    }

    [Fact]
    public async Task GetFileSystemContent_SystemFilesLookupFails_PropagatesInsteadOfFallingBack()
    {
        // Review finding (AB#6177): only an unknown type means "not found there"; a real failure must
        // surface instead of being reported as a missing item or silently falling back.
        GivenItem(NewItem, TypeState.Fails);
        GivenItem(OldItem, TypeState.Hit);

        await Assert.ThrowsAsync<TimeoutException>(RunGetContentAsync);
    }

    [Fact]
    public async Task GetFileSystemContent_NeitherTypeKnown_SurfacesTheRepositoryError()
    {
        GivenItem(NewItem, TypeState.Unknown);
        GivenItem(OldItem, TypeState.Unknown);

        var ex = await Assert.ThrowsAsync<CkCacheException>(RunGetContentAsync);
        Assert.Contains(NewItem, ex.Message);
    }
}
