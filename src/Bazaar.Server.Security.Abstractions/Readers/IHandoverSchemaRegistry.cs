using System.Diagnostics.CodeAnalysis;

namespace Bazaar.Server.Security.Readers;

/// <summary>Looks up the hand-over schemas the server has registered. Unknown ids are refused by the validator.</summary>
public interface IHandoverSchemaRegistry
{
    /// <summary>Finds a schema by exact id.</summary>
    bool TryGet(string schemaId, [NotNullWhen(true)] out HandoverSchema? schema);
}
