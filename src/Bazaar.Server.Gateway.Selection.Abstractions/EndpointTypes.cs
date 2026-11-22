namespace Bazaar.Server.Gateway.Selection;

/// <summary>An endpoint's identity: the provider and the model it serves. Written <c>provider:model</c>.</summary>
public readonly record struct EndpointKey(string ProviderId, string ModelId)
{
    public override string ToString() => $"{ProviderId}:{ModelId}";
}

/// <summary>What a call needs, and what an endpoint offers.</summary>
[Flags]
public enum EndpointCapabilities
{
    None = 0,
    Tools = 1,
    Vision = 2,
    Thinking = 4,
    PromptCaching = 8,
    StructuredOutput = 16,
}

/// <summary>Display prices in one currency unit per million tokens. A ranking signal only: money budgets are rejected, so a price never blocks a call.</summary>
public sealed record EndpointPrice(double InputPerMTok, double OutputPerMTok, double CacheReadPerMTok = 0);

/// <summary>
/// One endpoint the selector may choose. The caller (the gateway) builds this from its provider and model records, after
/// policy has fixed the allowed set. <c>Tier</c> 0 is the best tier; <c>Identity</c> is free text such as <c>native</c>,
/// <c>pooled</c> or <c>subscription</c>. <c>Quality</c> is an operator prior from 0 to 1, optionally per task class.
/// </summary>
public sealed record Endpoint(EndpointKey Key)
{
    public int Tier { get; init; }

    public string Identity { get; init; } = "native";

    public EndpointCapabilities Capabilities { get; init; } = EndpointCapabilities.None;

    public int? ContextWindowTokens { get; init; }

    public EndpointPrice? Price { get; init; }

    /// <summary>Operator quality prior, 0 to 1. Default 0.5 (no opinion).</summary>
    public double Quality { get; init; } = 0.5;

    /// <summary>Quality prior per task class, overriding <see cref="Quality"/> for that class.</summary>
    public IReadOnlyDictionary<string, double>? QualityByTask { get; init; }

    /// <summary>Relative share for the weighted strategies. Must be positive.</summary>
    public double Weight { get; init; } = 1;

    /// <summary>Concurrent calls the endpoint should take; past it the in-flight penalty grows. Null = no stated cap.</summary>
    public int? SoftConcurrency { get; init; }

    /// <summary>Breaker and quota facts the caller already knows. When set they win over the selector's <see cref="IExternalSignalSource"/>.</summary>
    public ExternalSignals? Signals { get; init; }
}

/// <summary>What one call needs from the endpoint it lands on.</summary>
public sealed record SelectionRequest
{
    public string TaskClass { get; init; } = "chat";

    public EndpointCapabilities Required { get; init; } = EndpointCapabilities.None;

    public int EstimatedInputTokens { get; init; }

    public int ExpectedOutputTokens { get; init; }

    /// <summary>Same key, same endpoint: keeps a prompt cache warm. Typically the agent and conversation id. Never a prompt.</summary>
    public string? AffinityKey { get; init; }

    public IReadOnlySet<EndpointKey>? Excluded { get; init; }

    /// <summary>True only for calls that are safe to run twice, which makes a hedge plan possible.</summary>
    public bool AllowHedge { get; init; }

    /// <summary>Profile name; null uses the selector's default.</summary>
    public string? Profile { get; init; }
}

/// <summary>Facts the gateway owns and passes in. The selector never duplicates breaker or quota state.</summary>
public sealed record ExternalSignals
{
    public static ExternalSignals None { get; } = new();

    public bool BreakerOpen { get; init; }

    public DateTimeOffset? QuotaBlockedUntil { get; init; }

    /// <summary>Fraction of the rate or token window left, 0 to 1; null when unknown.</summary>
    public double? QuotaHeadroom { get; init; }
}

/// <summary>Source of <see cref="ExternalSignals"/> per endpoint. The gateway implements it over its health and quota trackers.</summary>
public interface IExternalSignalSource
{
    ExternalSignals GetSignals(EndpointKey key);
}
