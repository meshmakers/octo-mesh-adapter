using System.Globalization;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Where one placeholder's value comes from.
/// </summary>
public enum PlaceholderSource
{
    /// <summary>The customer, or a billing document's own contact snapshot.</summary>
    Customer,

    /// <summary>The community configuration.</summary>
    Community,

    /// <summary>The billing document, absent on a path that is not sending one.</summary>
    BillingDocument
}

/// <summary>
/// How a raw attribute becomes the text an author expects to read.
/// </summary>
public enum PlaceholderFormat
{
    /// <summary>Whatever the attribute is, as a string.</summary>
    Text,

    /// <summary>A salutation enum key, rendered as the form of address.</summary>
    Salutation,

    /// <summary>A date, rendered dd.MM.yyyy.</summary>
    Date,

    /// <summary>A decimal, rendered with the Austrian separators an invoice uses.</summary>
    Money,

    /// <summary>The community's e-mail image, rendered as the reference that addresses it.</summary>
    InlineImage,

    /// <summary>A billing type key, rendered as the app already names it.</summary>
    BillingType
}

/// <summary>
/// One entry of the placeholder catalog.
/// </summary>
/// <param name="Token">The dotted name an author writes inside <c>${...}</c>.</param>
/// <param name="Source">Which entity carries it.</param>
/// <param name="AttributePath">Path under that entity's <c>Attributes</c>.</param>
/// <param name="Format">How the raw value becomes text.</param>
/// <param name="FallbackAttributePath">Read when the primary attribute is empty, for the fields
/// the app itself falls back on. Null where there is nothing to fall back to.</param>
public sealed record PlaceholderDefinition(
    string Token,
    PlaceholderSource Source,
    string AttributePath,
    PlaceholderFormat Format = PlaceholderFormat.Text,
    string? FallbackAttributePath = null);

/// <summary>
/// The single list of placeholders every send path resolves.
///
/// AB#2569 requires that "placeholder resolution must happen where the email is rendered, so
/// that ALL send paths share one resolver". Before this, each send pipeline carried its own
/// generated <c>PlaceholderReplace@1</c> rule blocks plus hand-written converter nodes, so a
/// fourth send path meant a fourth copy - and a token wired in one pipeline and not another
/// silently produced different mail from the same template.
///
/// The obvious alternative, a resolver *pipeline* called through
/// <c>ToPipelineDataEvent@1 awaitResult</c>, cannot be shared: that node requires the target to
/// live in the same DataFlow, and its queues are named per DataFlow. The three send pipelines
/// sit in three DataFlows, so it would have meant three resolvers again.
/// </summary>
public static class NotificationPlaceholderCatalog
{
    /// <summary>Austrian formatting: the invoice, the mail and the PDF must agree.</summary>
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("de-AT");

    /// <summary>
    /// The zone the tenant's dates are anchored in. A billing period is a date without a time of
    /// day, the CK has no date-only type, and the app anchors each boundary at Vienna midnight -
    /// so the instant on the wire is 22:00 or 23:00 of the previous day, and it has to be read
    /// back in the same zone to name the day the operator chose.
    /// </summary>
    public static readonly TimeZoneInfo TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");

