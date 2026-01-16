# Email Trigger Node Usage Example

The `FromEmailNode` is a trigger node that monitors an email inbox (via IMAP) and triggers a pipeline when new emails are received.

## Configuration

### Global Configuration
First, add the email server configuration to your global configuration:

```json
{
  "EmailServerConfig": {
    "Host": "imap.gmail.com",
    "Port": 993,
    "Username": "your-email@gmail.com",
    "Password": "your-app-password",
    "UseSsl": true,
    "Folder": "INBOX"
  }
}
```

### Node Configuration
Configure the trigger node in your pipeline:

```json
{
  "NodeType": "FromEmail",
  "Configuration": {
    "ServerConfiguration": "EmailServerConfig",
    "PollingIntervalSeconds": 60,
    "OnlyUnread": true,
    "MarkAsRead": true,
    "DeleteAfterProcessing": false,
    "SenderFilter": "@example.com",
    "SubjectFilter": "Important"
  }
}
```

## Configuration Options

- **ServerConfiguration**: Reference to the global email server configuration
- **PollingIntervalSeconds**: How often to check for new emails (default: 60)
- **OnlyUnread**: Process only unread emails (default: true)
- **MarkAsRead**: Mark emails as read after processing (default: true)
- **DeleteAfterProcessing**: Delete emails after processing (default: false)
- **SenderFilter**: Optional filter to process only emails from specific senders
- **SubjectFilter**: Optional filter to process only emails with specific subjects

## Output Data Structure

When emails are received, the pipeline is triggered with an `EmailBatch` object containing:

```json
{
  "Emails": [
    {
      "Subject": "Email subject",
      "From": "sender@example.com",
      "To": "recipient@example.com",
      "Date": "2024-01-15T10:30:00Z",
      "Body": "Email content",
      "HtmlBody": "<html>...</html>",
      "TextBody": "Plain text content",
      "MessageId": "message-id",
      "Attachments": [
        {
          "FileName": "document.pdf",
          "ContentType": "application/pdf"
        }
      ]
    }
  ],
  "Count": 1,
  "ProcessedAt": "2024-01-15T10:31:00Z"
}
```

## Notes

- The node uses IMAP protocol to receive emails (not SMTP, which is for sending)
- Supports SSL/TLS connections
- Processes emails in batches to optimize performance
- Maintains a list of processed email UIDs to avoid reprocessing
- Automatically reconnects if the connection is lost
- Supports filtering by sender and subject