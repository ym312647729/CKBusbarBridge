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
        internal void Show(BridgeDimensionEditRequest request, BridgeOpenGlViewport viewport,
            Func<float, string> validate, Func<string, Func<bool>, Task<string>> commit)
        {
            if (_window != null) throw new InvalidOperationException("当前尺寸编辑尚未结束。");
            var window = new ManualDrawDimensionWindow(request.Dimension.Label, request.Dimension.Value,
                request.ScreenX, request.ScreenY, viewport.IsEmptyDimensionViewport)
            {
                Validate = validate,
                CommitRequested = (value, isOpen) => commit(value.ToString("R", CultureInfo.InvariantCulture), isOpen)
            };
            var owner = GetAncestor(viewport.WindowHandle, 2);
            if (owner == IntPtr.Zero) throw new InvalidOperationException("无法取得尺寸编辑窗口的SolidWorks宿主窗口。");
            new WindowInteropHelper(window).Owner = owner;
            _window = window;
            window.Closed += (sender, args) =>
            {
                if (ReferenceEquals(_window, window)) _window = null;
                viewport.HighlightEditedDimension(null);
            };
            viewport.HighlightEditedDimension(request.Dimension);
            try { window.Show(); }
            catch { window.Close(); throw; }
        }
        public void Dispose() { _window?.Cancel(); }
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    }
    internal static class DimensionFeedback
    {
        internal static void Error(Exception error, string message, string title)
        { FaceToWallPreviewTrace.Error(message, error); UiFeedback.Error(message + "\n" + error.Message, title); }
    }
}
