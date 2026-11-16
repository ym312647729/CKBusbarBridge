#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

function Read-WorkerMap($properties) {
    $mapped = @{}
    foreach ($property in $properties.PSObject.Properties) { $mapped.Add($property.Name, $property.Value) }
    return $mapped
}

function Get-WorkerHash([string]$workerFile) {
    $hashAlgorithm = [Security.Cryptography.SHA256]::Create()
    $fileStream = $null
    try {
        $fileStream = [IO.File]::OpenRead($workerFile)
        return [BitConverter]::ToString($hashAlgorithm.ComputeHash($fileStream)).Replace('-', '')
    } finally {
        if ($null -ne $fileStream) { $fileStream.Dispose() }
        $hashAlgorithm.Dispose()
    }
}

$packageRoot = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ((Split-Path -Leaf $outputRoot) -ne 'AlgorithmWorker' -or
    $outputRoot.Equals($packageRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $outputRoot.StartsWith($packageRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Debug Worker 必须复制到生成目录下的 AlgorithmWorker。'
}

$manifestName = 'CompatibilityManifest.json'
$workerName = 'RouteLinkBar.Algorithm.Worker.exe'
$coreName = 'RouteLinkBar.Algorithm.dll'
$manifestPath = Join-Path $packageRoot $manifestName
$publishedManifest = Read-WorkerMap (Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath | ConvertFrom-Json)
$lockedInputs = Read-WorkerMap (Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $PSScriptRoot 'RuntimeInputs.json') | ConvertFrom-Json)
if ($publishedManifest.Protection -ne 'ConfuserEx 1.6.0 / non-public rename') {
    throw 'Debug Worker 交付目录必须是已混淆的包。'
}
foreach ($contractField in @('Profile', 'AlgorithmVersion', 'HostProtocolVersion', 'AlgorithmProtocolVersion', 'SourceCommit')) {
    if ($publishedManifest[$contractField] -ne $lockedInputs[$contractField]) {
        throw "Debug Worker 交付包已过期：$contractField 与锁定输入不同。请重新生成混淆交付包。"
    }
}
$publishedDefaults = Read-WorkerMap $publishedManifest.MissingFieldDefaults
$lockedDefaults = Read-WorkerMap $lockedInputs.MissingFieldDefaults
if ($publishedDefaults.Count -ne $lockedDefaults.Count) {
    throw 'Debug Worker 默认参数清单与锁定输入不同。'
}
foreach ($defaultField in $lockedDefaults.Keys) {
    if (!$publishedDefaults.ContainsKey($defaultField) -or $publishedDefaults[$defaultField] -ne $lockedDefaults[$defaultField]) {
        throw "Debug Worker 默认参数与锁定输入不同：$defaultField。"
    }
}

$publishedRuntime = Read-WorkerMap $publishedManifest.RuntimeFiles
$lockedRuntime = Read-WorkerMap $lockedInputs.RuntimeFiles
$lockedPreview = Read-WorkerMap $lockedInputs.PreviewFiles
if ($publishedRuntime.Count -ne $lockedRuntime.Count + 1 -or !$publishedRuntime.ContainsKey($workerName)) {
    throw 'Debug Worker 交付包的运行文件清单不完整。'
}
$packageHashes = [ordered]@{}
foreach ($runtimeName in $lockedRuntime.Keys) {
    $sameHash = $publishedRuntime[$runtimeName] -eq $lockedRuntime[$runtimeName]
    $isProtectedCore = $runtimeName -eq $coreName
    if (!$publishedRuntime.ContainsKey($runtimeName) -or ($isProtectedCore -and $sameHash) -or (!$isProtectedCore -and !$sameHash)) {
        throw "Debug Worker 运行文件与锁定的混淆范围不一致：$runtimeName。"
    }
    $packageHashes[(Join-Path 'AlgorithmRuntime' $runtimeName)] = $publishedRuntime[$runtimeName]
}
$packageHashes[(Join-Path 'AlgorithmRuntime' $workerName)] = $publishedRuntime[$workerName]
$packageHashes[$workerName] = $publishedManifest.LauncherSHA256
foreach ($previewName in $lockedPreview.Keys) {
    $packageHashes[$previewName] = $lockedPreview[$previewName]
}
foreach ($sharedName in @("$workerName.config", 'WorkerChangelog.json')) {
    $packageHashes[$sharedName] = $lockedRuntime[$sharedName]
}
$packageHashes[$manifestName] = Get-WorkerHash $manifestPath

if (@(Get-ChildItem -LiteralPath $packageRoot -Recurse -Force -File).Count -ne $packageHashes.Count) {
    throw 'Debug Worker 交付目录的文件数量与清单不同。'
}
foreach ($packageFile in $packageHashes.Keys) {
    if ($packageHashes[$packageFile] -notmatch '^[0-9a-fA-F]{64}$' -or
        (Get-WorkerHash (Join-Path $packageRoot $packageFile)) -ne $packageHashes[$packageFile]) {
        throw "Debug Worker 交付文件 SHA-256 校验失败：$packageFile。"
    }
}

# 先核对整个交付包，再复制变化的文件，最后更新运行清单。
foreach ($packageFile in $packageHashes.Keys) {
    $destinationFile = Join-Path $outputRoot $packageFile
    if (!(Test-Path -LiteralPath $destinationFile -PathType Leaf) -or
        (Get-WorkerHash $destinationFile) -ne $packageHashes[$packageFile]) {
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destinationFile))
        Copy-Item -LiteralPath (Join-Path $packageRoot $packageFile) -Destination $destinationFile -Force
    }
    if ((Get-WorkerHash $destinationFile) -ne $packageHashes[$packageFile]) {
        throw "Debug Worker 输出文件 SHA-256 校验失败：$packageFile。"
    }
}
Write-Output "已部署混淆 Debug Worker $($publishedManifest.AlgorithmVersion)：$outputRoot"
