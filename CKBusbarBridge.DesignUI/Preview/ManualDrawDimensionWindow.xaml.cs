using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CKBusbarBridgePreview;
using RD.UI.Wpf.Controls;

namespace CKBusbarBridge.DesignPreview
{
    /// <summary>尺寸经原参数入口预览；停止输入一秒后更新，确认保留一次修改，取消恢复快照。</summary>
    public partial class ManualDrawDimensionWindow : RDDialogWindow
    {
        private const float AdjustmentStep = 10f;
        private const double PixelsPerStep = 8;
        private readonly float originalValue;
        private float preciseValue;
        private string preciseText;
        private readonly string originalText;
        private readonly string unitSuffix;
        private readonly int popupScreenX;
        private readonly int popupScreenY;
        private readonly DispatcherTimer previewTimer;
        private int remainingWheelDelta;
        private double previousDragX;
        private double remainingDragPixels;
        private bool wheelDragging;
        private bool adjustingByStep;
        public Func<float, string> Validate { get; set; }
        internal Func<float, Task<string>> CommitRequested { get; set; }
        internal Func<float, Task<string>> PreviewRequested { get; set; }
        private Task<string> pendingPreview = Task.FromResult<string>(null);
        private int inputRevision;
        private bool finishing;

        public ManualDrawDimensionWindow(string dimensionName, float initialValue, bool isLength,
            int screenX, int screenY)
        {
            originalValue = initialValue;
            preciseValue = initialValue;
            unitSuffix = isLength ? "mm" : "°";
            originalText = initialValue.ToString("0.#", CultureInfo.CurrentCulture);
            preciseText = originalText;
            popupScreenX = screenX;
            popupScreenY = screenY;
            InitializeComponent();
            dimensionName = BridgePreviewDimension.DimensionName(dimensionName);
            Title = dimensionName;
            DimensionName.Text = dimensionName;
            DimensionName.ToolTip = dimensionName;
            ValueInput.Text = originalText;
            previewTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            previewTimer.Tick += OnPreviewTick;
            Closing += async (sender, args) =>
            {
                if (pendingPreview.IsCompleted) return;
                args.Cancel = true;
                try { await CancelEdit(); }
                catch (Exception failure) { DimensionFeedback.Error(failure, "等待尺寸预览结束失败。", Title); }
            };
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 每次按内容确定高度，不恢复历史尺寸；跟随双击位置，使用物理像素避免DPI二次换算。
                DrawingPreviewPopupPlacement.PlaceAtCapturedPoint(this, popupScreenX, popupScreenY);
                ValueInput.Focus();
                ValueInput.SelectAll();
            }
            catch (Exception failure) { DimensionFeedback.Error(failure, "初始化预览尺寸输入框失败。", Title); }
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            try
            {
                previewTimer.Stop();
                previewTimer.Tick -= OnPreviewTick;
                wheelDragging = false;
                if (AdjustmentWheel.IsMouseCaptured) AdjustmentWheel.ReleaseMouseCapture();
            }
            catch (Exception failure) { DimensionFeedback.Error(failure, "关闭预览尺寸输入框失败。", Title); }
        }

        private bool ValidateInput()
        {
            float inputValue;
            if (!TryReadValue(out inputValue)) { ShowError("请输入有效数值。"); return false; }
            string inputError = Validate?.Invoke(inputValue);
            if (!string.IsNullOrEmpty(inputError)) { ShowError(inputError); return false; }
            ErrorText.Visibility = Visibility.Collapsed;
            ValueInput.IsError = false;
            return true;
        }

