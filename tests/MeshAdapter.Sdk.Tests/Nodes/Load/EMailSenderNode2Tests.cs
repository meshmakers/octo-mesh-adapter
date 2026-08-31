using System.Diagnostics;
using System.Net;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Xunit;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

/// <summary>
/// The parts of SendEMail@2 that can be exercised without an SMTP server: what happens to a
/// `cid:` reference whose attachment is not there.
///
/// AB#2570 requires that removing the image leaves the mail sendable "no broken image icon" -
/// so an unresolved reference has to leave the markup, not just fail to load.
/// </summary>
public class EMailSenderNode2Tests
{
    [Fact]
    public void RemoveUnresolvedInlineReferences_drops_the_img_that_points_at_a_missing_attachment()
    {
        const string html = """<p>Hallo</p><img src="cid:community-logo" alt="Logo"><p>Tschuess</p>""";

        var result = EMailSenderNode2.RemoveUnresolvedInlineReferences(html, ["community-logo"]);

        Assert.Equal("<p>Hallo</p><p>Tschuess</p>", result);
    }

    [Fact]
    public void RemoveUnresolvedInlineReferences_keeps_an_img_whose_attachment_is_present()
    {
        const string html = """<img src="cid:community-logo" alt="Logo">""";

        var result = EMailSenderNode2.RemoveUnresolvedInlineReferences(html, []);

        Assert.Equal(html, result);
    }

    [Fact]
    public void RemoveUnresolvedInlineReferences_leaves_a_different_content_id_alone()
    {
        const string html = """<img src="cid:signature"><img src="cid:community-logo">""";

        var result = EMailSenderNode2.RemoveUnresolvedInlineReferences(html, ["community-logo"]);

        Assert.Equal("""<img src="cid:signature">""", result);
    }

    [Fact]
    public void RemoveUnresolvedInlineReferences_handles_the_self_closing_form_Markdig_emits()
    {
        // Markdig renders ![logo](cid:x) as a self-closing tag; a hand-written one is not.
        const string html = """<p><img src="cid:community-logo" alt="logo" /></p>""";

        var result = EMailSenderNode2.RemoveUnresolvedInlineReferences(html, ["community-logo"]);

        Assert.Equal("<p></p>", result);
    }

    [Fact]
    public void RemoveUnresolvedInlineReferences_is_case_insensitive_about_the_scheme()
    {
        const string html = """<IMG SRC="CID:Community-Logo">""";

        var result = EMailSenderNode2.RemoveUnresolvedInlineReferences(html, ["community-logo"]);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void RemoveUnresolvedInlineReferences_returns_the_body_untouched_when_nothing_is_missing()
    {
        const string html = "<p>kein Bild</p>";

        Assert.Equal(html, EMailSenderNode2.RemoveUnresolvedInlineReferences(html, []));
    }
}

/// <summary>
/// Every EnergyCommunity template declares RenderingType PLAIN, yet v1 runs Markdig over all of
/// them and sends HTML. That is not cosmetic: an operator writing "A)" got an ordered list, and
/// a literal "{something}" was swallowed into the paragraph tag as an HTML attribute. v2 lets
/// the pipeline say what the body actually is.
/// </summary>
public class EMailSenderNode2BodyFormatTests
{
    [Fact]
    public void Markdown_is_converted_as_before()
    {
        var html = EMailSenderNode2.RenderBody("**fett**", BodyFormats.Markdown);

        Assert.Contains("<strong>fett</strong>", html);
    }

    [Fact]
    public void PlainText_keeps_what_the_author_typed()
    {
        var html = EMailSenderNode2.RenderBody("**kein Markdown** und A) keine Liste", BodyFormats.PlainText);

        Assert.Contains("**kein Markdown** und A) keine Liste", html);
        Assert.DoesNotContain("<strong>", html);
        Assert.DoesNotContain("<ol", html);
    }

