using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
/// AB#2569 asks for one resolver shared by every send path, and for unit tests covering all
/// supported sources and the missing-value fallback. These exercise the resolver without a
/// pipeline; the node above it only maps data-context paths onto this.
/// </summary>
public class NotificationPlaceholderResolverTests
{
    private static string Render(string text, Dictionary<string, string?> values) =>
        NotificationPlaceholderResolver.Resolve(text, token =>
            values.TryGetValue(token, out var v)
                ? PlaceholderValue.Of(v ?? string.Empty)
                : PlaceholderValue.NoSource).Text;

    [Fact]
    public void Every_occurrence_of_a_token_is_replaced()
    {
        var text = Render("${a} und ${a}", new Dictionary<string, string?> { ["a"] = "x" });

        Assert.Equal("x und x", text);
    }

    [Fact]
    public void Text_around_the_tokens_is_untouched()
    {
        var text = Render("Liebe / lieber ${customer.firstName},", new Dictionary<string, string?>
        {
            ["customer.firstName"] = "Erika"
        });

        Assert.Equal("Liebe / lieber Erika,", text);
    }

    [Fact]
    public void An_unterminated_opening_does_not_swallow_the_rest_of_the_template()
    {
        var text = Render("${ohne Ende und ${a}", new Dictionary<string, string?> { ["a"] = "x" });

        Assert.Equal("${ohne Ende und x", text);
    }

    [Fact]
    public void An_empty_template_is_returned_as_it_is()
    {
        Assert.Equal(string.Empty,
            NotificationPlaceholderResolver.Resolve(null, _ => PlaceholderValue.Of("x")).Text);
    }

    /// <summary>
    /// A private customer has no company name and most people have no title. That is data, so
    /// the token renders as nothing and the mail goes - but it is still recorded, because a
    /// whole letter of blanks is worth seeing in the log.
    /// </summary>
    [Fact]
    public void An_attribute_that_is_genuinely_empty_renders_as_nothing_and_does_not_stop_the_send()
    {
        var result = NotificationPlaceholderResolver.Resolve(
            "Firma: ${customer.companyName}!", _ => PlaceholderValue.Empty);

        Assert.Equal("Firma: !", result.Text);
        Assert.Equal(["customer.companyName"], result.EmptyByData);
        Assert.Empty(result.SourceMissing);
    }

    /// <summary>
    /// The case that must not pass silently: nothing was supplied to read from, so an empty
    /// result would be indistinguishable from an empty value.
    ///
    /// The token is a billing one on purpose. `${community.iban}` stood here and made the test
    /// read as proof against a blank account number - which it is not: all three send pipelines
    /// pass a community configuration, so that token can never be NoSource in production. The
    /// case this really covers is a billing token on a bulk send, where no billing document
    /// exists at all.
    /// </summary>
    [Fact]
    public void A_token_whose_source_was_never_supplied_is_reported_separately()
    {
        var result = NotificationPlaceholderResolver.Resolve(
            "Betrag: ${billingDocument.grossTotal}", _ => PlaceholderValue.NoSource);

        Assert.Equal(["billingDocument.grossTotal"], result.SourceMissing);
        Assert.Empty(result.EmptyByData);
    }

    [Fact]
    public void The_two_kinds_of_empty_are_kept_apart_in_one_template()
    {
        var result = NotificationPlaceholderResolver.Resolve(
            "${a} ${b}",
            token => token == "a" ? PlaceholderValue.Empty : PlaceholderValue.NoSource);

        Assert.Equal(["a"], result.EmptyByData);
        Assert.Equal(["b"], result.SourceMissing);
    }

    [Fact]
    public void Each_token_is_reported_once_however_often_it_appears()
    {
        var result = NotificationPlaceholderResolver.Resolve(
            "${a} ${a} ${a}", _ => PlaceholderValue.NoSource);

        Assert.Equal(["a"], result.SourceMissing);
    }

    [Fact]
    public void A_filled_token_is_reported_nowhere()
    {
        var result = NotificationPlaceholderResolver.Resolve("${a}", _ => PlaceholderValue.Of("value"));

        Assert.Empty(result.EmptyByData);
        Assert.Empty(result.SourceMissing);
    }

