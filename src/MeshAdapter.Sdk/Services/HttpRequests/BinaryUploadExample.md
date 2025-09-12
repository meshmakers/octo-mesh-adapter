# HTTP Binary Upload Support

The `FromHttpRequestNode` now supports binary file uploads via POST requests. Binary data is automatically encoded as base64 to preserve data integrity.

## Supported Upload Methods

### 1. Direct Binary Upload
Send binary data directly in the request body with appropriate content type.

**Request:**
```http
POST /your-endpoint
Content-Type: application/octet-stream
Content-Length: 12345

[binary data]
```

**Pipeline Input:**
```json
{
  "path": "/your-endpoint",
  "method": "POST",
  "body": "base64EncodedData...",
  "bodyEncoding": "base64",
  "contentType": "application/octet-stream",
  "contentLength": 12345
}
```

### 2. Multipart Form Data (File Upload)
Upload files using standard HTML form with `multipart/form-data`.

**Request:**
```http
POST /your-endpoint
Content-Type: multipart/form-data; boundary=----WebKitFormBoundary

------WebKitFormBoundary
Content-Disposition: form-data; name="file"; filename="document.pdf"
Content-Type: application/pdf

[binary file content]
------WebKitFormBoundary
Content-Disposition: form-data; name="description"

This is a test document
------WebKitFormBoundary--
```

**Pipeline Input:**
```json
{
  "path": "/your-endpoint",
  "method": "POST",
  "files": [
    {
      "fileName": "document.pdf",
      "contentType": "application/pdf",
      "length": 12345,
      "data": "base64EncodedFileData...",
      "encoding": "base64"
    }
  ],
  "formData": {
    "description": "This is a test document"
  },
  "contentType": "multipart/form-data; boundary=..."
}
```

### 3. Text-Based Content
Text-based content types are automatically detected and preserved as text.

**Detected as text:**
- `text/*` (text/plain, text/html, text/csv, etc.)
- `application/json`
- `application/xml`
- `application/javascript`
- `application/x-www-form-urlencoded`
- Any content type containing `+json` or `+xml`

## Processing Binary Data in Pipeline

### Example: Save Uploaded File to Database

```csharp
public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
{
    // For direct binary upload
    var bodyEncoding = dataContext.GetSimpleValueByPath<string>("bodyEncoding");
    if (bodyEncoding == "base64")
    {
        var base64Data = dataContext.GetSimpleValueByPath<string>("body");
        var binaryData = Convert.FromBase64String(base64Data);
        // Process binary data...
    }
    
    // For multipart uploads
    var files = dataContext.GetArrayValueByPath<JObject>("files");
    if (files != null)
    {
        foreach (var file in files)
        {
            var fileName = file["fileName"]?.ToString();
            var contentType = file["contentType"]?.ToString();
            var base64Data = file["data"]?.ToString();
            
            if (base64Data != null)
            {
                var binaryData = Convert.FromBase64String(base64Data);
                // Save to database or file system...
            }
        }
    }
}
```

## Testing with cURL

### Upload a binary file:
```bash
curl -X POST http://localhost:5000/your-endpoint \
  -H "Content-Type: application/octet-stream" \
  --data-binary "@/path/to/file.pdf"
```

### Upload with multipart form:
```bash
curl -X POST http://localhost:5000/your-endpoint \
  -F "file=@/path/to/file.pdf" \
  -F "description=Test upload"
```

## Important Notes

1. **Base64 Encoding**: All binary data is automatically encoded as base64 to ensure it can be safely transmitted through the JSON pipeline
2. **Size Limits**: Be aware of memory constraints when uploading large files
3. **Content Type Detection**: The system automatically detects whether content should be treated as text or binary
4. **Error Handling**: If UTF-8 decoding fails for ambiguous content types, the data is treated as binary and encoded as base64