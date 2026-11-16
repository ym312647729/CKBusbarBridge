param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$allowedSource = @(
    'CKBusbarBridge.DesignUI/PreviewAssets/EmptyAssembly.asmdot',
    'CKBusbarBridge/CopperDrawing/Runtime/templates/Drawings/母线桥A3 - 中压模板.drwdot',
    'CKBusbarBridge/CopperDrawing/Runtime/templates/Drawings/母线桥A3.drwdot'
)
$allowedOutput = @(
    'PreviewAssets/EmptyAssembly.asmdot',
    'templates/Drawings/母线桥A3 - 中压模板.drwdot',
    'templates/Drawings/母线桥A3.drwdot'
)
function Assert-CadFiles([string]$base, [string[]]$allowed, [bool]$source) {
    $files = Get-ChildItem -LiteralPath $base -Recurse -File | ForEach-Object {
        [IO.Path]::GetRelativePath($base, $_.FullName).Replace('\','/')
    } | Where-Object {
        (!$source -or $_ -notmatch '(^|/)(bin|obj|packages|\.vs|\.git)(/|$)') -and
        $_ -match '\.(sldasm|sldprt|slddrw|asmdot|prtdot|drwdot|slddrt|sldmat|stp|step|igs|iges|x_t|x_b|stl|zip|7z|rar)$'
    }
    $unexpected = $files | Where-Object { $_ -notin $allowed }
    $missing = $allowed | Where-Object { $_ -notin $files }
    if ($unexpected -or $missing) {
        throw ('CAD 资源白名单不符：多余=' + ($unexpected -join ', ') + '；缺失=' + ($missing -join ', '))
    }
    [pscustomobject]@{ Scope = if ($source) { 'source' } else { 'output' }; NativeAssets = @($files).Count; Verified = $true }
}
Assert-CadFiles $root $allowedSource $true
if ($OutputDirectory) {
    Assert-CadFiles (Resolve-Path -LiteralPath $OutputDirectory).Path $allowedOutput $false
}
