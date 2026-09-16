using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

/// <summary>
///     Proves WriteVerifiedCaller@1 (AB#5136 / AB#5149): the execution's verified caller is
///     materialized at the target path with the contract field names — and the AB#5149
///     <c>preferredChannel</c> is emitted VERBATIM ("TEAMS" | "SIGNAL") when the principal carries
///     one, while a caller without a preference produces an object with the field ABSENT (not null),
///     so pre-AB#5149 pipelines see an unchanged shape and consumers can gate on field presence.
/// </summary>
public class WriteVerifiedCallerNodeTests : NodeTestBase
{
    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();

    private async Task<JsonObject?> RunAsync(VerifiedPrincipal? principal)
    {
        A.CallTo(() => _etlContext.VerifiedPrincipal).Returns(principal);

        var config = new WriteVerifiedCallerNodeConfiguration();
        var (dataContext, nodeContext, next) = PrepareTest<WriteVerifiedCallerNodeConfiguration>(config);

        JsonObject? emitted = null;
        A.CallTo(() => dataContext.Set(config.TargetPath, A<JsonObject>._, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes((string _, JsonObject value, DocumentModes _, ValueKinds _, TargetValueWriteModes _) =>
                emitted = value);

        var node = new WriteVerifiedCallerNode(next, _etlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
        return emitted;
    }

    [Theory]
    [InlineData("TEAMS")]
    [InlineData("SIGNAL")]
    public async Task Preferred_channel_is_emitted_verbatim_when_the_caller_carries_one(string channel)
    {
        // The exact spellings are the AB#5149 cross-repo contract with the app pipelines.
        var emitted = await RunAsync(new VerifiedPrincipal(
            "user-rt-1", "acme", "u@example.com", "u", ["Reader"], channel));

        Assert.NotNull(emitted);
        Assert.Equal(channel, emitted!["preferredChannel"]!.GetValue<string>());
    }

    [Fact]
    public async Task Caller_without_a_preference_omits_the_field_entirely()
    {
        var emitted = await RunAsync(new VerifiedPrincipal(
            "user-rt-1", "acme", "u@example.com", "u", ["Reader"]));

        Assert.NotNull(emitted);
        Assert.False(emitted!.ContainsKey("preferredChannel"));
    }

    [Fact]
    public async Task Caller_object_keeps_the_established_contract_fields()
    {
        var emitted = await RunAsync(new VerifiedPrincipal(
            "user-rt-1", "acme", "u@example.com", "u", ["Reader", "Writer"], "TEAMS"));

        Assert.NotNull(emitted);
        Assert.Equal("user-rt-1", emitted!["subjectId"]!.GetValue<string>());
        Assert.Equal("acme", emitted["tenantId"]!.GetValue<string>());
        Assert.Equal("u", emitted["name"]!.GetValue<string>());
        Assert.Equal("u@example.com", emitted["email"]!.GetValue<string>());
        Assert.Equal(2, emitted["roles"]!.AsArray().Count);
    }

    [Fact]
    public async Task Emitted_object_round_trips_through_serialization()
    {
        // The caller object is echoed into HTTP responses and persisted by
        // SetPipelineExecutionResult@1, so the JSON round trip is part of the contract.
        var emitted = await RunAsync(new VerifiedPrincipal(
            "user-rt-1", "acme", "u@example.com", "u", ["Reader"], "SIGNAL"));

        var reparsed = JsonNode.Parse(emitted!.ToJsonString(Options))!.AsObject();

        Assert.Equal("SIGNAL", reparsed["preferredChannel"]!.GetValue<string>());
        Assert.Equal("user-rt-1", reparsed["subjectId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Absent_field_is_tolerated_on_the_consuming_side()
    {
        // A pre-AB#5149 caller object carries no "preferredChannel" — reading it must yield null,
        // never throw (absent-field tolerance for pipelines probing $.caller.preferredChannel).
        var emitted = await RunAsync(new VerifiedPrincipal(
            "user-rt-1", "acme", "u@example.com", "u", ["Reader"]));

        var reparsed = JsonNode.Parse(emitted!.ToJsonString(Options))!.AsObject();

        Assert.Null(reparsed["preferredChannel"]);
    }

    [Fact]
    public async Task No_verified_caller_writes_nothing_and_continues_the_chain()
    {
        var emitted = await RunAsync(null);

        Assert.Null(emitted);
    }
}
