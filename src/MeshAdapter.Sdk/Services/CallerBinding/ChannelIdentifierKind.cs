namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

/// <summary>
///     The kind of external identifier a channel trigger extracts from a sender, before it is
///     resolved against the verified-identifier directory (AB#5126). A channel-neutral mirror of the
///     identity model's <c>RtIdentifierKindEnum</c> (AB#5122): the adapter SDK cannot reference the
///     generated identity CK model, so the numeric keys are kept identical so the future directory
///     wiring maps across the service boundary by value.
/// </summary>
public enum ChannelIdentifierKind
{
    /// <summary>A phone number, e.g. the sender number of a Signal message (AB#5123). Mirrors <c>PhoneNumber</c>.</summary>
    PhoneNumber = 0,

    /// <summary>An e-mail address, e.g. the From of an inbound mail (AB#5125). Mirrors <c>EmailAddress</c>.</summary>
    EmailAddress = 1,

    /// <summary>An EntraID object id, e.g. the aadObjectId of a Teams activity sender (AB#5124). Mirrors <c>EntraIdObjectId</c>.</summary>
    EntraIdObjectId = 2,

    /// <summary>A client-certificate fingerprint. Mirrors <c>ClientCertificateFingerprint</c>.</summary>
    ClientCertificateFingerprint = 3
}
