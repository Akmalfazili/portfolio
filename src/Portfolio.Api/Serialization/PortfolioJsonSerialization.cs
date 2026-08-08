using System.Text.Json;
using System.Text.Json.Serialization;

namespace Portfolio.Api.Serialization;

/// <summary>
/// The single definition of how this process encodes JSON, applied to <b>both</b> serializers it
/// runs: the minimal-API response serializer (<c>ConfigureHttpJsonOptions</c>) and SignalR's own
/// protocol serializer (<c>AddSignalR().AddJsonProtocol(...)</c>).
///
/// <para><b>Why this exists (D7).</b> SignalR does not inherit <c>ConfigureHttpJsonOptions</c> — it
/// has a completely separate serializer. Phase 5 found that the hard way: <c>QuoteProviderKind</c>
/// crossed the hub as <c>"source":0</c> while REST sent <c>"source":"TwelveData"</c>, one logical
/// field encoded two ways in payloads the frontend merges. That bug was fixed by registering the
/// converter twice, which left the two configurations agreeing only <i>by coincidence</i> — both
/// happened to default to camelCase, and nothing anywhere asserted it. Changing a naming policy on
/// one side, or adding a second protocol, would have silently reintroduced the same class of bug.
/// Now there is one place to change and one place to get it wrong.</para>
///
/// <para>Every setting here must be one both pipelines can honour, because the frontend parses the
/// REST payload and the hub payload with the same TypeScript types.</para>
/// </summary>
public static class PortfolioJsonSerialization
{
    /// <summary>
    /// Applies this API's JSON contract to <paramref name="options"/> and returns it. Callers pass
    /// the options object owned by each pipeline, so neither pipeline holds its own opinion.
    /// </summary>
    public static JsonSerializerOptions Apply(JsonSerializerOptions options)
    {
        // Set explicitly rather than relying on JsonSerializerDefaults.Web, which both pipelines
        // happen to default to today. The point of D7 is that the agreement is *stated*, not
        // inherited from two defaults that could drift apart independently.
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;

        // Enums serialize as their names ("Buy", "Crypto", "TwelveData") rather than raw ints —
        // friendlier for API consumers, and immune to an enum being reordered. The Angular client
        // types these as string unions (src/Portfolio.Web/src/app/core/api/models.ts).
        options.Converters.Add(new JsonStringEnumConverter());

        return options;
    }
}
