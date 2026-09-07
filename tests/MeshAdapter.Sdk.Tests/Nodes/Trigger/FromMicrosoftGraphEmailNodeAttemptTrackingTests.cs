using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
/// Tests for the persistent per-message attempt tracking of
/// <see cref="FromMicrosoftGraphEmailNode"/> (AB#5142): the attempt count is
/// carried as an Outlook category on the message so it survives adapter restarts
/// and counts pipeline runs that killed the process before any failure handler ran.
/// </summary>
public class FromMicrosoftGraphEmailNodeAttemptTrackingTests
{
    [Fact]
    public void GetAttemptCount_NoMarker_ReturnsZero()
    {
        Assert.Equal(0, FromMicrosoftGraphEmailNode.GetAttemptCount([]));
        Assert.Equal(0, FromMicrosoftGraphEmailNode.GetAttemptCount(["Red category", "Invoices"]));
    }

    [Fact]
    public void GetAttemptCount_Marker_ReturnsValue()
    {
        Assert.Equal(2, FromMicrosoftGraphEmailNode.GetAttemptCount(
            ["Invoices", FromMicrosoftGraphEmailNode.AttemptCategoryPrefix + "2"]));
    }

    [Fact]
    public void GetAttemptCount_MultipleMarkers_ReturnsMaximum()
    {
        Assert.Equal(3, FromMicrosoftGraphEmailNode.GetAttemptCount(
        [
            FromMicrosoftGraphEmailNode.AttemptCategoryPrefix + "1",
            FromMicrosoftGraphEmailNode.AttemptCategoryPrefix + "3"
        ]));
    }

    [Fact]
    public void GetAttemptCount_MalformedMarker_ReadsAsZero()
    {
        Assert.Equal(0, FromMicrosoftGraphEmailNode.GetAttemptCount(
            [FromMicrosoftGraphEmailNode.AttemptCategoryPrefix + "many"]));
    }

    [Fact]
    public void WithAttemptCategory_ReplacesMarkerAndKeepsUserCategories()
    {
        var result = FromMicrosoftGraphEmailNode.WithAttemptCategory(
            ["Invoices", FromMicrosoftGraphEmailNode.AttemptCategoryPrefix + "1"], 2);

        Assert.Equal(["Invoices", FromMicrosoftGraphEmailNode.AttemptCategoryPrefix + "2"], result);
    }

    [Fact]
    public void WithoutAttemptCategory_KeepsMalformedPrefixedCategory()
    {
        // A user category that merely shares the prefix without a numeric suffix is
        // metadata, not a marker — it must never be removed.
        var malformed = FromMicrosoftGraphEmailNode.AttemptCategoryPrefix + "many";

        var result = FromMicrosoftGraphEmailNode.WithoutAttemptCategory(
            [malformed, FromMicrosoftGraphEmailNode.AttemptCategoryPrefix + "2"]);

        Assert.Equal([malformed], result);
    }

    [Fact]
    public void WithoutAttemptCategory_RemovesEveryMarkerAndKeepsUserCategories()
    {
        var result = FromMicrosoftGraphEmailNode.WithoutAttemptCategory(
        [
            "Invoices",
            FromMicrosoftGraphEmailNode.AttemptCategoryPrefix + "1",
            FromMicrosoftGraphEmailNode.AttemptCategoryPrefix + "2"
        ]);

        Assert.Equal(["Invoices"], result);
    }
}
