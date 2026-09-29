using System.Text.Json.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

/// <summary>
///     Whether the pipeline confirmed that it imported what a mail trigger handed it (AB#5337,
///     shared by both mail channels since AB#5345).
/// </summary>
internal enum MailRunConfirmation
{
    /// <summary>No <c>successPath</c> is configured, so the outcome cannot be established.</summary>
    NotConfigured,

    /// <summary>The configured path resolved to the boolean <c>true</c>.</summary>
    Confirmed,

    /// <summary>The path is configured but did not resolve to <c>true</c> — absent, null or any other value.</summary>
    NotConfirmed
}

/// <summary>
///     The ONE per-run channel from a pipeline back to the trigger that started it: a boolean the
///     pipeline writes at a path the trigger was told to read (AB#5337). Shared by
///     <c>FromEmail@1</c> and <c>FromMicrosoftGraphEmail@1</c> since AB#5345, because both need
///     the same question answered — <b>was an inbox item created?</b> — before they may touch a
///     mailbox.
///     <para>
///     🔴 "The execution returned" is NOT a statement about the import. A pipeline ends normally
///     while a node reported an error and stopped its branch — <c>MakeHttpRequest@1</c>'s
///     <c>LogAndStop</c> is documented to do exactly that ("leaving the execution successful") —
///     and no per-node outcome reaches a trigger: <c>INodeContext.Error</c> only writes to the
///     pipeline log, <c>IEtlContext</c> carries no error state, and <c>PipelineExecutionStatus</c>
///     is <c>Completed</c> for everything that did not throw. The one channel back to the caller
///     is the returned data root, so the pipeline has to say so itself.
///     </para>
/// </summary>
internal static class MailSuccessPath
{
    /// <summary>
    ///     Reads the pipeline's own confirmation out of the data root <c>ExecuteAsync</c> returns.
    ///     <para>
    ///     Strict on purpose: only the boolean <c>true</c> confirms. Absent, null, <c>false</c>,
    ///     the string <c>"true"</c> and a number all read as "not confirmed", and every one of
    ///     those fails towards leaving the mail alone.
    ///     </para>
    ///     <para>
    ///     Reports only what the pipeline said; what to DO with an unconfigured path is
    ///     <see cref="IsPostProcessingAllowed" />'s decision.
    ///     </para>
    /// </summary>
    internal static MailRunConfirmation Evaluate(string? successPath, JsonNode? executionResult)
    {
        if (!TryParse(successPath, out var segments))
        {
            return MailRunConfirmation.NotConfigured;
        }

        var node = executionResult;
        foreach (var segment in segments)
        {
            if (node is not JsonObject obj || !obj.TryGetPropertyValue(segment, out node))
            {
                return MailRunConfirmation.NotConfirmed;
            }
        }

        return node is JsonValue value && value.TryGetValue<bool>(out var confirmed) && confirmed
            ? MailRunConfirmation.Confirmed
            : MailRunConfirmation.NotConfirmed;
    }

    /// <summary>
    ///     Parses a <c>successPath</c> into its property segments. Accepts a plain dotted path from
    ///     the data root with an optional <c>$.</c> prefix, and REJECTS anything that can select
    ///     more than one value — a confirmation that matches a set has no single truth value, and
    ///     quietly picking one would be the sort of guess this whole mechanism exists to remove.
    /// </summary>
    internal static bool TryParse(string? successPath, out IReadOnlyList<string> segments)
    {
        segments = [];

        if (string.IsNullOrWhiteSpace(successPath))
        {
            return false;
        }

        var trimmed = successPath.Trim();
        if (trimmed.StartsWith("$.", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }
        else if (trimmed.StartsWith('$'))
        {
            // "$" is the data root itself, never a boolean flag.
            return false;
        }

        var parsed = trimmed.Split('.');
        foreach (var segment in parsed)
        {
            if (segment.Length == 0 || segment.AsSpan().IndexOfAny("[]*?@$ ") >= 0)
            {
                return false;
            }
        }

        segments = parsed;
        return true;
    }

    /// <summary>
    ///     Whether a run that CAME BACK may have its message post-processed. Two of the three
    ///     levels live here; the third is the absence of a call:
    ///     <list type="number">
    ///         <item>
    ///         The run <b>threw</b> ⇒ never. The poll loop never reaches this method, which is the
    ///         actual protection: whatever is configured, a failed run leaves the mailbox alone.
    ///         </item>
    ///         <item>
    ///         No <c>successPath</c> configured ⇒ <b>yes</b>. Deliberately the pre-AB#5337 /
    ///         pre-AB#5345 behaviour, and the reason an already deployed pipeline keeps behaving
    ///         exactly as it did: a stricter default would stop every deployed mail trigger from
    ///         marking, moving or deleting anything — a certain fleet-wide regression traded for
    ///         one edge case.
    ///         </item>
    ///         <item>
    ///         <c>successPath</c> configured ⇒ only when the pipeline confirmed. The strict promise
    ///         stays exactly where somebody asked for it.
    ///         </item>
    ///     </list>
    /// </summary>
    internal static bool IsPostProcessingAllowed(MailRunConfirmation confirmation)
    {
        return confirmation is MailRunConfirmation.Confirmed or MailRunConfirmation.NotConfigured;
    }
}
