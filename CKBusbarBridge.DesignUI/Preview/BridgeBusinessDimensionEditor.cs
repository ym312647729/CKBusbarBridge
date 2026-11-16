using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Interop;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.DesignPreview
{
    internal sealed class BridgeBusinessDimensionEditor : IDisposable
    {
        private ManualDrawDimensionWindow _window;
        internal void Show(string label, float currentValue, int screenX, int screenY,
            IntPtr viewportWindow, Func<float, string> validate, Func<string, Task<string>> commit, Action cancel)
        {
            if (_window != null) throw new InvalidOperationException("当前尺寸编辑尚未结束。");
            var window = new ManualDrawDimensionWindow(label, currentValue, true, screenX, screenY)
            {
                Validate = validate,
                CommitRequested = value => commit(value.ToString("R", CultureInfo.InvariantCulture)),
                PreviewRequested = value => commit(value.ToString("R", CultureInfo.InvariantCulture))
            };
            var owner = GetAncestor(viewportWindow, 2);
            if (owner == IntPtr.Zero) throw new InvalidOperationException("无法取得尺寸编辑窗口的SolidWorks宿主窗口。");
            new WindowInteropHelper(window).Owner = owner;
            _window = window;
            var accepted = false;
            try { accepted = window.ShowDialog() == true; }
            finally
            {
                try { if (!accepted) cancel?.Invoke(); }
                finally { window.Close(); _window = null; }
            }
        }
        public void Dispose() { _window?.Close(); }
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    }
    internal static class DimensionFeedback
    {
        internal static void Error(Exception error, string message, string title)
        { FaceToWallPreviewTrace.Error(message, error); UiFeedback.Error(message + "\n" + error.Message, title); }
    }
}
