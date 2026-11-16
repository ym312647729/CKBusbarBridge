using HandyControl.Controls;
using HandyControl.Data;
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Window = System.Windows.Window;

namespace CKBusbarBridgePreview
{
    internal static class FeedbackRuntime
    {
        private const double ProgressCardMargin = 12d;
        private const double SolidWorksEdgeGap = 16d;
        private const double ProgressCardStackGap = 10d;
        internal const string DialogTag = "CKBusbarBridge.UiFeedbackDialog";
        private static readonly Logger ProgressToastLogger = LogManager.GetCurrentClassLogger();
        private static readonly object Gate = new object();
        private static readonly List<Window> OpenProgressWindows = new List<Window>();
        private static PresentationDispatcherThread _presentationThread;
        private static bool _presentationShutdownRequested;
        private static IntPtr _solidWorksMainWindowHandle;

        internal static void EnsureApplication()
        {
            var currentApplication = Application.Current;
            if (currentApplication != null)
            {
                return;
            }

            // 在首次调用提示的宿主 UI 线程建立 Application，避免随后创建的业务
            // 页面与 Application 分属两个 Dispatcher。
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                var application = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                return;
            }

            InvokePresentation(() => { EnsureApplication(); return true; });
        }

        internal static MessageBoxResult ShowDialog(
            Window owner,
            string message,
            string title,
            MessageBoxButton buttons,
            MessageBoxImage icon,
            MessageBoxResult defaultResult)
        {
            var dialog = CreateDialogWindow(owner, title);
            var options = GetDialogResults(buttons);
            var fallbackResult = ResolveFallbackResult(buttons, defaultResult);
            var selectedResult = fallbackResult;
            dialog.Content = CreateDialogContent(
                dialog.Title,
                message,
                icon,
                options,
                defaultResult,
                result =>
                {
                    selectedResult = result;
                    dialog.Close();
                });
            dialog.PreviewKeyDown += (ignoredSender, keyArgs) =>
            {
                if (keyArgs.Key == System.Windows.Input.Key.Escape)
                {
                    selectedResult = fallbackResult;
                    dialog.Close();
                    keyArgs.Handled = true;
                }
            };
            dialog.ShowDialog();
            return selectedResult;
        }

        private static Window CreateDialogWindow(Window owner, string title)
        {
            var dialog = new Window
            {
                Tag = DialogTag,
                Title = string.IsNullOrWhiteSpace(title) ? "提示" : title,
                Width = 480d,
                MinHeight = 190d,
                MaxHeight = Math.Max(240d, SystemParameters.WorkArea.Height - 80d),
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.SingleBorderWindow,
                Background = new SolidColorBrush(Color.FromRgb(247, 248, 250)),
                FontFamily = new FontFamily("Microsoft YaHei"),
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            // 对话框必须独立于宿主全局主题，避免 SolidWorks 或插件样式再次覆盖为黑底。
            dialog.SetValue(FrameworkElement.StyleProperty, null);
            if (owner != null && owner.IsVisible)
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                return dialog;
            }

            AssignWindowOwner(dialog);

            return dialog;
        }

        internal static void AssignWindowOwner(Window window)
        {
            if (window.Owner != null) return;
            var interop = new WindowInteropHelper(window);
            if (interop.Owner != IntPtr.Zero) return;
            var owner = GetForegroundWindow();
            GetWindowThreadProcessId(owner, out var processId);
            // 不能拥有其他程序的窗口；无活动宿主窗口时才回到 SW 主窗口。
            if (owner == IntPtr.Zero || processId != Process.GetCurrentProcess().Id)
                owner = ResolveSolidWorksMainWindowHandle();
            if (owner != IntPtr.Zero && owner != interop.Handle) interop.Owner = owner;
        }

