using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Microsoft.Extensions.Hosting;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services;

/// <summary>
/// Registers the adapter's key ring as the read-state classifier of the pipeline serialiser (AB#5538).
/// </summary>
/// <remarks>
/// <para>
/// Every node that reads entities (<c>GetRtEntities*</c>, <c>GetRtEntityById(s)</c>, association and graph
/// reads, <c>FromWatchRtEntity</c>, <c>BackfillFromRtEntity</c>, ...) puts them into the data context
/// through <see cref="SystemTextJsonOptions" />. With this registration a Secret value is written as a
/// marker that matches what <c>RevealSecret@1</c> would see: <c>{"isSet": true|false}</c>, or
/// <c>{"isSet": false, "keyMissing": true}</c> for a stored value whose key id is not in the ring.
/// </para>
/// <para>
/// The marker never contains the plaintext or the envelope; copying it back into
/// <c>CreateUpdateInfo@1</c> / <c>ApplyChanges@2</c> means "unchanged".
/// </para>
/// </remarks>
public sealed class PipelineSecretMarkerRegistration : IHostedService
{
    private readonly Func<RtSecretValue, SecretReadInfo> _classifier;

    /// <summary>
    /// Creates the registration for <paramref name="secretAttributeProtector" />.
    /// </summary>
    public PipelineSecretMarkerRegistration(ISecretAttributeProtector secretAttributeProtector)
    {
        ArgumentNullException.ThrowIfNull(secretAttributeProtector);
        _classifier = value => secretAttributeProtector.DescribeSecret(value);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        PipelineSecretValues.SetReadStateClassifier(_classifier);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        PipelineSecretValues.ResetReadStateClassifier(_classifier);
        return Task.CompletedTask;
    }
}
