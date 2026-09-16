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
        // The marker protocol is plain ASCII digits — signed or padded suffixes are
        // user metadata, not markers.
        Assert.Equal(0, FromMicrosoftGraphEmailNode.GetAttemptCount(
            [FromMicrosoftGraphEmailNode.AttemptCategoryPrefix + "+2"]));
        Assert.Equal(0, FromMicrosoftGraphEmailNode.GetAttemptCount(
            [FromMicrosoftGraphEmailNode.AttemptCategoryPrefix + " 2"]));
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

/// <summary>
/// Tests for the operator-facing half of the attempt guard (AB#5260): the marker is a
/// two-state protocol (attempt marker → parked marker), the parked marker seen in the
/// source folder is the "move it back to retry" gesture, the marker names are registered
/// so Outlook renders them, and a marker an operator cleared by hand beats the poll loop's
/// in-process fallback counter.
/// </summary>
public class FromMicrosoftGraphEmailNodeMarkerResetTests
{
    private const string Prefix = FromMicrosoftGraphEmailNode.AttemptCategoryPrefix;
    private const string Failed = FromMicrosoftGraphEmailNode.FailedCategory;

    // --- The parked marker ----------------------------------------------------

    [Fact]
    public void HasFailedCategory_DetectsTheParkedMarkerCaseInsensitively()
    {
        Assert.True(FromMicrosoftGraphEmailNode.HasFailedCategory(["Invoices", Failed]));
        Assert.True(FromMicrosoftGraphEmailNode.HasFailedCategory([Failed.ToLowerInvariant()]));
        Assert.False(FromMicrosoftGraphEmailNode.HasFailedCategory(["Invoices", Prefix + "3"]));
        Assert.False(FromMicrosoftGraphEmailNode.HasFailedCategory([]));
    }

    [Fact]
    public void WithFailedCategory_ReplacesTheAttemptMarkerAndKeepsUserCategories()
    {
        var result = FromMicrosoftGraphEmailNode.WithFailedCategory(["Invoices", Prefix + "3"]);

        Assert.Equal(["Invoices", Failed], result);
    }

    [Fact]
    public void WithFailedCategory_IsIdempotent()
    {
        // A message that was moved aside a second time (e.g. the operator moved it back and
        // the reset write failed) must not accumulate duplicate markers.
        var result = FromMicrosoftGraphEmailNode.WithFailedCategory(["Invoices", Failed]);

        Assert.Equal(["Invoices", Failed], result);
    }

    [Fact]
    public void WithAttemptCategory_DropsTheParkedMarker()
    {
        // A message being tried again is no longer parked; leaving the marker behind would
        // make the next poll read it as another "moved back" and grant a fresh budget forever.
        var result = FromMicrosoftGraphEmailNode.WithAttemptCategory(["Invoices", Failed], 1);

        Assert.Equal(["Invoices", Prefix + "1"], result);
    }

    [Fact]
    public void WithoutImportMarkers_RemovesBothMarkerKindsAndKeepsUserCategories()
    {
        var malformed = Prefix + "many";

        var result = FromMicrosoftGraphEmailNode.WithoutImportMarkers(
            ["Invoices", malformed, Prefix + "2", Failed]);

        Assert.Equal(["Invoices", malformed], result);
    }

    [Fact]
    public void WithoutAttemptCategory_LeavesTheParkedMarkerAlone()
    {
        // The two helpers are deliberately different: post-success cleanup clears everything,
        // while the attempt-only helper stays what AB#5142 documented.
        var result = FromMicrosoftGraphEmailNode.WithoutAttemptCategory([Prefix + "2", Failed]);

        Assert.Equal([Failed], result);
    }

    [Fact]
    public void ParkedMessageMovedBack_ResetsToAFullAttemptBudget()
    {
        // The whole retry affordance in one assertion: a parked message the operator dragged
        // back into the source folder is cleared of every marker, and the cleared categories
        // read as zero attempts — a full budget, no Graph access and no restart involved.
        IReadOnlyList<string> parked = ["Invoices", Failed];

        Assert.True(FromMicrosoftGraphEmailNode.HasFailedCategory(parked));
        var cleared = FromMicrosoftGraphEmailNode.WithoutImportMarkers(parked);

        Assert.Equal(["Invoices"], cleared);
        Assert.Equal(0, FromMicrosoftGraphEmailNode.GetAttemptCount(cleared));
        Assert.False(FromMicrosoftGraphEmailNode.HasFailedCategory(cleared));
    }

    // --- Category registration (visibility in Outlook) ------------------------

    [Fact]
    public void ImportCategoryDefinitions_CoverEveryReachableMarker()
    {
        var definitions = FromMicrosoftGraphEmailNode.ImportCategoryDefinitions(3).ToList();

        Assert.Equal(
            [Prefix + "1", Prefix + "2", Prefix + "3", Failed],
            definitions.Select(d => d.Name));
    }

