using System.Globalization;
using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Why a placeholder produced what it did.
/// </summary>
public enum PlaceholderOutcome
{
    /// <summary>The source was there and carried a value.</summary>
    Value,

    /// <summary>
    /// The source was there and the attribute is genuinely empty. A private customer has no
    /// company name and most people have no title; that is data, not a fault.
    /// </summary>
    EmptyByData,

    /// <summary>
    /// The entity the token reads from was not supplied at all - a billing token on a path
    /// sending no bill, or a customer token for an address the tenant does not know. The
    /// resolver cannot tell an empty value from an unknown one here, so it refuses to guess.
    /// </summary>
    SourceMissing
}

/// <summary>One placeholder's lookup result.</summary>
/// <param name="Outcome">Why it is what it is.</param>
/// <param name="Text">The rendered text; empty unless <see cref="PlaceholderOutcome.Value"/>.</param>
public sealed record PlaceholderValue(PlaceholderOutcome Outcome, string Text = "")
{
    /// <summary>A resolved value.</summary>
    public static PlaceholderValue Of(string text) =>
        string.IsNullOrEmpty(text) ? Empty : new PlaceholderValue(PlaceholderOutcome.Value, text);

    /// <summary>The source is present and the attribute is empty.</summary>
    public static readonly PlaceholderValue Empty = new(PlaceholderOutcome.EmptyByData);

    /// <summary>The source itself was never supplied.</summary>
    public static readonly PlaceholderValue NoSource = new(PlaceholderOutcome.SourceMissing);
}

/// <summary>
/// Substitutes <c>${...}</c> placeholders in a notification template.
///
/// Pure on purpose: it takes a lookup rather than a data context, so every source and every
/// missing-value case is unit-testable without a pipeline - which is what AB#2569's fourth
/// acceptance criterion asks for.
///
/// **On the fallback.** AB#2569 asks for a documented choice between an empty string and an
/// abort. Neither answers the whole question, because "missing" covers two different things.
/// A company name a private customer does not have is data: substituting nothing is correct and
/// the mail should go. A billing token on a path carrying no billing document, or a customer
/// token for an address that matched no customer, means the resolver was never given the thing
/// it reads from - and there a blank is indistinguishable from a value, which is a guess the
/// resolver has no business making. So: empty where the data is empty, refuse where the source
/// is absent. The loop above decides the blast radius through
/// <c>ForEach@1.ContinueOnError</c>; the refusal itself is never swallowed.
///
/// **What this does NOT protect against.** The line is drawn at the entity, so a present entity
/// with an empty attribute always substitutes nothing - including the community's own IBAN when
/// nobody filled it in Settings, which is how `offene-abrechnung` can still ask a member to pay
/// into a blank account number. That is a configuration-completeness problem and belongs where
/// the value is entered; moving the line to the attribute would turn every genuinely empty
/// optional field into a failed batch.
/// </summary>
public static class NotificationPlaceholderResolver
{
    /// <summary>
    /// Matches the substitution syntax itself, not the catalog: an unknown token has to be
    /// recognised in order to be reported. <c>[^{}]</c> keeps an unterminated <c>${</c> from
    /// swallowing the rest of the template.
    /// </summary>
    private static readonly Regex Placeholder = new(@"\$\{(?<token>[^{}]*)\}", RegexOptions.Compiled);

    /// <summary>
    /// What a resolution produced.
    /// </summary>
    /// <param name="Text">The rendered text.</param>
    /// <param name="EmptyByData">Tokens whose source was present and empty; worth a warning.</param>
    /// <param name="SourceMissing">Tokens whose source was never supplied; the send must not proceed.</param>
    public sealed record Result(
        string Text,
        IReadOnlyList<string> EmptyByData,
        IReadOnlyList<string> SourceMissing);

