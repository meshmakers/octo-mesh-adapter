using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using Markdig;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// Pipeline node that sends an email
/// </summary>
/// <param name="next">Next node in the pipeline</param>
/// <param name="etlContext">The ETL context</param>
[NodeConfiguration(typeof(EMailSenderNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class EMailSenderNode(
    NodeDelegate next,
    IMeshEtlContext etlContext)
    : IPipelineNode
{
    private const string EmailSemaphoresKey = "EmailSenderNode.Semaphores";

    // Transient SMTP failures (e.g. a relay throttling a burst of messages and closing the
    // connection mid-EHLO) are retried with exponential backoff so a single dropped connection
    // does not fail the whole ForEach batch. See EMailSenderNode retry handling below.
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

    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<EMailSenderNodeConfiguration>();

        try
        {
            if (!etlContext.GlobalConfiguration.IsDefined(c.ServerConfiguration))
            {
                throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(nodeContext,
                    nameof(c.ServerConfiguration), c.ServerConfiguration);
            }

            var eMailSenderConfiguration =
                etlContext.GlobalConfiguration.GetValue<EMailSenderConfiguration>(c.ServerConfiguration);

            // Get or create semaphore dictionary in context
            if (!etlContext.Properties.TryGetValue(EmailSemaphoresKey, out var semaphoresObj) || 
                semaphoresObj is not Dictionary<string, SemaphoreSlim> semaphores)
            {
                semaphores = new Dictionary<string, SemaphoreSlim>();
                etlContext.Properties[EmailSemaphoresKey] = semaphores;
            }

            // Get or create semaphore for this server configuration
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
                throw MeshAdapterPipelineExecutionException.NoRecipientsFound(nodeContext,
                    nameof(c.ToPath), c.ToPath);
            }

            var subject = dataContext.Get<string>(c.SubjectPath);
            if (subject == null)
            {
                throw PipelineExecutionException.ValueNotSet(
                    nodeContext, c.SubjectPath);
            }

            var body = dataContext.Get<string>(c.Path);
            if (body == null)
            {
                throw PipelineExecutionException.ValueNotSet(
                    nodeContext, c.Path);
            }

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
                    hasAttachment = c.AttachmentRtId != null || c.AttachmentRtIdPath != null,
                    attachmentRtId = c.AttachmentRtId,
                    attachmentRtIdPath = c.AttachmentRtIdPath
                });
                await next(dataContext, nodeContext);
                return;
            }

            var bodyInHtml = Markdown.ToHtml(body, _pipeline);

            var mailMessage = new MailMessage
            {
                Subject = subject,
                Body = bodyInHtml,
                IsBodyHtml = true
            };

            if (!string.IsNullOrWhiteSpace(eMailSenderConfiguration.SenderEmail))
            {
                mailMessage.From = new(eMailSenderConfiguration.SenderEmail);
            }

            var attachment = await GetAttachment(c, dataContext, nodeContext);

            if (attachment != null)
            {
                if(c.AttachmentFileName == null || c.AttachmentContentType == null)
                {
                    throw PipelineExecutionException.ValueNotSet(
                        nodeContext, nameof(c.AttachmentFileName));
                }
                
                var attachmentData = attachment.Stream;
                var attachmentItem = new Attachment(attachmentData, c.AttachmentFileName, c.AttachmentContentType);
                mailMessage.Attachments.Add(attachmentItem);
            }
            
            AddReciepients(dataContext, recipients, mailMessage, c);

            await SendMailWithRetryAsync(eMailSenderConfiguration, mailMessage, emailSemaphore, nodeContext);
        }
        catch (Exception e)
        {
            throw MeshAdapterPipelineExecutionException
                .CannotSendMail(nodeContext, e);
        }

        await next(dataContext, nodeContext);
    }

    /// <summary>
    /// Sends the mail, retrying transient SMTP failures with exponential backoff. A fresh
    /// <see cref="SmtpClient"/> is created per attempt (the .NET client is not reusable after a
    /// failed send), and the backoff delay is awaited outside the concurrency semaphore so other
    /// sends are not blocked while this one waits.
    /// </summary>
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

            // Reset seekable attachment streams so a retry re-sends the complete payload even if a
            // previous attempt already consumed part of the stream.
            foreach (var attachment in mailMessage.Attachments)
            {
                if (attachment.ContentStream.CanSeek)
                {
                    attachment.ContentStream.Position = 0;
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

    /// <summary>
    /// Determines whether an SMTP send failure is transient and worth retrying. Connection-level
    /// failures (dropped/reset socket, relay throttling closing the connection) are transient;
    /// a permanent recipient rejection is not.
    /// </summary>
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

    private async Task<IDownloadStreamHandler?> GetAttachment(
        EMailSenderNodeConfiguration eMailSenderNodeConfiguration,
        IDataContext dataContext, 
        INodeContext nodeContext)
    {
        if (eMailSenderNodeConfiguration.AttachmentRtId == null &&
            eMailSenderNodeConfiguration.AttachmentRtIdPath == null)
        {
            return null;
        }
        
        var attachmentRtId = eMailSenderNodeConfiguration.AttachmentRtId ??
                             dataContext.Get<string>(eMailSenderNodeConfiguration.AttachmentRtIdPath!);
        
        if (string.IsNullOrWhiteSpace(attachmentRtId))
        {
            nodeContext.Error("No attachment RT ID found");
            return null;
        }

        try
        {
            var tenantRepository = etlContext.TenantRepository;

            using var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
            session.StartTransaction();

            var streamHandler = await tenantRepository.DownloadLargeBinaryAsync(session,
                OctoObjectId.Parse(attachmentRtId), CancellationToken.None);

            await session.CommitTransactionAsync().ConfigureAwait(false);

            return streamHandler;
        }
        catch (Exception e)
        {
            nodeContext.Error(e, "Error getting attachment {RtId}", attachmentRtId);
            return null;
        }
    }
    
    private static void AddReciepients(IDataContext dataContext, IEnumerable<string?> recipients, MailMessage mailMessage,
        EMailSenderNodeConfiguration c)
    {
        foreach (var recipient in recipients)
        {
            if (!string.IsNullOrWhiteSpace(recipient))
            {
                mailMessage.To.Add(new MailAddress(recipient));
            }
        }
            
        var ccAddresses = c.CcAddresses != null && c.CcAddresses.Count > 0
            ? c.CcAddresses
            : !string.IsNullOrWhiteSpace(c.CcPath)
                ? dataContext.GetArray<string>(c.CcPath)
                : null;
            
        if (ccAddresses != null)
        {
            foreach (var cc in ccAddresses)
            {
                if (!string.IsNullOrWhiteSpace(cc))
                {
                    mailMessage.CC.Add(cc);
                }
            }
        }
            
        var bccAddresses = c.BccAddresses != null && c.BccAddresses.Count > 0
            ? c.BccAddresses
            : !string.IsNullOrWhiteSpace(c.BccPath)
                ? dataContext.GetArray<string>(c.BccPath)
                : null;
            
        if (bccAddresses != null)
        {
            foreach (var bcc in bccAddresses)
            {
                if (!string.IsNullOrWhiteSpace(bcc))
                {
                    mailMessage.Bcc.Add(bcc);
                }
            }
        }
    }
}