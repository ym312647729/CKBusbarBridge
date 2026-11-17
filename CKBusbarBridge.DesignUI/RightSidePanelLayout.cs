using System;
using System.Windows;

namespace CKBusbarBridge.views
{
    /// <summary>参数窗口沿屏幕右侧排列，底部给任务通知留出空间。</summary>
    public static class RightSidePanelLayout
    {
        public const double EdgeSpacing = 8d;
        public const double ProgressAreaHeight = 176d;

        public static void Apply(Window panelWindow, double? preferredHeight = null, bool halfWidth = false)
        {
            if (panelWindow == null) throw new ArgumentNullException(nameof(panelWindow));
            var screenWorkArea = SystemParameters.WorkArea;
            var panelWidth = Math.Min(screenWorkArea.Width - EdgeSpacing * 2d,
                Math.Max(320d, screenWorkArea.Width / (halfWidth ? 2d : 4d)));
            var availableHeight = Math.Max(320d,
                screenWorkArea.Height - ProgressAreaHeight - EdgeSpacing);
            var panelHeight = preferredHeight.HasValue
                ? Math.Min(availableHeight, Math.Max(320d, preferredHeight.Value))
                : availableHeight;

            panelWindow.WindowState = WindowState.Normal;
            panelWindow.WindowStartupLocation = WindowStartupLocation.Manual;
            panelWindow.MinWidth = Math.Min(320d, panelWidth);
            panelWindow.MinHeight = Math.Min(320d, panelHeight);
            panelWindow.Width = panelWidth;
            panelWindow.Height = panelHeight;
            panelWindow.Left = screenWorkArea.Right - panelWidth - EdgeSpacing;
            panelWindow.Top = screenWorkArea.Top + EdgeSpacing;
        }
    }
}