    [Fact]
    public void A_value_that_is_an_empty_string_counts_as_empty_data_not_as_a_missing_source()
    {
        var result = NotificationPlaceholderResolver.Resolve("${a}", _ => PlaceholderValue.Of(string.Empty));

        Assert.Equal(["a"], result.EmptyByData);
        Assert.Empty(result.SourceMissing);
    }
}

/// <summary>
/// The formatting half: every catalog format, and the values that reach it in practice.
/// </summary>
public class NotificationPlaceholderFormatTests
{
    [Theory]
    [InlineData(1, "Herr")]
    [InlineData(2, "Frau")]
    [InlineData(0, "")]
    [InlineData(3, "")]
    public void A_salutation_key_becomes_the_form_of_address(int key, string expected)
    {
        Assert.Equal(expected, NotificationPlaceholderResolver.Format(key, PlaceholderFormat.Salutation, null));
    }

    [Fact]
    public void A_salutation_that_arrived_as_text_is_read_the_same_way()
    {
        Assert.Equal("Frau", NotificationPlaceholderResolver.Format("2", PlaceholderFormat.Salutation, null));
    }

    [Fact]
    public void A_date_is_rendered_the_way_the_letter_writes_it()
    {
        var value = new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal("30.06.2026", NotificationPlaceholderResolver.Format(value, PlaceholderFormat.Date, null));
    }

    /// <summary>
    /// The case that made every period read one day early. A billing period carries no time of
    /// day, so InvoicePeriodService anchors it at Vienna midnight and sends the instant - 1 April
    /// 2026 leaves the browser as 2026-03-31T22:00:00Z. Formatting that without converting back
    /// printed 31.03.2026 in the mail while the grid the operator picked it from said 1.4.2026.
    /// Observed on a live send before the conversion existed.
    /// </summary>
    [Theory]
    [InlineData("2026-03-31T22:00:00Z", "01.04.2026")]
    [InlineData("2026-06-30T22:00:00Z", "01.07.2026")]
    public void A_summer_boundary_names_the_day_the_operator_picked(string iso, string expected)
    {
        Assert.Equal(expected, NotificationPlaceholderResolver.Format(iso, PlaceholderFormat.Date, null));
    }

    /// <summary>Winter is the other offset, so the shift is an hour smaller and still a day.</summary>
    [Fact]
    public void A_winter_boundary_is_converted_with_the_offset_that_applies_then()
    {
        Assert.Equal("01.01.2026",
            NotificationPlaceholderResolver.Format("2025-12-31T23:00:00Z", PlaceholderFormat.Date, null));
    }

    /// <summary>
    /// A value genuinely anchored at UTC midnight still names its own day - 02:00 in Vienna is the
    /// same date. Worth keeping in view: the fixtures used while diagnosing the shift above were
    /// built this way, which is exactly why they could not reproduce it.
    /// </summary>
    [Fact]
    public void An_iso_string_is_a_date_too_because_that_is_how_it_crosses_the_pipeline()
    {
        Assert.Equal("30.06.2026",
            NotificationPlaceholderResolver.Format("2026-06-30T00:00:00Z", PlaceholderFormat.Date, null));
    }

    [Fact]
    public void Something_that_is_not_a_date_is_left_as_written_rather_than_guessed_at()
    {
        Assert.Equal("bald", NotificationPlaceholderResolver.Format("bald", PlaceholderFormat.Date, null));
    }

    /// <summary>
    /// The defect this replaces: ConvertDataType@1 emitted the JSON number, so 128.40 reached
    /// the recipient as "128.4" while the invoice PDF beside it said "128,40".
    /// </summary>
    [Fact]
    public void An_amount_is_rendered_with_the_separators_the_invoice_uses()
    {
        Assert.Equal("128,40", NotificationPlaceholderResolver.Format(128.4m, PlaceholderFormat.Money, null));
    }

