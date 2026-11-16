using CKBusbarBridgePreview;
using System;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// 一次生成的阶段进度。子流程只回报文字和数量，不持有窗口或 SolidWorks 对象。
    /// </summary>
    internal sealed class GenerationProgress
    {
        private const int UnitsPerStage = 1000;
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly IProgress<FeedbackProgress> _sink;
        private int _completedStages;
        private int _totalStages;
        private string _stage;
        private int _stageUnits;

        internal GenerationProgress(IProgress<FeedbackProgress> sink)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            ShowPreparation("正在准备生成");
        }

        internal void ShowPreparation(string stage)
        {
            Publish(new FeedbackProgress { Stage = stage, Detail = string.Empty });
        }

        internal void SetStageCount(int count)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            _totalStages = count;
            _completedStages = 0;
        }

        internal T RunPreparation<T>(string stage, Func<T> execute)
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));
            // 解压和打开文档无法按数量估算，先显示滚动进度，随后进入已知的工作阶段。
            ShowPreparation(stage);
            return execute();
        }

        internal void RunStage(string stage, Action execute)
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));
            RunStage<object>(stage, () => { execute(); return null; });
        }

        internal T RunStage<T>(string stage, Func<T> execute)
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));
            _stage = stage;
            _stageUnits = 0;
            ReportDetail(string.Empty, 0, 0);
            T result = execute();
            // 只有业务调用正常结束才推进阶段；异常保持最后的真实进度，由外层收口。
            _stageUnits = UnitsPerStage;
            ReportDetail(string.Empty, 1, 1);
            _completedStages++;
            return result;
        }

        internal void ReportDetail(string detail, int current, int total)
        {
            // 子流程可以先准备模型再装配，同一阶段的总进度保持单调。
            if (total > 0)
                _stageUnits = Math.Max(_stageUnits,
                    Math.Min(UnitsPerStage - 1,
                        (int)(Math.Max(0d, Math.Min(1d, current / (double)total)) * UnitsPerStage)));
            Publish(new FeedbackProgress
            {
                Stage = _stage,
                Detail = detail ?? string.Empty,
                Current = _completedStages * UnitsPerStage + _stageUnits,
                Total = _totalStages * UnitsPerStage
            });
        }

        private void Publish(FeedbackProgress update)
        {
            try
            {
                _sink.Report(update);
            }
            catch (Exception exception)
            {
                // 提示线程关闭等故障不能触发模型事务回滚。
                Logger.Warn(exception, "生成进度上报失败，业务继续执行。");
            }
        }
    }
}
