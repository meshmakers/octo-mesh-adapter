using System.Text.Json.Nodes;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.ExcelImport;

namespace Meshmakers.Octo.Sdk.MeshAdapter;

internal class MeshAdapterPipelineExecutionException : PipelineExecutionException
{
    private MeshAdapterPipelineExecutionException()
    {
    }

    private MeshAdapterPipelineExecutionException(string message) : base(message)
    {
    }

    private MeshAdapterPipelineExecutionException(string message, Exception inner) : base(message, inner)
    {
    }

    /// <summary>
    /// The untruncated response body of a failed HTTP request, where the failure carries one. The
    /// message keeps only the first few hundred characters so a thrown failure stays readable; a
    /// caller that reports rather than throws restores the full text from here.
    /// </summary>
    public string? ResponseBody { get; private init; }

    public static Exception InputValueNull(INodeContext nodeContext, string path)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Path ${path} is null.");
    }

    public static Exception DelimitedValueNotScalar(INodeContext nodeContext, int recordIndex,
        int columnIndex, string valuePath)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: record {recordIndex}, column {columnIndex}: " +
            $"'{valuePath}' resolves to an object or array, which cannot be a column value.");
    }

    public static Exception DelimitedSourceNotAnArray(INodeContext nodeContext, string path)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: '{path}' must resolve to an array of records.");
    }

    public static Exception DelimitedDelimiterUnusable(INodeContext nodeContext, string? delimiter)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: delimiter '{delimiter}' must be exactly one character " +
            "and must not be a line break.");
    }

    public static Exception DelimitedReplacementUnusable(INodeContext nodeContext, string replacement)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: replacement '{replacement}' contains the delimiter or a " +
            "line break, which would only move the problem.");
    }

    public static Exception DelimitedValueBreaksStructure(INodeContext nodeContext, int recordIndex,
        int columnIndex, string value)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: record {recordIndex}, column {columnIndex}: the value " +
            $"'{value}' contains the delimiter or a line break and would shift every column after it.");
    }

    public static Exception DelimitedRequiredColumnEmpty(INodeContext nodeContext, int recordIndex,
        int columnIndex)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: record {recordIndex}, column {columnIndex} is required but " +
            "rendered empty.");
    }

    public static Exception DelimitedPathNotSet(INodeContext nodeContext, string property)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: '{property}' must be a JSONPath; an empty one would be " +
            "read as the document root.");
    }

    public static Exception DelimitedOptionUndefined(INodeContext nodeContext, string property,
        int value)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: '{property}' has the undefined value {value}.");
    }

    public static Exception DelimitedRecordNotAnObject(INodeContext nodeContext, int recordIndex)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: record {recordIndex} is not an object, so no column value " +
            "can be read from it.");
    }

    public static Exception DelimitedSourceDisagrees(INodeContext nodeContext, string path,
        int length)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: '{path}' reports {length} record(s) but none could be " +
            "iterated; refusing to write an empty document.");
    }

    public static Exception DelimitedColumnsNotSet(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: columns must list at least one column.");
    }

    public static Exception DelimitedColumnNull(INodeContext nodeContext, int columnIndex)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: column {columnIndex} is null.");
    }

    public static Exception DelimitedColumnAmbiguous(INodeContext nodeContext, int columnIndex)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: column {columnIndex} sets both value and valuePath.");
    }

    public static Exception InvalidValue(object? value)
    {
        return new MeshAdapterPipelineExecutionException($"Invalid value: {value}");
    }

    public static Exception InvalidValue(INodeContext nodeContext, object? value)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Invalid value: {value}");
    }

    public static Exception InvalidValue(INodeContext nodeContext, JsonNode? value)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Invalid value: {value}");
    }

    public static Exception TargetCkTypeIdNotSet(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: targetCkTypeId and targetCkTypeIdPath is not set.");
    }

    public static Exception TargetCkTypeIdValueNull(INodeContext nodeContext, string? path = null)
    {
        var pathInfo = path != null ? $" at path '{path}'" : "";
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Value of target CkTypeId is null{pathInfo}.");
    }

    public static Exception TargetRtIdNotFound(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: targetRtId and targetRtIdPath is not set.");
    }

    public static Exception TargetRtIdValueNull(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Value of target RtId is null.");
    }

    public static Exception UpdateKindPathNotFound(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: updateKind or updateKindPath is not set.");
    }

    public static Exception AssociationRoleIdPathNotSet(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: associationRoleId or associationRoleIdPath is not set.");
    }

    public static Exception UpdateKindNull(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Value of update kind is null.");
    }

    public static Exception AssociationRoleIdValueNull(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Value of association role id is null.");
    }

    public static Exception GraphDirectionNotSet(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Graph direction is not set. Please set graphDirection or graphDirectionPath.");
    }

    public static Exception OriginCkTypeIdNotSet(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Origin CkTypeId is not set. Please set originCkTypeId or originCkTypeIdPath.");
    }

    public static Exception OriginCkTypeIdValueNull(INodeContext nodeContext, string? path = null)
    {
        var pathInfo = path != null ? $" at path '{path}'" : "";
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Value of origin CkTypeId is null{pathInfo}.");
    }

    public static Exception OriginRtIdsNotSet(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Origin RtIds are not set. Please set originRtId or originRtIdPath.");
    }

    public static Exception OriginRtIdNotFound(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: originRtId and originRtIdPath is not set.");
    }

    public static Exception OriginRtIdValueNull(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Value of origin RtId is null.");
    }

    public static Exception CkTypeIdNotSet(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: CkTypeId is not set. Please set ckTypeId or ckTypeIdPath.");
    }

    public static Exception CkTypeIdValueNull(INodeContext nodeContext, string path)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: No CkTypeId found at path '{path}'.");
    }

    public static Exception GlobalConfigurationParameterNotFound(INodeContext nodeContext, string configurationName, string configurationValue)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Global configuration parameter '{configurationName}' with value '{configurationValue}' not found.");
    }

    public static Exception WellKnownNameNotSet(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: WellKnownName is not set. Please set wellKnownName or wellKnownNamePath.");
    }

    public static Exception WellKnownNameValueNull(INodeContext nodeContext, string path)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: No WellKnownName found at path '{path}'. Please ensure the value is set at the specified path.");
    }

    public static Exception NoRecipientsFound(INodeContext nodeContext, string toPathName, string toPathValue)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: No recipients found for path '{toPathName}' with value '{toPathValue}'. Please check the configuration.");
    }

    public static Exception CannotSendMail(INodeContext nodeContext, Exception exception)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Cannot send e-mail: {exception.Message}", exception);
    }

    public static Exception CannotUploadViaSftp(INodeContext nodeContext, Exception exception)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Cannot upload file via SFTP: {exception.Message}", exception);
    }

    public static Exception UnencodableContent(INodeContext nodeContext, string encodingName, string countText,
        string codePoints)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: {countText} character(s) of the string content are not representable in encoding '{encodingName}' and onEncodingError is 'Fail'. Offending code points: {codePoints}. The upload was aborted; no file was written to the target.");
    }

    public static Exception NoFileSourceSpecified(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: No file source specified. Set either Path for string content or FileRtId/FileRtIdPath for binary files.");
    }

    public static Exception AmbiguousFileSource(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Multiple file sources specified. Set either Path for string content or FileRtId/FileRtIdPath for binary files, not both.");
    }

    public static Exception FileNameNotConfigured(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: File name is not configured. Set either FileName or FileNamePath.");
    }

    public static Exception InvalidFileName(INodeContext nodeContext, string fileName)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Invalid file name '{fileName}'. Path components are stripped to the final segment; traversal segments such as '..' are not allowed.");
    }

    public static Exception SftpAuthNotConfigured(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: No SFTP authentication configured. Set either Password or PrivateKey in the server configuration.");
    }

    public static Exception InvalidMaxConcurrentConnections(INodeContext nodeContext, string serverConfigurationName,
        int value)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: SFTP server configuration '{serverConfigurationName}': MaxConcurrentConnections must be greater than zero, but was {value}.");
    }

    public static Exception CannotDecodeContent(INodeContext nodeContext, string encodingName)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Downloaded content is not valid '{encodingName}'. Set the correct encoding, or switch OnEncodingError to Replace to accept a lossy read.");
    }

    public static Exception NoRemotePathSpecified(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: No remote path specified. Set either 'remotePath' or 'remotePathPath'.");
    }

    public static Exception FilePatternNotConfigured(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: File pattern is not configured. Set 'filePattern', for example \"AR*TXT\".");
    }

    public static Exception RemoteDirectoryNotConfigured(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Remote directory is not configured. Set 'remoteDirectory', for example \"/out\".");
    }

    public static Exception SftpSlotWaitTimedOut(INodeContext nodeContext, string serverConfigurationName,
        int waitSeconds)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: SFTP server configuration '{serverConfigurationName}': no free connection slot after {waitSeconds}s. Either transfers are stalling or MaxConcurrentConnections is too low for the pipeline.");
    }

    public static Exception NegativeSftpTimeout(INodeContext nodeContext, string serverConfigurationName,
        string propertyName, int value)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: SFTP server configuration '{serverConfigurationName}' has {propertyName} = {value}. Use zero to keep the default or a positive number of seconds.");
    }

    public static Exception SftpTimeoutTooLarge(INodeContext nodeContext, string serverConfigurationName,
        string propertyName, int value, int maximumSeconds)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: SFTP server configuration '{serverConfigurationName}' has {propertyName} = {value}, which is beyond the {maximumSeconds}s the underlying timers accept. Use zero to keep the default or a smaller number of seconds.");
    }

    public static Exception InvalidSftpServerConfiguration(INodeContext nodeContext, string serverConfigurationName,
        Exception exception)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: SFTP server configuration '{serverConfigurationName}' cannot be read: {exception.Message}", exception);
    }

    public static Exception SftpFileTooLarge(INodeContext nodeContext, string remotePath, long? size, long maxBytes)
    {
        var reported = size is null
            ? "outgrew that while it was being read"
            : $"is {size} byte(s)";

        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Remote file '{remotePath}' {reported}, and MaxFileSizeBytes allows {maxBytes}. The whole file is held in memory and decoded to a string, so raise MaxFileSizeBytes only as far as this adapter can afford.");
    }

    public static Exception InvalidMaxFileSizeBytes(INodeContext nodeContext, long value)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: MaxFileSizeBytes must be greater than zero, but was {value}. There is no unlimited setting: the content is read into memory and decoded to a string, so an unbounded read would decide this adapter's memory from the remote side.");
    }

    public static Exception CannotListViaSftp(INodeContext nodeContext, Exception exception)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Cannot list directory via SFTP: {exception.Message}", exception);
    }

    public static Exception CannotDownloadViaSftp(INodeContext nodeContext, Exception exception)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Cannot download file via SFTP: {exception.Message}", exception);
    }

    public static Exception BlankHostKeyFingerprint(INodeContext nodeContext, string serverConfigurationName)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: SFTP server configuration '{serverConfigurationName}' has a blank HostKeyFingerprint. Remove the property to connect without host key verification, or set the SHA-256 fingerprint of the expected key.");
    }

    public static Exception SftpHostKeyMismatch(INodeContext nodeContext, string host, string expectedFingerprint,
        string presentedFingerprint)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Host key of '{host}' does not match the configured fingerprint. Expected '{expectedFingerprint}', server presented '{presentedFingerprint}'. Update HostKeyFingerprint in the server configuration if the key was rotated deliberately.");
    }

    public static Exception BinaryNotFound(INodeContext nodeContext, string rtId)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Binary file with RtId '{rtId}' not found in storage.");
    }

    public static Exception FileSystemItemNotFound(INodeContext nodeContext, string rtId)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: FileSystemItem with RtId '{rtId}' not found. " +
            "Ensure the RtId points to a System.Reporting/FileSystemItem entity on this tenant.");
    }

    public static Exception FileSystemItemMissingBinary(INodeContext nodeContext, string rtId)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: FileSystemItem '{rtId}' has no Content.BinaryId set. " +
            "The entity exists but is not bound to a binary payload.");
    }

    public static Exception PathParameterNameMissing(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Path parameter name is missing. Please set the Name property.");
    }

    public static Exception PathParameterValueMissing(INodeContext nodeContext, string pathParamName)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Path parameter value is missing for parameter '{pathParamName}'. Please set the Value or ValuePath property.");
    }

    public static Exception FileSystemFolderUriMissing(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: FileSystemFolderUri is missing. Please set the FileSystemFolderUri property.");
    }

    public static Exception ReportDefinitionUriMissing(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: ReportDefinitionUri is missing. Please set the ReportDefinitionUri property.");
    }

    public static Exception ReportFileNamePrefixMissing(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: ReportFileNamePrefix is missing. Please set the ReportFileNamePrefix property.");
    }

    public static Exception RtIdNotSet(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: RtId is not set. Please set rtId or rtIdPath.");
    }

    public static Exception DataContextIsNull(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Data context is null. Please ensure the data context is set before execution.");
    }

    public static Exception RtIdValueNull(INodeContext nodeContext, string? rtIdPath)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Value of RtId is null. Please ensure the value is set at path '{rtIdPath}'.");
    }


    public static Exception NoWellKnownNameValue(int layer, int lineNumber)
    {
        return new MeshAdapterPipelineExecutionException(
            $"No well-known name value found for layer {layer} at line {lineNumber}. Please ensure the well-known name is set correctly.");
    }

    public static Exception NoWellKnownNamesFound(int iLayer)
    {
        return new MeshAdapterPipelineExecutionException(
            $"No well-known names found for layer {iLayer}. Please ensure the well-known names are set correctly.");
    }

    public static Exception UnknownActionType(ColumnContext.ActionType actionType)
    {
        return new MeshAdapterPipelineExecutionException(
            $"Unknown action type: {actionType}. Please ensure the action type is valid and supported.");
    }

    public static Exception NoWellKnownNamesFoundForLayer(int iLayer)
    {
        return new MeshAdapterPipelineExecutionException(
            $"No well-known names found for layer {iLayer}. Please ensure the well-known configuration are set correctly.");
    }

    public static Exception NoEntityFound(int iLayer, string name)
    {
        return new MeshAdapterPipelineExecutionException(
            $"No entity found for layer {iLayer} with name '{name}'. Please ensure the entity exists and is correctly configured.");
    }

    public static Exception ParentNotFound(int iLayer)
    {
        return new MeshAdapterPipelineExecutionException(
            $"Parent not found for layer {iLayer}. Please ensure the parent entity is correctly configured and exists.");
    }

    public static Exception UnknownImportType(string importType)
    {
        return new MeshAdapterPipelineExecutionException(
            $"Unknown import type: {importType}. Please ensure the import type is valid and supported.");
    }

    public static Exception NotificationTemplateNotFound(INodeContext nodeContext, string templateName)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Notification template '{templateName}' not found. Please ensure the template exists and is correctly configured.");
    }

    public static Exception NotificationTemplateNameNotSet(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Notification template name is not set. Please set NotificationTemplateName or NotificationTemplateNamePath.");
    }

    public static Exception NotificationTemplateNameValueNull(INodeContext nodeContext, string path)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: No notification template name found at path '{path}'. Please ensure the value is set at the specified path.");
    }


    public static Exception FileNameNull(INodeContext nodeContext, string? fileNamePath)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: File name is null. Please ensure the file name is set at path '{fileNamePath}'.");
    }

    public static Exception ContentTypeNull(INodeContext nodeContext, string? cContentTypePath)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Content type is null. Please ensure the content type is set at path '{cContentTypePath}'.");
    }

    public static Exception ContentLengthNull(INodeContext nodeContext, string? contentLengthPath)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Content length is null. Please ensure the content length is set at path '{contentLengthPath}'.");
    }

    public static Exception RepositoryOperationFailed(Exception exception)
    {
        return new MeshAdapterPipelineExecutionException(
            $"Repository operation failed: {exception.Message}", exception);
    }

    public static Exception RootFolderNotFound(string rootFolderWellKnownName)
    {
        return new MeshAdapterPipelineExecutionException(
            $"Root folder with well-known name '{rootFolderWellKnownName}' not found. Please ensure the root folder exists and is correctly configured.");
    }

    public static Exception RepositoryUpdateOperationFailed(OperationResult operationResult)
    {
        return new MeshAdapterPipelineExecutionException(
            $"Repository update operation failed: {operationResult}");
    }

    public static Exception RootFolderWellKnownNameNotSet(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Root folder well-known name is not set. Please ensure the RootFolderWellKnownName property is set.");
    }

    public static Exception ProcessingError(INodeContext nodeContext, Exception exception)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Processing error: {exception.Message}", exception);
    }

    public static Exception ContextTooLarge(INodeContext nodeContext, int fullContextLength, int i)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Context too large: {fullContextLength} tokens (max {i}). Please reduce the context size.");
    }

    public static Exception FileTooLarge(INodeContext nodeContext, int pdfDataLength, int maxLength)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: File too large: {pdfDataLength} bytes (max {maxLength}). Please reduce the file size.");
    }

    public static Exception QueryNotFound(INodeContext nodeContext, OctoObjectId queryRtId)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Query with RtId '{queryRtId}' not found.");
    }

    public static Exception EntityNotFound(INodeContext nodeContext, OctoObjectId rtId)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Entity with RtId '{rtId}' not found.");
    }

    public static Exception FileContentNotFound(INodeContext nodeContext, OctoObjectId rtId)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: File system item '{rtId}' has no binary content.");
    }

    public static Exception UnsupportedQueryType(INodeContext nodeContext, string queryTypeName)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Unsupported query type '{queryTypeName}'.");
    }

    public static Exception StreamDataNotEnabled(INodeContext nodeContext, string tenantId)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Stream data repository is not available for tenant '{tenantId}'. " +
            "Ensure stream data is enabled for the tenant (AddCrateDbStreamDataRepository() during startup).");
    }

    public static Exception ArchiveRtIdMissing(INodeContext nodeContext, OctoObjectId queryRtId)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Stream-data query '{queryRtId}' has no ArchiveRtId set. " +
            "The query must reference the CkArchive it reads from.");
    }

    public static Exception ArchiveNotFound(INodeContext nodeContext, OctoObjectId archiveRtId)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Archive '{archiveRtId}' was not found in the runtime model. " +
            "Check the configured ArchiveRtId; a soft-deleted archive is not readable either.");
    }

    public static Exception StreamDataArchiveQueryFailed(INodeContext nodeContext,
        OctoObjectId archiveRtId, Exception inner)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Reading archive '{archiveRtId}' failed: {inner.Message}",
            inner);
    }

    public static Exception StreamDataTimeRangeInvalid(INodeContext nodeContext, DateTime? from,
        DateTime? to)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: The time range From '{from:O}' / To '{to:O}' is empty. " +
            "From must be earlier than To.");
    }

    public static Exception StreamDataLimitInvalid(INodeContext nodeContext, int? limit)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Limit '{limit}' is not a valid row cap. " +
            "Provide a value greater than zero, or leave it unset to read every matching row.");
    }

    public static Exception UnknownStreamDataColumn(INodeContext nodeContext, string column,
        string usage, string knownColumns)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: '{column}' is not a column of this archive and cannot be " +
            $"used for {usage}. The storage layer would ignore it without an error, so the query is " +
            $"refused instead. Available: {knownColumns}.");
    }

    public static Exception GapDetectionRequiresWindowedArchive(INodeContext nodeContext,
        OctoObjectId archiveRtId)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Gap detection needs an archive that stores a row window, " +
            $"but '{archiveRtId}' is a raw archive holding single timestamps. There is no interval " +
            "coverage to check — remove GapsTargetPath, or point the node at a time-range or " +
            "rollup archive.");
    }

    public static Exception GapDetectionTimeRangeRequired(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Gap detection needs both From and To — coverage can only be " +
            "judged against a closed time range. Set them literally or via FromPath / ToPath.");
    }

    public static Exception GapsOnlyWithoutTarget(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: GapsOnly skips the data query, so GapsTargetPath must be set " +
            "— otherwise the node would produce no output at all.");
    }

    public static Exception AggregationColumnsMissing(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: No aggregations configured. Provide at least one entry with " +
            "an attribute path and a function (Count, Minimum, Maximum, Average or Sum).");
    }

    public static Exception UnsupportedAggregationFunction(INodeContext nodeContext, string function,
        string attributePath)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Aggregation '{function}' on '{attributePath}' is not " +
            "supported here. Available: Count, Minimum, Maximum, Average, Sum. A time-weighted " +
            "average and a state duration need per-column metadata this node cannot carry — use " +
            "GetQueryById@1 with a persisted query that defines the aggregation per column.");
    }

    public static Exception AggregationGapGuardFailed(INodeContext nodeContext,
        OctoObjectId archiveRtId, string incompleteSeries)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: RequireGapFree is set, but archive '{archiveRtId}' does not " +
            $"cover the requested range for every entity — {incompleteSeries}. Aggregating anyway " +
            "would return a figure that looks valid but is too low, so the node stops instead.");
    }

    public static Exception GapScanRowLimitInvalid(INodeContext nodeContext, int? maxRows)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: MaxGapScanRows '{maxRows}' is not a usable cap. Provide a " +
            "value greater than zero, or leave it unset for the default. To scan without a practical " +
            "cap, set it to the largest possible integer rather than zero.");
    }

    public static Exception ExpectedIntervalInvalid(INodeContext nodeContext, TimeSpan interval)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: ExpectedInterval '{interval}' must be greater than zero — " +
            "the gap counts divide by it. Leave it unset to use the period declared on the archive.");
    }

    public static Exception GapScanRowLimitExceeded(INodeContext nodeContext, int maxRows)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: The gap scan hit its row cap of {maxRows}. A report built " +
            "from a truncated scan would invent gaps, so the node fails instead. Narrow the time " +
            "range or the set of entities, or raise MaxGapScanRows.");
    }

    public static Exception InvalidRtId(INodeContext nodeContext, string value)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: The value '{value}' is not a valid runtime id.");
    }

    public static Exception InvalidDateTimeAtPath(INodeContext nodeContext, string path, object? value)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: The value '{value}' at path '{path}' cannot be read as a date/time. " +
            "Provide an ISO-8601 timestamp; values without a time-zone offset are interpreted as UTC.");
    }

    public static Exception InvalidIntegerAtPath(INodeContext nodeContext, string path, object? value)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: The value '{value}' at path '{path}' cannot be read as a 32-bit integer.");
    }

    public static Exception StreamDataQueryFailed(INodeContext nodeContext, OctoObjectId queryRtId,
        Exception inner)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Execution of stream-data query '{queryRtId}' failed: {inner.Message}",
            inner);
    }

    public static Exception DownsamplingColumnsMissing(INodeContext nodeContext, OctoObjectId queryRtId)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Downsampling query '{queryRtId}' has no aggregation columns. " +
            "A downsampling query must define at least one column (attribute path + aggregation type).");
    }

    public static Exception DownsamplingTimeRangeInvalid(INodeContext nodeContext, OctoObjectId queryRtId,
        DateTime? from, DateTime? to)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Downsampling query '{queryRtId}' needs a time range with From < To " +
            $"(effective From='{from?.ToString("O") ?? "<null>"}', To='{to?.ToString("O") ?? "<null>"}'). " +
            "Set it on the query entity or override it with From/To (or FromPath/ToPath) on the node.");
    }

    public static Exception DownsamplingLimitInvalid(INodeContext nodeContext, OctoObjectId queryRtId, int? limit)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Downsampling query '{queryRtId}' needs a positive bucket count " +
            $"(effective Limit='{limit?.ToString() ?? "<null>"}'). Set Limit on the query entity or override it " +
            "with Limit (or LimitPath) on the node.");
    }

    public static Exception UnsupportedAggregationType(INodeContext nodeContext, string aggregationType)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Aggregation '{aggregationType}' cannot be executed as a downsampling " +
            "aggregation. Supported: Count, Minimum, Maximum, Average, Sum.");
    }

    public static Exception SeriesResolutionFailed(INodeContext nodeContext, OctoObjectId queryRtId,
        Exception inner)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Resolution-aware archive selection for query '{queryRtId}' failed: " +
            $"{inner.Message}", inner);
    }

    public static Exception AggregationResultNull(INodeContext nodeContext, OctoObjectId queryRtId)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Aggregation result is null for query '{queryRtId}'.");
    }

    public static Exception FieldAggregationResultNull(INodeContext nodeContext, OctoObjectId queryRtId)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Field aggregation result is null for query '{queryRtId}'.");
    }

    public static Exception GroupingOfDataFailed(INodeContext nodeContext, string detectorGroupByPath)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Grouping of data failed for path '{detectorGroupByPath}'. Please ensure the path is valid and the data can be grouped.");
    }

    public static Exception InputValueInvalidFormat(INodeContext nodeContext, string detectorPath, FormatException formatException)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Invalid format for value at path '{detectorPath}': {formatException.Message}", formatException);
    }

    public static Exception SpikeDetectionFailed(INodeContext nodeContext, Exception exception)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Spike detection failed: {exception.Message}", exception);
    }

    public static Exception ChangePointDetectionFailed(INodeContext nodeContext, Exception exception)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Change point detection failed: {exception.Message}", exception);
    }

    public static Exception DiscordApiFailed(INodeContext nodeContext, int statusCode, string responseBody,
        string channelId, string? retryAfter)
    {
        var retryPart = retryAfter == null ? "" : $" Retry-After={retryAfter}s.";
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Discord API returned HTTP {statusCode} for channel '{channelId}'.{retryPart} Response body: {responseBody}");
    }

    public static Exception DataSheetModelInvalid(INodeContext nodeContext, string path)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: The data-sheet model at path '{path}' is missing or is not a JSON object. " +
            "Please provide an object with 'title', optional 'subtitle', a 'sections' array and an optional footer.");
    }

    public static Exception PdfRenderFailed(INodeContext nodeContext, Exception exception)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Rendering the data-sheet PDF failed: {exception.Message}", exception);
    }

    public static Exception HtmlPdfRenderFailed(INodeContext nodeContext, Exception exception)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Rendering the HTML PDF failed: {exception.Message}", exception);
    }

    public static Exception PdfMergeInputEmpty(INodeContext nodeContext, string path)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: No PDFs to merge at path '{path}'. Please provide a non-empty array of base64 PDFs.");
    }

    public static Exception PdfMergeItemInvalid(INodeContext nodeContext, int index, Exception exception)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: PDF at index {index} could not be imported for merging: {exception.Message}. " +
            "Set FailOnInvalidPdf=false to skip unreadable PDFs instead.", exception);
    }

    public static Exception PdfMergeProducedNothing(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: None of the supplied PDFs could be imported, so no merged document was produced.");
    }

    public static Exception PdfTransformSourcesEmpty(INodeContext nodeContext, string path)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: No source PDFs at path '{path}'. Please provide a non-empty array of base64 PDFs.");
    }

    public static Exception PdfTransformOpsEmpty(INodeContext nodeContext, string path)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: No page operations at path '{path}'. Please provide a non-empty array of " +
            "{{ sourceIndex, pageIndex, rotate?, crop? }} objects.");
    }

    public static Exception PdfTransformSourceIndexOutOfRange(INodeContext nodeContext, int opIndex, int sourceIndex,
        int sourceCount)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Op {opIndex} references source index {sourceIndex}, but only {sourceCount} " +
            "source PDF(s) were supplied.");
    }

    public static Exception PdfTransformPageIndexOutOfRange(INodeContext nodeContext, int opIndex, int sourceIndex,
        int pageIndex, int pageCount)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Op {opIndex} references page {pageIndex} of source {sourceIndex}, which has " +
            $"{pageCount} page(s).");
    }

    public static Exception PdfTransformInvalidRotation(INodeContext nodeContext, int opIndex, int rotate)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Op {opIndex} has rotation {rotate}, which is not a multiple of 90 degrees.");
    }

    public static Exception PdfTransformSourceInvalid(INodeContext nodeContext, int sourceIndex, Exception? exception)
    {
        var detail = exception != null ? $": {exception.Message}" : ".";
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Source PDF at index {sourceIndex} could not be imported{detail} " +
            "Set FailOnInvalidPdf=false to skip unreadable sources instead.", exception ?? new InvalidOperationException("Unreadable source PDF."));
    }

    public static Exception PdfTransformProducedNothing(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: No pages could be assembled, so no output document was produced.");
    }

    public static Exception ZipEntriesInvalid(INodeContext nodeContext, string path)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: The ZIP entries at path '{path}' are missing or are not a JSON array of " +
            "{{ fileName, contentBase64 }} objects.");
    }

    public static Exception ZipEntryInvalid(INodeContext nodeContext, int index, string reason)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: ZIP entry at index {index} is invalid: {reason}.");
    }

    public static Exception ScratchSpaceRequired(INodeContext nodeContext, string reason)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: a per-execution scratch space is required ({reason}).");
    }

    public static Exception InvalidHttpApiConfiguration(INodeContext nodeContext, string configurationName,
        Exception inner)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Global configuration '{configurationName}' cannot be read as HTTP API settings.",
            inner);
    }

    public static Exception IncompleteHttpApiConfiguration(INodeContext nodeContext, string configurationName)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Global configuration '{configurationName}' must provide both 'baseUrl' and 'apiKey'.");
    }

    public static Exception HttpPagingItemsPathUnusable(INodeContext nodeContext, string itemsPath,
        int page)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: The response for page {page} carries no array at '{itemsPath}'. " +
            "An empty array ends the walk; a missing or non-array value means the response shape changed.");
    }

    public static Exception HttpPagingCapReached(INodeContext nodeContext, int maxPages)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: The paged request reached its limit of {maxPages} pages. " +
            "Raise maxPages if the collection really is that large, or check that the target honours " +
            "the page parameter - the result would otherwise be truncated silently.");
    }

    public static Exception HttpPagingConflictsWithOption(INodeContext nodeContext, string option)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: {option} describes a single response and cannot be combined " +
            "with paging, which writes the collected elements of every page.");
    }

    public static Exception HttpPagingParameterAlreadyInQuery(INodeContext nodeContext,
        string parameterName)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: The URL already carries a '{parameterName}' query parameter, " +
            "which the page walk also appends. Remove it from the URL, or name the paging parameter " +
            "differently.");
    }

    public static Exception HttpPagingItemsPathNotSet(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Paging needs an itemsPath naming the array inside one response.");
    }

    public static Exception HttpRequestFailed(INodeContext nodeContext, string url, int? statusCode,
        int attempts, string detail, string? responseBody = null)
    {
        var status = statusCode is null ? "no response" : $"HTTP {statusCode}";
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Request to '{url}' failed after {attempts} attempts ({status}): {detail}")
        {
            ResponseBody = responseBody
        };
    }

    public static Exception InvalidHttpNodeOption(INodeContext nodeContext, string detail)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: {detail}");
    }

    public static Exception UnusableAuthHeaderName(INodeContext nodeContext, string headerName)
    {
        // The value is deliberately absent from the message: it is the API key.
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: authHeaderName '{headerName}' is not a usable HTTP header name. " +
            "A header name is a token: letters, digits and !#$%&'*+-.^_`|~, with no spaces.");
    }

    public static Exception HttpPagingUrlHasFragment(INodeContext nodeContext, string url)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: URL '{url}' carries a fragment while paging is configured. " +
            "A fragment is never sent to the server, so the page parameters appended after it would " +
            "never reach the target and the walk would run to its limit against the first page. " +
            "Remove the fragment from the URL.");
    }

    public static Exception InvalidHttpRetryOptions(INodeContext nodeContext, string detail)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: {detail}");
    }

    public static Exception HttpApiBaseUrlNotAbsolute(INodeContext nodeContext, string configurationName,
        string baseUrl)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Global configuration '{configurationName}' has baseUrl '{baseUrl}', " +
            "which is not an absolute http or https URL.");
    }

    public static Exception AuthHeaderCollision(INodeContext nodeContext, string headerName)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Header '{headerName}' is both the auth header of the configured " +
            "API and a header parameter, so the request would carry two values for it. Rename one of " +
            "them, or drop the ApiConfiguration and supply the header yourself.");
    }

    public static Exception AuthHeaderNotAccepted(INodeContext nodeContext, string headerName)
    {
        // The value is deliberately absent from the message: it is the API key.
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Header name '{headerName}' is not a valid HTTP header name.");
    }

    public static Exception SchemeQualifiedUrlWithHttpApiConfiguration(INodeContext nodeContext, string url)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: URL '{url}' names its own scheme while an ApiConfiguration is " +
            "set, which configures the host to talk to. Configure a path relative to the configured " +
            "base URL, or drop the ApiConfiguration and supply the header yourself.");
    }
}
