using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CKBusbarBridge.Services
{
    internal static class BusbarWorkerAuthorization
    {
        internal static string Resolve()
        {
            var configured = System.Environment.GetEnvironmentVariable("CK_BUSBAR_WORKER_AUTHORIZATION")?.Trim();
            if (!string.IsNullOrWhiteSpace(configured)) return configured;

            // 只读取当前 Windows 用户的本项目配置，不依赖联络排安装目录。
            var localFolder = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localFolder))
            {
                byte[] decodedAuthorization = null;
                try
                {
                    var encryptedAuthorization = File.ReadAllBytes(Path.Combine(localFolder, "CKBusbarBridge", "WorkerAuthorization.dat"));
                    decodedAuthorization = ProtectedData.Unprotect(encryptedAuthorization, null, DataProtectionScope.CurrentUser);
                    var storedAuthorization = Encoding.UTF8.GetString(decodedAuthorization).Trim();
                    if (!string.IsNullOrWhiteSpace(storedAuthorization)) return storedAuthorization;
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (Exception failure) when (failure is IOException || failure is UnauthorizedAccessException || failure is CryptographicException)
                {
                    throw new InvalidOperationException("无法读取当前 Windows 用户的母线桥算法授权，请在此账号下重新配置。", failure);
                }
                finally
                {
                    if (decodedAuthorization != null) Array.Clear(decodedAuthorization, 0, decodedAuthorization.Length);
                }
            }

            throw new InvalidOperationException("此方案需要母线桥算法授权，请联系管理员为当前 Windows 用户配置授权。");
        }
    }
}
