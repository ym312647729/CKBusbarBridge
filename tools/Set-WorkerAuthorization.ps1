# Run under the Windows account that will use the SolidWorks plugin.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

$secureAuthorization = Read-Host '请输入获准用于母线桥项目的算法授权码' -AsSecureString
$authorizationPointer = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($secureAuthorization)
$authorizationBytes = $null
try {
    $authorizationCode = [Runtime.InteropServices.Marshal]::PtrToStringUni($authorizationPointer).Trim()
    if ([string]::IsNullOrWhiteSpace($authorizationCode)) { throw '算法授权码不能为空。' }
    $authorizationBytes = [Text.Encoding]::UTF8.GetBytes($authorizationCode)
    $protectedAuthorization = [Security.Cryptography.ProtectedData]::Protect(
        $authorizationBytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $userDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrEmpty($userDirectory)) { throw '当前 Windows 用户没有可用的本地配置目录。' }
    $targetDirectory = Join-Path $userDirectory 'CKBusbarBridge'
    [void][IO.Directory]::CreateDirectory($targetDirectory)
    [IO.File]::WriteAllBytes((Join-Path $targetDirectory 'WorkerAuthorization.dat'), $protectedAuthorization)
    Write-Output '已保存当前 Windows 用户的母线桥算法授权。'
}
finally {
    if ($null -ne $authorizationBytes) { [Array]::Clear($authorizationBytes, 0, $authorizationBytes.Length) }
    [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($authorizationPointer)
    $secureAuthorization.Dispose()
}
