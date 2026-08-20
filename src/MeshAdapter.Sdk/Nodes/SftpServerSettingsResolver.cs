using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// Resolves the named GlobalConfiguration entry into <see cref="SftpServerSettings" /> and
/// rejects an entry that cannot authenticate. Shared by every SFTP node, so the checks cannot
/// drift apart between the read and the write direction.
/// </summary>
public static class SftpServerSettingsResolver
{
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

        var settings = etlContext.GlobalConfiguration.GetValue<SftpServerSettings>(serverConfigurationName);

        if (string.IsNullOrWhiteSpace(settings.PrivateKey) && string.IsNullOrWhiteSpace(settings.Password))
        {
            throw MeshAdapterPipelineExecutionException.SftpAuthNotConfigured(nodeContext);
        }

        return settings;
    }
}
