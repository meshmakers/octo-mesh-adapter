using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// Resolves the named GlobalConfiguration entry into <see cref="HttpApiSettings" />. A configured
/// entry that is missing or half-filled fails loudly: it is a configuration mistake, and answering
/// it with a log would leave an operator with a green execution that called nothing.
/// </summary>
internal static class HttpApiSettingsResolver
{
    /// <summary>
    /// Resolves and validates the settings behind an API configuration name.
    /// </summary>
    /// <param name="etlContext">The ETL context carrying the tenant global configuration</param>
    /// <param name="apiConfigurationName">Name of the global configuration entry</param>
    /// <param name="nodeContext">The node context, for error reporting</param>
    /// <returns>The resolved settings</returns>
    public static HttpApiSettings Resolve(IMeshEtlContext etlContext, string apiConfigurationName,
        INodeContext nodeContext)
    {
        if (!etlContext.GlobalConfiguration.IsDefined(apiConfigurationName))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(
                nodeContext, "ApiConfiguration", apiConfigurationName);
        }

        HttpApiSettings? settings;
        try
        {
            settings = etlContext.GlobalConfiguration.GetValue<HttpApiSettings>(apiConfigurationName);
        }
        catch (Exception e)
        {
            throw MeshAdapterPipelineExecutionException.InvalidHttpApiConfiguration(
                nodeContext, apiConfigurationName, e);
        }

        // A ConfigurationValue of literal null deserializes to null despite the non-null contract.
        if (settings is null || string.IsNullOrWhiteSpace(settings.BaseUrl) ||
            string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw MeshAdapterPipelineExecutionException.IncompleteHttpApiConfiguration(
                nodeContext, apiConfigurationName);
        }

        // Present is not the same as usable. A base without a scheme survives the blank check and
        // then fails inside the send as a bare InvalidOperationException about a relative URI,
        // which the node reports and swallows - leaving a green execution that fetched nothing.
        // Checked here, it is what it is: a configuration mistake.
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw MeshAdapterPipelineExecutionException.HttpApiBaseUrlNotAbsolute(
                nodeContext, apiConfigurationName, settings.BaseUrl);
        }

        return settings;
    }
}
