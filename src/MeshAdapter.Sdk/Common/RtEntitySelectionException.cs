using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Common;

/// <summary>
///     The stable <see cref="RtEntitySelectionException.Code" /> values of <c>GetRtEntitiesByType@1</c> failures
///     (AB#6933). Language-neutral, safe to match on in a client.
/// </summary>
public static class RtEntitySelectionCodes
{
    /// <summary>The node configuration is invalid; <see cref="RtEntitySelectionException.Reason" /> names the case.</summary>
    public const string NodeConfigurationInvalid = "NODE_CONFIGURATION_INVALID";

    /// <summary>
    ///     The engine's interface query refused the request (engine message number 7019); the engine reason token and
    ///     parameters are passed through unchanged.
    /// </summary>
    public const string InterfaceQuery = "INTERFACE_QUERY";
}

/// <summary>
///     The <see cref="RtEntitySelectionException.Reason" /> tokens of <see cref="RtEntitySelectionCodes.NodeConfigurationInvalid" />.
/// </summary>
public static class RtEntitySelectionReasons
{
    /// <summary><c>ckInterfaceId</c> is set together with <c>ckTypeId</c> or <c>ckTypeIdPath</c>. Parameters: <c>node</c>.</summary>
    public const string CkTypeAndCkInterfaceBothSet = "CK_TYPE_AND_CK_INTERFACE_BOTH_SET";

    /// <summary>Neither a type nor an interface is configured. Parameters: <c>node</c>.</summary>
    public const string CkTypeOrCkInterfaceRequired = "CK_TYPE_OR_CK_INTERFACE_REQUIRED";

    /// <summary><c>ckInterfaceId</c> is not a valid runtime CK id. Parameters: <c>node</c>, <c>ckInterfaceId</c>.</summary>
    public const string CkInterfaceIdInvalid = "CK_INTERFACE_ID_INVALID";

    /// <summary>An interface query needs <c>take</c>. Parameters: <c>node</c>.</summary>
    public const string TakeRequiredForInterface = "TAKE_REQUIRED_FOR_INTERFACE";

    /// <summary><c>skip</c> or <c>take</c> is negative. Parameters: <c>node</c>, <c>skip</c>, <c>take</c>.</summary>
    public const string PagingNegative = "PAGING_NEGATIVE";
}

/// <summary>
///     A <c>GetRtEntitiesByType@1</c> run that selects by CK interface failed (AB#6933): either the node
///     configuration is invalid or the engine's interface query refused the request.
/// </summary>
/// <remarks>
///     The structured members (<see cref="Code" />, <see cref="Reason" />, <see cref="MessageNumber" />,
///     <see cref="Parameters" />) are the contract; the text of <see cref="Exception.Message" /> is English for logs and
///     may be reworded. For <see cref="RtEntitySelectionCodes.InterfaceQuery" /> the number (7019), reason token and
///     parameters are those of the engine's <c>InterfaceQueryException</c> and travel unchanged; the original exception
///     is the inner exception.
/// </remarks>
public sealed class RtEntitySelectionException : PipelineExecutionException
{
    /// <summary>
    ///     Creates the exception.
    /// </summary>
    /// <param name="code">One of <see cref="RtEntitySelectionCodes" /></param>
    /// <param name="reason">The reason token</param>
    /// <param name="messageNumber">The engine message number, null for a configuration failure</param>
    /// <param name="parameters">Machine-readable parameters by name</param>
    /// <param name="message">English text for logs</param>
    /// <param name="inner">The engine exception, if any</param>
    public RtEntitySelectionException(string code, string reason, int? messageNumber,
        IReadOnlyDictionary<string, string> parameters, string message, Exception? inner = null)
        : base(message, inner!)
    {
        Code = code;
        Reason = reason;
        MessageNumber = messageNumber;
        Parameters = parameters;
    }

    /// <summary>The stable error code, one of <see cref="RtEntitySelectionCodes" />.</summary>
    public string Code { get; }

    /// <summary>The stable reason token (the engine's token for <see cref="RtEntitySelectionCodes.InterfaceQuery" />).</summary>
    public string Reason { get; }

    /// <summary>The engine message number (7019) for an interface query refusal, otherwise null.</summary>
    public int? MessageNumber { get; }

    /// <summary>The machine-readable parameters by name, invariant culture.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }
}
