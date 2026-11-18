namespace Bazaar.Identity.Kek.OpenBao;

/// <summary>Reads the OpenBao token from a bootstrap reference (doc 03): <c>env:NAME</c> or <c>file:&lt;path&gt;</c>. Read on every use, never cached.</summary>
internal static class TokenReference
{
    public static string Read(string reference)
    {
        string? value;
        if (reference.StartsWith("env:", StringComparison.Ordinal))
        {
            var name = reference[4..];
            value = Environment.GetEnvironmentVariable(name)
                ?? throw new InvalidOperationException($"The OpenBao token environment variable '{name}' is not set.");
        }
        else if (reference.StartsWith("file:", StringComparison.Ordinal))
        {
            var path = reference[5..];
            if (!Path.IsPathRooted(path))
                throw new InvalidOperationException("The OpenBao token file path must be absolute.");
            value = File.ReadAllText(path);
        }
        else
        {
            throw new InvalidOperationException("The OpenBao token reference must be 'env:NAME' or 'file:<path>'.");
        }

        value = value.Trim();
        return value.Length > 0 ? value : throw new InvalidOperationException("The OpenBao token reference resolves to an empty value.");
    }
}
