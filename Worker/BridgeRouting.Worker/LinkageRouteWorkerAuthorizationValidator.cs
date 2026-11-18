using System;
using System.Security.Cryptography;
using System.Text;

namespace RouteLinkBar.Algorithm.Worker
{
    /// <summary>
    /// 校验调用方随握手和每次求解请求发送的共享授权码。
    /// Worker 只保存 SHA-256，不在源码、协议响应或日志中保留授权码明文。
    /// </summary>
    internal static class LinkageRouteWorkerAuthorizationValidator
    {
        private const int MaximumAuthorizationCodeLength = 128;

        private static readonly byte[] ExpectedAuthorizationCodeHash =
        {
            0x51, 0x4B, 0x24, 0x26, 0xF4, 0x82, 0xCF, 0x37,
            0x0F, 0x5F, 0x50, 0xBF, 0x5C, 0x67, 0xDD, 0x75,
            0x32, 0xB9, 0x2C, 0xCD, 0xAC, 0x04, 0x95, 0x8D,
            0x0F, 0xA9, 0xB5, 0x59, 0x16, 0xE0, 0x3A, 0xA6
        };

        public static bool IsAuthorized(string providedAuthorizationCode)
        {
            if (string.IsNullOrWhiteSpace(providedAuthorizationCode) ||
                providedAuthorizationCode.Length > MaximumAuthorizationCodeLength)
            {
                return false;
            }

            byte[] providedAuthorizationCodeHash;
            using (var authorizationCodeHasher = SHA256.Create())
            {
                providedAuthorizationCodeHash = authorizationCodeHasher.ComputeHash(
                    Encoding.UTF8.GetBytes(providedAuthorizationCode));
            }

            return FixedTimeEquals(
                ExpectedAuthorizationCodeHash,
                providedAuthorizationCodeHash);
        }

        private static bool FixedTimeEquals(
            byte[] expectedAuthorizationCodeHash,
            byte[] providedAuthorizationCodeHash)
        {
            if (expectedAuthorizationCodeHash == null ||
                providedAuthorizationCodeHash == null ||
                expectedAuthorizationCodeHash.Length != providedAuthorizationCodeHash.Length)
            {
                return false;
            }

            var accumulatedHashDifference = 0;
            for (var hashByteIndex = 0;
                hashByteIndex < expectedAuthorizationCodeHash.Length;
                hashByteIndex++)
            {
                accumulatedHashDifference |= expectedAuthorizationCodeHash[hashByteIndex] ^
                    providedAuthorizationCodeHash[hashByteIndex];
            }

            return accumulatedHashDifference == 0;
        }
    }
}
