using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Bazaar.Server.Security.Readers;

/// <summary>
/// An immutable registry of hand-over schemas. The shipped server registers none; schemas such as <c>mail_triage</c>
/// arrive with their features. Build one with <see cref="Builder"/>.
/// </summary>
public sealed class HandoverSchemaRegistry : IHandoverSchemaRegistry
{
    private readonly ImmutableDictionary<string, HandoverSchema> _schemas;

    private HandoverSchemaRegistry(ImmutableDictionary<string, HandoverSchema> schemas) => _schemas = schemas;

    /// <summary>A registry with no schemas. Every submission is refused as an unknown schema.</summary>
    public static HandoverSchemaRegistry Empty { get; } = new(ImmutableDictionary<string, HandoverSchema>.Empty.WithComparers(StringComparer.Ordinal));

    /// <inheritdoc />
    public bool TryGet(string schemaId, [NotNullWhen(true)] out HandoverSchema? schema)
    {
        ArgumentNullException.ThrowIfNull(schemaId);
        return _schemas.TryGetValue(schemaId, out schema);
    }

    /// <summary>Collects schemas, then freezes them into a registry. Used by the server's startup wiring and by tests.</summary>
    public sealed class Builder
    {
        private readonly ImmutableDictionary<string, HandoverSchema>.Builder _schemas =
            ImmutableDictionary.CreateBuilder<string, HandoverSchema>(StringComparer.Ordinal);

        /// <summary>Adds a schema.</summary>
        /// <exception cref="ArgumentException">A schema with the same id is already registered.</exception>
        public Builder Register(HandoverSchema schema)
        {
            ArgumentNullException.ThrowIfNull(schema);
            if (!_schemas.TryAdd(schema.Id, schema))
            {
                throw new ArgumentException($"Schema '{schema.Id}' is registered twice.", nameof(schema));
            }

            return this;
        }

        /// <summary>Freezes the collected schemas.</summary>
        public HandoverSchemaRegistry Build() => new(_schemas.ToImmutable());
    }
}
