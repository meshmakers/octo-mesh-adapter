using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// Resolves the named GlobalConfiguration entry into <see cref="SftpServerSettings" /> and
/// rejects an entry that cannot authenticate. Shared by every SFTP node, so the checks cannot
/// drift apart between the read and the write direction.
/// </summary>
internal static class SftpServerSettingsResolver
{
    // Every one of the three ends up as a millisecond count in an Int32: SemaphoreSlim.WaitAsync
    // and SSH.NET's own timeout properties both refuse anything beyond that. Caught here, the
    // operator reads which property is wrong; caught there, it is a bare ArgumentOutOfRangeException
    // - and for the connect timeout it arrives after the client was already created.
    private const int MaxTimeoutSeconds = int.MaxValue / 1000;

    /// <summary>
    /// Resolves and validates the settings behind a server configuration name.
    /// </summary>
    /// <param name="etlContext">The ETL context carrying the tenant global configuration</param>
    /// <param name="serverConfigurationName">Name of the global configuration entry</param>
    /// <param name="nodeContext">The node context, for error reporting</param>
    /// <returns>The resolved settings</returns>
    public static SftpServerSettings Resolve(IMeshEtlContext etlContext, string serverConfigurationName,
        INodeContext nodeContext)
    {
        if (!etlContext.GlobalConfiguration.IsDefined(serverConfigurationName))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(
                nodeContext, "ServerConfiguration", serverConfigurationName);
        }

        SftpServerSettings settings;
        try
        {
            settings = etlContext.GlobalConfiguration.GetValue<SftpServerSettings>(serverConfigurationName);
        }
        catch (Exception e)
        {
            // An entry whose payload does not fit the shape - a string where a number belongs,
            // a missing required attribute - surfaces from the deserializer as a bare message
            // about a JSON path. Naming the node and the entry turns that into something an
            // operator can act on without reading the adapter's source.
            throw MeshAdapterPipelineExecutionException.InvalidSftpServerConfiguration(
                nodeContext, serverConfigurationName, e);
        }

        if (string.IsNullOrWhiteSpace(settings.PrivateKey) && string.IsNullOrWhiteSpace(settings.Password))
        {
            throw MeshAdapterPipelineExecutionException.SftpAuthNotConfigured(nodeContext);
        }

        // Leaving the field out disables pinning deliberately, which is the compatibility
        // path. A present but blank value is a typo or an unset template variable, and
        // accepting it silently leaves an operator believing the server is pinned.
        if (settings.HostKeyFingerprint is not null && string.IsNullOrWhiteSpace(settings.HostKeyFingerprint))
        {
            throw MeshAdapterPipelineExecutionException.BlankHostKeyFingerprint(
                nodeContext, serverConfigurationName);
        }

        // Checked here rather than only when the session is opened: a node resolves its
        // settings before it does any work, while opening the session is the last step - the
        // upload node has already read a binary out of storage by then.
        if (settings.MaxConcurrentConnections <= 0)
        {
            throw MeshAdapterPipelineExecutionException.InvalidMaxConcurrentConnections(
                nodeContext, serverConfigurationName, settings.MaxConcurrentConnections);
        }

        // Zero means "leave it as it is", so a negative value is a mistake rather than a
        // synonym for it. Reading it as zero would leave an operator believing a limit applies.
        CheckTimeout(settings.ConnectTimeoutSeconds, nameof(settings.ConnectTimeoutSeconds));
        CheckTimeout(settings.OperationTimeoutSeconds, nameof(settings.OperationTimeoutSeconds));
        CheckTimeout(settings.WaitForSlotTimeoutSeconds, nameof(settings.WaitForSlotTimeoutSeconds));

        return settings;

        void CheckTimeout(int value, string propertyName)
        {
            if (value < 0)
            {
                throw MeshAdapterPipelineExecutionException.NegativeSftpTimeout(
                    nodeContext, serverConfigurationName, propertyName, value);
            }

            if (value > MaxTimeoutSeconds)
            {
                throw MeshAdapterPipelineExecutionException.SftpTimeoutTooLarge(
                    nodeContext, serverConfigurationName, propertyName, value, MaxTimeoutSeconds);
            }
        }
    }
}
