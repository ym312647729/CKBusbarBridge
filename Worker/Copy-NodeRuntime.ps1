param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$nodeEngine=(Get-Command node.exe -ErrorAction Stop).Source
$major=[int]((& $nodeEngine --version).TrimStart('v').Split('.')[0])
if($major -lt 22){throw '母线桥离线计算需要 Node.js 22 或以上。'}
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$targetEngine=Join-Path $OutputDirectory 'node.exe'
if(!(Test-Path -LiteralPath $targetEngine) -or
   (Get-Item -LiteralPath $targetEngine).Length -ne (Get-Item -LiteralPath $nodeEngine).Length -or
   (Get-Item -LiteralPath $targetEngine).LastWriteTimeUtc -ne (Get-Item -LiteralPath $nodeEngine).LastWriteTimeUtc){
    Copy-Item -LiteralPath $nodeEngine -Destination $targetEngine -Force
}
$license=Join-Path (Split-Path -Parent $nodeEngine) 'LICENSE'
if(Test-Path -LiteralPath $license){Copy-Item -LiteralPath $license -Destination (Join-Path $OutputDirectory 'NODE-LICENSE.txt') -Force}