    [Fact]
    public void PlainText_escapes_markup_instead_of_emitting_it()
    {
        var html = EMailSenderNode2.RenderBody("<script>alert(1)</script>", BodyFormats.PlainText);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void PlainText_does_not_let_a_brace_become_an_html_attribute()
    {
        // Markdig's generic-attributes extension turns {x=1} into an attribute on the element.
        var html = EMailSenderNode2.RenderBody("Betrag {netto=100}", BodyFormats.PlainText);

        Assert.Contains("{netto=100}", html);
        Assert.DoesNotContain("netto=\"100\"", html);
    }

    [Fact]
    public void PlainText_turns_a_newline_into_a_line_break()
    {
        var html = EMailSenderNode2.RenderBody("erste\nzweite", BodyFormats.PlainText);

        Assert.Contains("<br", html);
    }

    [Fact]
    public void Html_is_passed_through_untouched()
    {
        const string body = """<p class="x">schon HTML</p>""";

        Assert.Equal(body, EMailSenderNode2.RenderBody(body, BodyFormats.Html));
    }

    [Fact]
    public void A_plain_text_alternative_is_offered_for_markdown_and_plain_bodies()
    {
        Assert.Equal("**fett**", EMailSenderNode2.PlainTextAlternative("**fett**", BodyFormats.Markdown));
        Assert.Equal("nur Text", EMailSenderNode2.PlainTextAlternative("nur Text", BodyFormats.PlainText));
    }

    [Fact]
    public void No_plain_text_alternative_is_invented_for_an_html_body()
    {
        Assert.Null(EMailSenderNode2.PlainTextAlternative("<p>x</p>", BodyFormats.Html));
    }

    [Fact]
    public void A_markdown_image_is_dropped_from_the_plain_alternative()
    {
        Assert.Equal(
            "davor Logo danach",
            EMailSenderNode2.PlainTextAlternative("davor ![Logo](cid:community-footer) danach", BodyFormats.Markdown));
    }

    /// <summary>
    /// A plain body is literal text, so nothing in it is an image reference to remove. Stripping
    /// it would delete characters the author typed - and only from the text part, since the HTML
    /// part shows them escaped, which is the shape observed on a live send: the reader saw the
    /// raw <c>![Logo](cid:...)</c> while the text alternative beside it said "Logo".
    /// </summary>
    [Theory]
    [InlineData("davor ![Logo](cid:community-footer) danach")]
    [InlineData("""davor <img src="cid:community-footer" alt="Logo" /> danach""")]
    public void Image_markup_typed_into_a_plain_body_stays_as_the_author_wrote_it(string body)
    {
        Assert.Equal(body, EMailSenderNode2.PlainTextAlternative(body, BodyFormats.PlainText));
    }

    /// <summary>
    /// The two alternatives of one mail must say the same words. For a plain body the HTML part
    /// is the escaped source, so decoding it back has to yield the text part unchanged.
    /// </summary>
    [Theory]
    [InlineData("davor ![Logo](cid:community-footer) danach")]
    [InlineData("""<img src="cid:community-footer" alt="Logo" />""")]
    [InlineData("erste Zeile\nzweite Zeile")]
    public void Both_alternatives_of_a_plain_body_carry_the_same_text(string body)
    {
        var html = EMailSenderNode2.RenderBody(body, BodyFormats.PlainText);
        var plain = EMailSenderNode2.PlainTextAlternative(body, BodyFormats.PlainText);

        var htmlAsText = WebUtility.HtmlDecode(
            html.Replace("<br />" + Environment.NewLine, "\n"));

        Assert.Equal(plain, htmlAsText);
    }
}

/// <summary>
/// A NotificationTemplate says whether it is Plain or Html; GetNotificationTemplate@1 can now
/// forward that, and the sender has to translate it into how the body is rendered. The mapping
/// follows what the notification layer already does: Plain renders as text, anything else is run
/// through Markdig (MarkdownRenderService.RenderHtml).
/// </summary>
public class EMailSenderNode2RenderingTypeTests
{
    [Theory]
    [InlineData("Plain", BodyFormats.PlainText)]
    [InlineData("plain", BodyFormats.PlainText)]
    [InlineData("Html", BodyFormats.Markdown)]
    [InlineData("HTML", BodyFormats.Markdown)]
    public void A_rendering_type_decides_the_body_format(string renderingType, BodyFormats expected)
    {
        Assert.Equal(expected, EMailSenderNode2.ResolveBodyFormat(renderingType, BodyFormats.Html));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_rendering_type_leaves_the_configured_format_in_place(string? renderingType)
    {
        Assert.Equal(BodyFormats.Html, EMailSenderNode2.ResolveBodyFormat(renderingType, BodyFormats.Html));
    }

    [Fact]
    public void An_unknown_rendering_type_falls_back_rather_than_guessing()
    {
        Assert.Equal(BodyFormats.Markdown, EMailSenderNode2.ResolveBodyFormat("Klingon", BodyFormats.Markdown));
    }
}

/// <summary>
/// A binary stored on an entity carries its own file name and content type, and an operator who
/// swaps a PNG logo for a JPEG has no reason to also edit the pipeline. The attachment therefore
/// prefers what the pipeline resolved over what the node was configured with.
/// </summary>
public class EMailSenderNode2AttachmentMetadataTests
{
    [Fact]
    public void A_resolved_value_wins_over_the_configured_one()
    {
        Assert.Equal("image/jpeg", EMailSenderNode2.PreferResolved("image/jpeg", "image/png"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_resolved_leaves_the_configured_value_in_place(string? resolved)
    {
        Assert.Equal("image/png", EMailSenderNode2.PreferResolved(resolved, "image/png"));
    }

    [Fact]
    public void The_configured_value_is_returned_verbatim_including_its_own_blanks()
    {
        Assert.Equal("logo.png", EMailSenderNode2.PreferResolved(null, "logo.png"));
    }
}

/// <summary>
/// A body carrying unclosed `&lt;img` text used to take the inline-image scan quadratic, because
/// its runs were `[^&gt;]` and could therefore scan past the next tag: 540 ms at 6 KB, 43 s at
/// 27 KB, 263 s at 54 KB - on the path every send takes, with no cancellation. An operator
/// writing about embedding, or pasting truncated HTML, produces exactly that shape.
/// </summary>
public class EMailSenderNode2InlineScanCostTests
{
    private static string UnclosedImgRuns(int count) =>
        string.Concat(Enumerable.Repeat("""<img src="cid:""", count));

    /// <summary>
    /// A wall-clock assertion is a blunt instrument, but the defect was three orders of magnitude
    /// over this bound, so the margin is enormous and the test is not timing-sensitive. Both
    /// entry points are covered: one scans, the other rewrites.
    /// </summary>
    [Fact]
    public void An_unclosed_img_run_does_not_make_the_scan_superlinear()
    {
        var body = UnclosedImgRuns(4000);

        // Warm the compiled regexes so the measurement is not the JIT.
        EMailSenderNode2.IsInlineReferenceUsed("<p>warm</p>", "community-footer");
        EMailSenderNode2.StripInlineImageMarkup("<p>warm</p>");

        var started = Stopwatch.StartNew();
        EMailSenderNode2.IsInlineReferenceUsed(body, "community-footer");
        EMailSenderNode2.StripInlineImageMarkup(body);
        started.Stop();

        Assert.True(started.ElapsedMilliseconds < 2000,
            $"scanning {body.Length} characters took {started.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void An_unclosed_img_run_carries_no_reference()
    {
        Assert.False(EMailSenderNode2.IsInlineReferenceUsed(UnclosedImgRuns(50), "community-footer"));
    }
}

/// <summary>
/// Markdig's advanced bundle includes generic attributes, which reads a trailing `{...}` as HTML
/// attributes for the enclosing element. Any trailing placeholder is swallowed whole - the
/// recipient sees a bare dollar sign, and the token's text silently becomes an attribute on the
/// paragraph. Observed on a live send (AB#2570) with a token that was unfilled at the time; the
/// resolver refuses such a token now, but an operator can still type a brace by hand.
/// </summary>
public class EMailSenderNode2GenericAttributesTests
{
    [Fact]
    public void An_unresolved_placeholder_survives_rendering()
    {
        var html = EMailSenderNode2.RenderBody("Nummer: ${billingDocument.documentNumber}", BodyFormats.Markdown);

        Assert.Contains("${billingDocument.documentNumber}", html);
    }

    [Fact]
    public void A_placeholder_does_not_become_an_attribute_on_the_element_around_it()
    {
        var html = EMailSenderNode2.RenderBody("Nummer: ${billingDocument.documentNumber}", BodyFormats.Markdown);

        Assert.DoesNotContain("billingDocument.documentNumber=", html);
    }

    [Fact]
    public void Braces_an_author_typed_on_purpose_are_left_alone()
    {
        var html = EMailSenderNode2.RenderBody("Setze {netto=100} ein", BodyFormats.Markdown);

        Assert.Contains("{netto=100}", html);
    }

    [Fact]
    public void The_extensions_a_template_actually_uses_still_work()
    {
        var html = EMailSenderNode2.RenderBody("**fett** und ~~durchgestrichen~~", BodyFormats.Markdown);

        Assert.Contains("<strong>fett</strong>", html);
        Assert.Contains("<del>durchgestrichen</del>", html);
    }
}

/// <summary>
/// An inline attachment exists to be addressed from the body. The EnergyCommunity pipelines
/// attach the community footer image to every send, but the templates a tenant is seeded with
/// reference it nowhere - so every mail carried 26 kB the recipient could never see. An inline
/// attachment nothing references is not attached at all.
/// </summary>
public class EMailSenderNode2InlineUsageTests
{
    [Fact]
    public void A_referenced_image_is_kept()
    {
        Assert.True(EMailSenderNode2.IsInlineReferenceUsed(
            """<p>Hallo</p><img src="cid:community-footer" />""", "community-footer"));
    }

    [Fact]
    public void An_image_nothing_references_is_dropped()
    {
        Assert.False(EMailSenderNode2.IsInlineReferenceUsed("<p>Hallo</p>", "community-footer"));
    }

    [Fact]
    public void A_reference_to_a_different_id_does_not_count()
    {
        Assert.False(EMailSenderNode2.IsInlineReferenceUsed(
            """<img src="cid:letterhead" />""", "community-footer"));
    }

    [Fact]
    public void Single_quotes_and_odd_casing_still_count_as_a_reference()
    {
        Assert.True(EMailSenderNode2.IsInlineReferenceUsed(
            "<IMG SRC='CID:Community-Footer'>", "community-footer"));
    }

    [Fact]
    public void A_plain_text_body_mentioning_the_id_is_not_a_reference()
    {
        Assert.False(EMailSenderNode2.IsInlineReferenceUsed(
            "Schreiben Sie ![](cid:community-footer) in die Vorlage", "community-footer"));
    }
}

/// <summary>
/// The text/plain alternative is the Markdown source, so it still carries the author's
/// `![](cid:...)` - a reference no text-only client can ever resolve, present or missing.
/// It read as literal punctuation in every mail this node sent.
/// </summary>
public class EMailSenderNode2PlainTextReferenceTests
{
    [Fact]
    public void An_inline_image_reference_is_dropped_from_the_plain_alternative()
    {
        var text = EMailSenderNode2.StripInlineImageMarkup("Gruesse\n\n![](cid:community-footer)\n");

        Assert.DoesNotContain("cid:", text);
    }

    [Fact]
    public void The_alt_text_survives_because_it_is_what_the_author_wrote_for_this_reader()
    {
        var text = EMailSenderNode2.StripInlineImageMarkup("![Logo der Gemeinschaft](cid:community-footer)");

        Assert.Equal("Logo der Gemeinschaft", text);
    }

    [Fact]
    public void An_ordinary_link_is_not_an_image_and_stays()
    {
        const string body = "Siehe [unsere Seite](https://example.at)";

        Assert.Equal(body, EMailSenderNode2.StripInlineImageMarkup(body));
    }

    [Fact]
    public void An_image_pointing_somewhere_other_than_cid_is_left_alone()
    {
        const string body = "![Karte](https://example.at/karte.png)";

        Assert.Equal(body, EMailSenderNode2.StripInlineImageMarkup(body));
    }

    [Fact]
    public void Text_with_no_image_at_all_is_returned_unchanged()
    {
        const string body = "Liebe / lieber Erika,\n\nbeste Gruesse";

        Assert.Equal(body, EMailSenderNode2.StripInlineImageMarkup(body));
    }
}

/// <summary>
/// Since ${community.logo} substitutes to an img element rather than to Markdown, the plain
/// alternative can carry either form. Both are unreachable for a text-only reader.
/// </summary>
public class EMailSenderNode2PlainTextHtmlReferenceTests
{
    [Fact]
    public void An_html_image_element_pointing_at_a_cid_is_dropped()
    {
        var text = EMailSenderNode2.StripInlineImageMarkup(
            """Gruesse<img src="cid:community-footer" alt="" />Ende""");

        Assert.Equal("GruesseEnde", text);
    }

    [Fact]
    public void Its_alt_text_survives_when_the_author_wrote_one()
    {
        var text = EMailSenderNode2.StripInlineImageMarkup(
            """<img src="cid:community-footer" alt="Logo" />""");

        Assert.Equal("Logo", text);
    }

    [Fact]
    public void An_image_element_pointing_elsewhere_is_left_alone()
    {
        const string body = """<img src="https://example.at/logo.png" alt="Logo" />""";

        Assert.Equal(body, EMailSenderNode2.StripInlineImageMarkup(body));
    }

    [Fact]
    public void Both_notations_are_handled_in_one_body()
    {
        var text = EMailSenderNode2.StripInlineImageMarkup(
            """A![x](cid:a)B<img src='cid:b' alt='y'>C""");

        Assert.Equal("AxByC", text);
    }
}
