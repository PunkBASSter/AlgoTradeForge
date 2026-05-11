using System.Text.Json.Serialization;

namespace AlgoTradeForge.Domain.Strategy.Subscriptions;

/// <summary>
/// Polymorphic wire/command shape for a data-feed subscription. Concrete subtypes
/// (<see cref="TimeBarSubscription"/>, <see cref="AltBarSubscription"/>,
/// <see cref="TickSubscription"/>, <see cref="SideFeedSubscription"/>) carry kind-specific
/// payloads and serialize via a <c>"kind"</c> discriminator. The type hierarchy IS the
/// discriminator — no <c>Kind</c> property; callers switch via pattern matching.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TimeBarSubscription), typeDiscriminator: "TimeBar")]
[JsonDerivedType(typeof(AltBarSubscription), typeDiscriminator: "AltBar")]
[JsonDerivedType(typeof(TickSubscription), typeDiscriminator: "Tick")]
[JsonDerivedType(typeof(SideFeedSubscription), typeDiscriminator: "Side")]
public abstract record DataFeedSubscription(string AssetName, string Exchange, DataFeedRole Role);
