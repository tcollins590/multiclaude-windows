<#
.SYNOPSIS
  Builds MultiClaude.exe into .\dist.

.DESCRIPTION
  Two builds, both a single .exe:
    dist\MultiClaude.exe                 small; needs the .NET 8 Desktop Runtime
    dist\self-contained\MultiClaude.exe  larger; runs anywhere on Windows 10 or 11 (x64)

.EXAMPLE
  ./build.ps1            # build both
  ./build.ps1 -Run       # build, then start the small one
#>
param([switch]$Run)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\MultiClaude.csproj'
$dist = Join-Path $PSScriptRoot 'dist'

dotnet publish $project -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -o $dist
dotnet publish $project -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o (Join-Path $dist 'self-contained')

Get-ChildItem $dist -Recurse -Filter *.pdb | Remove-Item
Get-ChildItem $dist -Recurse -Filter MultiClaude.exe | ForEach-Object { '{0}  {1:N1} MB' -f $_.FullName, ($_.Length / 1MB) }

if ($Run) {
  Get-Process MultiClaude -ErrorAction SilentlyContinue | Stop-Process
  Start-Process (Join-Path $dist 'MultiClaude.exe')
}
