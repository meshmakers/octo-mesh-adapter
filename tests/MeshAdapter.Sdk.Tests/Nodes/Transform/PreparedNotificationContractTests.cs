using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

public class PreparedNotificationContractTests
{
    [Fact]
    public void Serialized_UsesTheCamelCaseNamesThePipelineReads()
    {
        var json = JsonSerializer.SerializeToNode(
            new PreparedNotification("p", "st", "s", "subject", "body", "Html", 1, ["a"], ["b"], "q", true), SystemTextJsonOptions.Default)!
            .AsObject();

        Assert.Equal([
                "preferenceRtId", "stateRtId", "subjectId", "subject", "body", "renderingType", "newCount",
                "seenRtIds", "skippedRtIds", "queryRtId", "skipExistingRows", "sent"
            ],
            json.Select(property => property.Key));
    }

    [Fact]
    public void RoundTrip_KeepsTheValuesCommitNeeds()
    {
        var text = JsonSerializer.Serialize(new PreparedNotification("p", "st", "s", "subject", "body", "Html", 1, ["a"], ["b"], "q", true),
            SystemTextJsonOptions.Default);

        var back = JsonSerializer.Deserialize<PreparedNotification>(text, SystemTextJsonOptions.Default)!;

        Assert.Equal("p", back.PreferenceRtId);
        Assert.Equal("st", back.StateRtId);
        Assert.Equal(["a"], back.SeenRtIds);
        Assert.Equal(["b"], back.SkippedRtIds);
        Assert.Equal("q", back.QueryRtId);
        Assert.Equal("s", back.SubjectId);
        Assert.True(back.SkipExistingRows);
    }

    [Fact]
    public void RoundTrip_KeepsTheSentMarker()
    {
        var text = JsonSerializer.Serialize(
            new PreparedNotification("p", "st", "s", "subject", "body", "Html", 1, ["a"], ["b"], "q", true) with { Sent = true },
            SystemTextJsonOptions.Default);

        Assert.True(JsonSerializer.Deserialize<PreparedNotification>(text, SystemTextJsonOptions.Default)!.Sent);
    }
}
