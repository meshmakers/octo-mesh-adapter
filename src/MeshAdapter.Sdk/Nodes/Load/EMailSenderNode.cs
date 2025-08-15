using System.Net;
using System.Net.Mail;
using Markdig;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

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

            var recipients = dataContext.GetSimpleArrayValueByPath<string>(c.ToPath);
            if (recipients == null)
            {
                throw MeshAdapterPipelineExecutionException.NoRecipientsFound(nodeContext,
                    nameof(c.ToPath), c.ToPath);
            }

            var subject = dataContext.GetSimpleValueByPath<string>(c.SubjectPath);
            if (subject == null)
            {
                throw MeshAdapterPipelineExecutionException.ValueNotSet(
                    nodeContext, c.SubjectPath);
            }

            var body = dataContext.GetSimpleValueByPath<string>(c.Path);
            if (body == null)
            {
                throw MeshAdapterPipelineExecutionException.ValueNotSet(
                    nodeContext, c.Path);
            }
            

            var bodyInHtml = Markdown.ToHtml(body, _pipeline);


            var client = new SmtpClient(eMailSenderConfiguration.Host, eMailSenderConfiguration.Port)
            {
                Credentials =
                    new NetworkCredential(eMailSenderConfiguration.Username, eMailSenderConfiguration.Password),
                EnableSsl = eMailSenderConfiguration.IsSslEnabled
            };

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
                    throw MeshAdapterPipelineExecutionException.ValueNotSet(
                        nodeContext, nameof(c.AttachmentFileName));
                }
                
                var attachmentData = attachment.Stream;
                var attachmentItem = new Attachment(attachmentData, c.AttachmentFileName, c.AttachmentContentType);
                mailMessage.Attachments.Add(attachmentItem);
            }
            
            AddReciepients(dataContext, recipients, mailMessage, c);

            await emailSemaphore.WaitAsync();
            try
            {
                await client.SendMailAsync(mailMessage);
            }
            finally
            {
                emailSemaphore.Release();
            }
        }
        catch (Exception e)
        {
            throw MeshAdapterPipelineExecutionException
                .CannotSendMail(nodeContext, e);
        }

        await next(dataContext, nodeContext);
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
                             dataContext.GetSimpleValueByPath<string>(eMailSenderNodeConfiguration
                                 .AttachmentRtIdPath);
        
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
                ? dataContext.GetSimpleArrayValueByPath<string>(c.CcPath)
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
                ? dataContext.GetSimpleArrayValueByPath<string>(c.BccPath)
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