using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

/// <summary>
///     The sender of a channel message, reduced to what the verified-identifier directory needs to
///     resolve it: the kind of identifier, its normalized value, and the <b>message-trust</b> the
///     channel could vouch for the message itself (AB#5126).
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>Extension point for the per-channel resolution WIs.</b> In AB#5126 the channel
///         triggers fill this in with the obvious raw identifier (phone number, e-mail address, Teams
///         aadObjectId) and a conservative <see cref="MessageTrust" />. The sibling WIs refine both:
///         <list type="bullet">
///             <item>AB#5124 — Teams: map/validate the EntraID oid and set its message trust.</item>
///             <item>AB#5123 — phone: OTP-enrolment and the phone-number normalization / message trust.</item>
///             <item>AB#5125 — e-mail: derive <see cref="MessageTrust" /> from the DKIM / Authentication-Results verdict.</item>
///         </list>
///         The directory itself takes <c>effective = min(enrollmentTrust, messageTrust)</c> — this
///         value is the <b>message</b> dimension only.
///     </para>
/// </remarks>
/// <param name="Kind">The kind of identifier.</param>
/// <param name="Value">The normalized identifier value (e.g. "+4366012345678", "user@example.com").</param>
/// <param name="MessageTrust">
///     How strongly the channel authenticated <b>this message</b> as coming from
///     <paramref name="Value" />. Conservative (<see cref="CallerTrustLevel.None" /> / <see cref="CallerTrustLevel.Weak" />)
///     until the per-channel WI computes the real verdict.
/// </param>
public sealed record ChannelSender(
    ChannelIdentifierKind Kind,
    string Value,
    CallerTrustLevel MessageTrust);
