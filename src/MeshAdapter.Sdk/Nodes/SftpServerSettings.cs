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
    /// Upper bound of sessions one pipeline registration opens against the server at the same
    /// time. Two pipelines that use the same server configuration are counted separately, so
    /// the load a server actually sees is this value times the number of pipelines addressing
    /// it - the same arithmetic <c>MaxConcurrentEmails</c> has always had.
    /// </summary>
    public int MaxConcurrentConnections { get; init; } = 3;

    /// <summary>
    /// Seconds to wait for the connection to be established. Zero keeps SSH.NET's own default
    /// of 30 seconds; a negative value is rejected when the settings are resolved.
    /// </summary>
    public int ConnectTimeoutSeconds { get; init; }

    /// <summary>
    /// Seconds an individual operation - a listing, a download, an upload - may take. Zero
    /// keeps SSH.NET's default, which is no limit at all: a server that accepts the connection
    /// and then stalls holds the slot until the process restarts. Set it on a server whose
    /// transfers have a known upper bound.
    /// </summary>
    public int OperationTimeoutSeconds { get; init; }

    /// <summary>
    /// Seconds to wait for a free slot of <see cref="MaxConcurrentConnections" /> before
    /// failing. Zero waits indefinitely, which is the behaviour of every release before this
    /// option existed.
    /// </summary>
    public int WaitForSlotTimeoutSeconds { get; init; }

    /// <summary>
    /// SHA-256 fingerprint of the expected host key, non-padded base64 as printed by
    /// <c>ssh-keygen -lf</c>, with or without the <c>SHA256:</c> prefix. When set, a server
    /// presenting a different key is refused. When absent, any host key is accepted, which is
    /// how every release before this option behaved.
    /// </summary>
    public string? HostKeyFingerprint { get; init; }

    /// <summary>
    /// Redacted on purpose. A record prints every property, so a single interpolation into a
    /// log line or an exception message would ship the password, the private key and its
    /// passphrase to wherever logs are collected.
    /// </summary>
    public override string ToString()
    {
        return $"SftpServerSettings {{ Host = {Host}, Port = {Port}, Username = {Username}, "
               + $"Credentials = <redacted>, MaxConcurrentConnections = {MaxConcurrentConnections}, "
               + $"HostKeyFingerprint = {HostKeyFingerprint ?? "<none>"} }}";
    }
}
