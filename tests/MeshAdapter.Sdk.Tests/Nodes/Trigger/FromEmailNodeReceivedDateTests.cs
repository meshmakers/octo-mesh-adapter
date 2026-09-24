using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Xunit;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
/// AB#5339. <c>FromEmail@1</c> mapped <c>Date = message.Date.DateTime</c>, and MimeKit reports a mail
/// with no <c>Date:</c> header — or one it cannot parse — as <see cref="DateTimeOffset.MinValue"/>.
/// That travelled through the pipeline into <c>UploadedDocument.SourceReceivedAt</c> as
/// <c>0001-01-01T00:00:00Z</c>: the receipt sorts to the very front of the accounting inbox and falls
/// outside every fiscal year, so it cannot be assigned to a period at all. Found on prod-1/gastroacker
/// with 67 mails from a sender that emits no <c>Date:</c> header.
///
/// The resolution order is pinned here: the header when it is usable, otherwise the server's IMAP
/// INTERNALDATE, otherwise null — never year 1.
/// </summary>
public class FromEmailNodeReceivedDateTests
{
    private static readonly DateTimeOffset HeaderDate =
        new(2026, 9, 12, 8, 30, 0, TimeSpan.FromHours(2));

    private static readonly DateTimeOffset InternalDate =
        new(2026, 9, 12, 6, 31, 17, TimeSpan.Zero);

    [Fact]
    public void HeaderDate_WinsWhenItIsUsable()
    {
        // The header is what the sender stated and stays authoritative; the INTERNALDATE is a
        // fallback, not a correction.
        Assert.Equal(HeaderDate.DateTime, FromEmailNode.ResolveReceivedAt(HeaderDate, InternalDate));
    }

    [Fact]
    public void MissingHeaderDate_FallsBackToTheInternalDate()
    {
        // The gastroacker shape: no `Date:` header at all, so MimeKit yields the default value.
        Assert.Equal(InternalDate.DateTime,
            FromEmailNode.ResolveReceivedAt(default, InternalDate));
    }

    [Fact]
    public void UnparsableHeaderDate_FallsBackToTheInternalDate()
    {
        // MimeKit reports "present but unparsable" the same way it reports "absent", so one guard
        // covers both — which is why the check is on the value, not on the header's presence.
        Assert.Equal(InternalDate.DateTime,
            FromEmailNode.ResolveReceivedAt(DateTimeOffset.MinValue, InternalDate));
    }

    [Fact]
    public void NoHeaderAndNoInternalDate_IsNullRatherThanYearOne()
    {
        // "Unknown" is a state the consumer can handle: CreateUpdateInfo@1 writes a JSON null as a
        // null attribute value. Year 1 looks like a real date and silently leaves the fiscal year.
        Assert.Null(FromEmailNode.ResolveReceivedAt(default, null));
    }

    [Fact]
    public void AnUnusableInternalDate_IsNotWrittenEither()
    {
        // A server that answers the FETCH with a default-valued INTERNALDATE must not reintroduce
        // exactly the year-1 timestamp this fix exists to prevent.
        Assert.Null(FromEmailNode.ResolveReceivedAt(default, DateTimeOffset.MinValue));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void NoCombinationEverYieldsYearOne(bool hasHeaderDate, bool hasInternalDate)
    {
        var resolved = FromEmailNode.ResolveReceivedAt(
            hasHeaderDate ? HeaderDate : default,
            hasInternalDate ? InternalDate : null);

        Assert.True(resolved is null or { Year: > 1 });
    }

    [Fact]
    public void BothSourcesKeepTheirWallClockTime()
    {
        // The header path has always emitted the stated wall-clock time without the offset, and the
        // consumers store it as-is. Converting to UTC here would shift every existing timestamp of
        // every mail that DOES carry a header, so the fallback follows the same rule.
        Assert.Equal(new DateTime(2026, 9, 12, 8, 30, 0),
            FromEmailNode.ResolveReceivedAt(HeaderDate, null));
        Assert.Equal(new DateTime(2026, 9, 12, 6, 31, 17),
            FromEmailNode.ResolveReceivedAt(default, InternalDate));
    }

    [Fact]
    public void TheEmailDataFieldCanCarryTheUnknown()
    {
        // The whole fix depends on EmailData.Date being nullable — a non-nullable field would turn
        // the resolved "unknown" back into DateTime.MinValue on assignment.
        var emailData = new EmailData { Date = FromEmailNode.ResolveReceivedAt(default, null) };

        Assert.Null(emailData.Date);
    }
}
