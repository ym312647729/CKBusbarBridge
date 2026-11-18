param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$outputRoot=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if($outputRoot -notmatch '\\bin\\'){throw '只能清理项目生成目录。'}
foreach($folder in @('AlgorithmWorker','WebPreviewWorker','PreviewRuntime')){
    $candidate=[IO.Path]::GetFullPath((Join-Path $outputRoot $folder))
    if(!(Test-Path -LiteralPath $candidate)){continue}
    $resolved=(Resolve-Path -LiteralPath $candidate).Path
    if($resolved -ne $candidate -or !(Split-Path -Parent $resolved).Equals($outputRoot,[StringComparison]::OrdinalIgnoreCase) -or
       ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)){
        throw '旧运行目录路径未通过范围核查。'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
$oldBaseConfig=Join-Path $outputRoot 'Settings\BaseDimSizeDefine.json'
if(Test-Path -LiteralPath $oldBaseConfig){Remove-Item -LiteralPath $oldBaseConfig -Force}
