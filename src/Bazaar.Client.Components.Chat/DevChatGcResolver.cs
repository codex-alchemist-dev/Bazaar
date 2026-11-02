namespace Bazaar.Client.Components.Chat;

/// <summary>
/// Picks the dev chat's group chat id: the <c>--dev-gc</c> override first, then the id cached from an earlier run.
/// Returns null when neither is set. Nothing here creates a GC; the server has no create verb yet.
/// </summary>
public static class DevChatGcResolver
{
    public const string OverrideFlag = "--dev-gc";

    /// <exception cref="ArgumentException"><c>--dev-gc</c> is given without a valid GUID.</exception>
    public static Guid? Resolve(IReadOnlyList<string> args, string? cachedId)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] != OverrideFlag)
            {
                continue;
            }

            if (i + 1 >= args.Count || !Guid.TryParse(args[i + 1], out var overrideId))
            {
                throw new ArgumentException($"{OverrideFlag} needs a group chat id (a GUID).", nameof(args));
            }

            return overrideId;
        }

        return Guid.TryParse(cachedId, out var cached) ? cached : null;
    }
}
