using System.Text.Json;

namespace Bazaar.Server.Security.Readers;

/// <summary>Checks a reader's hand-over payload against a registered schema. Pure: the same input always gives the same result.</summary>
public interface IHandoverValidator
{
    /// <summary>Validates one payload.</summary>
    HandoverResult Validate(string schemaId, JsonElement payload);
}
