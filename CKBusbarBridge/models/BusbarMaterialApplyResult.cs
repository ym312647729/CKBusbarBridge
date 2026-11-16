using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.models
{
    /// <summary>
    /// 总装配体铜排批量设置材质的执行结果。
    /// </summary>
    public class BusbarMaterialApplyResult
    {
        public bool IsDatabaseResolved { get; set; }

        public bool IsDefaultMaterialFallback { get; set; }

        public string MaterialName { get; set; }

        public string DatabasePath { get; set; }

        public int SuccessCount { get; set; }

        public int FailedCount { get; set; }

        public int SkippedCount { get; set; }

        public string ErrorMessage { get; set; }

        public List<string> Failures { get; } = new List<string>();

        /// <summary>
        /// 生成用于界面提示的摘要文本。
        /// </summary>
        public string BuildSummaryMessage()
        {
            if (!string.IsNullOrWhiteSpace(ErrorMessage))
            {
                return ErrorMessage;
            }

            if (IsDefaultMaterialFallback)
            {
                return "铜排材质库不可用，已跳过材质设置并保留零件当前材质，继续执行后续流程。";
            }

            var summary = $"铜排材质：{MaterialName}（成功 {SuccessCount}";
            if (FailedCount > 0)
            {
                summary += $"，失败 {FailedCount}";
            }

            if (SkippedCount > 0)
            {
                summary += $"，跳过 {SkippedCount}";
            }

            summary += "）";

            if (Failures.Count == 0)
            {
                return summary;
            }

            var detail = string.Join("\n", Failures.Take(5));
            if (Failures.Count > 5)
            {
                detail += $"\n... 另有 {Failures.Count - 5} 项失败";
            }

            return summary + "\n" + detail;
        }
    }

    /// <summary>
    /// 将铜排材质设置隔离为非致命步骤；自动生成传空提示回调，仅记日志并继续。
    /// </summary>
    internal static class BusbarMaterialFailurePolicy
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        internal static BusbarMaterialApplyResult Execute(
            Func<BusbarMaterialApplyResult> applyMaterial,
            string materialName,
            Action<string> showWarning)
        {
            BusbarMaterialApplyResult result;
            try
            {
                result = applyMaterial?.Invoke();
                if (result == null)
                {
                    result = CreateFailureResult(
                        materialName,
                        "铜排材质设置未返回执行结果。");
                }
            }
            catch (Exception exception)
            {
                Logger.Warn(exception, "铜排材质设置异常，继续后续流程：材质={0}", materialName);
                result = CreateFailureResult(materialName, exception.Message);
            }

            if (HasFailure(result))
            {
                Logger.Warn("铜排材质设置未完成，继续后续流程：材质={0}，原因={1}", materialName, result.BuildSummaryMessage());
                try
                {
                    showWarning?.Invoke(BuildContinueWarning(result));
                }
                catch
                {
                    // 提示窗口自身失败也不能反向中断生成流程。
                }
            }

            return result;
        }

        internal static bool HasFailure(BusbarMaterialApplyResult result)
        {
            return result == null ||
                !string.IsNullOrWhiteSpace(result.ErrorMessage) ||
                result.FailedCount > 0;
        }

        internal static string BuildContinueWarning(
            BusbarMaterialApplyResult result)
        {
            var failureSummary = result?.BuildSummaryMessage();
            if (string.IsNullOrWhiteSpace(failureSummary))
            {
                failureSummary = "未知材质设置错误。";
            }

            return "铜排材质设置失败，本次生成将跳过后续铜排材质设置。" +
                "\n点击“确定”继续执行后续驱动流程。\n\n" +
                failureSummary;
        }

        private static BusbarMaterialApplyResult CreateFailureResult(
            string materialName,
            string errorMessage)
        {
            return new BusbarMaterialApplyResult
            {
                MaterialName = materialName,
                ErrorMessage = string.IsNullOrWhiteSpace(errorMessage)
                    ? "铜排材质设置发生未知错误。"
                    : errorMessage
            };
        }
    }

    /// <summary>
    /// 将铜排材质反读隔离为非致命步骤；反读不到材质时静默保留当前参数值。
    /// </summary>
    internal static class BusbarMaterialReadFailurePolicy
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        internal static bool NotifyIfFailed(
            string materialName,
            Action<string> showWarning)
        {
            if (!string.IsNullOrWhiteSpace(materialName))
            {
                return false;
            }

            // 保留现有回调参数以兼容各场景反读入口，但材质缺失不再调用提示窗口。
            Logger.Warn("伸出排材质无法反读，保留当前材质参数并继续读取其他参数。");
            return true;
        }

        internal static string BuildContinueWarning()
        {
            return "铜排材质反读失败，本次将跳过铜排材质反读并保留当前默认值。" +
                "\n点击“确定”继续执行其他参数反读和后续流程。";
        }
    }
}