        private bool TryReadValue(out float value)
        {
            string inputText = (ValueInput.Text ?? string.Empty).Trim();
            // 一位小数只是显示精度，未经修改就确认时保留原几何精度。
            if (inputText == preciseText) { value = preciseValue; return true; }
            if (inputText == originalText) { value = originalValue; return true; }
            if (inputText.EndsWith(unitSuffix, StringComparison.OrdinalIgnoreCase))
                inputText = inputText.Substring(0, inputText.Length - unitSuffix.Length).Trim();
            bool parsed = float.TryParse(inputText, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                float.TryParse(inputText, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            if (!parsed || float.IsNaN(value) || float.IsInfinity(value)) return false;
            return true;
        }

        private async void OnConfirm(object sender, RoutedEventArgs e)
        {
            try { await Confirm(); }
            catch (Exception failure) { DimensionFeedback.Error(failure, "确认预览尺寸失败。", Title); }
        }
        private async Task Confirm()
        {
            if (finishing) return;
            finishing = true;
            ValueInput.IsEnabled = AdjustmentWheel.IsEnabled = false;
            try
            {
                previewTimer.Stop();
                await pendingPreview;
                if (!ValidateInput() || !TryReadValue(out var acceptedValue)) return;
                var failure = CommitRequested == null ? null : await CommitRequested(acceptedValue);
                if (!string.IsNullOrEmpty(failure)) { ShowError(failure); return; }
                DialogResult = true;
            }
            finally { finishing = false; ValueInput.IsEnabled = AdjustmentWheel.IsEnabled = true; }
        }

        private async void OnCancel(object sender, RoutedEventArgs e)
        {
            try { await CancelEdit(); }
            catch (Exception failure) { DimensionFeedback.Error(failure, "取消预览尺寸修改失败。", Title); }
        }
        private async Task CancelEdit()
        {
            if (finishing) return;
            finishing = true;
            try
            {
                previewTimer.Stop();
                try { await pendingPreview; }
                catch (Exception failure) { FaceToWallPreviewTrace.Error("取消尺寸时等待预览失败，继续恢复编辑前快照", failure); }
                DialogResult = false;
            }
            finally { finishing = false; }
        }

        private void QueuePreview()
        {
            if (previewTimer == null) return;
            inputRevision++;
            previewTimer.Stop();
            previewTimer.Interval = TimeSpan.FromSeconds(1);
            previewTimer.Start();
        }

        private async void OnPreviewTick(object sender, EventArgs e)
        {
            try
            {
                previewTimer.Stop();
                var requestedRevision = inputRevision;
                await pendingPreview;
                if (finishing || requestedRevision != inputRevision || !IsVisible || !ValidateInput() || !TryReadValue(out var candidate)) return;
                pendingPreview = PreviewRequested == null ? Task.FromResult<string>(null) : PreviewRequested(candidate);
                var error = await pendingPreview;
                if (IsVisible && requestedRevision == inputRevision && !string.IsNullOrEmpty(error)) ShowError(error);
            }
            catch (Exception failure) { DimensionFeedback.Error(failure, "预览尺寸修改失败。", Title); }
            finally
            {
                // An unexpected failed preview must not leave Confirm/Cancel awaiting
                // the same faulted task forever. The host owns the value rollback.
                if (pendingPreview.IsFaulted || pendingPreview.IsCanceled) pendingPreview = Task.FromResult<string>(null);
            }
        }

        private void ChangeValue(int numberOfSteps)
        {
            if (numberOfSteps == 0) return;
            float inputValue;
            if (!TryReadValue(out inputValue)) { ShowError("请输入有效数值后再微调。"); return; }
            float adjustedValue = inputValue + numberOfSteps * AdjustmentStep;
            if (float.IsNaN(adjustedValue) || float.IsInfinity(adjustedValue)) { ShowError("尺寸数值超出有效范围。"); return; }
            adjustingByStep = true;
            try { preciseValue = adjustedValue; preciseText = adjustedValue.ToString("0.#", CultureInfo.CurrentCulture); ValueInput.Text = preciseText; }
            finally { adjustingByStep = false; }
            QueuePreview();
        }

        private void OnEditorMouseWheel(object sender, MouseWheelEventArgs e)
        {
            try
            {
                e.Handled = true;
                remainingWheelDelta += e.Delta;
                int numberOfSteps = remainingWheelDelta / Mouse.MouseWheelDeltaForOneLine;
                remainingWheelDelta %= Mouse.MouseWheelDeltaForOneLine;
                if (numberOfSteps == 0) return;
                ChangeValue(numberOfSteps);
                WheelRotation.X = (WheelRotation.X + numberOfSteps * 2) % PixelsPerStep;
            }
            catch (Exception failure) { DimensionFeedback.Error(failure, "滚动调整预览尺寸失败。", Title); }
        }

        private void OnWheelPressed(object sender, MouseButtonEventArgs e)
        {
            try
            {
                e.Handled = true;
                previousDragX = e.GetPosition(this).X;
                remainingDragPixels = 0;
                wheelDragging = AdjustmentWheel.CaptureMouse();
            }
            catch (Exception failure) { DimensionFeedback.Error(failure, "开始滚轮微调失败。", Title); }
        }

        private void OnWheelMoved(object sender, MouseEventArgs e)
        {
            try
            {
                if (!wheelDragging) return;
                if (e.LeftButton != MouseButtonState.Pressed) { AdjustmentWheel.ReleaseMouseCapture(); return; }
                e.Handled = true;
                double dragX = e.GetPosition(this).X;
                double movement = dragX - previousDragX;
                previousDragX = dragX;
                remainingDragPixels += movement;
                int numberOfSteps = (int)(remainingDragPixels / PixelsPerStep);
                remainingDragPixels -= numberOfSteps * PixelsPerStep;
                WheelRotation.X = (WheelRotation.X + movement) % PixelsPerStep;
                ChangeValue(numberOfSteps);
            }
            catch (Exception failure) { DimensionFeedback.Error(failure, "滚轮微调预览尺寸失败。", Title); }
        }

        private void OnWheelReleased(object sender, MouseButtonEventArgs e)
        {
            try { e.Handled = true; AdjustmentWheel.ReleaseMouseCapture(); ValueInput.Focus(); ValueInput.SelectAll(); }
            catch (Exception failure) { DimensionFeedback.Error(failure, "结束滚轮微调失败。", Title); }
        }

        private void OnWheelCaptureLost(object sender, MouseEventArgs e)
        {
            try { wheelDragging = false; remainingDragPixels = 0; }
            catch (Exception failure) { DimensionFeedback.Error(failure, "释放尺寸滚轮失败。", Title); }
        }

        private async void OnEditorKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (e.Key == Key.Escape) { e.Handled = true; await CancelEdit(); }
                else if (e.Key == Key.Enter) { e.Handled = true; await Confirm(); }
                else if (e.Key == Key.Up || e.Key == Key.Down)
                { e.Handled = true; ChangeValue(e.Key == Key.Up ? 1 : -1); }
            }
            catch (Exception failure) { DimensionFeedback.Error(failure, "处理预览尺寸快捷键失败。", Title); }
        }

        private void OnValueChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                if (ErrorText == null) return;
                ErrorText.Text = string.Empty;
                ErrorText.Visibility = Visibility.Collapsed;
                ValueInput.IsError = false;
                if (!adjustingByStep) QueuePreview();
            }
            catch (Exception failure) { DimensionFeedback.Error(failure, "更新尺寸输入提示失败。", Title); }
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.ToolTip = message;
            ErrorText.Visibility = Visibility.Visible;
            ValueInput.IsError = true;
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
