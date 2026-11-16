using CKBusbarBridgePreview;
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// 停靠在 SolidWorks 主窗口右下角的 WPF 进度卡。
    /// 进度卡运行在独立 STA UI 线程上，即使 SolidWorks 主线程正在执行耗时 COM
    /// 操作，进度条仍可持续绘制和播放动画。
    /// </summary>
    internal static class SolidWorksProgressToast
    {
        private const double ProgressCardWidth = 380d;
        private const double ProgressCardMargin = 12d;
        private const double SolidWorksEdgeGap = 16d;
        private const double ProgressCardStackGap = 10d;
        private const double MessageCardDefaultAutoCloseSeconds = 6d;

        private static readonly Logger ProgressToastLogger =
            LogManager.GetCurrentClassLogger();
        private static readonly List<Window> OpenProgressWindows =
            new List<Window>();

        private static IntPtr _solidWorksMainWindowHandle;

        public static SolidWorksProgressToastHandle Begin(
            string progressTitle)
        {
            try
            {
                var normalizedProgressTitle = string.IsNullOrWhiteSpace(
                        progressTitle)
                    ? "正在处理"
                    : progressTitle.Trim();
                return UiFeedback.InvokePresentation(
                    new Func<SolidWorksProgressToastHandle>(() =>
                        CreateProgressCardOnUiThread(
                            normalizedProgressTitle)));
            }
            catch (Exception progressCreationException)
            {
                ProgressToastLogger.Error(
                    progressCreationException,
                    "SolidWorks 右下角进度卡创建失败，不影响业务执行。");
                return SolidWorksProgressToastHandle.None;
            }
        }

        public static void ShowError(
            string errorTitle,
            string conciseErrorMessage)
        {
            UiFeedback.Error(
                string.IsNullOrWhiteSpace(conciseErrorMessage)
                    ? "缺少必要数据，请检查后重试。"
                    : conciseErrorMessage.Trim(),
                string.IsNullOrWhiteSpace(errorTitle)
                    ? "操作失败"
                    : errorTitle.Trim());
        }

        public static void ShowWarning(
            string warningTitle,
            string conciseWarningMessage)
        {
            ShowMessageCard(
                warningTitle,
                conciseWarningMessage,
                Color.FromRgb(0xF5, 0x9E, 0x0B),
                "警告",
                "!",
                MessageCardDefaultAutoCloseSeconds);
        }

        public static void ShowInformation(
            string informationTitle,
            string conciseInformationMessage)
        {
            ShowMessageCard(
                informationTitle,
                conciseInformationMessage,
                Color.FromRgb(0x16, 0xA3, 0x4A),
                "成功",
                "✓",
                MessageCardDefaultAutoCloseSeconds);
        }

        private static void ShowMessageCard(
            string messageTitle,
            string conciseMessage,
            Color accentColor,
            string statusText,
            string statusGlyph,
            double autoCloseSeconds)
        {
            try
            {
                var normalizedMessageTitle = string.IsNullOrWhiteSpace(
                    messageTitle)
                    ? "操作失败"
                    : messageTitle.Trim();
                var normalizedMessage = string.IsNullOrWhiteSpace(
                    conciseMessage)
                    ? "缺少必要数据，请检查后重试。"
                    : conciseMessage.Trim();
                UiFeedback.PostPresentation(new Action(() =>
                    CreateMessageCardOnUiThread(
                        normalizedMessageTitle,
                        normalizedMessage,
                        accentColor,
                        statusText,
                        statusGlyph,
                        autoCloseSeconds)));
            }
            catch (Exception messageCardException)
            {
                ProgressToastLogger.Error(
                    messageCardException,
                    "SolidWorks 右下角消息卡创建失败。");
            }
        }

        private static void CreateMessageCardOnUiThread(
            string messageTitle,
            string conciseMessage,
            Color accentColor,
            string statusText,
            string statusGlyph,
            double autoCloseSeconds)
        {
            var messageAccentBrush = new SolidColorBrush(accentColor);
            var messageWindow = CreateProgressWindow(
                out var messageCardBorder);
            var messageRootGrid = new Grid();
            messageCardBorder.Child = messageRootGrid;
            messageRootGrid.Children.Add(new Border
            {
                Width = 4d,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = messageAccentBrush,
                CornerRadius = new CornerRadius(6d, 0d, 0d, 6d)
            });

            var messageBodyGrid = new Grid
            {
                Margin = new Thickness(16d, 12d, 12d, 12d)
            };
            messageBodyGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = GridLength.Auto });
            messageBodyGrid.ColumnDefinitions.Add(new ColumnDefinition());
            messageRootGrid.Children.Add(messageBodyGrid);

            messageBodyGrid.Children.Add(new Border
            {
                Width = 30d,
                Height = 30d,
                CornerRadius = new CornerRadius(15d),
                Background = messageAccentBrush,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0d, 2d, 10d, 0d),
                Child = new TextBlock
                {
                    Text = statusGlyph,
                    Foreground = Brushes.White,
                    FontSize = 15d,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });

            var messageTextStack = new StackPanel();
            Grid.SetColumn(messageTextStack, 1);

            var messageTitleRow = new DockPanel();
            var messageTitleLeftStack = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };
            messageTitleLeftStack.Children.Add(new TextBlock
            {
                Text = messageTitle,
                Foreground = new SolidColorBrush(
                    Color.FromRgb(0x30, 0x31, 0x33)),
                FontSize = 13d,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            messageTitleLeftStack.Children.Add(new Border
            {
                Margin = new Thickness(8d, 0d, 0d, 0d),
                Padding = new Thickness(7d, 1d, 7d, 1d),
                CornerRadius = new CornerRadius(9d),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(
                    0x22,
                    accentColor.R,
                    accentColor.G,
                    accentColor.B)),
                Child = new TextBlock
                {
                    Text = statusText,
                    FontSize = 11d,
                    Foreground = messageAccentBrush
                }
            });
            messageTitleRow.Children.Add(messageTitleLeftStack);
            messageTextStack.Children.Add(messageTitleRow);
            messageTextStack.Children.Add(new TextBlock
            {
                Text = conciseMessage,
                Foreground = new SolidColorBrush(
                    Color.FromRgb(0x5A, 0x5E, 0x66)),
                FontSize = 12.5d,
                Margin = new Thickness(0d, 4d, 0d, 0d),
                TextWrapping = TextWrapping.Wrap
            });

            var closeButton = new Button
            {
                Content = "关闭",
                Width = 64d,
                Height = 26d,
                Margin = new Thickness(0d, 10d, 0d, 0d),
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = Brushes.White,
                Foreground = messageAccentBrush,
                BorderBrush = messageAccentBrush,
                BorderThickness = new Thickness(1d)
            };
            closeButton.Click += (ignoredSender, ignoredEventArgs) =>
                Dismiss(messageWindow, messageCardBorder);
            messageTextStack.Children.Add(closeButton);
            messageBodyGrid.Children.Add(messageTextStack);
            if (!ShowProgressWindow(messageWindow)) return;

            var autoCloseTimer = new DispatcherTimer(
                DispatcherPriority.Normal,
                messageWindow.Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(autoCloseSeconds)
            };
            autoCloseTimer.Tick += (ignoredSender, ignoredEventArgs) =>
            {
                autoCloseTimer.Stop();
                if (messageWindow.IsVisible)
                {
                    Dismiss(messageWindow, messageCardBorder);
                }
            };
            messageWindow.Closed += (ignoredSender, ignoredEventArgs) =>
                autoCloseTimer.Stop();
            autoCloseTimer.Start();
        }

        private static SolidWorksProgressToastHandle
            CreateProgressCardOnUiThread(string progressTitle)
        {
            var progressAccentColor = Color.FromRgb(0x40, 0x9E, 0xFF);
            var progressWindow = CreateProgressWindow(
                out var progressCardBorder);
            var progressRootGrid = new Grid();
            progressCardBorder.Child = progressRootGrid;
            progressRootGrid.Children.Add(new Border
            {
                Width = 4d,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(progressAccentColor),
                CornerRadius = new CornerRadius(6d, 0d, 0d, 6d)
            });

            var progressBodyGrid = new Grid
            {
                Margin = new Thickness(16d, 12d, 12d, 12d)
            };
            progressBodyGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = GridLength.Auto });
            progressBodyGrid.ColumnDefinitions.Add(new ColumnDefinition());
            progressRootGrid.Children.Add(progressBodyGrid);

            progressBodyGrid.Children.Add(new Border
            {
                Width = 30d,
                Height = 30d,
                CornerRadius = new CornerRadius(15d),
                Background = new SolidColorBrush(progressAccentColor),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0d, 2d, 10d, 0d),
                Child = new TextBlock
                {
                    Text = "⟳",
                    Foreground = Brushes.White,
                    FontSize = 15d,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });

            var progressTextStack = new StackPanel();
            Grid.SetColumn(progressTextStack, 1);
            progressBodyGrid.Children.Add(progressTextStack);

            var progressTitleRow = new DockPanel();
            var progressPercentText = new TextBlock
            {
                Text = "0%",
                FontSize = 11d,
                Foreground = new SolidColorBrush(progressAccentColor),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(progressPercentText, Dock.Right);
            progressTitleRow.Children.Add(progressPercentText);

            var progressTitleLeftStack = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };
            progressTitleLeftStack.Children.Add(new TextBlock
            {
                Text = progressTitle,
                FontSize = 13d,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(
                    Color.FromRgb(0x30, 0x31, 0x33)),
                VerticalAlignment = VerticalAlignment.Center
            });
            progressTitleLeftStack.Children.Add(new Border
            {
                Margin = new Thickness(8d, 0d, 0d, 0d),
                Padding = new Thickness(7d, 1d, 7d, 1d),
                CornerRadius = new CornerRadius(9d),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(
                    0x22,
                    progressAccentColor.R,
                    progressAccentColor.G,
                    progressAccentColor.B)),
                Child = new TextBlock
                {
                    Text = "进行中",
                    FontSize = 11d,
                    Foreground = new SolidColorBrush(
                        Color.FromRgb(0x2B, 0x7C, 0xC9))
                }
            });
            progressTitleRow.Children.Add(progressTitleLeftStack);
            progressTextStack.Children.Add(progressTitleRow);

            var progressStageText = new TextBlock
            {
                Text = "准备中…",
                FontSize = 12.5d,
                Foreground = new SolidColorBrush(
                    Color.FromRgb(0x5A, 0x5E, 0x66)),
                Margin = new Thickness(0d, 4d, 0d, 0d)
            };
            progressTextStack.Children.Add(progressStageText);

            var progressFillScale = new ScaleTransform(0d, 1d);
            var progressFillTranslate = new TranslateTransform(0d, 0d);
            var progressFillBar = new Border
            {
                CornerRadius = new CornerRadius(3d),
                Background = new SolidColorBrush(progressAccentColor),
                RenderTransform = new TransformGroup
                {
                    Children =
                    {
                        progressFillScale,
                        progressFillTranslate
                    }
                },
                RenderTransformOrigin = new Point(0d, 0.5d)
            };
            progressTextStack.Children.Add(new Border
            {
                Height = 6d,
                CornerRadius = new CornerRadius(3d),
                Background = new SolidColorBrush(
                    Color.FromRgb(0xEE, 0xF1, 0xF5)),
                Margin = new Thickness(0d, 8d, 0d, 0d),
                ClipToBounds = true,
                Child = progressFillBar
            });

            var progressDetailText = new TextBlock
            {
                Text = " ",
                FontSize = 11.5d,
                Foreground = new SolidColorBrush(
                    Color.FromRgb(0x9A, 0xA0, 0xA8)),
                Margin = new Thickness(0d, 5d, 0d, 0d),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var progressTimingText = new TextBlock
            {
                Text = "已用时 0.0 秒",
                FontSize = 11.5d,
                Foreground = new SolidColorBrush(
                    Color.FromRgb(0x6F, 0x75, 0x7D)),
                Margin = new Thickness(0d, 5d, 0d, 0d),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            progressTextStack.Children.Add(progressTimingText);
            progressTextStack.Children.Add(progressDetailText);

            if (!ShowProgressWindow(progressWindow)) return SolidWorksProgressToastHandle.None;
            var progressEntryAnimation = new DoubleAnimation(
                40d,
                0d,
                TimeSpan.FromMilliseconds(250d))
            {
                EasingFunction = new QuadraticEase()
            };
            ((TranslateTransform)progressCardBorder.RenderTransform)
                .BeginAnimation(
                    TranslateTransform.XProperty,
                    progressEntryAnimation);
            progressCardBorder.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(
                    0d,
                    1d,
                    TimeSpan.FromMilliseconds(250d)));

            return new SolidWorksProgressToastHandle(
                progressWindow,
                progressCardBorder,
                progressStageText,
                progressPercentText,
                progressTimingText,
                progressDetailText,
                progressFillScale,
                progressFillTranslate,
                progressFillBar);
        }

        private static Window CreateProgressWindow(
            out Border progressCardBorder)
        {
            var progressWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = false,
                ResizeMode = ResizeMode.NoResize,
                FontFamily = new FontFamily("Microsoft YaHei"),
                Left = 0d,
                Top = 0d
            };
            progressCardBorder = new Border
            {
                Width = ProgressCardWidth,
                Margin = new Thickness(ProgressCardMargin),
                Background = Brushes.White,
                CornerRadius = new CornerRadius(6d),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 18d,
                    ShadowDepth = 2d,
                    Opacity = 0.25d
                },
                RenderTransform = new TranslateTransform()
            };
            progressWindow.Content = progressCardBorder;
            return progressWindow;
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
            if (!UiFeedback.ShowPresentationWindow(progressWindow)) return false;
            RelayoutProgressWindows();
            return true;
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

        internal static void Dismiss(
            Window progressWindow,
            Border progressCardBorder)
        {
            try
            {
                var progressExitAnimation = new DoubleAnimation(
                    0d,
                    TimeSpan.FromMilliseconds(150d));
                progressExitAnimation.Completed +=
                    (ignoredSender, ignoredEventArgs) =>
                        progressWindow.Close();
                ((TranslateTransform)progressCardBorder.RenderTransform)
                    .BeginAnimation(
                        TranslateTransform.XProperty,
                        new DoubleAnimation(
                            0d,
                            40d,
                            TimeSpan.FromMilliseconds(150d)));
                progressCardBorder.BeginAnimation(
                    UIElement.OpacityProperty,
                    progressExitAnimation);
            }
            catch
            {
                progressWindow.Close();
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

    internal sealed class SolidWorksProgressToastHandle :
        IProgress<SolidWorksProgressToastUpdate>
    {
        private static readonly Logger ProgressHandleLogger =
            LogManager.GetCurrentClassLogger();

        internal static readonly SolidWorksProgressToastHandle None =
            new SolidWorksProgressToastHandle(
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null);

        private readonly Window _progressWindow;
        private readonly Border _progressCardBorder;
        private readonly TextBlock _progressStageText;
        private readonly TextBlock _progressPercentText;
        private readonly TextBlock _progressTimingText;
        private readonly TextBlock _progressDetailText;
        private readonly ScaleTransform _progressFillScale;
        private readonly TranslateTransform _progressFillTranslate;
        private readonly Border _progressFillBar;
        private readonly Stopwatch _progressElapsedStopwatch;
        private readonly DispatcherTimer _progressTimingTimer;

        private bool _isIndeterminate;
        private volatile bool _isFinished;
        private long? _estimatedDurationMilliseconds;

        internal SolidWorksProgressToastHandle(
            Window progressWindow,
            Border progressCardBorder,
            TextBlock progressStageText,
            TextBlock progressPercentText,
            TextBlock progressTimingText,
            TextBlock progressDetailText,
            ScaleTransform progressFillScale,
            TranslateTransform progressFillTranslate,
            Border progressFillBar)
        {
            _progressWindow = progressWindow;
            _progressCardBorder = progressCardBorder;
            _progressStageText = progressStageText;
            _progressPercentText = progressPercentText;
            _progressTimingText = progressTimingText;
            _progressDetailText = progressDetailText;
            _progressFillScale = progressFillScale;
            _progressFillTranslate = progressFillTranslate;
            _progressFillBar = progressFillBar;

            if (_progressWindow != null)
            {
                _progressElapsedStopwatch = Stopwatch.StartNew();
                _progressTimingTimer = new DispatcherTimer(
                    TimeSpan.FromMilliseconds(250d),
                    DispatcherPriority.Background,
                    (ignoredSender, ignoredEventArgs) => UpdateTimingText(),
                    _progressWindow.Dispatcher);
                _progressTimingTimer.Start();
                _progressWindow.Closed += (sender, args) =>
                {
                    _isFinished = true;
                    _progressTimingTimer.Stop();
                    _progressElapsedStopwatch.Stop();
                    StopIndeterminateAnimation();
                };
            }
            else
            {
                _progressElapsedStopwatch = null;
                _progressTimingTimer = null;
            }
        }

        public void Report(SolidWorksProgressToastUpdate progressUpdate)
        {
            if (_progressWindow == null || _isFinished ||
                progressUpdate == null)
            {
                return;
            }

            try
            {
                _progressWindow.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_isFinished) return;
                    try
                    {
                        ApplyProgressUpdate(progressUpdate);
                    }
                    catch (Exception progressApplyException)
                    {
                        ProgressHandleLogger.Error(
                            progressApplyException,
                            "SolidWorks 右下角进度卡更新失败，不影响业务执行。");
                    }
                }));
            }
            catch (Exception progressDispatchException)
            {
                ProgressHandleLogger.Error(
                    progressDispatchException,
                    "SolidWorks 右下角进度更新投递失败，不影响业务执行。");
            }
        }

        private void ApplyProgressUpdate(
            SolidWorksProgressToastUpdate progressUpdate)
        {
            if (!string.IsNullOrWhiteSpace(progressUpdate.Stage))
            {
                _progressStageText.Text = progressUpdate.Stage;
            }

            if (!string.IsNullOrWhiteSpace(progressUpdate.Detail))
            {
                _progressDetailText.Text = progressUpdate.Detail;
            }

            if (progressUpdate.EstimatedDurationMilliseconds.HasValue)
            {
                _estimatedDurationMilliseconds =
                    progressUpdate.EstimatedDurationMilliseconds.Value > 0L
                        ? progressUpdate.EstimatedDurationMilliseconds
                        : null;
            }
            UpdateTimingText();

            var useIndeterminateAnimation =
                progressUpdate.Total <= 0 || progressUpdate.Current <= 0;
            if (useIndeterminateAnimation)
            {
                StartIndeterminateAnimation();
                return;
            }

            StopIndeterminateAnimation();
            var progressPercent = Math.Max(
                0,
                Math.Min(
                    100,
                    progressUpdate.Current * 100 / progressUpdate.Total));
            _progressPercentText.Text = progressPercent + "%";
            _progressFillScale.ScaleX = progressPercent / 100d;
        }

        private void UpdateTimingText()
        {
            if (_progressTimingText == null ||
                _progressElapsedStopwatch == null)
            {
                return;
            }

            var elapsedTime = FormatElapsedTime(
                _progressElapsedStopwatch.Elapsed);
            if (_estimatedDurationMilliseconds.HasValue)
            {
                _progressTimingText.Text = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    "已用时 {0} / 预计 {1}",
                    elapsedTime,
                    FormatElapsedTime(TimeSpan.FromMilliseconds(
                        _estimatedDurationMilliseconds.Value)));
                return;
            }

            _progressTimingText.Text = "已用时 " + elapsedTime;
        }

        private static string FormatElapsedTime(TimeSpan elapsedTime)
        {
            if (elapsedTime.TotalHours >= 1d)
            {
                return string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    "{0}时{1:00}分{2:00}秒",
                    (int)elapsedTime.TotalHours,
                    elapsedTime.Minutes,
                    elapsedTime.Seconds);
            }

            if (elapsedTime.TotalMinutes >= 1d)
            {
                return string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    "{0}分{1:00}秒",
                    (int)elapsedTime.TotalMinutes,
                    elapsedTime.Seconds);
            }

            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                "{0:0.0}秒",
                elapsedTime.TotalSeconds);
        }

        private void StartIndeterminateAnimation()
        {
            if (_isIndeterminate)
            {
                return;
            }

            const double progressSegmentScale = 0.3d;
            var progressTrackWidth = _progressFillBar.ActualWidth > 0d
                ? _progressFillBar.ActualWidth
                : 300d;
            _isIndeterminate = true;
            _progressPercentText.Text = "…";
            _progressFillScale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                null);
            _progressFillScale.ScaleX = progressSegmentScale;
            _progressFillTranslate.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(
                    0d,
                    progressTrackWidth * (1d - progressSegmentScale),
                    TimeSpan.FromMilliseconds(1500d))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new QuadraticEase
                    {
                        EasingMode = EasingMode.EaseInOut
                    }
                });
        }

        private void StopIndeterminateAnimation()
        {
            if (!_isIndeterminate)
            {
                return;
            }

            _isIndeterminate = false;
            _progressFillTranslate.BeginAnimation(
                TranslateTransform.XProperty,
                null);
            _progressFillTranslate.X = 0d;
            _progressFillScale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                null);
        }

        public void Finish()
        {
            if (_progressWindow == null || _isFinished)
            {
                return;
            }

            _isFinished = true;
            try
            {
                _progressWindow.Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        _progressTimingTimer?.Stop();
                        _progressElapsedStopwatch?.Stop();
                        SolidWorksProgressToast.Dismiss(
                            _progressWindow,
                            _progressCardBorder);
                    }));
            }
            catch (Exception progressFinishException)
            {
                ProgressHandleLogger.Warn(
                    progressFinishException,
                    "SolidWorks 右下角进度卡关闭失败。");
            }
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
