[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$localDotNet = Join-Path $env:LOCALAPPDATA 'WeChatJevHud\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotNet) { $localDotNet } else { (Get-Command dotnet -ErrorAction Stop).Source }
# Inherit the key only from the local environment. Never echo or persist it.
& $dotnet run --project (Join-Path $repositoryRoot 'src\WeChatJevHud.Diagnostics\WeChatJevHud.Diagnostics.csproj') -- --jev-smoke
exit $LASTEXITCODE
