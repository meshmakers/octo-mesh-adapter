using System.Net;
using System.Net.Mail;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

[NodeConfiguration(typeof(FromEmailNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class FromEmailNode(ILogger<FromEmailNode> logger) : ITriggerPipelineNode
{
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _pollingTask;
    private ImapClient? _imapClient;
    
    // ReSharper disable once ClassNeverInstantiated.Local
    private record EmailServerConfiguration
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Local
        public required string Host { get; init; }
        public required int Port { get; init; }
        public required string Username { get; init; }
        public required string Password { get; init; }
        public required bool IsSslEnabled { get; init; }
        public string Folder { get; init; } = "INBOX";
        // ReSharper restore UnusedAutoPropertyAccessor.Local
    }

    public async Task StartAsync(ITriggerContext context)
    {
        var c = context.NodeContext.GetNodeConfiguration<FromEmailNodeConfiguration>();
        
        if (!context.GlobalConfiguration.IsDefined(c.ServerConfiguration))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(
                context.NodeContext,
                nameof(c.ServerConfiguration),
                c.ServerConfiguration);
        }

        var serverConfig = context.GlobalConfiguration.GetValue<EmailServerConfiguration>(c.ServerConfiguration);
        
        _cancellationTokenSource = new CancellationTokenSource();
        _imapClient = new ImapClient();
        
        // Connect and authenticate
        await ConnectAndAuthenticateAsync(serverConfig);
        
        // Start polling task
        _pollingTask = Task.Run(async () => await PollForEmailsAsync(context, serverConfig, c), _cancellationTokenSource.Token);
    }

    private async Task ConnectAndAuthenticateAsync(EmailServerConfiguration config)
    {
        try
        {
            if (config.IsSslEnabled)
            {
                await _imapClient!.ConnectAsync(config.Host, config.Port, SecureSocketOptions.SslOnConnect);
            }
            else
            {
                await _imapClient!.ConnectAsync(config.Host, config.Port, SecureSocketOptions.StartTlsWhenAvailable);
            }
            
            await _imapClient.AuthenticateAsync(config.Username, config.Password);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to IMAP server");
            throw;
        }
    }

    private async Task PollForEmailsAsync(ITriggerContext context, EmailServerConfiguration serverConfig, FromEmailNodeConfiguration nodeConfig)
    {
        var processedUids = new HashSet<UniqueId>();
        
        while (!_cancellationTokenSource!.Token.IsCancellationRequested)
        {
            try
            {
                // Ensure we're connected
                if (!_imapClient!.IsConnected)
                {
                    await ConnectAndAuthenticateAsync(serverConfig);
                }
                
                // Open the folder
                var folder = await _imapClient.GetFolderAsync(serverConfig.Folder);
                await folder.OpenAsync(FolderAccess.ReadWrite);
                
                // Search for unread messages
                var searchQuery = nodeConfig.OnlyUnread ? SearchQuery.NotSeen : SearchQuery.All;
                var uids = await folder.SearchAsync(searchQuery);
                
                // Process new emails
                var newEmails = new List<EmailData>();
                foreach (var uid in uids)
                {
                    if (processedUids.Contains(uid))
                        continue;
                    
                    var message = await folder.GetMessageAsync(uid);
                    
                    // Apply sender filter if specified
                    if (!string.IsNullOrWhiteSpace(nodeConfig.SenderFilter))
                    {
                        var senderAddress = message.From?.Mailboxes?.FirstOrDefault()?.Address;
                        if (senderAddress == null || !senderAddress.Contains(nodeConfig.SenderFilter))
                            continue;
                    }
                    
                    // Apply subject filter if specified
                    if (!string.IsNullOrWhiteSpace(nodeConfig.SubjectFilter))
                    {
                        if (message.Subject == null || !message.Subject.Contains(nodeConfig.SubjectFilter))
                            continue;
                    }
                    
                    var emailData = new EmailData
                    {
                        Subject = message.Subject,
                        From = message.From?.ToString(),
                        FromAddress = message.From?.Mailboxes?.FirstOrDefault()?.Address,
                        To = message.To?.ToString(),
                        Date = message.Date.DateTime,
                        Body = message.TextBody ?? message.HtmlBody,
                        HtmlBody = message.HtmlBody,
                        TextBody = message.TextBody,
                        MessageId = message.MessageId,
                        Attachments = message.Attachments?.Select(a =>
                        {
                            var attachment = new AttachmentData
                            {
                                FileName = a.ContentDisposition?.FileName ?? "unknown",
                                ContentType = a.ContentType?.MimeType ?? "application/octet-stream"
                            };

                            if (a is MimePart { Content: not null } mimePart)
                            {
                                using var memoryStream = new MemoryStream();
                                mimePart.Content.DecodeTo(memoryStream);
                                attachment.Data = Convert.ToBase64String(memoryStream.ToArray());
                                attachment.Length = memoryStream.Length;
                            }

                            return attachment;
                        }).ToList() ?? new List<AttachmentData>()
                    };
                    
                    newEmails.Add(emailData);
                    processedUids.Add(uid);
                    
                    // Mark as read if configured
                    if (nodeConfig.MarkAsRead)
                    {
                        await folder.AddFlagsAsync(uid, MessageFlags.Seen, true);
                    }
                    
                    // Delete if configured
                    if (nodeConfig.DeleteAfterProcessing)
                    {
                        await folder.AddFlagsAsync(uid, MessageFlags.Deleted, true);
                    }
                }
                
                // Trigger the pipeline if we have new emails
                if (newEmails.Count > 0)
                {
                    var emailBatch = new EmailBatch
                    {
                        Emails = newEmails,
                        Count = newEmails.Count,
                        ProcessedAt = DateTime.UtcNow
                    };
                    
                    await context.ExecuteAsync(new ExecutePipelineOptions(DateTime.UtcNow), emailBatch);
                    
                    logger.LogInformation("Processed {Count} new emails", newEmails.Count);
                }
                
                // Expunge deleted messages if any were deleted
                if (nodeConfig.DeleteAfterProcessing && newEmails.Count > 0)
                {
                    await folder.ExpungeAsync();
                }
                
                await folder.CloseAsync();
                
                // Wait for the polling interval
                await Task.Delay(TimeSpan.FromSeconds(nodeConfig.PollingIntervalSeconds), _cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error while polling for emails");
                
                // Wait before retrying
                await Task.Delay(TimeSpan.FromSeconds(30), _cancellationTokenSource.Token);
            }
        }
    }

    public async Task StopAsync(ITriggerContext context)
    {
        _cancellationTokenSource?.Cancel();
        
        if (_pollingTask != null)
        {
            try
            {
                await _pollingTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                logger.LogWarning("Email polling task did not complete within timeout");
            }
        }
        
        if (_imapClient?.IsConnected == true)
        {
            await _imapClient.DisconnectAsync(true);
        }
        
        _imapClient?.Dispose();
        _cancellationTokenSource?.Dispose();
    }
}

/// <summary>
/// Represents an email message
/// </summary>
public class EmailData
{
    /// <summary>
    /// Email subject
    /// </summary>
    public string? Subject { get; set; }
    
    /// <summary>
    /// Email sender (display name and address)
    /// </summary>
    public string? From { get; set; }

    /// <summary>
    /// Email sender address only (e.g. user@example.com)
    /// </summary>
    public string? FromAddress { get; set; }
    
    /// <summary>
    /// Email recipients
    /// </summary>
    public string? To { get; set; }
    
    /// <summary>
    /// Email date
    /// </summary>
    public DateTime Date { get; set; }
    
    /// <summary>
    /// Email body (text or HTML)
    /// </summary>
    public string? Body { get; set; }
    
    /// <summary>
    /// HTML body of the email
    /// </summary>
    public string? HtmlBody { get; set; }
    
    /// <summary>
    /// Plain text body of the email
    /// </summary>
    public string? TextBody { get; set; }
    
    /// <summary>
    /// Email message ID
    /// </summary>
    public string? MessageId { get; set; }
    
    /// <summary>
    /// List of email attachments
    /// </summary>
    public List<AttachmentData> Attachments { get; set; } = new();
}

/// <summary>
/// Represents an email attachment
/// </summary>
public class AttachmentData
{
    /// <summary>
    /// Attachment file name
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// MIME content type
    /// </summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// Base64-encoded attachment content
    /// </summary>
    public string? Data { get; set; }

    /// <summary>
    /// Content length in bytes
    /// </summary>
    public long Length { get; set; }
}

/// <summary>
/// Represents a batch of emails
/// </summary>
public class EmailBatch
{
    /// <summary>
    /// List of emails in the batch
    /// </summary>
    public List<EmailData> Emails { get; set; } = new();
    
    /// <summary>
    /// Number of emails in the batch
    /// </summary>
    public int Count { get; set; }
    
    /// <summary>
    /// Timestamp when the batch was processed
    /// </summary>
    public DateTime ProcessedAt { get; set; }
}