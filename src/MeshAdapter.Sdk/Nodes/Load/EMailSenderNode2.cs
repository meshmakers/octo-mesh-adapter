using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.GenericAttributes;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Loads;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// Sends an e-mail with any number of attachments, each of which may be linked into the body.
///
/// v1 carries one attachment and cannot reference it from the HTML, so a template can show an
/// image only by pointing at an external URL - which the asset service refuses without a bearer
/// token - or by inlining a data URI, which Outlook and Gmail strip. AB#2570 asks for a logo in
/// the mail; that needs a `cid:` reference backed by a linked resource, which is what this node
/// adds.
/// </summary>
/// <param name="next">Next node in the pipeline</param>
/// <param name="etlContext">The ETL context</param>
[NodeConfiguration(typeof(EMailSenderNodeConfiguration2))]
// ReSharper disable once ClassNeverInstantiated.Global
public class EMailSenderNode2(
    NodeDelegate next,
    IMeshEtlContext etlContext)
    : IPipelineNode
{
    private const string EmailSemaphoresKey = "EmailSenderNode.Semaphores";

    private const int MaxSendAttempts = 4;
    private const double InitialRetryDelaySeconds = 2.0;

    // ReSharper disable once ClassNeverInstantiated.Local
    private record EMailSenderConfiguration
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Local
        public required string Host { get; init; }
        public required int Port { get; init; }
        public string? SenderEmail { get; init; }
        public required string Username { get; init; }
        public required string Password { get; init; }
        public required bool IsSslEnabled { get; init; }
        public int MaxConcurrentEmails { get; init; } = 3;
        // ReSharper restore UnusedAutoPropertyAccessor.Local
    }

    /// <summary>
    /// The advanced bundle minus generic attributes.
    ///
    /// That extension reads a trailing <c>{...}</c> as HTML attributes for the element around it,
    /// which turns any trailing <c>${...}</c> into a bare <c>$</c> and moves the token's text onto
    /// the paragraph as an attribute. A mail template is written by an operator, not by
    /// a documentation author, so the notation is a liability with no use here - and letting text
    /// an operator typed become markup is an injection surface besides.
    /// </summary>
    private static readonly MarkdownPipeline SharedPipeline = BuildPipeline();

    private static MarkdownPipeline BuildPipeline()
    {
        var builder = new MarkdownPipelineBuilder().UseAdvancedExtensions();
        builder.Extensions.RemoveAll(extension => extension is GenericAttributesExtension);
        return builder.Build();
    }

    /// <summary>
    /// Matches an img element whose src is a cid reference, capturing the id. Deliberately narrow:
    /// only the element that carries the reference is removed, never the text around it.
    /// </summary>
    private static readonly Regex InlineImageReference = new(
        """<img\b[^>]*?\bsrc\s*=\s*(?:"cid:(?<id>[^"]*)"|'cid:(?<id>[^']*)')[^>]*?/?>""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The Markdown an author writes for the same thing, matched on the plain-text side where no
    /// element exists yet.
    /// </summary>
    private static readonly Regex InlineImageMarkdown = new(
        """!\[(?<alt>[^\]]*)\]\(\s*cid:[^)\s]*\s*\)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The same reference as an element, which is what a placeholder resolving to
    /// <c>${community.logo}</c> puts in the body. Keeps the alt text, as the Markdown form does.
    /// </summary>
    private static readonly Regex InlineImageElement = new(
        """<img\b(?=[^>]*?\bsrc\s*=\s*(?:"cid:|'cid:))[^>]*?\balt\s*=\s*(?:"(?<alt>[^"]*)"|'(?<alt>[^']*)')[^>]*?/?>|<img\b(?=[^>]*?\bsrc\s*=\s*(?:"cid:|'cid:))[^>]*?/?>""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<EMailSenderNodeConfiguration2>();

        try
        {
            if (!etlContext.GlobalConfiguration.IsDefined(c.ServerConfiguration))
            {
                throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(nodeContext,
                    nameof(c.ServerConfiguration), c.ServerConfiguration);
            }

            var eMailSenderConfiguration =
                etlContext.GlobalConfiguration.GetValue<EMailSenderConfiguration>(c.ServerConfiguration);

            if (!etlContext.Properties.TryGetValue(EmailSemaphoresKey, out var semaphoresObj) ||
                semaphoresObj is not Dictionary<string, SemaphoreSlim> semaphores)
            {
                semaphores = new Dictionary<string, SemaphoreSlim>();
                etlContext.Properties[EmailSemaphoresKey] = semaphores;
            }

            if (!semaphores.TryGetValue(c.ServerConfiguration, out var emailSemaphore))
            {
                emailSemaphore = new SemaphoreSlim(
                    eMailSenderConfiguration.MaxConcurrentEmails,
                    eMailSenderConfiguration.MaxConcurrentEmails);
                semaphores[c.ServerConfiguration] = emailSemaphore;
            }

            var recipients = dataContext.GetArray<string>(c.ToPath);
            if (recipients == null)
            {
                throw MeshAdapterPipelineExecutionException.NoRecipientsFound(nodeContext, nameof(c.ToPath), c.ToPath);
            }

            var subject = dataContext.Get<string>(c.SubjectPath)
                          ?? throw PipelineExecutionException.ValueNotSet(nodeContext, c.SubjectPath);
            var body = dataContext.Get<string>(c.Path)
                       ?? throw PipelineExecutionException.ValueNotSet(nodeContext, c.Path);

            if (nodeContext.PipelineExecutionMode?.IsDryRun == true)
            {
                nodeContext.RecordDryRunIntent(DryRunHonouredLoadNodes.SendEMail, new
                {
                    host = eMailSenderConfiguration.Host,
                    port = eMailSenderConfiguration.Port,
                    sender = eMailSenderConfiguration.SenderEmail,
                    recipientsCount = recipients.Count(),
                    recipients = recipients.ToArray(),
                    subject,
                    bodyMarkdownLength = body.Length,
                    attachments = c.Attachments
                        .Select(a => new { a.FileName, a.ContentType, a.ContentId, a.Optional, a.BinaryId, a.BinaryIdPath })
                        .ToArray()
                });
                await next(dataContext, nodeContext);
                return;
            }

            // Rendered first: an inline attachment the body never addresses is not fetched at
            // all. Every EnergyCommunity send attaches the community footer image, and no
            // seeded template references it - that is 26 kB per mail the recipient cannot see.
            var bodyFormat = ResolveBodyFormat(
                c.BodyFormatPath != null ? dataContext.Get<string>(c.BodyFormatPath) : null, c.BodyFormat);
            var bodyInHtml = RenderBody(body, bodyFormat);
            var resolved = await ResolveAttachmentsAsync(c, dataContext, nodeContext, bodyInHtml);

            // An optional attachment that is not there leaves a dangling cid: reference, which
            // every client renders as a broken image. Removing the element is the documented
            // fallback: the mail still goes out and shows nothing where the image would be.
            var missing = c.Attachments
                .Where(a => a.ContentId != null
                            && IsInlineReferenceUsed(bodyInHtml, a.ContentId)
                            && resolved.All(r => r.Configuration != a))
                .Select(a => a.ContentId!)
                .ToArray();
            if (missing.Length > 0)
            {
                nodeContext.Warning("Inline attachment(s) {0} not available; their references were removed from the body",
                    string.Join(", ", missing));
                bodyInHtml = RemoveUnresolvedInlineReferences(bodyInHtml, missing);
            }

            using var mailMessage = new MailMessage { Subject = subject };

            if (!string.IsNullOrWhiteSpace(eMailSenderConfiguration.SenderEmail))
            {
                mailMessage.From = new MailAddress(eMailSenderConfiguration.SenderEmail);
            }

            BuildBody(mailMessage, bodyInHtml, PlainTextAlternative(body, bodyFormat), resolved);
            AddFileAttachments(mailMessage, resolved);
            AddRecipients(dataContext, recipients, mailMessage, c);

            var replyTo = c.ReplyToPath != null ? dataContext.Get<string>(c.ReplyToPath) : c.ReplyToAddress;
            if (!string.IsNullOrWhiteSpace(replyTo))
            {
                mailMessage.ReplyToList.Add(new MailAddress(replyTo));
            }

            await SendMailWithRetryAsync(eMailSenderConfiguration, mailMessage, emailSemaphore, nodeContext);
        }
        catch (Exception e)
        {
            throw MeshAdapterPipelineExecutionException.CannotSendMail(nodeContext, e);
        }

        await next(dataContext, nodeContext);
    }

    /// <summary>
    /// Translates a NotificationTemplate's RenderingType into how the body is rendered, falling
    /// back to the configured format when the pipeline supplies nothing or something unknown.
    ///
    /// The mapping is the one the notification layer already applies: Plain renders as text,
    /// anything else goes through Markdig (MarkdownRenderService.RenderHtml).
    /// </summary>
    public static BodyFormats ResolveBodyFormat(string? renderingType, BodyFormats configured)
    {
        if (string.IsNullOrWhiteSpace(renderingType))
        {
            return configured;
        }

        if (string.Equals(renderingType, "Plain", StringComparison.OrdinalIgnoreCase))
        {
            return BodyFormats.PlainText;
        }

        return string.Equals(renderingType, "Html", StringComparison.OrdinalIgnoreCase)
            ? BodyFormats.Markdown
            : configured;
    }

    /// <summary>
    /// Turns the configured body into the HTML that goes on the wire. Public so the formats can
    /// be exercised without an SMTP server.
    /// </summary>
    public static string RenderBody(string body, BodyFormats format)
    {
        return format switch
        {
            BodyFormats.Html => body,
            // Escape first, then reinstate the author's line breaks. Anything that looks like
            // markup - a brace, an angle bracket, a leading "A)" - stays what it was typed as.
            BodyFormats.PlainText => WebUtility.HtmlEncode(body)
                .Replace("\r\n", "\n")
                .Replace("\n", "<br />" + Environment.NewLine),
            _ => Markdown.ToHtml(body, SharedPipeline)
        };
    }

    /// <summary>
    /// The text/plain part to offer beside the HTML, or null when none can be derived honestly.
    /// Markdown and plain text are already readable as they stand; HTML is not, and inventing a
    /// stripped-down version would ship something the author never reviewed.
    /// </summary>
    public static string? PlainTextAlternative(string body, BodyFormats format)
    {
        return format == BodyFormats.Html ? null : StripInlineImageMarkup(body);
    }

    /// <summary>
    /// Removes Markdown image references that point at a <c>cid:</c>, keeping their alt text.
    ///
    /// The plain alternative is the author's source, so it carried <c>![](cid:community-footer)</c>
    /// verbatim - punctuation to a text-only reader, whether or not the image was attached. Only
    /// <c>cid:</c> targets are touched: an image with an http target is still unreachable in plain
    /// text, but it at least names somewhere the reader could go.
    /// </summary>
    public static string StripInlineImageMarkup(string body)
    {
        var withoutMarkdown = InlineImageMarkdown.Replace(body, match => match.Groups["alt"].Value);
        return InlineImageElement.Replace(withoutMarkdown, match => match.Groups["alt"].Value);
    }

    /// <summary>
    /// Strips every img element that points at one of the given content ids. Public so the
    /// fallback AB#2570 asks for can be exercised without an SMTP server.
    /// </summary>
    public static string RemoveUnresolvedInlineReferences(string html, IEnumerable<string> missingContentIds)
    {
        var missing = new HashSet<string>(missingContentIds, StringComparer.OrdinalIgnoreCase);
        if (missing.Count == 0)
        {
            return html;
        }

        return InlineImageReference.Replace(html, match =>
        {
            var id = match.Groups["id"].Value;
            return missing.Contains(id) ? string.Empty : match.Value;
        });
    }

    /// <summary>
    /// Picks the value the pipeline resolved over the one the node was configured with. The
    /// stored binary knows its own file name and content type; a configured literal is the
    /// fallback for a pipeline that supplies neither.
    /// </summary>
    public static string PreferResolved(string? resolved, string configured)
    {
        return string.IsNullOrWhiteSpace(resolved) ? configured : resolved;
    }

    private sealed record ResolvedAttachment(
        EMailAttachment Configuration, Stream Content, string FileName, string ContentType);

    /// <summary>
    /// Whether the rendered body addresses this content id from an img element. Deliberately the
    /// same shape <see cref="InlineImageReference"/> matches, so what counts as a reference here
    /// is exactly what would be stripped if the attachment turned out to be missing - the two
    /// must not disagree. Text that merely mentions the id, as an unrendered plain-text body
    /// would, is not a reference.
    /// </summary>
    public static bool IsInlineReferenceUsed(string bodyInHtml, string contentId)
    {
        return InlineImageReference.Matches(bodyInHtml).Any(match =>
            string.Equals(
                match.Groups["id"].Success && match.Groups["id"].Length > 0
                    ? match.Groups["id"].Value
                    : null,
                contentId,
                StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<ResolvedAttachment>> ResolveAttachmentsAsync(
        EMailSenderNodeConfiguration2 c, IDataContext dataContext, INodeContext nodeContext,
        string bodyInHtml)
    {
        var resolved = new List<ResolvedAttachment>();

        foreach (var attachment in c.Attachments)
        {
            // An inline attachment exists to be addressed from the body; one nothing addresses
            // would travel with the mail and be invisible.
            if (attachment.ContentId != null && !IsInlineReferenceUsed(bodyInHtml, attachment.ContentId))
            {
                continue;
            }

            var binaryId = attachment.BinaryId ??
                           (attachment.BinaryIdPath != null ? dataContext.Get<string>(attachment.BinaryIdPath) : null);

            if (string.IsNullOrWhiteSpace(binaryId))
            {
                if (attachment.Optional)
                {
                    continue;
                }

                throw PipelineExecutionException.ValueNotSet(
                    nodeContext, attachment.BinaryIdPath ?? nameof(attachment.BinaryId));
            }

            IDownloadStreamHandler streamHandler;
            try
            {
                var tenantRepository = etlContext.TenantRepository;
                using var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
                session.StartTransaction();

                streamHandler = await tenantRepository.DownloadLargeBinaryAsync(
                    session, OctoObjectId.Parse(binaryId), CancellationToken.None);

                await session.CommitTransactionAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is PersistenceException or FormatException)
            {
                // The attribute still names a binary the store no longer has - a restore without
                // its blobs leaves exactly this. `Optional` promises the mail goes anyway, and it
                // has to be honoured here rather than on a null return: the repository declares a
                // non-nullable handler and the GridFS provider throws instead of returning null,
                // so a null check could never have run. Without this, one stale logo id stops a
                // billing mail whose invoice PDF resolved perfectly.
                //
                // Caught at PersistenceException rather than the provider's EntityNotFoundException,
                // which lives in the MongoDb package this SDK does not reference - and for an
                // attachment declared optional, a store that cannot answer at all is the same
                // answer as a store that has nothing.
                if (attachment.Optional)
                {
                    nodeContext.Warning(
                        "Optional attachment {0} was not found in the binary store and is omitted", binaryId);
                    continue;
                }

                throw MeshAdapterPipelineExecutionException.AttachmentBinaryNotFound(nodeContext, binaryId, e);
            }

            resolved.Add(new ResolvedAttachment(
                attachment,
                streamHandler.Stream,
                PreferResolved(
                    attachment.FileNamePath != null ? dataContext.Get<string>(attachment.FileNamePath) : null,
                    attachment.FileName),
                PreferResolved(
                    attachment.ContentTypePath != null ? dataContext.Get<string>(attachment.ContentTypePath) : null,
                    attachment.ContentType)));
        }

        return resolved;
    }

    /// <summary>
    /// Builds the HTML view and hangs every inline attachment off it as a linked resource, which
    /// is what makes <c>cid:</c> resolve in a mail client.
    /// </summary>
    private static void BuildBody(MailMessage mailMessage, string bodyInHtml, string? plainText,
        List<ResolvedAttachment> resolved)
    {
        var inline = resolved.Where(r => r.Configuration.ContentId != null).ToArray();

        // A text/plain part beside the HTML is what a text-only client, a screen reader and most
        // spam filters read. v1 sent HTML alone.
        if (plainText != null)
        {
            mailMessage.AlternateViews.Add(
                AlternateView.CreateAlternateViewFromString(plainText, null, MediaTypeNames.Text.Plain));
        }
        else if (inline.Length == 0)
        {
            mailMessage.Body = bodyInHtml;
            mailMessage.IsBodyHtml = true;
            return;
        }

        var view = AlternateView.CreateAlternateViewFromString(bodyInHtml, null, MediaTypeNames.Text.Html);
        foreach (var item in inline)
        {
            view.LinkedResources.Add(new LinkedResource(item.Content, item.ContentType)
            {
                ContentId = item.Configuration.ContentId,
                TransferEncoding = TransferEncoding.Base64
            });
        }

        mailMessage.AlternateViews.Add(view);
    }

    private static void AddFileAttachments(MailMessage mailMessage, List<ResolvedAttachment> resolved)
    {
        foreach (var item in resolved.Where(r => r.Configuration.ContentId == null))
        {
            mailMessage.Attachments.Add(
                new Attachment(item.Content, item.FileName, item.ContentType));
        }
    }

    private static async Task SendMailWithRetryAsync(
        EMailSenderConfiguration configuration,
        MailMessage mailMessage,
        SemaphoreSlim emailSemaphore,
        INodeContext nodeContext)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;

            // Reset seekable streams so a retry re-sends the complete payload. Linked resources
            // need this as much as attachments do - v1 only rewound the latter, because it had
            // no linked resources to forget.
            foreach (var attachment in mailMessage.Attachments)
            {
                if (attachment.ContentStream.CanSeek)
                {
                    attachment.ContentStream.Position = 0;
                }
            }

            foreach (var resource in mailMessage.AlternateViews.SelectMany(v => v.LinkedResources))
            {
                if (resource.ContentStream.CanSeek)
                {
                    resource.ContentStream.Position = 0;
                }
            }

            TimeSpan retryDelay;
            using (var client = new SmtpClient(configuration.Host, configuration.Port)
                   {
                       Credentials = new NetworkCredential(configuration.Username, configuration.Password),
                       EnableSsl = configuration.IsSslEnabled
                   })
            {
                await emailSemaphore.WaitAsync();
                try
                {
                    await client.SendMailAsync(mailMessage);
                    return;
                }
                catch (Exception e) when (attempt < MaxSendAttempts && IsTransientSmtpFailure(e))
                {
                    retryDelay = TimeSpan.FromSeconds(InitialRetryDelaySeconds * Math.Pow(2, attempt - 1));
                    nodeContext.Warning(
                        "Transient e-mail send failure on attempt {0}/{1} to {2}:{3}, retrying in {4}s: {5}",
                        attempt, MaxSendAttempts, configuration.Host, configuration.Port,
                        retryDelay.TotalSeconds, e.Message);
                }
                finally
                {
                    emailSemaphore.Release();
                }
            }

            await Task.Delay(retryDelay);
        }
    }

    private static bool IsTransientSmtpFailure(Exception e)
    {
        return e switch
        {
            SmtpFailedRecipientException => false,
            SmtpException => true,
            IOException => true,
            SocketException => true,
            _ => false
        };
    }

    private static void AddRecipients(IDataContext dataContext, IEnumerable<string?> recipients,
        MailMessage mailMessage, EMailSenderNodeConfiguration2 c)
    {
        foreach (var recipient in recipients.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            mailMessage.To.Add(new MailAddress(recipient!));
        }

        var ccAddresses = c.CcAddresses is { Count: > 0 }
            ? c.CcAddresses
            : !string.IsNullOrWhiteSpace(c.CcPath)
                ? dataContext.GetArray<string>(c.CcPath)
                : null;

        foreach (var cc in ccAddresses?.Where(a => !string.IsNullOrWhiteSpace(a)) ?? [])
        {
            mailMessage.CC.Add(cc!);
        }

        var bccAddresses = c.BccAddresses is { Count: > 0 }
            ? c.BccAddresses
            : !string.IsNullOrWhiteSpace(c.BccPath)
                ? dataContext.GetArray<string>(c.BccPath)
                : null;

        foreach (var bcc in bccAddresses?.Where(a => !string.IsNullOrWhiteSpace(a)) ?? [])
        {
            mailMessage.Bcc.Add(bcc!);
        }
    }
}
