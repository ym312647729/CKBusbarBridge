param(
    [Parameter(Mandatory = $true)]
    [string]$AssemblyPath
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$bridgeMachineRegistry = $null
$bridgeUserRegistry = $null

try {
    $bridgeAssemblyPath = [System.IO.Path]::GetFullPath($AssemblyPath)
    if (-not (Test-Path -LiteralPath $bridgeAssemblyPath -PathType Leaf)) {
        throw "未找到本次生成的插件：$bridgeAssemblyPath"
    }
    if (@(Get-Process -Name SLDWORKS -ErrorAction SilentlyContinue).Count -gt 0) {
        throw '请先保存工作并退出 SolidWorks，再重新生成或按 F5。不会自动结束现有进程。'
    }
    $bridgeWindowsPrincipal = [System.Security.Principal.WindowsPrincipal]::new([System.Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $bridgeWindowsPrincipal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '请以管理员身份启动 Visual Studio 后重新生成；手动运行注册脚本也需要管理员权限。'
    }
    $bridgeRegAsmPath = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
    if (-not (Test-Path -LiteralPath $bridgeRegAsmPath -PathType Leaf)) {
        throw "未找到 64 位 .NET Framework 注册工具：$bridgeRegAsmPath"
    }

    $bridgeAddinId = '{46af9620-9777-42ed-9df4-25a2ac4f860d}'
    $bridgeComRegistryPath = "SOFTWARE\Classes\CLSID\$bridgeAddinId\InprocServer32"
    $bridgeStartupRegistryPath = "Software\SolidWorks\AddInsStartup\$bridgeAddinId"
    $bridgeMachineRegistry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    $bridgeUserRegistry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)

    # 更换工作副本时，使用旧注册指向的程序集卸载其实际版本。
    $bridgePreviousAssemblyPath = $bridgeAssemblyPath
    $bridgePreviousComKey = $bridgeMachineRegistry.OpenSubKey($bridgeComRegistryPath)
    if ($null -ne $bridgePreviousComKey) {
        try {
            $bridgePreviousCodeBase = $bridgePreviousComKey.GetValue('CodeBase')
            if (-not [string]::IsNullOrWhiteSpace($bridgePreviousCodeBase)) {
                $bridgePreviousUri = [uri]$bridgePreviousCodeBase
                if (-not $bridgePreviousUri.IsFile) { throw '旧插件的注册路径不是本地文件，无法自动卸载。' }
                $bridgePreviousAssemblyPath = $bridgePreviousUri.LocalPath
                if (-not (Test-Path -LiteralPath $bridgePreviousAssemblyPath -PathType Leaf)) {
                    throw "旧注册指向的插件已不存在，请先恢复旧文件以完成卸载：$bridgePreviousAssemblyPath"
                }
            }
        }
        finally { $bridgePreviousComKey.Dispose() }
    }

    Write-Host "正在卸载中压母线桥旧注册：$bridgePreviousAssemblyPath"
    & $bridgeRegAsmPath $bridgePreviousAssemblyPath /unregister /nologo
    if ($LASTEXITCODE -ne 0) { throw "卸载旧插件失败，注册工具退出码：$LASTEXITCODE" }
    $bridgeRemainingComKey = $bridgeMachineRegistry.OpenSubKey($bridgeComRegistryPath)
    if ($null -ne $bridgeRemainingComKey) {
        $bridgeRemainingComKey.Dispose()
        throw '旧 COM 注册未完全卸载，已停止注册；请检查旧程序集版本。'
    }

    Write-Host "正在注册本次生成的中压母线桥：$bridgeAssemblyPath"
    & $bridgeRegAsmPath $bridgeAssemblyPath /codebase /nologo
    if ($LASTEXITCODE -ne 0) { throw "注册插件失败，注册工具退出码：$LASTEXITCODE" }

    $bridgeCurrentComKey = $bridgeMachineRegistry.OpenSubKey($bridgeComRegistryPath)
    if ($null -eq $bridgeCurrentComKey) { throw '注册工具执行后未找到插件 COM 注册。' }
    try {
        $bridgeCurrentCodeBase = $bridgeCurrentComKey.GetValue('CodeBase')
        if ([string]::IsNullOrWhiteSpace($bridgeCurrentCodeBase) -or
            -not [string]::Equals(([uri]$bridgeCurrentCodeBase).LocalPath, $bridgeAssemblyPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw '注册后的插件路径与本次生成路径不一致。'
        }
    }
    finally { $bridgeCurrentComKey.Dispose() }

    $bridgeInstalledKey = $bridgeMachineRegistry.OpenSubKey("SOFTWARE\SolidWorks\Addins\$bridgeAddinId")
    if ($null -eq $bridgeInstalledKey) { throw '未找到 SolidWorks 加载项登记，不能作为插件调试。' }
    try {
        if ($bridgeInstalledKey.GetValue('Title') -ne '中压母线桥') { throw 'SolidWorks 加载项标题不正确。' }
    }
    finally { $bridgeInstalledKey.Dispose() }

    $bridgeStartupKey = $bridgeUserRegistry.CreateSubKey($bridgeStartupRegistryPath)
    try {
        $bridgeStartupKey.SetValue('', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        if ($bridgeStartupKey.GetValue('') -ne 1) { throw '未能启用 SolidWorks 启动时默认加载。' }
    }
    finally { $bridgeStartupKey.Dispose() }
    Write-Host '中压母线桥注册成功，已启用启动时默认加载。可在 Visual Studio 按 F5 调试。'
}
catch {
    [Console]::Error.WriteLine('中压母线桥注册失败：' + $_.Exception.Message)
    exit 1
}
finally {
    if ($null -ne $bridgeUserRegistry) { $bridgeUserRegistry.Dispose() }
    if ($null -ne $bridgeMachineRegistry) { $bridgeMachineRegistry.Dispose() }
}
exit 0