    [Fact]
    public void ImportCategoryDefinitions_GiveTheParkedMarkerItsOwnColour()
    {
        var definitions = FromMicrosoftGraphEmailNode.ImportCategoryDefinitions(2).ToList();
        var attemptColours = definitions.Where(d => d.Name != Failed).Select(d => d.Colour).Distinct().ToList();

        Assert.Single(attemptColours);
        Assert.DoesNotContain(definitions.Single(d => d.Name == Failed).Colour, attemptColours);
        // Graph only accepts its own preset palette names.
        Assert.All(definitions, d => Assert.StartsWith("preset", d.Colour));
    }

    [Fact]
    public void ImportCategoryDefinitions_RegisterOnlyTheParkedMarkerWhenNoAttemptIsReachable()
    {
        // MaxAttemptsPerMessage = 0 disables retrying altogether; no attempt marker can ever
        // be written, so registering one would only litter the operator's category list.
        var definitions = FromMicrosoftGraphEmailNode.ImportCategoryDefinitions(0).ToList();

        Assert.Equal([Failed], definitions.Select(d => d.Name));
    }

    // --- The category beats the in-process fallback counter -------------------

    [Fact]
    public void ResolveAttemptCount_NoMemory_UsesTheStampedCount()
    {
        var attempts = FromMicrosoftGraphEmailNode.ResolveAttemptCount(
            [Prefix + "2"], remembered: null, out var operatorReset);

        Assert.Equal(2, attempts);
        Assert.False(operatorReset);
    }

    [Fact]
    public void ResolveAttemptCount_ClearedMarkerAfterASuccessfulStamp_IsAnOperatorReset()
    {
        // The regression AB#5260 is about: the operator removed the category in Outlook, the
        // poll loop still remembered 3 — and Math.Max kept the message burnt until the adapter
        // restarted or the DataFlow was redeployed.
        var attempts = FromMicrosoftGraphEmailNode.ResolveAttemptCount(
            ["Invoices"], new FromMicrosoftGraphEmailNode.AttemptMemory(3, Stamped: true),
            out var operatorReset);

        Assert.Equal(0, attempts);
        Assert.True(operatorReset);
    }

    [Fact]
    public void ResolveAttemptCount_LoweredMarkerAfterASuccessfulStamp_IsAnOperatorReset()
    {
        // Clearing is the documented gesture, but an operator who edits the marker down to a
        // lower count means the same thing and must be obeyed just as well.
        var attempts = FromMicrosoftGraphEmailNode.ResolveAttemptCount(
            [Prefix + "1"], new FromMicrosoftGraphEmailNode.AttemptMemory(3, Stamped: true),
            out var operatorReset);

        Assert.Equal(1, attempts);
        Assert.True(operatorReset);
    }

    [Fact]
    public void ResolveAttemptCount_MarkerStillInStep_KeepsCountingAndIsNoReset()
    {
        var attempts = FromMicrosoftGraphEmailNode.ResolveAttemptCount(
            [Prefix + "3"], new FromMicrosoftGraphEmailNode.AttemptMemory(3, Stamped: true),
            out var operatorReset);

        Assert.Equal(3, attempts);
        Assert.False(operatorReset);
    }

    [Fact]
    public void ResolveAttemptCount_UnstampedMailbox_KeepsTheInProcessFallback()
    {
        // The mailbox never accepted a stamp, so the absent marker says nothing. Reading it as
        // a reset here would resurrect the poison-mail loop AB#5142 closed — on exactly the
        // mailboxes the fallback counter exists for.
        var attempts = FromMicrosoftGraphEmailNode.ResolveAttemptCount(
            [], new FromMicrosoftGraphEmailNode.AttemptMemory(3, Stamped: false), out var operatorReset);

        Assert.Equal(3, attempts);
        Assert.False(operatorReset);
    }

    [Fact]
    public void ResolveAttemptCount_StampFailedOnTheLastAttemptOnly_LosesNoAttempt()
    {
        // Attempts 1 and 2 stamped fine, attempt 3's write failed: the marker legitimately
        // trails the truth, so the remembered count wins and nothing is read as a reset.
        var attempts = FromMicrosoftGraphEmailNode.ResolveAttemptCount(
            [Prefix + "2"], new FromMicrosoftGraphEmailNode.AttemptMemory(3, Stamped: false),
            out var operatorReset);

        Assert.Equal(3, attempts);
        Assert.False(operatorReset);
    }

    [Fact]
    public void ResolveAttemptCount_MarkerAheadOfMemory_TakesTheMarker()
    {
        // A restart-surviving marker written by an earlier node lifetime (or by the run that
        // killed the process) outranks whatever this lifetime happens to remember.
        var attempts = FromMicrosoftGraphEmailNode.ResolveAttemptCount(
            [Prefix + "3"], new FromMicrosoftGraphEmailNode.AttemptMemory(1, Stamped: true),
            out var operatorReset);

        Assert.Equal(3, attempts);
        Assert.False(operatorReset);
    }
}
