using System.Text;
using System.Text.Json.Nodes;

namespace Bazaar.Server.VmMounts.Providers.Smb;

/// <summary>A PowerShell run: a constant script text and one stdin line of JSON parameters.</summary>
/// <remarks>
/// The script is passed with <c>-EncodedCommand</c> and is one of the constants of <see cref="SmbScripts"/>: no value is ever spliced into
/// it. The values travel as one JSON line on stdin, which the script reads with <c>[Console]::In.ReadLine()</c> and binds to cmdlet
/// parameters, so a quote or a line break in a value is data, never code. Same stdin-parameter pattern as the volume unit's scripts.
/// This was not run against a real PowerShell from Linux (UNVERIFIED until the Category=Hyperv run).
/// </remarks>
internal sealed record PowerShellInvocation(string Script, string? StandardInput)
{
    public IReadOnlyList<string> Arguments =>
        ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script))];

    public HostCommand ToCommand() => new(HostTools.PowerShell, Arguments, StandardInput);
}

/// <summary>The constant SMB scripts and the builders of their parameter lines.</summary>
internal static class SmbScripts
{
    /// <summary>
    /// Creates one share. The share is built with its single account in the create call (so there is no moment with the default "Everyone
    /// read" entry), granted again for the record, every other entry is revoked, and the result is checked: exactly one Allow entry, for
    /// the named account, with the named right, encryption on, and the path as given. Any failure removes the share again and throws.
    /// A host that does not require SMB signing is refused before anything is created. An existing share of the same name is removed and
    /// rebuilt, so a repeat attach ends in the same state and never keeps an older, wider ACL.
    /// </summary>
    public const string Attach = """
        $ErrorActionPreference = 'Stop'
        $p = [Console]::In.ReadLine() | ConvertFrom-Json
        if (-not (Get-SmbServerConfiguration).RequireSecuritySignature) { throw 'SMB signing is not required on this host' }
        if (Get-SmbShare -Name $p.Name -ErrorAction SilentlyContinue) { Remove-SmbShare -Name $p.Name -Force }
        $share = @{ Name = $p.Name; Path = $p.Path; Description = $p.Description; FolderEnumerationMode = 'AccessBased'; EncryptData = $true; CachingMode = 'None' }
        if ($p.Right -eq 'Read') { $share.ReadAccess = $p.Account } else { $share.ChangeAccess = $p.Account }
        try {
          New-SmbShare @share | Out-Null
          Grant-SmbShareAccess -Name $p.Name -AccountName $p.Account -AccessRight $p.Right -Force | Out-Null
          foreach ($e in @(Get-SmbShareAccess -Name $p.Name)) {
            if ($e.AccountName -ne $p.Account) { Revoke-SmbShareAccess -Name $p.Name -AccountName $e.AccountName -Force | Out-Null }
          }
          $final = @(Get-SmbShareAccess -Name $p.Name)
          if ($final.Count -ne 1 -or $final[0].AccountName -ne $p.Account -or [string]$final[0].AccessControlType -ne 'Allow' -or [string]$final[0].AccessRight -ne $p.Right) { throw 'share access is not exactly the requested grant' }
          $info = Get-SmbShare -Name $p.Name
          if ($info.Path -ne $p.Path -or -not $info.EncryptData) { throw 'share path or encryption is not as requested' }
        } catch {
          Remove-SmbShare -Name $p.Name -Force -ErrorAction SilentlyContinue
          throw
        }
        Write-Output 'ok'
        """;

    /// <summary>
    /// Removes one share, first revoking every access entry and closing files still open through it, so host-side access ends before the
    /// share object goes. The share is removed even when an earlier step fails (the failure is still thrown afterwards). A share that does
    /// not exist is not an error, which makes detach and reconcile idempotent.
    /// </summary>
    public const string Remove = """
        $ErrorActionPreference = 'Stop'
        $p = [Console]::In.ReadLine() | ConvertFrom-Json
        $share = Get-SmbShare -Name $p.Name -ErrorAction SilentlyContinue
        if ($null -ne $share) {
          try {
            foreach ($e in @(Get-SmbShareAccess -Name $p.Name)) { Revoke-SmbShareAccess -Name $p.Name -AccountName $e.AccountName -Force | Out-Null }
            $root = [string]$share.Path + '\'
            foreach ($f in @(Get-SmbOpenFile)) {
              if (([string]$f.Path).StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { Close-SmbOpenFile -FileId $f.FileId -Force }
            }
          } finally {
            Remove-SmbShare -Name $p.Name -Force
          }
        }
        Write-Output 'ok'
        """;

    /// <summary>Lists the names of the shares that start with the fixed prefix, one per line. The caller matches each name against the strict pattern.</summary>
    public const string ListPrefixed = """
        $ErrorActionPreference = 'Stop'
        Get-SmbShare | ForEach-Object { $_.Name } | Where-Object { $_.StartsWith('bazaar-', [StringComparison]::OrdinalIgnoreCase) }
        """;

    public static PowerShellInvocation BuildAttach(
        string name, string path, string account, bool readOnly, string description, bool requireDrivePath = true)
    {
        if (!SmbShareNames.IsValidShare(name))
            throw new ArgumentException("The share name is not valid.", nameof(name));
        if (!SmbShareNames.IsValidAccount(account))
            throw new ArgumentException("The account name is not valid.", nameof(account));
        ScriptText.RequireSafe(path, "path", requireDrivePath);
        ScriptText.RequireSafe(description, "description", requireDrivePath: false);

        var parameters = new JsonObject
        {
            ["Name"] = name,
            ["Path"] = path,
            ["Account"] = account,
            ["Right"] = readOnly ? "Read" : "Change",
            ["Description"] = description,
        };
        return new PowerShellInvocation(Attach, parameters.ToJsonString() + "\n");
    }

    public static PowerShellInvocation BuildRemove(string name)
    {
        if (!SmbShareNames.IsValidShare(name))
            throw new ArgumentException("The share name is not valid.", nameof(name));
        return new PowerShellInvocation(Remove, new JsonObject { ["Name"] = name }.ToJsonString() + "\n");
    }

    public static PowerShellInvocation BuildList() => new(ListPrefixed, null);
}

/// <summary>
/// Validation of the free text that is passed to a script as a parameter. The values are data, not code, but stdin is read in the
/// console code page, so anything outside printable ASCII could change meaning on the way. A path must be absolute with a drive letter,
/// with no quotes, wildcards, shell characters or relative segments.
/// </summary>
internal static class ScriptText
{
    private const string _forbidden = "\"'`<>|*?%$;&^!";

    public static void RequireSafe(string value, string parameterName, bool requireDrivePath)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 240)
            throw new ArgumentException("The value is empty or too long.", parameterName);
        foreach (var c in value)
        {
            if (c < 0x20 || c > 0x7E || _forbidden.Contains(c, StringComparison.Ordinal))
                throw new ArgumentException("The value contains a character that is not allowed in a script parameter.", parameterName);
        }

        if (!requireDrivePath)
            return;
        if (value.Length < 3 || !char.IsAsciiLetter(value[0]) || value[1] != ':' || value[2] != '\\')
            throw new ArgumentException("The path must be absolute with a drive letter.", parameterName);
        foreach (var segment in value.Split('\\'))
        {
            if (segment is "." or "..")
                throw new ArgumentException("The path must not contain relative segments.", parameterName);
        }
    }
}