    /// <summary>
    /// Grouping follows the culture rather than a hand-picked separator: de-AT groups with a
    /// narrow no-break space, not the dot a German reader might expect. Asserted explicitly so
    /// that a future change of culture is a failing test rather than a surprise in someone's
    /// inbox - the decimal comma is the part that must never move.
    /// </summary>
    [Fact]
    public void A_thousands_separator_is_the_one_the_tenant_culture_uses()
    {
        var grouped = NotificationPlaceholderResolver.Format(1234.5m, PlaceholderFormat.Money, null);

        Assert.EndsWith("234,50", grouped, StringComparison.Ordinal);
        Assert.StartsWith("1", grouped, StringComparison.Ordinal);
        Assert.Equal(8, grouped.Length);
    }

    [Fact]
    public void An_amount_that_arrived_as_a_double_formats_the_same()
    {
        Assert.Equal("128,40", NotificationPlaceholderResolver.Format(128.4d, PlaceholderFormat.Money, null));
    }

    [Fact]
    public void An_amount_that_arrived_as_an_invariant_string_formats_the_same()
    {
        Assert.Equal("128,40", NotificationPlaceholderResolver.Format("128.4", PlaceholderFormat.Money, null));
    }

    [Fact]
    public void The_logo_becomes_its_reference_when_there_is_an_image_and_the_template_can_show_it()
    {
        var markup = NotificationPlaceholderResolver.Format(
            "6a913ef7dc429493c0ce489f", PlaceholderFormat.InlineImage, """<img src="cid:x" alt="" />""");

        Assert.Equal("""<img src="cid:x" alt="" />""", markup);
    }

    [Fact]
    public void The_logo_becomes_nothing_when_the_community_uploaded_none()
    {
        Assert.Equal(string.Empty,
            NotificationPlaceholderResolver.Format(null, PlaceholderFormat.InlineImage, """<img src="cid:x" />"""));
    }

    [Fact]
    public void The_logo_becomes_nothing_in_a_template_that_cannot_render_markup()
    {
        Assert.Equal(string.Empty,
            NotificationPlaceholderResolver.Format("binary-id", PlaceholderFormat.InlineImage, null));
    }

    [Fact]
    public void A_missing_value_is_empty_whatever_the_format()
    {
        foreach (var format in Enum.GetValues<PlaceholderFormat>())
        {
            Assert.Equal(string.Empty, NotificationPlaceholderResolver.Format(null, format, null));
        }
    }
}

