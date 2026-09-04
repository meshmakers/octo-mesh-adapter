namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

/// <summary>
///     A per-kind verified-caller directory leaf (AB#5123): an <see cref="IVerifiedCallerDirectory" />
///     that owns one (or a few) <see cref="ChannelIdentifierKind" />s and resolves only those. The
///     <see cref="CompositeVerifiedCallerDirectory" /> aggregates every leaf and dispatches a sender to
///     the leaf that owns its kind, so the EntraID (AB#5124), phone (AB#5123) and — later — e-mail
///     (AB#5125) directories COEXIST behind the single <see cref="IVerifiedCallerDirectory" /> the
///     channel binder consumes. A new channel adds one leaf and registers it; nothing else changes.
/// </summary>
internal interface IKindVerifiedCallerDirectory : IVerifiedCallerDirectory
{
    /// <summary>Whether this leaf owns (and can resolve) senders of the given <paramref name="kind" />.</summary>
    bool Owns(ChannelIdentifierKind kind);
}
