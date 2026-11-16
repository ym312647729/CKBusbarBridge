using System;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 统一检查独立基座流程中 SolidWorks 返回的关键布尔结果。
    /// </summary>
    internal static class FaceToFaceBaseOperationGuard
    {
        internal static void EnsureRebuildSucceeded(
            bool rebuildSucceeded,
            string operationName,
            string modelIdentity)
        {
            EnsureRebuildSucceeded(
                rebuildSucceeded,
                operationName,
                modelIdentity,
                null);
        }

        internal static void EnsureRebuildSucceeded(
            bool rebuildSucceeded,
            string operationName,
            string modelIdentity,
            Func<string> diagnosticsFactory)
        {
            if (rebuildSucceeded)
            {
                return;
            }

            string diagnostics = null;
            if (diagnosticsFactory != null)
            {
                try
                {
                    diagnostics = diagnosticsFactory();
                }
                catch (Exception exception)
                {
                    diagnostics = "重建诊断采集失败：" + exception.Message;
                }
            }

            var message = $"{operationName}重建失败：{modelIdentity}";
            if (!string.IsNullOrWhiteSpace(diagnostics))
            {
                message += System.Environment.NewLine + diagnostics;
            }

            throw new InvalidOperationException(
                message);
        }
    }
}
