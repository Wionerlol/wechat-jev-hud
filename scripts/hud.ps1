[CmdletBinding()]
param([switch]$Demo, [switch]$Jev, [switch]$HudDebug, [switch]$CaptureAudit, [int]$Seconds=0,
    [ValidateSet('cpu','gpu:0')][string]$PaddleDevice='gpu:0')
$ErrorActionPreference='Stop'
$repositoryRoot=Split-Path -Parent $PSScriptRoot
$localDotNet=Join-Path $env:LOCALAPPDATA 'WeChatJevHud\dotnet\dotnet.exe'
$dotnet=if(Test-Path $localDotNet){$localDotNet}else{(Get-Command dotnet -ErrorAction Stop).Source}
$arguments=@('run','--project',(Join-Path $repositoryRoot 'src\WeChatJevHud.App\WeChatJevHud.App.csproj'),'--','--paddle-device',$PaddleDevice)
if($Demo){$arguments+='--demo'}
if($Jev){$arguments+='--jev'}
if($HudDebug){$arguments+='--hud-debug'}
if($CaptureAudit){$arguments+='--capture-audit'}
if($Seconds -gt 0){$arguments+=@('--seconds',$Seconds)}
Push-Location $repositoryRoot
try { & $dotnet @arguments; $hudExit=$LASTEXITCODE } finally { Pop-Location }
exit $hudExit
