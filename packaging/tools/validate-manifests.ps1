#Requires -Version 5.1
<#
.SYNOPSIS
    Validates the packaging manifests under packaging/ for required fields.

.DESCRIPTION
    Checks the winget, Scoop, Chocolatey, Homebrew cask and Snap files for required fields.
    Each channel must contain at least one manifest.

    Without -Release, {{PLACEHOLDER}} tokens are allowed, so templates can be checked before
    the release pipeline fills them in. With -Release, any leftover {{PLACEHOLDER}} token under
    packaging/ (outside packaging/tools/) is a failure.

    Exit code 0 means every check passed; 1 means at least one check failed.
    Design: docs/design/37-distribution-matrix-and-packaging.md.

.PARAMETER Release
    Reject leftover {{PLACEHOLDER}} tokens.

.PARAMETER PackagingRoot
    The packaging directory to validate. Defaults to the packaging/ folder above this script.

.EXAMPLE
    pwsh -File packaging/tools/validate-manifests.ps1

.EXAMPLE
    pwsh -File packaging/tools/validate-manifests.ps1 -Release
#>
[CmdletBinding()]
param(
    [switch]$Release,
    [string]$PackagingRoot = (Join-Path $PSScriptRoot '..')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$PlaceholderPattern = '\{\{[A-Za-z_][A-Za-z0-9_]*\}\}'
$Sha256Pattern = '^[0-9A-Fa-f]{64}$'
$Mode = if ($Release) { 'release' } else { 'dev' }

$script:Failures = New-Object System.Collections.Generic.List[string]
$script:CheckedCount = 0
$script:RootPath = ''

function Add-Failure {
    param([string]$File, [string]$Message)
    $script:Failures.Add("${File}: $Message")
}

function Get-PackagingRelativePath {
    param([string]$FullName)
    return $FullName.Substring($script:RootPath.Length).TrimStart('\', '/').Replace('\', '/')
}

function Read-FileText {
    param([System.IO.FileInfo]$File)
    $text = Get-Content -LiteralPath $File.FullName -Raw
    if ($null -eq $text) { return '' }
    # Normalise line endings so the line-anchored patterns behave the same on Windows and Linux.
    return $text.Replace("`r`n", "`n")
}

function Test-Placeholder {
    param([string]$Value)
    return [regex]::IsMatch($Value, $PlaceholderPattern)
}

# A hash is valid when it is 64 hex characters, or a placeholder that the release pipeline fills in.
function Test-Sha256Value {
    param([string]$Value)
    return ([regex]::IsMatch($Value, $Sha256Pattern) -or (Test-Placeholder $Value))
}

# Returns the trimmed, unquoted values of a YAML key. Top-level only unless -Nested is given.
function Get-YamlValues {
    param([string]$Text, [string]$Key, [switch]$Nested)
    $prefix = '^'
    if ($Nested) { $prefix = '^[ \t]*' }
    $pattern = '(?m)' + $prefix + '(?:-[ \t]+)?' + [regex]::Escape($Key) + ':[ \t]*(.*?)[ \t]*$'
    foreach ($match in [regex]::Matches($Text, $pattern)) {
        $match.Groups[1].Value.Trim().Trim('"', "'")
    }
}

function Test-YamlScalarFields {
    param([string]$File, [string]$Text, [string[]]$Fields)
    foreach ($field in $Fields) {
        $values = @(Get-YamlValues -Text $Text -Key $field)
        if ($values.Count -eq 0) {
            Add-Failure $File "missing required field '$field'"
        } elseif ([string]::IsNullOrWhiteSpace($values[0])) {
            Add-Failure $File "required field '$field' is empty"
        }
    }
}

function Get-JsonProperty {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Test-JsonText {
    param($Object, [string]$Name)
    $value = Get-JsonProperty $Object $Name
    return (($value -is [string]) -and ($value.Trim().Length -gt 0))
}

# Returns the first double-quoted argument of a Ruby cask stanza such as: version "1.2.3"
function Get-RubyStanza {
    param([string]$Text, [string]$Stanza)
    $match = [regex]::Match($Text, '(?m)^[ \t]*' + $Stanza + '[ \t]+"([^"]*)"')
    if ($match.Success) { return $match.Groups[1].Value }
    return $null
}

function Test-WingetManifest {
    param([System.IO.FileInfo]$File)
    $rel = Get-PackagingRelativePath $File.FullName
    $text = Read-FileText $File
    Test-YamlScalarFields -File $rel -Text $text -Fields @(
        'PackageIdentifier', 'PackageVersion', 'PackageLocale', 'Publisher', 'PackageName',
        'License', 'ShortDescription', 'ManifestType', 'ManifestVersion')
    if (-not [regex]::IsMatch($text, '(?m)^Installers:[ \t]*$')) {
        Add-Failure $rel "missing 'Installers' block"
    }
    foreach ($field in 'Architecture', 'InstallerType', 'InstallerUrl', 'InstallerSha256') {
        if (@(Get-YamlValues -Text $text -Key $field -Nested).Count -eq 0) {
            Add-Failure $rel "no installer entry sets '$field'"
        }
    }
    foreach ($sha in @(Get-YamlValues -Text $text -Key 'InstallerSha256' -Nested)) {
        if (-not (Test-Sha256Value $sha)) {
            Add-Failure $rel "InstallerSha256 is not a 64-character hex SHA-256: '$sha'"
        }
    }
}

function Test-ScoopManifest {
    param([System.IO.FileInfo]$File)
    $rel = Get-PackagingRelativePath $File.FullName
    try {
        $manifest = Read-FileText $File | ConvertFrom-Json
    } catch {
        Add-Failure $rel "not valid JSON: $($_.Exception.Message)"
        return
    }
    foreach ($field in 'version', 'description', 'homepage', 'license') {
        if (-not (Test-JsonText $manifest $field)) {
            Add-Failure $rel "missing or empty required field '$field'"
        }
    }
    $architecture = Get-JsonProperty $manifest 'architecture'
    $source = Get-JsonProperty $architecture '64bit'
    if ($null -eq $source) { $source = $manifest }
    if (-not (Test-JsonText $source 'url')) {
        Add-Failure $rel "no 64bit download url (architecture.64bit.url or url)"
    }
    $hash = Get-JsonProperty $source 'hash'
    if (-not (Test-JsonText $source 'hash')) {
        Add-Failure $rel "no 64bit hash (architecture.64bit.hash or hash)"
    } elseif (-not (Test-Sha256Value ([string]$hash))) {
        Add-Failure $rel "hash is not a 64-character hex SHA-256"
    }
}

function Test-ChocolateyPackage {
    param([System.IO.FileInfo]$File)
    $rel = Get-PackagingRelativePath $File.FullName
    try {
        [xml]$nuspec = Read-FileText $File
    } catch {
        Add-Failure $rel "not valid XML: $($_.Exception.Message)"
        return
    }
    foreach ($field in 'id', 'version', 'authors', 'description') {
        # Namespace-agnostic XPath: nuspec files declare a default xmlns.
        $node = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='$field']")
        if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
            Add-Failure $rel "missing or empty <metadata>/<$field>"
        }
    }
    $install = Join-Path (Join-Path $File.DirectoryName 'tools') 'chocolateyInstall.ps1'
    if (-not (Test-Path -LiteralPath $install -PathType Leaf)) {
        Add-Failure $rel "no tools/chocolateyInstall.ps1 next to the nuspec"
    }
}

function Test-HomebrewCask {
    param([System.IO.FileInfo]$File)
    $rel = Get-PackagingRelativePath $File.FullName
    $text = Read-FileText $File
    if (-not [regex]::IsMatch($text, '(?m)^cask[ \t]+"[^"]+"[ \t]+do[ \t]*$')) {
        Add-Failure $rel "missing cask header (cask ""<token>"" do)"
    }
    foreach ($stanza in 'version', 'url', 'name', 'desc', 'homepage') {
        if ([string]::IsNullOrWhiteSpace((Get-RubyStanza $text $stanza))) {
            Add-Failure $rel "missing or empty '$stanza' stanza"
        }
    }
    $sha = [regex]::Match($text, '(?m)^[ \t]*sha256[ \t]+(?::no_check|"([^"]*)")')
    if (-not $sha.Success) {
        Add-Failure $rel "missing 'sha256' stanza"
    } elseif ($sha.Groups[1].Success -and -not (Test-Sha256Value $sha.Groups[1].Value)) {
        Add-Failure $rel "sha256 is not a 64-character hex SHA-256"
    }
}

function Test-SnapcraftManifest {
    param([System.IO.FileInfo]$File)
    $rel = Get-PackagingRelativePath $File.FullName
    $text = Read-FileText $File
    Test-YamlScalarFields -File $rel -Text $text -Fields @(
        'name', 'version', 'summary', 'description', 'base', 'grade', 'confinement')
    foreach ($block in 'apps', 'parts') {
        if (-not [regex]::IsMatch($text, "(?m)^${block}[ \t]*:[ \t]*$")) {
            Add-Failure $rel "missing '$block' block"
        }
    }
    $name = @(Get-YamlValues -Text $text -Key 'name')
    if ($name.Count -gt 0 -and -not [string]::IsNullOrEmpty($name[0])) {
        if (($name[0].Length -gt 40) -or ($name[0] -cnotmatch '^[a-z0-9]([a-z0-9-]*[a-z0-9])?$')) {
            Add-Failure $rel "name must be lowercase letters, digits and hyphens, at most 40 characters"
        }
    }
    $summary = @(Get-YamlValues -Text $text -Key 'summary')
    if ($summary.Count -gt 0 -and $summary[0].Length -gt 79) {
        Add-Failure $rel "summary is longer than 79 characters"
    }
}

if (-not (Test-Path -LiteralPath $PackagingRoot -PathType Container)) {
    Write-Host "FAIL  packaging root not found: $PackagingRoot"
    exit 1
}
$script:RootPath = (Resolve-Path -LiteralPath $PackagingRoot).ProviderPath.TrimEnd('\', '/')

$Channels = @(
    @{ Name = 'winget';        Dir = 'winget';     Extensions = @('.yaml', '.yml') },
    @{ Name = 'Scoop';         Dir = 'scoop';      Extensions = @('.json') },
    @{ Name = 'Chocolatey';    Dir = 'chocolatey'; Extensions = @('.nuspec') },
    @{ Name = 'Homebrew cask'; Dir = 'homebrew';   Extensions = @('.rb') },
    @{ Name = 'Snap';          Dir = 'snap';       Extensions = @('.yaml') }
)

foreach ($channel in $Channels) {
    $dirPath = Join-Path $script:RootPath $channel.Dir
    $files = @()
    if (Test-Path -LiteralPath $dirPath -PathType Container) {
        $files = @(Get-ChildItem -LiteralPath $dirPath -Recurse -File |
            Where-Object { $channel.Extensions -contains $_.Extension.ToLowerInvariant() })
    }
    if ($files.Count -eq 0) {
        Add-Failure "$($channel.Dir)/" "no $($channel.Name) manifest found"
        continue
    }
    foreach ($file in $files) {
        $script:CheckedCount++
        switch ($channel.Dir) {
            'winget'     { Test-WingetManifest $file }
            'scoop'      { Test-ScoopManifest $file }
            'chocolatey' { Test-ChocolateyPackage $file }
            'homebrew'   { Test-HomebrewCask $file }
            'snap'       { Test-SnapcraftManifest $file }
        }
    }
}

if ($Release) {
    foreach ($file in @(Get-ChildItem -LiteralPath $script:RootPath -Recurse -File)) {
        $rel = Get-PackagingRelativePath $file.FullName
        if ($rel -like 'tools/*') { continue }
        $text = Read-FileText $file
        $tokens = @([regex]::Matches($text, $PlaceholderPattern) | ForEach-Object { $_.Value } | Sort-Object -Unique)
        foreach ($token in $tokens) {
            Add-Failure $rel "leftover placeholder token $token (release mode)"
        }
    }
}

foreach ($failure in $script:Failures) { Write-Host "FAIL  $failure" }
if ($script:Failures.Count -gt 0) {
    Write-Host "validate-manifests ($Mode mode): $($script:Failures.Count) problem(s) in $($script:CheckedCount) manifest file(s)."
    exit 1
}
Write-Host "validate-manifests ($Mode mode): $($script:CheckedCount) manifest file(s) passed."
exit 0
