using System.Globalization;
using System.Text;

namespace Bazaar.Server.DataVolume.Windows;

/// <summary>
/// A PowerShell run: the script text and the stdin lines. The script is passed with <c>-EncodedCommand</c> (it holds no
/// secret and no caller-supplied free text), and the volume key is passed as the first stdin line, base64, read by the
/// script with <c>[Console]::In.ReadLine()</c>. The key is never in argv, the environment, a file or the script.
/// </summary>
/// <remarks>
/// Why not "script on stdin" with <c>-Command -</c>: PowerShell then consumes stdin line by line as it executes the script,
/// so a key line on the same stream would be run as a command or swallowed depending on host buffering. A script in
/// <c>-EncodedCommand</c> leaves stdin free for the key. This was not run against a real PowerShell from Linux.
/// </remarks>
internal sealed record PowerShellInvocation(string Script, string? StandardInput)
{
    public IReadOnlyList<string> Arguments =>
        ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script))];

    public CommandSpec ToCommand() => new(SystemTools.PowerShell, Arguments, StandardInput);
}

/// <summary>The Win32_EncryptableVolume methods the volume unit calls.</summary>
internal static class BitLockerScripts
{
    private const string _namespace = "root/CIMV2/Security/MicrosoftVolumeEncryption";

    /// <summary>The label of the external key protector. Fixed text, never caller-supplied.</summary>
    public const string ProtectorName = "Bazaar data volume";

    public static PowerShellInvocation ProtectWithExternalKey(string canonicalGuid, ReadOnlySpan<byte> key) =>
        new(Build(canonicalGuid, "ProtectKeyWithExternalKey", $"@{{ FriendlyName = '{ProtectorName}'; ExternalKey = $key }}", [], readsKey: true),
            KeyLine(key));

    public static PowerShellInvocation Encrypt(string canonicalGuid, int method, int flags) =>
        new(Build(
            canonicalGuid,
            "Encrypt",
            $"@{{ EncryptionMethod = {method.ToString(CultureInfo.InvariantCulture)}; EncryptionFlags = {flags.ToString(CultureInfo.InvariantCulture)} }}",
            []),
            null);

    public static PowerShellInvocation GetConversionStatus(string canonicalGuid) =>
        new(Build(canonicalGuid, "GetConversionStatus", "@{}", ["ConversionStatus", "EncryptionPercentage"]), null);

    public static PowerShellInvocation GetProtectionStatus(string canonicalGuid) =>
        new(Build(canonicalGuid, "GetProtectionStatus", "@{}", ["ProtectionStatus"]), null);

    public static PowerShellInvocation GetEncryptionMethod(string canonicalGuid) =>
        new(Build(canonicalGuid, "GetEncryptionMethod", "@{}", ["EncryptionMethod"]), null);

    public static PowerShellInvocation GetLockStatus(string canonicalGuid) =>
        new(Build(canonicalGuid, "GetLockStatus", "@{}", ["LockStatus"]), null);

    public static PowerShellInvocation UnlockWithExternalKey(string canonicalGuid, ReadOnlySpan<byte> key) =>
        new(Build(canonicalGuid, "UnlockWithExternalKey", "@{ ExternalKey = $key }", [], readsKey: true), KeyLine(key));

    public static PowerShellInvocation Lock(string canonicalGuid, bool forceDismount) =>
        new(Build(canonicalGuid, "Lock", $"@{{ ForceDismount = ${(forceDismount ? "true" : "false")} }}", []), null);

    private static string KeyLine(ReadOnlySpan<byte> key) => Convert.ToBase64String(key) + "\n";

    private static string Build(string canonicalGuid, string method, string arguments, string[] outputs, bool readsKey = false)
    {
        if (!VolumeGuid.IsCanonical(canonicalGuid))
            throw new ArgumentException("The volume GUID must be in canonical form.", nameof(canonicalGuid));

        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        if (readsKey)
            script.AppendLine("$key = [Convert]::FromBase64String([Console]::In.ReadLine())");

        script.AppendLine(CultureInfo.InvariantCulture, $"$id = '{VolumeGuid.ToVolumePath(canonicalGuid)}'");
        script.AppendLine(CultureInfo.InvariantCulture, $"$v = Get-CimInstance -Namespace '{_namespace}' -ClassName Win32_EncryptableVolume | Where-Object {{ $_.DeviceID -ieq $id }}");
        script.AppendLine("if ($null -eq $v) { throw 'volume not found' }");
        script.AppendLine(CultureInfo.InvariantCulture, $"$r = Invoke-CimMethod -InputObject $v -MethodName '{method}' -Arguments {arguments}");
        var fields = string.Join(',', ["ReturnValue", .. outputs]);
        script.AppendLine(CultureInfo.InvariantCulture, $"$r | Select-Object {fields} | ConvertTo-Json -Compress");
        return script.ToString().ReplaceLineEndings("\n");
    }
}

/// <summary>The Storage-module query that tells whether a VHDX is attached (Get-DiskImage ships with Windows).</summary>
internal static class DiskImageScripts
{
    public static PowerShellInvocation IsAttached(string vhdxPath)
    {
        ScriptPaths.Validate(vhdxPath, nameof(vhdxPath), requireVhdx: true);
        return new PowerShellInvocation($"$ErrorActionPreference = 'Stop'\n(Get-DiskImage -ImagePath '{vhdxPath}').Attached\n", null);
    }
}
