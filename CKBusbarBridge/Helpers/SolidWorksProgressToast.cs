using CKBusbarBridgePreview;
using NLog;
using RD.UI.Wpf.Controls;
using System;

namespace CKBusbarBridge.Helpers
{
    /// <summary>在提示专用 STA 上显示 RD.UI.Wpf 右下角进度。</summary>
    internal static class SolidWorksProgressToast
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public static SolidWorksProgressToastHandle Begin(string progressTitle)
        {
            try
            {
                return UiFeedback.InvokePresentation(() =>
                    new SolidWorksProgressToastHandle(RDToast.BeginProgress(
                        string.IsNullOrWhiteSpace(progressTitle) ? "正在处理" : progressTitle.Trim())));
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "RD 进度提示创建失败，不影响业务执行。");
                return SolidWorksProgressToastHandle.None;
            }
        }

        public static void ShowError(string title, string message) =>
            UiFeedback.Error(message, title);

        public static void ShowWarning(string title, string message) =>
            UiFeedback.Warning(message, title);

        public static void ShowInformation(string title, string message) =>
            UiFeedback.Information(message, title);
    }

    internal sealed class SolidWorksProgressToastHandle : IProgress<SolidWorksProgressToastUpdate>
    {
        internal static readonly SolidWorksProgressToastHandle None = new SolidWorksProgressToastHandle(null);
        private readonly RDProgressToast _toast;
        private bool _finished;

        internal SolidWorksProgressToastHandle(RDProgressToast toast)
        {
            _toast = toast;
        }

        public void Report(SolidWorksProgressToastUpdate update)
        {
            if (_toast == null || _finished || update == null) return;
            var stage = update.Stage;
            var current = update.Current;
            var total = update.Total;
            var detail = update.Detail;
            UiFeedback.PostPresentation(() =>
            {
                if (_finished) return;
                _toast.Report(new RDToastProgress
                {
                    Stage = stage,
                    Current = current,
                    Total = total,
                    Detail = detail
                });
            });
        }

        public void Finish()
        {
            if (_toast == null || _finished) return;
            UiFeedback.PostPresentation(() =>
            {
                if (_finished) return;
                _finished = true;
                _toast.Finish();
            });
        }
    }

    internal sealed class SolidWorksProgressToastUpdate
    {
        public string Stage { get; set; }
        public int Current { get; set; }
        public int Total { get; set; }
        public string Detail { get; set; }
        public long? EstimatedDurationMilliseconds { get; set; }
    }
}