        internal static void ActivateWindow(Window window)
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            if (!IsWindowEnabled(handle) || !window.IsEnabled)
            {
                var popup = GetLastActivePopup(handle);
                if (popup != handle && IsWindowVisible(popup) && IsWindowEnabled(popup))
                    SetForegroundWindow(popup);
                return;
            }
            window.Activate();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern IntPtr GetLastActivePopup(IntPtr windowHandle);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowEnabled(IntPtr windowHandle);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr windowHandle);

        private static Border CreateDialogContent(
            string title,
            string message,
            MessageBoxImage icon,
            IReadOnlyList<MessageBoxResult> options,
            MessageBoxResult defaultResult,
            Action<MessageBoxResult> complete)
        {
            var root = new Grid { Margin = new Thickness(24d) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1d, GridUnitType.Star) });
            var iconElement = CreateDialogIcon(icon);
            if (iconElement != null) content.Children.Add(iconElement);

            var textPanel = new StackPanel
            {
                Margin = new Thickness(iconElement == null ? 0d : 18d, 0d, 0d, 0d)
            };
            Grid.SetColumn(textPanel, 1);
            textPanel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 17d,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55))
            });
            textPanel.Children.Add(new System.Windows.Controls.ScrollViewer
            {
                Margin = new Thickness(0d, 10d, 0d, 0d),
                MaxHeight = 420d,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new TextBlock
                {
                    Text = message ?? string.Empty,
                    MaxWidth = 360d,
                    FontSize = 14d,
                    LineHeight = 22d,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(52, 64, 84))
                }
            });
            content.Children.Add(textPanel);
            root.Children.Add(content);

            var buttonPanel = new StackPanel
            {
                Margin = new Thickness(0d, 24d, 0d, 0d),
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetRow(buttonPanel, 1);
            foreach (var option in options)
            {
                var capturedOption = option;
                var button = CreateDialogButton(
                    GetDialogButtonText(option),
                    option == ResolveDefaultButton(options, defaultResult),
                    option == MessageBoxResult.Cancel);
                button.Tag = option;
                button.Click += (ignoredSender, ignoredArgs) => complete(capturedOption);
                buttonPanel.Children.Add(button);
            }

            root.Children.Add(buttonPanel);
            return new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(228, 231, 236)),
                BorderThickness = new Thickness(1d),
                Child = root
            };
        }

        private static FrameworkElement CreateDialogIcon(MessageBoxImage icon)
        {
            string text;
            Color foreground;
            Color background;
            switch (icon)
            {
                case MessageBoxImage.Warning:
                    text = "!";
                    foreground = Color.FromRgb(181, 71, 8);
                    background = Color.FromRgb(254, 240, 199);
                    break;
                case MessageBoxImage.Error:
                    text = "×";
                    foreground = Color.FromRgb(180, 35, 24);
                    background = Color.FromRgb(254, 228, 226);
                    break;
                case MessageBoxImage.Question:
                    text = "?";
                    foreground = Color.FromRgb(23, 92, 211);
                    background = Color.FromRgb(219, 234, 254);
                    break;
                case MessageBoxImage.Information:
                    text = "i";
                    foreground = Color.FromRgb(2, 106, 162);
                    background = Color.FromRgb(224, 242, 254);
                    break;
                default:
                    return null;
            }

            return new Border
            {
                Width = 42d,
                Height = 42d,
                CornerRadius = new CornerRadius(21d),
                Background = new SolidColorBrush(background),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 26d,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(foreground),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        private static Button CreateDialogButton(
            string text,
            bool isDefault,
            bool isCancel)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 88d,
                Height = 34d,
                Margin = new Thickness(10d, 0d, 0d, 0d),
                Padding = new Thickness(16d, 0d, 16d, 0d),
                Background = Brushes.White,
                Foreground = new SolidColorBrush(Color.FromRgb(52, 64, 84)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(208, 213, 221)),
                BorderThickness = new Thickness(1d),
                IsDefault = isDefault,
                IsCancel = isCancel
            };
            // 阻止宿主进程可能注册的隐式按钮样式覆盖这里的显式配色。
            button.SetValue(FrameworkElement.StyleProperty, null);
            return button;
        }

        private static MessageBoxResult[] GetDialogResults(MessageBoxButton buttons)
        {
            switch (buttons)
            {
                case MessageBoxButton.OKCancel:
                    return new[] { MessageBoxResult.OK, MessageBoxResult.Cancel };
                case MessageBoxButton.YesNo:
                    return new[] { MessageBoxResult.Yes, MessageBoxResult.No };
                case MessageBoxButton.YesNoCancel:
                    return new[] { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel };
                default:
                    return new[] { MessageBoxResult.OK };
            }
        }

        private static MessageBoxResult ResolveDefaultButton(
            IReadOnlyList<MessageBoxResult> options,
            MessageBoxResult defaultResult)
        {
            return options.Contains(defaultResult) ? defaultResult : options[0];
        }

        private static MessageBoxResult ResolveFallbackResult(
            MessageBoxButton buttons,
            MessageBoxResult defaultResult)
        {
            if (defaultResult != MessageBoxResult.None)
            {
                return defaultResult;
            }

            return buttons == MessageBoxButton.OK ? MessageBoxResult.OK
                : buttons == MessageBoxButton.YesNo ? MessageBoxResult.No
                : MessageBoxResult.Cancel;
        }

        private static string GetDialogButtonText(MessageBoxResult result)
        {
            switch (result)
            {
                case MessageBoxResult.Yes:
                    return "是";
                case MessageBoxResult.No:
                    return "否";
                case MessageBoxResult.Cancel:
                    return "取消";
                default:
                    return "确定";
            }
        }

        private static PresentationDispatcherThread PresentationThread
        {
            get
            {
                lock (Gate)
                {
                    if (_presentationShutdownRequested)
                        throw new ObjectDisposedException(nameof(FeedbackRuntime));
                    if (_presentationThread == null) _presentationThread = new PresentationDispatcherThread();
                    return _presentationThread;
                }
            }
        }

        internal static void InitializePresentation()
        {
            lock (Gate)
            {
                if (_presentationThread != null && _presentationThread.IsAlive)
                {
                    if (_presentationShutdownRequested || _presentationThread.IsStopping)
                        throw new InvalidOperationException("上次提示界面线程尚未退出，不能重新加载插件界面。");
                    return;
                }
                _presentationThread = null;
                _presentationShutdownRequested = false;
                _solidWorksMainWindowHandle = IntPtr.Zero;
            }
        }

        internal static T InvokePresentation<T>(Func<T> presentationAction) => PresentationThread.Invoke(presentationAction);

        internal static void PostPresentation(Action presentationAction)
        {
            try { PresentationThread.Post(presentationAction); }
            catch (ObjectDisposedException) { /* 插件已开始卸载，停止投递可选的提示界面。 */ }
            catch (Exception exception) { ProgressToastLogger.Error(exception, "提示界面操作投递失败。"); }
        }

        internal static bool ShowPresentationWindow(Window presentationWindow)
        {
            // 正在执行的显示回调可能与卸载交错；让原线程关闭新窗口，不能再创建线程。
            PresentationDispatcherThread presentationThread;
            lock (Gate) presentationThread = _presentationThread;
            if (presentationThread == null) throw new InvalidOperationException("提示窗口必须在所属 STA 线程内显示。");
            return presentationThread.ShowWindow(presentationWindow);
        }

        internal static void PrepareForShutdown()
        {
            PresentationDispatcherThread presentationThread;
            lock (Gate)
            {
                _presentationShutdownRequested = true;
                presentationThread = _presentationThread;
            }
            presentationThread?.PrepareForShutdown();
        }

        internal static void ShutdownPresentation()
        {
            PresentationDispatcherThread presentationThread;
            lock (Gate)
            {
                _presentationShutdownRequested = true;
                presentationThread = _presentationThread;
            }
            presentationThread?.Shutdown();
        }

        internal static FeedbackProgressScope BeginProgress(string title)
        {
            try
            {
                return InvokePresentation(() =>
                {
                    var window = CreateWindow();
                    var body = new StackPanel { Margin = new Thickness(18) };
                    body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
                    var stage = new TextBlock { Text = "准备中…", Margin = new Thickness(0, 8, 0, 6), TextWrapping = TextWrapping.Wrap };
                    body.Children.Add(stage);
                    var bar = new System.Windows.Controls.ProgressBar
                    {
                        Minimum = 0, Maximum = 100, IsIndeterminate = true, Height = 22,
                        Style = (Style)window.FindResource("ProgressBarBaseStyle")
                    };
                    body.Children.Add(bar);
                    var detail = new TextBlock { Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                    var timing = new TextBlock { Margin = new Thickness(0, 6, 0, 0) };
                    body.Children.Add(detail);
                    body.Children.Add(timing);
                    window.Content = new Border
                    {
                        Margin = new Thickness(ProgressCardMargin), Background = Brushes.White,
                        CornerRadius = new CornerRadius(6), Child = body
                    };
                    if (!ShowProgressWindow(window)) return new FeedbackProgressScope();
                    return new FeedbackProgressScope(window, bar, stage, detail, timing);
                });
            }
            catch (Exception ex)
            {
                ProgressToastLogger.Error(ex, "进度组件创建失败，不影响业务执行。");
                return new FeedbackProgressScope();
            }
        }

        internal static void Notify(string message, string title, bool success)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            ProgressToastLogger.Info("{0}：{1}", title, message);
            try
            {
                PostPresentation(new Action(() =>
                {
                    Window window = null;
                    try
                    {
                        window = CreateWindow();
                        var panel = new StackPanel { Margin = new Thickness(ProgressCardMargin) };
                        window.Content = panel;
                        // 3.5.1 的 Growl.Info/Success 强制调用 Application.Dispatcher，
                        // 会在 SolidWorks 忙于 COM 操作时阻塞。直接在专用线程承载同一个 Growl 控件。
                        var growl = new Growl
                        {
                            Message = string.IsNullOrWhiteSpace(title) ? message : title + "\n" + message,
                            Type = success ? InfoType.Success : InfoType.Info,
                            Icon = (Geometry)window.FindResource(success ? "SuccessGeometry" : "InfoGeometry"),
                            IconBrush = (Brush)window.FindResource(success ? "SuccessBrush" : "InfoBrush"),
                            ShowDateTime = false
                        };
                        panel.Children.Add(growl);
                        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(UiFeedback.NotificationSeconds) };
                        Window notificationWindow = window;
                        timer.Tick += (sender, args) => notificationWindow.Close();
                        window.Closed += (sender, args) =>
                        {
                            timer.Stop();
                            // 通过公开命令同时停止 Growl 自身的内部计时器。
                            HandyControl.Interactivity.ControlCommands.Close.Execute(null, growl);
                            panel.Children.Clear();
                        };
                        if (!ShowProgressWindow(window)) return;
                        timer.Start(); // 固定 4 秒关闭，即使鼠标悬停也不会阻碍后续操作。
                    }
                    catch (Exception ex)
                    {
                        window?.Close();
                        ProgressToastLogger.Error(ex, "通知显示失败。");
                    }
                }));
            }
            catch (Exception ex)
            {
                ProgressToastLogger.Error(ex, "通知投递失败。");
            }
        }

        private static Window CreateWindow()
        {
            var window = new Window
            {
                Width = 404, SizeToContent = SizeToContent.Height, MaxHeight = SystemParameters.WorkArea.Height - 32,
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false,
                ShowActivated = false, ResizeMode = ResizeMode.NoResize, FontFamily = new FontFamily("Microsoft YaHei")
            };
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/HandyControl;component/Themes/SkinDefault.xaml")
            });
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/HandyControl;component/Themes/Theme.xaml")
            });
            window.SizeChanged += (sender, args) => RelayoutProgressWindows();
            return window;
        }

        private static bool ShowProgressWindow(Window progressWindow)
        {
            var solidWorksMainWindowHandle =
                ResolveSolidWorksMainWindowHandle();
            if (solidWorksMainWindowHandle != IntPtr.Zero)
            {
                new WindowInteropHelper(progressWindow)
                {
                    Owner = solidWorksMainWindowHandle
                };
            }

            progressWindow.Closed += (ignoredSender, ignoredEventArgs) =>
            {
                OpenProgressWindows.Remove(progressWindow);
                RelayoutProgressWindows();
            };
            OpenProgressWindows.Add(progressWindow);
            try
            {
                if (!ShowPresentationWindow(progressWindow)) return false;
                RelayoutProgressWindows();
                return true;
            }
            catch
            {
                OpenProgressWindows.Remove(progressWindow);
                progressWindow.Close();
                throw;
            }
        }

        private static void RelayoutProgressWindows()
        {
            if (OpenProgressWindows.Count == 0)
            {
                return;
            }

            try
            {
                Rect solidWorksFrameRectangle;
                var solidWorksMainWindowHandle =
                    ResolveSolidWorksMainWindowHandle();
                if (solidWorksMainWindowHandle != IntPtr.Zero &&
                    GetWindowRect(
                        solidWorksMainWindowHandle,
                        out var solidWorksDeviceRectangle))
                {
                    var deviceToWpfTransform = GetDeviceToWpfTransform(
                        OpenProgressWindows[0]);
                    var solidWorksTopLeft = deviceToWpfTransform.Transform(
                        new Point(
                            solidWorksDeviceRectangle.Left,
                            solidWorksDeviceRectangle.Top));
                    var solidWorksBottomRight = deviceToWpfTransform.Transform(
                        new Point(
                            solidWorksDeviceRectangle.Right,
                            solidWorksDeviceRectangle.Bottom));
                    solidWorksFrameRectangle = new Rect(
                        solidWorksTopLeft,
                        solidWorksBottomRight);
                }
                else
                {
                    solidWorksFrameRectangle = SystemParameters.WorkArea;
                }

                var visibleProgressCardRight =
                    solidWorksFrameRectangle.Right - SolidWorksEdgeGap;
                var visibleProgressCardBottom =
                    solidWorksFrameRectangle.Bottom - SolidWorksEdgeGap;
                for (var progressWindowIndex =
                        OpenProgressWindows.Count - 1;
                    progressWindowIndex >= 0;
                    progressWindowIndex--)
                {
                    var progressWindow =
                        OpenProgressWindows[progressWindowIndex];
                    progressWindow.Left =
                        visibleProgressCardRight + ProgressCardMargin -
                        progressWindow.ActualWidth;
                    progressWindow.Top =
                        visibleProgressCardBottom + ProgressCardMargin -
                        progressWindow.ActualHeight;
                    visibleProgressCardBottom =
                        progressWindow.Top + ProgressCardMargin -
                        ProgressCardStackGap;
                }
            }
            catch (Exception progressLayoutException)
            {
                ProgressToastLogger.Error(
                    progressLayoutException,
                    "SolidWorks 右下角进度卡定位失败。");
            }
        }

        private static Matrix GetDeviceToWpfTransform(
            Window visibleProgressWindow)
        {
            var presentationSource = PresentationSource.FromVisual(
                visibleProgressWindow);
            return presentationSource?.CompositionTarget?.TransformFromDevice ??
                Matrix.Identity;
        }

        private static IntPtr ResolveSolidWorksMainWindowHandle()
        {
            try
            {
                if (_solidWorksMainWindowHandle != IntPtr.Zero &&
                    IsWindow(_solidWorksMainWindowHandle))
                {
                    return _solidWorksMainWindowHandle;
                }

                var solidWorksProcessId =
                    System.Diagnostics.Process.GetCurrentProcess().Id;
                var largestVisibleWindowHandle = IntPtr.Zero;
                long largestVisibleWindowArea = 0L;
                EnumWindows((candidateWindowHandle, ignoredParameter) =>
                {
                    GetWindowThreadProcessId(
                        candidateWindowHandle,
                        out var candidateWindowProcessId);
                    if (candidateWindowProcessId != solidWorksProcessId ||
                        !IsWindowVisible(candidateWindowHandle) ||
                        !GetWindowRect(
                            candidateWindowHandle,
                            out var candidateWindowRectangle))
                    {
                        return true;
                    }

                    var candidateWindowArea =
                        (long)(candidateWindowRectangle.Right -
                            candidateWindowRectangle.Left) *
                        (candidateWindowRectangle.Bottom -
                            candidateWindowRectangle.Top);
                    if (candidateWindowArea > largestVisibleWindowArea)
                    {
                        largestVisibleWindowArea = candidateWindowArea;
                        largestVisibleWindowHandle = candidateWindowHandle;
                    }

                    return true;
                }, IntPtr.Zero);
                _solidWorksMainWindowHandle = largestVisibleWindowHandle;
                return _solidWorksMainWindowHandle;
            }
            catch (Exception solidWorksWindowException)
            {
                ProgressToastLogger.Warn(
                    solidWorksWindowException,
                    "无法取得 SolidWorks 主窗口句柄，进度卡将停靠到桌面工作区。");
                return IntPtr.Zero;
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(
            IntPtr windowHandle,
            out NativeWindowRectangle windowRectangle);

        private delegate bool EnumWindowsCallback(
            IntPtr windowHandle,
            IntPtr callbackParameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(
            EnumWindowsCallback callback,
            IntPtr callbackParameter);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr windowHandle,
            out int processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr windowHandle);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeWindowRectangle
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }

    internal sealed class FeedbackProgressScope : IProgress<FeedbackProgress>, IDisposable
    {
        private readonly Window _window;
        private readonly System.Windows.Controls.ProgressBar _bar;
        private readonly TextBlock _stage;
        private readonly TextBlock _detail;
        private readonly TextBlock _timing;
        private readonly Stopwatch _elapsed;
        private readonly DispatcherTimer _timer;
        private long? _estimatedMilliseconds;
        private int _disposed;

        internal FeedbackProgressScope() { }

        internal FeedbackProgressScope(Window window, System.Windows.Controls.ProgressBar bar,
            TextBlock stage, TextBlock detail, TextBlock timing)
        {
            _window = window;
            _bar = bar;
            _stage = stage;
            _detail = detail;
            _timing = timing;
            _elapsed = Stopwatch.StartNew();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (sender, args) => UpdateTiming();
            window.Closed += (sender, args) =>
            {
                Interlocked.Exchange(ref _disposed, 1);
                _timer.Stop();
                _elapsed.Stop();
            };
            _timer.Start();
            UpdateTiming();
        }

        public void Report(FeedbackProgress update)
        {
            if (_window == null || update == null || Volatile.Read(ref _disposed) != 0) return;
            // 复制参数，避免调用方复用模型时与 UI 线程争用。
            string stage = update.Stage, detail = update.Detail;
            int current = update.Current, total = update.Total;
            long? estimate = update.EstimatedDurationMilliseconds;
            _window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                if (stage != null) _stage.Text = stage;
                if (detail != null) _detail.Text = detail;
                _bar.IsIndeterminate = total <= 0;
                _bar.Value = total <= 0 ? 0 : Math.Max(0, Math.Min(100, current * 100d / total));
                if (estimate.HasValue) _estimatedMilliseconds = estimate > 0 ? estimate : null;
                UpdateTiming();
            }));
        }

        private void UpdateTiming()
        {
            _timing.Text = "已用时 " + _elapsed.Elapsed.TotalSeconds.ToString("0.0") + " 秒";
            if (_estimatedMilliseconds.HasValue)
                _timing.Text += " / 预计 " + (_estimatedMilliseconds.Value / 1000d).ToString("0.0") + " 秒";
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0 || _window == null) return;
            try
            {
                _window.Dispatcher.Invoke(new Action(() => _window.Close()));
            }
            catch (Exception ex)
            {
                LogManager.GetCurrentClassLogger().Warn(ex, "进度窗口关闭失败。");
            }
        }
    }
}
