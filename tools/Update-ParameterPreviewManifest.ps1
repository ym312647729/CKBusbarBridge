$ErrorActionPreference = 'Stop'
$manifestRoot = Split-Path -Parent $PSScriptRoot
$runtimeRoot = Join-Path $manifestRoot 'CKBusbarBridge.DesignUI/PreviewRuntime'
function Get-PreviewManifestHash([string]$Path) {
    $extension = [IO.Path]::GetExtension($Path)
    if ($extension -notin @('.cs', '.csproj', '.xaml', '.json', '.js', '.mjs', '.html') -and [IO.Path]::GetFileName($Path) -notlike '*LICENSE') {
        return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    # Preserve every byte except CRLF in text. Git may materialize LF or CRLF on checkout.
    $byteEncoding = [Text.Encoding]::GetEncoding(28591)
    $text = $byteEncoding.GetString([IO.File]::ReadAllBytes($Path))
    $canonicalBytes = $byteEncoding.GetBytes($text.Replace("`r`n", "`n"))
    $fileHasher = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($fileHasher.ComputeHash($canonicalBytes))).Replace('-','').ToLowerInvariant() }
    finally { $fileHasher.Dispose() }
}
if (@(Get-ChildItem -LiteralPath $runtimeRoot -Recurse -File | Where-Object Extension -match '^\.(sldprt|sldasm|slddrw|zip)$').Count) { throw 'Runtime must not contain native model copies or archives.' }
$definitionCount = @(Get-ChildItem -LiteralPath (Join-Path $runtimeRoot 'templates') -Recurse -Filter *.virtual.json -File).Count
$runtimeEntries = @(Get-ChildItem -LiteralPath $runtimeRoot -Recurse -File | Where-Object Name -ne 'runtime-manifest.json' | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($runtimeRoot, $_.FullName).Replace('\','/'); sha256 = Get-PreviewManifestHash $_.FullName }
})
$managedPaths = @(Get-ChildItem -LiteralPath (Join-Path $manifestRoot 'CKBusbarBridge.DesignUI/Preview') -File | Where-Object Extension -in @('.cs','.xaml','.json') | ForEach-Object FullName)
$managedPaths += @('models/BridgeGenerationContracts.cs','Services/BridgeDraftGenerationPlanFactory.cs','Services/BridgeGenerationRecipeCodec.cs','Services/BridgeCabinetPlanBuilder.cs','Services/BridgeCabinetChangeAnalyzer.cs','Drivers/BridgeParameterDraftGeometry.cs','Drivers/BridgeParameterDraftDocuments.cs','viewmodels/CabinetDraftWorkflowBindings.cs') | ForEach-Object { Join-Path $manifestRoot ('CKBusbarBridge/' + $_) }
foreach ($workerSourceFolder in @('CKBusbarBridge.Computation','BridgeRouting','BridgeRouting.Worker')) {
    $managedPaths += @(Get-ChildItem -LiteralPath (Join-Path $manifestRoot ('Worker/' + $workerSourceFolder)) -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Extension -in @('.cs','.csproj','.json') } | ForEach-Object FullName)
}
foreach ($manifestProject in @('CKBusbarBridge.DesignUI/CKBusbarBridge.DesignUI.csproj','CKBusbarBridgePreview/CKBusbarBridgePreview.csproj')) {
    $manifestProjectPath = Join-Path $manifestRoot $manifestProject
    [xml]$manifestProjectXml = [IO.File]::ReadAllText($manifestProjectPath)
    foreach ($manifestCompile in $manifestProjectXml.SelectNodes("//*[local-name()='Compile']")) {
        $managedPaths += Get-ChildItem -Path (Join-Path (Split-Path -Parent $manifestProjectPath) $manifestCompile.Include) -File | ForEach-Object FullName
    }
}
$managedPaths = @($managedPaths | ForEach-Object { [IO.Path]::GetFullPath($_) } | Sort-Object -Unique)
$managedEntries = @($managedPaths | Sort-Object | ForEach-Object { [ordered]@{ path = [IO.Path]::GetRelativePath($manifestRoot, $_).Replace('\','/'); sha256 = Get-PreviewManifestHash $_ } })
$manifestSignatureText = (@($runtimeEntries) + @($managedEntries) | ForEach-Object { $_.path + ':' + $_.sha256 }) -join "`n"
$manifestHasher = [Security.Cryptography.SHA256]::Create()
try { $manifestDigest = ([BitConverter]::ToString($manifestHasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($manifestSignatureText)))).Replace('-','').ToLowerInvariant() } finally { $manifestHasher.Dispose() }
$manifest = [ordered]@{
    schema = 'dq-preview-runtime-manifest/1'
    calculatorVersion = 'dq-sw-mesh/1'
    packageSha256 = $manifestDigest
    reference = [ordered]@{ project = 'CKBusbarBridgeVirtualDesigner'; commit = '511d64b1248e1c26bf27430e9016fecdc5e9ce5f'; cachedDefinitions = $definitionCount; runtimeRequiresNativeModels = $false }
    annotationReference = [ordered]@{ project = 'BusbarTools1'; commit = '8da2e5717c32835e58097251caa24f20c8248a19'; entry = 'BusbarManualDrawSession / ManualDrawTextLayout / ArrowPreviewFrame / ManualDrawDimensionWindow'; adaptation = 'Original window and resources copied; namespace, float, original-field callbacks and project 10 mm adjustment / 1 second idle preview adapted. Annotation layout uses original business anchors.' }
    dependencies = @([ordered]@{name='manifold-3d';version='3.3.2';license='Apache-2.0';path='vendor/manifold/LICENSE'},[ordered]@{name='earcut';version='3.0.1';license='ISC';path='vendor/earcut/LICENSE';source='three 0.180.0 src/extras/lib/earcut.js (MIT distribution)';distributionLicense='vendor/earcut/THREE-LICENSE'})
    assets = $runtimeEntries
    managedSources = $managedEntries
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runtimeRoot 'runtime-manifest.json') -Encoding utf8
[ordered]@{ PackageSha256 = $manifestDigest; RuntimeFiles = $runtimeEntries.Count; ManagedSources = $managedEntries.Count; CachedDefinitions = $definitionCount } | ConvertTo-Json
