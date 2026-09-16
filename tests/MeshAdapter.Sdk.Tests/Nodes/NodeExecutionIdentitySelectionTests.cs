using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

namespace MeshAdapter.Sdk.Tests.Nodes;

/// <summary>
///     Proves a data node picks its session from its configuration's <c>identity</c> value (AB#5127),
///     driven end to end through a representative node (<see cref="GetRtEntitiesByTypeNode" />). The
///     mapping itself is unit-tested in <see cref="Services.MeshEtlContextIdentitySelectionTests" />;
///     this shows a real node honours it.
/// </summary>
public class NodeExecutionIdentitySelectionTests : SessionNodeTestBase
{
    private static readonly RtCkId<CkTypeId> TestCkTypeId = new("TestModel/TestType");

    private async Task DriveAsync(NodeExecutionIdentity? identity)
    {
        var config = new GetRtEntitiesByTypeNodeConfiguration { CkTypeId = TestCkTypeId };
        if (identity != null)
        {
            config = config with { Identity = identity.Value };
        }

        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = new GetRtEntitiesByTypeNode(next, EtlContext);
        await Record.ExceptionAsync(() => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task MissingIdentity_OpensAScopedSession()
    {
        // A pipeline authored before AB#5127 carries no identity; it must stay caller-scoped.
        await DriveAsync(identity: null);

        AssertScopedSessionOpened();
    }

    [Fact]
    public async Task Caller_OpensAScopedSession()
    {
        await DriveAsync(NodeExecutionIdentity.Caller);

        AssertScopedSessionOpened();
    }

    [Fact]
    public async Task ServiceAccount_OpensAServiceAccountSession_NotTheCaller()
    {
        await DriveAsync(NodeExecutionIdentity.ServiceAccount);

        AssertServiceAccountSessionOpened();
    }

    [Fact]
    public async Task System_OpensASystemSession()
    {
        // System is a deliberate elevation here, so the base's guard-2 needs the explicit opt-in.
        GivenSystemSessionIsExpected();

        await DriveAsync(NodeExecutionIdentity.System);

        AssertSystemSessionOpened();
    }
}
