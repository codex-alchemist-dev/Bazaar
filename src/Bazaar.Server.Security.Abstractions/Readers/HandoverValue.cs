using System.Collections.Immutable;

namespace Bazaar.Server.Security.Readers;

/// <summary>A validated field value. Only the validator creates these, so every value has passed its schema's limits.</summary>
public abstract record HandoverValue;

/// <summary>A validated string, enum value or URL.</summary>
public sealed record TextValue(string Value) : HandoverValue;

/// <summary>A validated whole number.</summary>
public sealed record IntegerValue(long Value) : HandoverValue;

/// <summary>A validated boolean.</summary>
public sealed record BoolValue(bool Value) : HandoverValue;

/// <summary>A validated bounded list of strings.</summary>
public sealed record TextListValue(ImmutableArray<string> Items) : HandoverValue;
