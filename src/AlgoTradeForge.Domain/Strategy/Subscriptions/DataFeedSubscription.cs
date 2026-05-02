using System.Text.Json.Serialization;

namespace AlgoTradeForge.Domain.Strategy.Subscriptions;

/// <summary>
/// Polymorphic wire/command shape for a data-feed subscription (TRD §9.2). The four
/// concrete subtypes (<see cref="TimeBarSubscription"/>, <see cref="AltBarSubscription"/>,
/// <see cref="TickSubscription"/>, <see cref="SideFeedSubscription"/>) carry kind-specific
/// payloads (TimeFrame / FeedId / nothing / FeedId) and serialize with a <c>"kind"</c>
/// discriminator via System.Text.Json's built-in polymorphism (no new package required).
/// </summary>
/// <remarks>
/// <para>
/// This is the <em>unresolved</em> shape — strings for asset and exchange, no <c>Asset</c>
/// object, no <c>TimeSeries</c> binding. <c>BacktestPreparer</c> resolves it into the
/// strategy-side <see cref="DataSubscription"/> (which keeps <c>Asset</c> + <c>TimeFrame</c>
/// + <c>FeedKey</c>) downstream. Coexistence is intentional: replacing the strategy-side
/// type would explode blast radius into every strategy's <c>OnContextUpdated</c> signature
/// for zero domain benefit.
/// </para>
/// <para>
/// No <c>Kind</c> property — the type hierarchy IS the discriminator. Callers that need to
/// switch on kind use C# pattern matching (<c>sub is TimeBarSubscription tb</c>) which also
/// surfaces the typed payload. The wire's <c>"kind"</c> field is owned exclusively by
/// <c>[JsonPolymorphic]</c>.
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TimeBarSubscription), typeDiscriminator: "TimeBar")]
[JsonDerivedType(typeof(AltBarSubscription), typeDiscriminator: "AltBar")]
[JsonDerivedType(typeof(TickSubscription), typeDiscriminator: "Tick")]
[JsonDerivedType(typeof(SideFeedSubscription), typeDiscriminator: "Side")]
public abstract record DataFeedSubscription(string AssetName, string Exchange, DataFeedRole Role);
