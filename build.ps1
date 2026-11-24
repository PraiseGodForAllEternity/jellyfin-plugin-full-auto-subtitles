<#
.SYNOPSIS
    Builds the plugin for Jellyfin 10.11 and 12 and lists both in manifest.json.
.DESCRIPTION
    Jellyfin offers the newest version whose targetAbi isn't newer than the server. The 12
    build's version ends in .12, so 12 servers prefer it and 10.11 servers never see it.
.EXAMPLE
    ./build.ps1 -Version 1.0.0.0 -Changelog "What changed"
    gh release create v1.0.0.0 dist/full-auto-subtitles_1.0.0.0.zip dist/full-auto-subtitles_1.0.0.12.zip --verify-tag
    Push manifest.json last, so it never points at files that don't exist yet.
#>
param([Parameter(Mandatory = $true)][string]$Version, [string]$Changelog = "")
$ErrorActionPreference = "Stop"
if ($Version -notmatch '^\d+\.\d+\.\d+\.0$') { throw "Use a version like 1.0.0.0 (the last part marks the Jellyfin 12 build)." }
$name = "Jellyfin.Plugin.FullAutoSubtitles"
$repo = "PraiseGodForAllEternity/jellyfin-plugin-full-auto-subtitles"
$base = $Version.Substring(0, $Version.Length - 2)
$guid = [regex]::Match((Get-Content "$PSScriptRoot\$name\Plugin.cs" -Raw), 'new\("([0-9a-f-]{36})"\)').Groups[1].Value
$stamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
New-Item -ItemType Directory -Force "$PSScriptRoot\dist" | Out-Null
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$entries = foreach ($t in @(@{ J = "12"; V = "$base.12"; Abi = "12.0.0.0"; Tfm = "net10.0" }, @{ J = "10.11"; V = $Version; Abi = "10.11.0.0"; Tfm = "net9.0" })) {
    dotnet build "$PSScriptRoot\$name\$name.csproj" -c Release -nologo -v q "-p:JellyfinTarget=$($t.J)" "-p:Version=$($t.V)" "-p:AssemblyVersion=$($t.V)" "-p:FileVersion=$($t.V)" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Build for Jellyfin $($t.J) failed" }
    # The catalog unpacks into a folder of its own, so the DLL sits at the top of the zip.
    $zip = "$PSScriptRoot\dist\full-auto-subtitles_$($t.V).zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
    $z = [IO.Compression.ZipFile]::Open($zip, "Create")
    try { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, "$PSScriptRoot\$name\bin\Release\$($t.Tfm)\$name.dll", "$name.dll") } finally { $z.Dispose() }
    [pscustomobject][ordered]@{
        version = $t.V; changelog = $Changelog; targetAbi = $t.Abi
        sourceUrl = "https://github.com/$repo/releases/download/v$Version/full-auto-subtitles_$($t.V).zip"
        checksum = (Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant(); timestamp = $stamp
    }
}

$path = "$PSScriptRoot\manifest.json"
$old = @()
if (Test-Path $path) { $old = @((Get-Content $path -Raw | ConvertFrom-Json)[0].versions | Where-Object { @($entries.version) -notcontains $_.version }) }
$plugin = [ordered]@{
    guid = $guid; name = "Full Auto Subtitles"; owner = "PraiseGodForAllEternity"; category = "Subtitles"
    description = "Fetches missing subtitles before you watch, and fixes the timing of ones you have."
    overview = "Gets subtitles ready for what each person is about to watch, in their own language, using your subtitle provider. Fix timing corrects subtitles that are late, early or drifting."
    imageUrl = "https://raw.githubusercontent.com/$repo/main/images/logo.png"
    versions = @($entries) + $old
}
[IO.File]::WriteAllText($path, "[`n" + ($plugin | ConvertTo-Json -Depth 10) + "`n]`n", (New-Object Text.UTF8Encoding($false)))
$entries | ForEach-Object { Write-Host "$($_.version) for Jellyfin $($_.targetAbi): $($_.checksum)" }
