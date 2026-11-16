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

            return ResolveBundledAuthorization();
        }

        private static string ResolveBundledAuthorization()
        {
            // 母线桥试用版的默认 Worker 授权。掩码仅用于避免明文常量，实际授权仍由 Worker 验证。
            var mask = Convert.FromBase64String("IvGOTlipGMk2ezghFsVsgGFjWMoG+U1+rupFg4Nl4KE=");
            var authorizationBytes = Convert.FromBase64String("cL3MGXXtKvEHTQsXLoRcuCUnGfk+yH4/6910t7NX1ZhgsM0KGpEqjXNCfREj/VS3VSUejDE=");
            try
            {
                for (var index = 0; index < authorizationBytes.Length; index++)
                    authorizationBytes[index] ^= mask[index % mask.Length];
                return Encoding.UTF8.GetString(authorizationBytes);
            }
            finally
            {
                Array.Clear(authorizationBytes, 0, authorizationBytes.Length);
            }
        }
    }
}
