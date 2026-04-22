using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.ExcelImport;
using Newtonsoft.Json.Linq;

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

    public static Exception InputValueNull(INodeContext nodeContext, string path)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Path ${path} is null.");
    }

    public static Exception InvalidValue(JToken jToken)
    {
        return new MeshAdapterPipelineExecutionException($"Invalid value: {jToken}");
    }

    public static Exception InvalidValue(INodeContext nodeContext, JToken jToken)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Invalid value: {jToken}");
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

    public static Exception InvalidMaxConcurrentConnections(string serverConfigurationName, int value)
    {
        return new MeshAdapterPipelineExecutionException(
            $"SFTP server configuration '{serverConfigurationName}': MaxConcurrentConnections must be greater than zero, but was {value}.");
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

    public static Exception UnsupportedQueryType(INodeContext nodeContext, string queryTypeName)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Unsupported query type '{queryTypeName}'.");
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
}
