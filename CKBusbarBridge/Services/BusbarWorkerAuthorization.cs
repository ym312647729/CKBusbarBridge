using System;

namespace CKBusbarBridge.Services
{
    internal static class BusbarWorkerAuthorization
    {
        // The preview worker uses its original license validation. Customer authorization is provisioned separately.
        internal static string Resolve()
        {
            var configured = System.Environment.GetEnvironmentVariable("CK_BUSBAR_WORKER_AUTHORIZATION")?.Trim();
            if (string.IsNullOrWhiteSpace(configured))
                throw new InvalidOperationException("此方案需要川开试用授权，请联系管理员配置 CK_BUSBAR_WORKER_AUTHORIZATION 后重启 SolidWorks。");
            return configured;
        }
    }
}
