[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot "dist"
}

$compilerCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) {
    throw "The Windows .NET Framework C# compiler was not found."
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$outputPath = Join-Path $OutputDirectory "Media-Nexus-ARM.exe"
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot "src") -Filter "*.cs" | Select-Object -ExpandProperty FullName)
$tagLib = Join-Path $PSScriptRoot "lib\TagLibSharp\TagLibSharp.dll"

& $compiler `
    /nologo `
    /target:winexe `
    /platform:x64 `
    /optimize+ `
    "/out:$outputPath" `
    "/win32icon:$PSScriptRoot\src\Media-Nexus-ARM.ico" `
    "/win32manifest:$PSScriptRoot\src\app.manifest" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Management.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.IO.Compression.dll `
    /reference:System.IO.Compression.FileSystem.dll `
    "/reference:$tagLib" `
    "/resource:$tagLib,MediaNexus.TagLibSharp.dll" `
    $sources

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}

Write-Host "Built $outputPath"
