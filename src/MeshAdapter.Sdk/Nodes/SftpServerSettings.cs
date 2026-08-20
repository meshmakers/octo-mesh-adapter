namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// Shape of the tenant GlobalConfiguration entry that the SFTP nodes reference by name.
/// </summary>
public sealed record SftpServerSettings
{
    /// <summary>
    /// Host name or address of the SFTP server
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// Port of the SFTP server
    /// </summary>
    public int Port { get; init; } = 22;

    /// <summary>
    /// User name to authenticate with
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// Password authentication; alternative to <see cref="PrivateKey" />
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Private key in OpenSSH format; alternative to <see cref="Password" />
    /// </summary>
    public string? PrivateKey { get; init; }

    /// <summary>
    /// Passphrase protecting <see cref="PrivateKey" />, if any
    /// </summary>
    public string? PrivateKeyPassphrase { get; init; }

    /// <summary>
    /// Upper bound of sessions this process opens against the server at the same time
    /// </summary>
    public int MaxConcurrentConnections { get; init; } = 3;

    /// <summary>
    /// SHA-256 fingerprint of the expected host key, non-padded base64 as printed by
    /// <c>ssh-keygen -lf</c>, with or without the <c>SHA256:</c> prefix. When set, a server
    /// presenting a different key is refused. When absent, any host key is accepted, which is
    /// how every release before this option behaved.
    /// </summary>
    public string? HostKeyFingerprint { get; init; }
}
