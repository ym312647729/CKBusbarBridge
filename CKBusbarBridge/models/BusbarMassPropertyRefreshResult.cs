using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.models
{
    /// <summary>
    /// 刷新铜排开孔后重新计算质量属性的执行结果。
    /// </summary>
    public class BusbarMassPropertyRefreshResult
    {
        public int SuccessCount { get; set; }

        public int FailedCount { get; set; }

        public int SkippedCount { get; set; }

        /// <summary>
        /// 本次刷新在铜排特征树中统计到的绝缘子开孔草图总数。
        /// </summary>
        public int InsulatorHoleCount { get; set; }

        public string ErrorMessage { get; set; }

        public List<string> Failures { get; } = new List<string>();

        /// <summary>
        /// 生成用于日志或界面提示的摘要文本。
        /// </summary>
        public string BuildSummaryMessage()
        {
            if (!string.IsNullOrWhiteSpace(ErrorMessage))
            {
                return ErrorMessage;
            }

            var summary = $"铜排开孔后质量属性：成功 {SuccessCount}，绝缘子孔 {InsulatorHoleCount} 个";
            if (FailedCount > 0)
            {
                summary += $"，失败 {FailedCount}";
            }

            if (SkippedCount > 0)
            {
                summary += $"，跳过 {SkippedCount}";
            }

            summary += "。";

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
}
