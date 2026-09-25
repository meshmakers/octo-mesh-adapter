using System.Text.Json;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Reads the post-processing mode out of a settings entity, for both mail channels (AB#5372).
/// <para>
/// It exists for ONE value that must not travel the reader's ordinary route:
/// <c>ConfigurationSettingsReader.ReadEnum</c> answers <c>null</c> for a name the enum does not
/// know, which means "not configured" and hands the decision back to the node property — the right
/// rule for a typo, and the wrong one for <c>None</c>. <c>None</c> was a real, storable mode until
/// AB#5372 removed it, so a settings entity out there may still carry it; read as "not configured"
/// it would silently be replaced by the DERIVED mode, which on the accounting seed
/// (<c>markAsRead: true</c>) means the node starts flagging mail in a mailbox whose operator had
/// asked it to change nothing. Loud beats silent for exactly one value.
/// </para>
/// </summary>
internal static class MailPostProcessingModeSetting
{
    /// <summary>
    ///     The removed member's name, matched case-insensitively — the reader itself is
    ///     case-insensitive, so <c>none</c> out of a hand-edited entity has to be caught too.
    /// </summary>
    private const string RemovedModeName = "None";

    /// <summary>
    ///     The mode the settings entity configures, <c>null</c> when it configures none (absent,
    ///     null, blank, numeric or an unknown name — see <c>ConfigurationSettingsReader.ReadEnum</c>).
    /// </summary>
    /// <exception cref="Exception">
    ///     The entity stores the removed mode <c>None</c>. Thrown rather than ignored, and thrown
    ///     from the settings overlay, which both nodes resolve in <c>StartAsync</c> — so an operator
    ///     hears it when the pipeline is deployed.
    /// </exception>
    internal static MailPostProcessingMode? Read(JsonElement? attributes, string? attributeName,
        string nodeType, string suggestion)
    {
        var raw = ConfigurationSettingsReader.ReadString(attributes, attributeName);
        if (raw is not null &&
            string.Equals(raw.Trim(), RemovedModeName, StringComparison.OrdinalIgnoreCase))
        {
            throw MeshAdapterPipelineExecutionException.MailPostProcessingModeRemoved(
                nodeType, attributeName!, raw.Trim(), suggestion);
        }

        return ConfigurationSettingsReader.ReadEnum<MailPostProcessingMode>(attributes, attributeName);
    }
}