    /// <summary>
    /// Replaces every placeholder with its value, recording why each empty one is empty.
    /// </summary>
    public static Result Resolve(string? text, Func<string, PlaceholderValue> lookup)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new Result(text ?? string.Empty, [], []);
        }

        var emptyByData = new List<string>();
        var sourceMissing = new List<string>();

        var rendered = Placeholder.Replace(text, match =>
        {
            var token = match.Groups["token"].Value;
            var value = lookup(token);

            switch (value.Outcome)
            {
                case PlaceholderOutcome.Value:
                    return value.Text;
                case PlaceholderOutcome.EmptyByData:
                    Record(emptyByData, token);
                    return string.Empty;
                default:
                    Record(sourceMissing, token);
                    return string.Empty;
            }
        });

        return new Result(rendered, emptyByData, sourceMissing);
    }

    private static void Record(List<string> into, string token)
    {
        if (!into.Contains(token, StringComparer.OrdinalIgnoreCase))
        {
            into.Add(token);
        }
    }

    /// <summary>
    /// Turns a raw attribute value into the text an author expects to read.
    /// </summary>
    /// <param name="raw">The value as the data context holds it.</param>
    /// <param name="format">How the catalog says to render it.</param>
    /// <param name="inlineImageMarkup">
    /// What an <see cref="PlaceholderFormat.InlineImage"/> becomes when the image is there and
    /// the template can show it; null when neither holds.
    /// </param>
    /// <param name="encodeForMarkup">
    /// Whether the result is going into text that will be rendered as HTML. The caller decides:
    /// a body usually is, a subject never is. See <see cref="EncodeForMarkup"/>.
    /// </param>
    public static string Format(
        object? raw, PlaceholderFormat format, string? inlineImageMarkup, bool encodeForMarkup = false)
    {
        if (format == PlaceholderFormat.InlineImage)
        {
            // The markup is only produced when the caller established both that an image exists
            // and that the template renders markup at all. Exempt from encoding by definition -
            // this one IS markup, and it is the node's own, not a value out of a record.
            return raw == null || inlineImageMarkup == null ? string.Empty : inlineImageMarkup;
        }

        if (raw == null)
        {
            return string.Empty;
        }

        var text = format switch
        {
            PlaceholderFormat.Salutation => NotificationPlaceholderCatalog.Salutation(raw),
            PlaceholderFormat.BillingType => NotificationPlaceholderCatalog.BillingType(raw),
            PlaceholderFormat.Date => FormatDate(raw),
            PlaceholderFormat.Money => FormatMoney(raw),
            _ => raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty
        };

        // Every format, not only the free-text one. The four above produce text this code chose,
        // so encoding them changes nothing today - but the reason they are safe is an argument
        // about their implementations, and the next format added would inherit the exemption
        // rather than the rule.
        return encodeForMarkup ? EncodeForMarkup(text) : text;
    }

    /// <summary>
    /// Escapes the five characters that can start markup or break out of an attribute, and
    /// nothing else.
    ///
    /// Deliberately not <see cref="System.Net.WebUtility.HtmlEncode(string)"/>, which also turns every
    /// character above ASCII into a numeric entity: the text/plain alternative is derived from
    /// this same string, so <c>Müller</c> would reach a text-only reader as <c>M&amp;#252;ller</c>.
    /// The five here are the HTML-context set, and a German name, a currency amount and the
    /// no-break space in it all pass through untouched.
    /// </summary>
    public static string EncodeForMarkup(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);

    private static string FormatDate(object raw)
    {
        if (raw is DateTime dateTime)
        {
            return FormatCalendarDay(dateTime);
        }

        // A date crosses the pipeline as an ISO string more often than as a DateTime, and an
        // unparsable value is left as written rather than replaced with a wrong date.
        var text = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? FormatCalendarDay(parsed)
            : text;
    }

    /// <summary>
    /// The calendar day this instant falls on in the tenant's zone.
    ///
    /// The conversion is the whole point. A billing period has no time of day and the CK has no
    /// date-only type, so the app anchors each boundary at local midnight and sends the instant:
    /// 1 April 2026 leaves the browser as <c>2026-03-31T22:00:00Z</c>. <c>DateTime.ToString</c>
    /// formats the struct's own fields and never converts, so printing that value directly gave
    /// 31.03.2026 - one day before the period the operator picked, and one day before the grid
    /// they picked it from. Kind is Utc for every value the pipeline delivers (System.Text.Json
    /// keeps the offset), and an Unspecified one is read as UTC rather than as the adapter
    /// machine's zone, which in a container is UTC anyway and in development is not.
    /// </summary>
    private static string FormatCalendarDay(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        return TimeZoneInfo.ConvertTimeFromUtc(utc, NotificationPlaceholderCatalog.TimeZone)
            .ToString("dd.MM.yyyy", NotificationPlaceholderCatalog.Culture);
    }

    /// <summary>
    /// An amount the recipient is asked to pay. Rendered in the tenant's culture, because
    /// ConvertDataType@1 - which the pipelines used before this node - emits a JSON number, so
    /// a gross total of 128.40 reached the reader as "128.4" while the invoice PDF beside it
    /// said "128,40".
    /// </summary>
    private static string FormatMoney(object raw)
    {
        if (raw is decimal or double or float or int or long)
        {
            return Convert.ToDecimal(raw, CultureInfo.InvariantCulture)
                .ToString("N2", NotificationPlaceholderCatalog.Culture);
        }

        var text = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;
        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount)
            ? amount.ToString("N2", NotificationPlaceholderCatalog.Culture)
            : text;
    }
}
