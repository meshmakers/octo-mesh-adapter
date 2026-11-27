using System.Globalization;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Common;

/// <summary>
/// How often an SFTP node (<c>SftpList@1</c>, <c>SftpDownload@1</c>, <c>SftpUpload@1</c>,
/// <c>SftpDelete@1</c>) tries to open its session when the server refuses or drops the connection
/// attempt, and how long it waits in between. Absent means a single attempt, as before the option.
/// <para />
/// What the SSH client's <c>Connect</c> raises is repeated - authentication, subsystem or channel
/// refused, connection refused, reset or dropped, connect timeout - except a refused host key and a
/// blank host; never a cancellation, unreadable key material or a failure after the session is open.
/// A permanent mistake that only shows while connecting (a host that does not resolve, a wrong
/// password) is repeated too, at the full waits: it cannot be told from a passing refusal. Every
/// attempt that reaches the server's authentication is a login, which a lockout policy counts.
/// </summary>
/// <remarks>
/// Shaped like the retry options of <c>MakeHttpRequest@1</c> (nullable members with constant
/// defaults, as an explicit null overwrites an initializer). Unlike there, a value out of range or a
/// base that is not finite is refused when the definition is bound, like the other SFTP options.
/// </remarks>
public record SftpConnectRetryOptions
{
    /// <summary>The <see cref="MaxAttempts" /> an unset property resolves to.</summary>
    public const int DefaultMaxAttempts = 1;

    /// <summary>The <see cref="BackoffBaseSeconds" /> an unset property resolves to.</summary>
    public const double DefaultBackoffBaseSeconds = 1;

    /// <summary>Upper bound on <see cref="MaxAttempts" />: beyond it a server is down, not refusing for a moment.</summary>
    public const int MaxAllowedAttempts = 10;

    /// <summary>Upper bound on a single wait; doubling alone would reach waits of hours.</summary>
    public const int MaxBackoffSeconds = 60;

    private int? _maxAttempts = DefaultMaxAttempts;
    private double? _backoffBaseSeconds = DefaultBackoffBaseSeconds;

    /// <summary>Total connection attempts of one node execution, 1 to 10; 1 means no retry.</summary>
    public int? MaxAttempts
    {
        get => _maxAttempts;
        set
        {
            if (value is { } attempts && (attempts < 1 || attempts > MaxAllowedAttempts))
            {
                throw new ArgumentException(
                    $"connectRetry.maxAttempts is {attempts.ToString(CultureInfo.InvariantCulture)}. Use 1 (a single attempt, no retry) to {MaxAllowedAttempts}.",
                    nameof(value));
            }

            _maxAttempts = value;
        }
    }

    /// <summary>
    /// The wait after attempt n is this times 2^(n - 1) seconds, at most
    /// <see cref="MaxBackoffSeconds" />; 0 retries without waiting. With 10: 10 s, 20 s, 40 s, 60 s.
    /// </summary>
    public double? BackoffBaseSeconds
    {
        get => _backoffBaseSeconds;
        set
        {
            if (value is { } seconds && (!double.IsFinite(seconds) || seconds < 0))
            {
                throw new ArgumentException(
                    $"connectRetry.backoffBaseSeconds is {seconds.ToString(CultureInfo.InvariantCulture)}. Use zero to retry without waiting or a positive number of seconds.",
                    nameof(value));
            }

            _backoffBaseSeconds = value;
        }
    }
}
