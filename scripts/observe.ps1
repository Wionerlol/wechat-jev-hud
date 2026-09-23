[CmdletBinding()]
param(
    [ValidateRange(50, 5000)]
    [int]$IntervalMilliseconds = 200,

    [ValidateRange(1, 86400)]
    [int]$Seconds,

    [switch]$DebugText,

    # Explicit opt-in: uploads trusted Remote NEW + bounded trusted prior text to TypeSafe.
    [switch]$Jev,

    [ValidateSet('cpu', 'gpu:0')]
    [string]$PaddleDevice = 'gpu:0',

    [string]$PaddlePython,

    [switch]$PaddleWorkerDebug
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$localDotNet = Join-Path $env:LOCALAPPDATA 'WeChatJevHud\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotNet) { $localDotNet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$arguments = @(
    'run',
    '--project',
    (Join-Path $repositoryRoot 'src\WeChatJevHud.Diagnostics\WeChatJevHud.Diagnostics.csproj'),
    '--',
    '--observe',
    '--interval-ms',
    $IntervalMilliseconds
)
if ($PSBoundParameters.ContainsKey('Seconds')) {
    $arguments += @('--observe-seconds', $Seconds)
}
if ($DebugText) {
    $arguments += '--debug-text'
}
if ($Jev) {
    $arguments += '--jev'
}
$arguments += @('--paddle-device', $PaddleDevice)
if ($PSBoundParameters.ContainsKey('PaddlePython')) {
    $arguments += @('--paddle-python', $PaddlePython)
}
if ($PaddleWorkerDebug) {
    $arguments += '--paddle-worker-debug'
}

& $dotnet @arguments
exit $LASTEXITCODE
