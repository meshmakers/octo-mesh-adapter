using System.Text.Json.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;

/// <summary>
/// Keeps the request envelope out of an HTTP route's response (AB#5674).
/// </summary>
/// <remarks>
/// The response of a route is the final pipeline data root, and the same data root starts out as
/// the request: path, method, caller principal, request headers, body, query and uploaded files.
/// A pipeline that does not shape its result with <c>Project@1 clear: true</c> therefore echoed
/// the caller's own request - and with it roles, e-mail addresses and, on a trigger that receives
/// credential headers, an <c>Authorization</c> value - back to every caller (AB#5627).
///
/// The guard enforces two rules, in one place for every trigger that goes through
/// <see cref="HttpRequestService" />:
/// <list type="number">
/// <item>On an object response the reserved request keys are removed at the top level.</item>
/// <item>Anywhere in the response, a value that is a whole copy of this request's <c>headers</c> or
/// <c>principal</c> object is removed (a copy into another field, or into every element of a
/// ForEach result array).</item>
/// </list>
/// A single field the author maps on purpose (e.g. <c>$.principal.email</c> into
/// <c>$.createdBy</c>) is the author's explicit output and stays. This is a safeguard against the
/// accidental echo, not a defence against an author who may write pipelines. There is deliberately
/// no switch that turns it off; the pipeline debugger shows the data per node.
/// </remarks>
internal static class HttpResponseGuard
{
    /// <summary>The keys <see cref="HttpRequestService" /> injects into the data root of a request.</summary>
    internal static readonly string[] ReservedRequestKeys =
    [
        "path", "method", "principal", "headers", "body", "bodyEncoding", "query", "files", "formData",
        "contentType", "contentLength"
    ];

    /// <summary>
    /// Snapshot of the request objects a whole copy is recognised by. Taken before the pipeline
    /// runs, because a node may change the data root it is handed.
    /// </summary>
    internal readonly record struct RequestEnvelope(JsonNode? Headers, JsonNode? Principal)
    {
        public static RequestEnvelope Capture(JsonObject input) =>
            new(Distinctive(input["headers"]), Distinctive(input["principal"]));

        // An empty object equals every unrelated empty object (e.g. "metadata": {}) in a response,
        // so it is no recognisable copy of anything.
        private static JsonNode? Distinctive(JsonNode? node) =>
            node is JsonObject { Count: > 0 } ? node.DeepClone() : null;
    }

    /// <summary>
    /// Removes the request envelope from <paramref name="response" /> in place and returns what was
    /// removed - key names and locations only, never values. Scalar responses and
    /// <c>null</c> are left alone; an array response is only scanned for whole copies.
    /// </summary>
    internal static List<string> Apply(JsonNode? response, RequestEnvelope envelope)
    {
        var removed = new List<string>();
        if (response is JsonArray rootArray)
        {
            // A ForEach result can be the response itself: its elements may hold whole copies.
            RemoveWholeCopies(rootArray, "$", envelope, removed);
            return removed;
        }

        if (response is not JsonObject root)
        {
            return removed;
        }

        foreach (var key in ReservedRequestKeys)
        {
            if (root.Remove(key))
            {
                removed.Add(key);
            }
        }

        RemoveWholeCopies(root, "$", envelope, removed);
        return removed;
    }

    private static void RemoveWholeCopies(JsonNode node, string location, RequestEnvelope envelope,
        List<string> removed)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj.ToList())
                {
                    if (child == null)
                    {
                        continue;
                    }

                    var childLocation = $"{location}.{name}";
                    var copyOf = CopyOf(child, envelope);
                    if (copyOf != null)
                    {
                        obj.Remove(name);
                        removed.Add($"{childLocation} (copy of {copyOf})");
                        continue;
                    }

                    RemoveWholeCopies(child, childLocation, envelope, removed);
                }

                break;
            case JsonArray array:
                for (var i = array.Count - 1; i >= 0; i--)
                {
                    var child = array[i];
                    if (child == null)
                    {
                        continue;
                    }

                    var childLocation = $"{location}[{i}]";
                    var copyOf = CopyOf(child, envelope);
                    if (copyOf != null)
                    {
                        array.RemoveAt(i);
                        removed.Add($"{childLocation} (copy of {copyOf})");
                        continue;
                    }

                    RemoveWholeCopies(child, childLocation, envelope, removed);
                }

                break;
        }
    }

    private static string? CopyOf(JsonNode candidate, RequestEnvelope envelope)
    {
        if (candidate is not JsonObject)
        {
            return null;
        }

        if (envelope.Headers != null && JsonNode.DeepEquals(candidate, envelope.Headers))
        {
            return "headers";
        }

        if (envelope.Principal != null && JsonNode.DeepEquals(candidate, envelope.Principal))
        {
            return "principal";
        }

        return null;
    }
}
