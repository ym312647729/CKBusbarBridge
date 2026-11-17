using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CKBusbarBridgePreview;
using RD.UI.Wpf.Controls;

namespace CKBusbarBridge.DesignPreview
{
    /// <summary>视口双击尺寸后直接输入；点击空白处提交，右键或 Esc 取消。</summary>
    public partial class ManualDrawDimensionWindow : Window
    {
        private static readonly Lazy<Cursor> ConfirmCursor = new Lazy<Cursor>(() =>
        {
            using (var stream = typeof(ManualDrawDimensionWindow).Assembly.GetManifestResourceStream("CKBusbarBridge.DimensionConfirm.cur"))
                return stream == null ? Cursors.Hand : new Cursor(stream);
        });
        private readonly string _initialText;
        private readonly float _initialValue;
        private readonly Func<Point, bool> _isEmptyViewport;
        private Cursor _previousCursor;
        private bool _confirmPressed, _cancelPressed, _showConfirmCursor, _applying, _closing, _closed, _closeAfterApply;
        private long _lastHover;
        internal Func<float, string> Validate { get; set; }
        internal Func<float, Func<bool>, Task<string>> CommitRequested { get; set; }

        internal ManualDrawDimensionWindow(string label, float initialValue, int screenX, int screenY, Func<Point, bool> isEmptyViewport)
        {
            _initialValue = initialValue;
            _initialText = initialValue.ToString("0.###", CultureInfo.CurrentCulture);
            _isEmptyViewport = isEmptyViewport;
            InitializeComponent();
            Title = BridgePreviewDimension.DimensionName(label);
            AutomationProperties.SetName(ValueInput, Title);
            AutomationProperties.SetHelpText(ValueInput, "单位：毫米。点击视口空白处确认，右键或 Esc 取消。");
            ValueInput.Text = _initialText;
            ValueInput.TextChanged += (sender, args) => { ValueInput.IsError = false; ValueInput.ToolTip = null; };
            Loaded += (sender, args) =>
            {
                try
                {
                    DrawingPreviewPopupPlacement.PlaceAtCapturedPoint(this, screenX - 12, screenY - 12);
                    ValueInput.Focus(); ValueInput.SelectAll();
                    if (!Mouse.Capture(Surface, CaptureMode.SubTree)) Cancel();
                }
                catch (Exception error) { DimensionFeedback.Error(error, "初始化预览尺寸输入框失败。", Title); Cancel(); }
            };
            Deactivated += (sender, args) => Cancel();
            PreviewKeyDown += (sender, args) =>
            {
                if (args.Key == Key.Escape) { args.Handled = true; Cancel(); }
            };
            Mouse.AddPreviewMouseDownOutsideCapturedElementHandler(Surface, BeginOutside);
            Mouse.AddPreviewMouseUpOutsideCapturedElementHandler(Surface, FinishPointer);
            Surface.AddHandler(PreviewMouseUpEvent, new MouseButtonEventHandler(FinishPointer), true);
            Surface.AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler((sender, args) =>
            {
                if (args.ChangedButton == MouseButton.Right) { args.Handled = true; _cancelPressed = true; }
            }), true);
            Surface.PreviewMouseMove += (sender, args) =>
            {
                var local = args.GetPosition(Surface);
                if (Inside(local) || _applying || _closing) { ShowConfirmCursor(false); return; }
                long now = Stopwatch.GetTimestamp();
                if (now - _lastHover < Stopwatch.Frequency / 25) return;
                _lastHover = now;
                ShowConfirmCursor(_isEmptyViewport(Surface.PointToScreen(local)));
            };
            Surface.LostMouseCapture += (sender, args) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (_closed || _closing) return;
                if (Mouse.Captured is Visual child && (ReferenceEquals(child, Surface) || Surface.IsAncestorOf(child))) return;
                if (!IsActive || Mouse.Captured != null || !Mouse.Capture(Surface, CaptureMode.SubTree)) Cancel();
            }));
        }

        private bool Inside(Point point) => new Rect(new Point(), Surface.RenderSize).Contains(point);
        private void BeginOutside(object sender, MouseButtonEventArgs args)
        {
            args.Handled = true;
            if (_closing) return;
            if (args.ChangedButton == MouseButton.Right) _cancelPressed = true;
            if (!_applying && args.ChangedButton == MouseButton.Left)
                _confirmPressed = _isEmptyViewport(Surface.PointToScreen(args.GetPosition(Surface)));
        }
        private void FinishPointer(object sender, MouseButtonEventArgs args)
        {
            if (args.ChangedButton == MouseButton.Right && _cancelPressed)
            { _cancelPressed = false; args.Handled = true; Cancel(); return; }
            if (args.ChangedButton != MouseButton.Left || !_confirmPressed) return;
            args.Handled = true; _confirmPressed = false;
            var local = args.GetPosition(Surface);
            if (Inside(local) || _applying || _closing || !_isEmptyViewport(Surface.PointToScreen(local))) return;
            _applying = true; ShowConfirmCursor(false);
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(Confirm));
        }
        private async void Confirm()
        {
            if (_closed || _closing) return;
            ValueInput.IsReadOnly = true;
            bool accepted = false;
            try
            {
                var text = ValueInput.Text?.Trim();
                float value = _initialValue;
                if (text != _initialText &&
                    (!(float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                       float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) || float.IsNaN(value) || float.IsInfinity(value)))
                { ShowError("请输入有效的毫米数值。"); return; }
                if (value == _initialValue) { accepted = true; return; }
                var failure = Validate?.Invoke(value);
                if (string.IsNullOrEmpty(failure) && CommitRequested != null)
                    failure = await CommitRequested(value, () => !_closed && !_closing && !_closeAfterApply);
                if (string.IsNullOrEmpty(failure)) accepted = true;
                else if (!_closeAfterApply) ShowError(failure);
            }
            catch (Exception error) { ShowError(error.Message); }
            finally
            {
                _applying = false;
                ValueInput.IsReadOnly = false;
                if (accepted || _closeAfterApply) Cancel();
            }
        }
        private void ShowError(string error)
        {
            if (_closed || _closing) return;
            ValueInput.IsError = true; ValueInput.ToolTip = error;
            ValueInput.Focus(); ValueInput.SelectAll();
            UiFeedback.Error(error, "修改尺寸");
        }
        private void ShowConfirmCursor(bool visible)
        {
            if (visible == _showConfirmCursor) return;
            _showConfirmCursor = visible;
            if (visible) { _previousCursor = Mouse.OverrideCursor; Mouse.OverrideCursor = ConfirmCursor.Value; }
            else Mouse.OverrideCursor = _previousCursor;
        }
        internal void Cancel()
        {
            if (_closed || _closing) return;
            if (_applying) { _closeAfterApply = true; return; }
            _closing = true;
            ShowConfirmCursor(false);
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (!_closed) Close(); }));
        }
        protected override void OnClosed(EventArgs args)
        {
            _closed = true; ShowConfirmCursor(false);
            if (Mouse.Captured is Visual child && (ReferenceEquals(child, Surface) || Surface.IsAncestorOf(child))) Mouse.Capture(null);
            base.OnClosed(args);
        }
    }


    /// <summary>所有绘制预览编辑框共用物理像素定位，保持跨屏DPI和工作区边界一致。</summary>
    internal static class DrawingPreviewPopupPlacement
    {
        internal static void PlaceAtCapturedPoint(Window popup, int popupScreenX, int popupScreenY)
        {
            IntPtr popupHandle = new WindowInteropHelper(popup).Handle;
            NativeRectangle popupBounds;
            if (!GetWindowRect(popupHandle, out popupBounds)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var workArea = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(popupScreenX, popupScreenY)).WorkingArea;
            int popupLeft = Math.Max(workArea.Left, Math.Min(popupScreenX, workArea.Right - (popupBounds.Right - popupBounds.Left)));
            int popupTop = Math.Max(workArea.Top, Math.Min(popupScreenY, workArea.Bottom - (popupBounds.Bottom - popupBounds.Top)));
            if (!SetWindowPos(popupHandle, IntPtr.Zero, popupLeft, popupTop, 0, 0, 0x15))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法将预览编辑框定位到双击位置。");
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativeRectangle { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr window, out NativeRectangle rectangle);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    }
}
