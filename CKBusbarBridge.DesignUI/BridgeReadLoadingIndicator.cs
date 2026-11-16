using System;
using System.Threading;
using System.Windows;
using CKBusbarBridgePreview;
using RD.UI.Wpf.Controls;

namespace CKBusbarBridge.views
{
    /// <summary>读取状态交给 RD.UI.Wpf 的右下角任务卡呈现。</summary>
    internal sealed class BridgeReadLoadingIndicator : IDisposable
    {
        private RDProgressToast _progressToast;
        private int _disposed;

        internal BridgeReadLoadingIndicator(IntPtr ownerHandle, Point screenCenter)
        {
            UiFeedback.PostPresentation(() =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                _progressToast = RDToast.BeginProgress("读取母线桥方案");
                _progressToast.Report(new RDToastProgress
                {
                    Stage = "正在读取装配体",
                    Detail = "正在准备参数与视口预览"
                });
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            UiFeedback.PostPresentation(() =>
            {
                _progressToast?.Finish();
                _progressToast = null;
            });
        }
    }
}
