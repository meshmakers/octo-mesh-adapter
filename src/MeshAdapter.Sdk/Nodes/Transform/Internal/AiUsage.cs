using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

/// <summary>
///     List prices of a Claude model in USD per million tokens (AB#8658). Cache writes use the 5-minute
///     TTL rate, which is what the node's <c>ephemeral</c> breakpoints write.
/// </summary>
internal sealed record AnthropicModelPrice(
    decimal InputPerMTok,
    decimal OutputPerMTok,
    decimal CacheReadPerMTok,
    decimal CacheWrite5mPerMTok);

/// <summary>
///     Built-in Anthropic list prices for the cost <b>estimate</b> of <c>AnthropicAiQuery@1</c> (AB#8658).
///     First-party API rates as of 2026-10 (input/output per MTok; cache reads as published per model, else
///     0.1× input; cache writes 1.25× input). Partner platforms (Bedrock, Vertex) price differently, and
///     tiered long-context prices are not modelled. An unknown model yields no estimate rather than a
///     wrong one. Update the table when Anthropic changes prices or ships a model.
/// </summary>
internal static class AnthropicPricing
{
    /// <summary>The date of the price list, written next to every estimate.</summary>
    internal const string PriceListDate = "2026-10";

    // Longest prefix wins, so a dated snapshot id ("claude-sonnet-4-6-2026…") maps to its family.
    private static readonly (string Prefix, AnthropicModelPrice Price)[] Prices =
    [
        ("claude-fable-5-1", Price(10m, 50m, 0.25m)),
        ("claude-mythos-5-1", Price(10m, 50m, 0.25m)),
        ("claude-fable-5", Price(10m, 50m, 1.00m)),
        ("claude-mythos-5", Price(10m, 50m, 1.00m)),
        ("claude-opus-5-5", Price(4m, 20m, 0.20m)),
        ("claude-opus-5", Price(5m, 25m)),
        ("claude-opus-4-8", Price(5m, 25m)),
        ("claude-opus-4-7", Price(5m, 25m)),
        ("claude-opus-4-6", Price(5m, 25m)),
        ("claude-sonnet-5-5", Price(2m, 10m, 0.20m)),
        ("claude-sonnet-5", Price(2m, 10m)),
        ("claude-sonnet-4-6", Price(3m, 15m)),
        ("claude-haiku-5-5", Price(0.10m, 0.50m)),
        ("claude-haiku-4-5", Price(1m, 5m))
    ];

    private static AnthropicModelPrice Price(decimal input, decimal output, decimal? cacheRead = null) =>
        new(input, output, cacheRead ?? input * 0.1m, input * 1.25m);

    internal static AnthropicModelPrice? Find(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return Prices
            .Where(p => model.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Prefix.Length)
            .Select(p => p.Price)
            .FirstOrDefault();
    }
}

/// <summary>
///     Sums the <c>usage</c> blocks of every Messages API call one node execution makes — tool rounds and
///     JSON repair calls included (AB#8658) — and renders them for the data context.
/// </summary>
internal sealed class AiUsageAccumulator
{
    public int Calls { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long CacheReadInputTokens { get; private set; }
    public long CacheCreationInputTokens { get; private set; }

    /// <summary>Adds the <c>usage</c> of one API response; a response without usage still counts as a call.</summary>
    public void Add(JsonElement apiResponse)
    {
        Calls++;
        if (apiResponse.ValueKind != JsonValueKind.Object ||
            !apiResponse.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        InputTokens += Read(usage, "input_tokens");
        OutputTokens += Read(usage, "output_tokens");
        CacheReadInputTokens += Read(usage, "cache_read_input_tokens");
        CacheCreationInputTokens += Read(usage, "cache_creation_input_tokens");
    }

    private static long Read(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : 0;

    /// <summary>
    ///     Cost estimate in USD at list prices, or <c>null</c> for a model the price table does not know.
    ///     <c>input_tokens</c> is the uncached remainder, so the three input fields are priced separately.
    /// </summary>
    public decimal? EstimateCostUsd(string? model)
    {
        if (AnthropicPricing.Find(model) is not { } price)
        {
            return null;
        }

        var cost = (InputTokens * price.InputPerMTok
                    + OutputTokens * price.OutputPerMTok
                    + CacheReadInputTokens * price.CacheReadPerMTok
                    + CacheCreationInputTokens * price.CacheWrite5mPerMTok) / 1_000_000m;
        return Math.Round(cost, 6);
    }

    public JsonObject ToJson(string? model)
    {
        var cost = EstimateCostUsd(model);
        return new JsonObject
        {
            ["model"] = model,
            ["calls"] = Calls,
            ["inputTokens"] = InputTokens,
            ["outputTokens"] = OutputTokens,
            ["cacheReadInputTokens"] = CacheReadInputTokens,
            ["cacheCreationInputTokens"] = CacheCreationInputTokens,
            ["estimatedCostUsd"] = cost,
            ["priceListDate"] = cost.HasValue ? AnthropicPricing.PriceListDate : null
        };
    }
}
