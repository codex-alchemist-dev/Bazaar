using System.Globalization;
using System.Text.Json;

namespace Bazaar.Server.DataVolume.Windows;

/// <summary>
/// <see cref="IBitLockerVolumes"/> over PowerShell and CIM (no extra package: <c>Microsoft.Management.Infrastructure</c> is not
/// in <c>Directory.Packages.props</c>). Each call is one <c>powershell.exe</c> run; the key, when there is one, is the first
/// stdin line. The run's own output is one compact JSON line.
/// </summary>
internal sealed class PowerShellBitLockerVolumes(ICommandRunner runner) : IBitLockerVolumes
{
    public async ValueTask ProtectKeyWithExternalKeyAsync(string volumeGuid, ReadOnlyMemory<byte> externalKey, CancellationToken cancellationToken)
    {
        RequireKey(externalKey);
        using var reply = await RunAsync(BitLockerScripts.ProtectWithExternalKey(volumeGuid, externalKey.Span), cancellationToken);
    }

    public async ValueTask EncryptAsync(string volumeGuid, int encryptionMethod, int encryptionFlags, CancellationToken cancellationToken)
    {
        using var reply = await RunAsync(BitLockerScripts.Encrypt(volumeGuid, encryptionMethod, encryptionFlags), cancellationToken);
    }

    public async ValueTask<int> GetConversionStatusAsync(string volumeGuid, CancellationToken cancellationToken) =>
        ReadInt(await RunAsync(BitLockerScripts.GetConversionStatus(volumeGuid), cancellationToken), "ConversionStatus");

    public async ValueTask<int> GetProtectionStatusAsync(string volumeGuid, CancellationToken cancellationToken) =>
        ReadInt(await RunAsync(BitLockerScripts.GetProtectionStatus(volumeGuid), cancellationToken), "ProtectionStatus");

    public async ValueTask<int> GetEncryptionMethodAsync(string volumeGuid, CancellationToken cancellationToken) =>
        ReadInt(await RunAsync(BitLockerScripts.GetEncryptionMethod(volumeGuid), cancellationToken), "EncryptionMethod");

    public async ValueTask<int> GetLockStatusAsync(string volumeGuid, CancellationToken cancellationToken) =>
        ReadInt(await RunAsync(BitLockerScripts.GetLockStatus(volumeGuid), cancellationToken), "LockStatus");

    public async ValueTask UnlockWithExternalKeyAsync(string volumeGuid, ReadOnlyMemory<byte> externalKey, CancellationToken cancellationToken)
    {
        RequireKey(externalKey);
        using var reply = await RunAsync(BitLockerScripts.UnlockWithExternalKey(volumeGuid, externalKey.Span), cancellationToken);
    }

    public async ValueTask<uint> LockAsync(string volumeGuid, bool forceDismount, CancellationToken cancellationToken)
    {
        // The call itself reports "in use" as a return value, so read it instead of throwing.
        var result = await runner.RunAsync(BitLockerScripts.Lock(volumeGuid, forceDismount).ToCommand(), cancellationToken);
        if (!result.Succeeded)
            throw new BitLockerException("The BitLocker lock call could not run.");

        using var document = ParseObject(result.StandardOutput);
        return ReturnValue(document.RootElement);
    }

    private static void RequireKey(ReadOnlyMemory<byte> key)
    {
        if (key.Length != 32)
            throw new ArgumentException("The external key must be 32 bytes.", nameof(key));
    }

    /// <summary>Runs one call and returns its parsed output. A failed process or a non-zero return value throws.</summary>
    private async ValueTask<JsonDocument> RunAsync(PowerShellInvocation invocation, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(invocation.ToCommand(), cancellationToken);
        if (!result.Succeeded)
            throw new BitLockerException($"A BitLocker WMI call failed (exit {result.ExitCode}). {FirstLine(result.StandardError)}");

        var document = ParseObject(result.StandardOutput);
        var value = ReturnValue(document.RootElement);
        if (value != 0)
        {
            document.Dispose();
            throw new BitLockerException($"A BitLocker WMI call returned 0x{value:X8}.", value);
        }

        return document;
    }

    private static JsonDocument ParseObject(string output)
    {
        try
        {
            var document = JsonDocument.Parse(output.Trim());
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return document;

            document.Dispose();
        }
        catch (JsonException)
        {
            // Falls through to the shared error below.
        }

        throw new BitLockerException("A BitLocker WMI call returned output that is not a JSON object.");
    }

    private static uint ReturnValue(JsonElement root) =>
        root.TryGetProperty("ReturnValue", out var value) && value.TryGetUInt32(out var number)
            ? number
            : throw new BitLockerException("A BitLocker WMI call returned no ReturnValue.");

    private static int ReadInt(JsonDocument document, string property)
    {
        using (document)
        {
            return document.RootElement.TryGetProperty(property, out var value) && value.TryGetInt32(out var number)
                ? number
                : throw new BitLockerException($"A BitLocker WMI call returned no {property}.");
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 200 ? string.Create(CultureInfo.InvariantCulture, $"{line[..200]}...") : line;
    }
}