/// <summary>
/// The catalog is the contract the editor's list and the documentation are written against.
/// </summary>
public class NotificationPlaceholderCatalogTests
{
    [Fact]
    public void Every_token_is_named_once()
    {
        var tokens = NotificationPlaceholderCatalog.Definitions.Select(d => d.Token).ToArray();

        Assert.Equal(tokens.Length, tokens.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Every_token_is_prefixed_with_its_source_group()
    {
        foreach (var definition in NotificationPlaceholderCatalog.Definitions)
        {
            var expected = definition.Source switch
            {
                PlaceholderSource.Customer => "customer.",
                PlaceholderSource.Community => "community.",
                _ => "billingDocument."
            };
            Assert.StartsWith(expected, definition.Token, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_definition_names_an_attribute_to_read()
    {
        Assert.All(NotificationPlaceholderCatalog.Definitions,
            d => Assert.False(string.IsNullOrWhiteSpace(d.AttributePath)));
    }

    /// <summary>
    /// The app shows the community name where the title or the heading is unset
    /// (ThemeService.applyAppName, AppComponent.ngOnInit). A mail that left those blank would
    /// contradict the screen the template author copied the wording from.
    /// </summary>
    [Fact]
    public void The_two_tokens_the_app_falls_back_on_declare_the_same_fallback()
    {
        var withFallback = NotificationPlaceholderCatalog.Definitions
            .Where(d => d.FallbackAttributePath != null)
            .ToDictionary(d => d.Token, d => d.FallbackAttributePath);

        Assert.Equal(
            new Dictionary<string, string?>
            {
                ["community.appTitle"] = "Name",
                ["community.appHeading"] = "Name",
            },
            withFallback);
    }

    [Fact]
    public void The_three_sources_the_issue_names_as_a_minimum_are_all_covered()
    {
        var sources = NotificationPlaceholderCatalog.Definitions.Select(d => d.Source).Distinct().ToArray();

        Assert.Equal(3, sources.Length);
    }
}

/// <summary>
/// Six tokens the catalog offered but no resolver filled. They were never impossible - every one
/// reads an attribute that exists on an entity the resolver is already handed. Calling that
/// "no send path can fill this" described an unfinished list as if it were a limit of the model.
/// </summary>
public class NotificationPlaceholderCoverageTests
{
    private static PlaceholderDefinition Find(string token) =>
        NotificationPlaceholderCatalog.Definitions.Single(d =>
            string.Equals(d.Token, token, StringComparison.Ordinal));

    [Theory]
    [InlineData("customer.phone", "Contact.Attributes.Address.Attributes.Phone.Attributes.Number")]
    [InlineData("customer.iban", "BankAccount.Attributes.Iban")]
    [InlineData("customer.accountHolder", "BankAccount.Attributes.AccountHolder")]
    public void A_customer_token_reads_the_attribute_the_model_carries(string token, string attributePath)
    {
        var definition = Find(token);

        Assert.Equal(PlaceholderSource.Customer, definition.Source);
        Assert.Equal(attributePath, definition.AttributePath);
    }

    [Theory]
    [InlineData("community.consumerPrice", "ConsumerPrice")]
    [InlineData("community.producerPrice", "ProducerPrice")]
    [InlineData("community.taxRate", "TaxRate")]
    public void A_community_token_reads_the_attribute_the_configuration_carries(string token, string attributePath)
    {
        var definition = Find(token);

        Assert.Equal(PlaceholderSource.Community, definition.Source);
        Assert.Equal(attributePath, definition.AttributePath);
    }

    [Fact]
    public void A_price_is_an_amount_so_it_reads_like_one()
    {
        Assert.Equal(PlaceholderFormat.Money, Find("community.consumerPrice").Format);
        Assert.Equal("10,00", NotificationPlaceholderResolver.Format(10m, PlaceholderFormat.Money, null));
    }

    /// <summary>
    /// A tax rate is a whole percentage, not an amount: "20", not "20,00".
    /// </summary>
    [Fact]
    public void A_tax_rate_is_not_formatted_as_money()
    {
        Assert.Equal(PlaceholderFormat.Text, Find("community.taxRate").Format);
        Assert.Equal("20", NotificationPlaceholderResolver.Format(20, PlaceholderFormat.Text, null));
    }
}

/// <summary>
/// The billing type is an enum, so it needs words. They are not invented here: the app already
/// shows this enum to the same operator as Rechnung / Gutschrift
/// (energy_community.billing.types.*), and a mail that called the document something else would
/// contradict the list the operator just clicked it from.
/// </summary>
public class NotificationPlaceholderBillingTypeTests
{
    [Theory]
    [InlineData(0, "Rechnung")]
    [InlineData("0", "Rechnung")]
    [InlineData(1, "Gutschrift")]
    [InlineData("1", "Gutschrift")]
    public void The_billing_type_reads_as_the_app_names_it(object key, string expected)
    {
        Assert.Equal(expected, NotificationPlaceholderResolver.Format(key, PlaceholderFormat.BillingType, null));
    }

    [Fact]
    public void An_unknown_key_yields_nothing_rather_than_a_guess()
    {
        Assert.Equal(string.Empty, NotificationPlaceholderResolver.Format(7, PlaceholderFormat.BillingType, null));
    }

    [Fact]
    public void The_token_is_in_the_catalog_now()
    {
        var definition = NotificationPlaceholderCatalog.Definitions
            .Single(d => d.Token == "billingDocument.billingType");

        Assert.Equal(PlaceholderSource.BillingDocument, definition.Source);
        Assert.Equal("BillingType", definition.AttributePath);
        Assert.Equal(PlaceholderFormat.BillingType, definition.Format);
    }
}
