using System.Net;
using System.Net.Mail;
using Markdig;
using Meshmakers.Octo.Communication.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Load;

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
        // ReSharper restore UnusedAutoPropertyAccessor.Local
    }

    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var c = dataContext.NodeContext.GetNodeConfiguration<EMailSenderNodeConfiguration>();

        try
        {
            var session = await etlContext.TenantRepository.GetSessionAsync();
            session.StartTransaction();

            if (!etlContext.GlobalConfiguration.IsDefined(c.ServerConfiguration))
            {
                dataContext.NodeContext.Error($"Server configuration '{c.ServerConfiguration}' not found");
                return;
            }

            var eMailSenderConfiguration =
                etlContext.GlobalConfiguration.GetValue<EMailSenderConfiguration>(c.ServerConfiguration);
            
            var recipients = dataContext.GetSimpleArrayValueByPath<string>(c.ToPath);
            if (recipients == null)
            {
                dataContext.NodeContext.Error("No recipients found");
                return;
            }

            var subject = dataContext.GetSimpleValueByPath<string>(c.SubjectPath);
            if (subject == null)
            {
                dataContext.NodeContext.Error("No subject found");
                return;
            }

            var body = dataContext.GetSimpleValueByPath<string>(c.Path);
            if (body == null)
            {
                dataContext.NodeContext.Error("No body found");
                return;
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
                From = new MailAddress(eMailSenderConfiguration.SenderEmail ?? eMailSenderConfiguration.Username),
                Subject = subject,
                Body = bodyInHtml,
                IsBodyHtml = true
            };
            foreach (var recipient in recipients)
            {
                if (!string.IsNullOrWhiteSpace(recipient))
                {
                    mailMessage.To.Add(new MailAddress(recipient));
                }
            }
            await client.SendMailAsync(mailMessage);
        }
        catch (Exception e)
        {
            dataContext.NodeContext.Error(e, "Error sending email");
        }

        await next(dataContext);
    }
}