    /// <summary>
    /// Every placeholder a send path can fill, and where each one reads from. The editor's own
    /// catalog is written against this list; a token in one and not the other means the editor
    /// is stale, never that a mail goes out wrong.
    /// </summary>
    public static readonly IReadOnlyList<PlaceholderDefinition> Definitions =
    [
        new("customer.salutation", PlaceholderSource.Customer, "Contact.Attributes.Salutation", PlaceholderFormat.Salutation),
        new("customer.titlePrefix", PlaceholderSource.Customer, "Contact.Attributes.TitlePrefix"),
        new("customer.firstName", PlaceholderSource.Customer, "Contact.Attributes.FirstName"),
        new("customer.lastName", PlaceholderSource.Customer, "Contact.Attributes.LastName"),
        new("customer.titleSuffix", PlaceholderSource.Customer, "Contact.Attributes.TitleSuffix"),
        new("customer.companyName", PlaceholderSource.Customer, "Contact.Attributes.CompanyName"),
        new("customer.customerNumber", PlaceholderSource.Customer, "CustomerNumber"),
        new("customer.email", PlaceholderSource.Customer, "Contact.Attributes.Email"),
        new("customer.street", PlaceholderSource.Customer, "Contact.Attributes.Address.Attributes.Street"),
        new("customer.zipcode", PlaceholderSource.Customer, "Contact.Attributes.Address.Attributes.Zipcode"),
        new("customer.cityTown", PlaceholderSource.Customer, "Contact.Attributes.Address.Attributes.CityTown"),
        new("customer.nationalCode", PlaceholderSource.Customer, "Contact.Attributes.Address.Attributes.NationalCode"),
        new("customer.phone", PlaceholderSource.Customer, "Contact.Attributes.Address.Attributes.Phone.Attributes.Number"),
        // The customer's own account, i.e. where a credit note is paid out to - not the
        // community's, which is what community.iban names.
        new("customer.iban", PlaceholderSource.Customer, "BankAccount.Attributes.Iban"),
        new("customer.accountHolder", PlaceholderSource.Customer, "BankAccount.Attributes.AccountHolder"),

        new("community.name", PlaceholderSource.Community, "Name"),
        // Both fall back to the community name, because the app does: it sets the browser title
        // to `appTitle || name` (ThemeService.applyAppName) and the page heading to
        // `appHeading || name` (AppComponent.ngOnInit). Without the same fallback a template
        // author reads "EEG Musterstadt" off the screen, writes the token the screen calls it,
        // and the mail carries nothing.
        new("community.appTitle", PlaceholderSource.Community, "AppTitle", FallbackAttributePath: "Name"),
        new("community.appHeading", PlaceholderSource.Community, "AppHeading", FallbackAttributePath: "Name"),
        new("community.contactEmail", PlaceholderSource.Community, "NotificationEmailContact"),
        new("community.accountHolder", PlaceholderSource.Community, "BankAccountHolder"),
        new("community.iban", PlaceholderSource.Community, "BankAccountIban"),
        new("community.logo", PlaceholderSource.Community, "EmailFooterImage.BinaryId", PlaceholderFormat.InlineImage),
        new("community.consumerPrice", PlaceholderSource.Community, "ConsumerPrice", PlaceholderFormat.Money),
        new("community.producerPrice", PlaceholderSource.Community, "ProducerPrice", PlaceholderFormat.Money),
        // A whole percentage, so it reads as "20" rather than as the "20,00" an amount would get.
        new("community.taxRate", PlaceholderSource.Community, "TaxRate"),

        new("billingDocument.documentNumber", PlaceholderSource.BillingDocument, "DocumentNumber"),
        new("billingDocument.documentDate", PlaceholderSource.BillingDocument, "DocumentDate", PlaceholderFormat.Date),
        new("billingDocument.periodFrom", PlaceholderSource.BillingDocument, "TimeRange.Attributes.From", PlaceholderFormat.Date),
        new("billingDocument.periodTo", PlaceholderSource.BillingDocument, "TimeRange.Attributes.To", PlaceholderFormat.Date),
        new("billingDocument.grossTotal", PlaceholderSource.BillingDocument, "GrossTotal", PlaceholderFormat.Money),
        new("billingDocument.billingType", PlaceholderSource.BillingDocument, "BillingType", PlaceholderFormat.BillingType)
    ];

    /// <summary>
    /// What the document is called. Deliberately the words the app already shows for this enum
    /// (energy_community.billing.types.DEBIT/CREDIT) rather than new ones: the operator picked
    /// the document off a list that called it this, and the mail must not rename it. An unknown
    /// key yields nothing - calling a credit note an invoice is worse than saying neither.
    /// </summary>
    public static string BillingType(object? key)
    {
        return key switch
        {
            0 or "0" => "Rechnung",
            1 or "1" => "Gutschrift",
            _ => string.Empty
        };
    }

    /// <summary>
    /// The form of address for a salutation key. The keys are the CK enum's, and an unset or
    /// unknown one yields nothing rather than a guess - a letter opening "Liebe / lieber Herr"
    /// to someone who never said is worse than one that just uses the name.
    /// </summary>
    public static string Salutation(object? key)
    {
        return key switch
        {
            1 or "1" => "Herr",
            2 or "2" => "Frau",
            _ => string.Empty
        };
    }
}
