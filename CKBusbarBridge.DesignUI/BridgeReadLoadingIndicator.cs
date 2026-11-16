using System;
using CKBusbarBridgePreview;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace CKBusbarBridge.views
{
    // 加载动画复用插件提示 STA；读取结束只关窗口，不销毁 COM 上下文。
    internal sealed class BridgeReadLoadingIndicator : IDisposable
    {
        private readonly IntPtr _owner;
        private readonly Point _center;
        private Window _window;
        private int _disposed;

        internal BridgeReadLoadingIndicator(IntPtr owner, Point center)
        {
            _owner = owner;
            _center = center;
            UiFeedback.PostPresentation(ShowOnPresentationThread);
        }

        private void ShowOnPresentationThread()
        {
            // 快速读取时 Dispose 可能先于显示回调，不再创建任何 WPF 窗口资源。
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                var window = CreateWindow();
                _window = window;
                if (_owner != IntPtr.Zero) new WindowInteropHelper(window).Owner = _owner;
                window.SourceInitialized += (sender, args) =>
                {
                    var transform = PresentationSource.FromVisual(window).CompositionTarget.TransformFromDevice;
                    var center = transform.Transform(_center);
                    window.Left = center.X - window.Width / 2;
                    window.Top = center.Y - window.Height / 2;
                };
                window.Closed += (sender, args) => _window = null;
                if (Volatile.Read(ref _disposed) != 0)
                {
                    CloseOnPresentationThread();
                    return;
                }
                UiFeedback.ShowPresentationWindow(window);
            }
            catch (Exception error)
            {
                NLog.LogManager.GetCurrentClassLogger().Error(error, "母线桥读取加载动画失败");
                CloseOnPresentationThread();
            }
        }

        private Window CreateWindow()
        {
            // 同步 COM 读取期间遮罩可能尚未重绘，必须同时匹配预览画布的底色。
            var background = new SolidColorBrush(BusbarBridgeJointPreviewControl.PreviewBackgroundColor);
            var rotation = new RotateTransform();
            var spinner = new Grid { Width = 40, Height = 40, Margin = new Thickness(0, 0, 0, 14) };
            spinner.Children.Add(new Ellipse { Stroke = new SolidColorBrush(Color.FromRgb(195, 205, 219)), StrokeThickness = 3, Margin = new Thickness(3) });
            spinner.Children.Add(new Path
            {
                Data = Geometry.Parse("M 20,3 A 17,17 0 1 1 3,20"),
                Stroke = new SolidColorBrush(Color.FromRgb(40, 115, 203)), StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = rotation
            });
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
            content.Children.Add(spinner);
            content.Children.Add(new TextBlock { Text = "正在加载母线桥设计…", FontSize = 15, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center });
            content.Children.Add(new TextBlock { Text = "正在读取装配体并准备预览，请稍候", Foreground = Brushes.SlateGray, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });
            var window = new Window
            {
                Width = 360, Height = 170, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, ShowActivated = false, Focusable = false,
                Background = background, Title = "母线桥设计加载中",
                Content = new Border { Background = background, Child = content }
            };
            window.Loaded += (sender, args) => rotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
            window.Closed += (sender, args) => rotation.BeginAnimation(RotateTransform.AngleProperty, null);
            return window;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            UiFeedback.PostPresentation(CloseOnPresentationThread);
        }

        private void CloseOnPresentationThread()
        {
            var window = _window;
            _window = null;
            window?.Close();
        }
    }
}
