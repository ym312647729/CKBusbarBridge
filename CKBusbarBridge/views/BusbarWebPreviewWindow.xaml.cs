using Microsoft.Web.WebView2.Core;
using RD.WebPreview.Client;
using RD.WebPreview.Contracts;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using RD.UI.Wpf.Controls;

namespace CKBusbarBridge.views
{
    /// <summary>母排设置窗口拥有的非模态 Web 三维方案预览窗。</summary>
    public partial class BusbarWebPreviewWindow : RDWindow
    {
        private const double PreviewWindowGap = 12d;
        private const double PreviewViewportHeightOffset = 140d;
        private readonly WebPreviewWorkerClient _webPreviewWorkerClient =
            new WebPreviewWorkerClient();
        private readonly HashSet<string> _ownedPreviewDirectories =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource _webPreviewRequestCancellationSource;
        private Task _webViewInitializationTask;
        private int _webPreviewRequestVersion;
        private bool _isPreviewWindowClosing;
        private bool _isOwnerWindowCloseRequested;
        private bool _isPreviewWindowSizeAdjustmentActive;
        private bool _isPreviewWindowStateRestorationActive;
        private readonly bool _allowUserClose;
        private RDProgressToast _loadingToast;

        public BusbarWebPreviewWindow(Window ownerWindow)
            : this(ownerWindow, false)
        {
        }

        public BusbarWebPreviewWindow(Window ownerWindow, bool allowUserClose)
        {
            _allowUserClose = allowUserClose;
            Owner = ownerWindow ?? throw new ArgumentNullException(nameof(ownerWindow));
            InitializeComponent();
            CKBusbarBridgePreview.SystemWindowAppearance.Apply(this);
            PrototypeCloseButton.Visibility = allowUserClose ? Visibility.Visible : Visibility.Collapsed;
            if (allowUserClose) Title = "母线桥设计 · 只读三维预览";
        }

        private void PrototypeCloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_allowUserClose) Close();
        }

        public async Task ShowSceneAsync(
            WebPreviewSceneDto previewScene,
            CancellationToken ownerCancellationToken)
        {
            if (previewScene == null)
            {
                throw new ArgumentNullException(nameof(previewScene));
            }

            if (_isPreviewWindowClosing)
            {
                return;
            }

            if (!IsVisible)
            {
                PlaceBesideOwnerWindow();
                Show();
            }

            var activePreviewRequestVersion =
                Interlocked.Increment(ref _webPreviewRequestVersion);
            _webPreviewRequestCancellationSource?.Cancel();
            _webPreviewRequestCancellationSource?.Dispose();
            _webPreviewRequestCancellationSource =
                CancellationTokenSource.CreateLinkedTokenSource(
                    ownerCancellationToken);
            var activePreviewCancellationToken =
                _webPreviewRequestCancellationSource.Token;

            PreviewTitleTextBlock.Text = string.IsNullOrWhiteSpace(previewScene.Title)
                ? "母排方案 Web 预览"
                : previewScene.Title;
            SetLoadingState(true, "正在准备 Web 三维预览…");
            // 切换草稿期间不展示旧版本；渲染失败也不能把旧内容当成当前方案。
            PreviewWebView.Visibility = Visibility.Hidden;
            try
            {
                await EnsureWebViewInitializedAsync();
                activePreviewCancellationToken.ThrowIfCancellationRequested();
                var previewGenerationResult = await CKBusbarBridgePreview.UiFeedback.RunAsync(
                    "Web 三维预览",
                    previewProgress => _webPreviewWorkerClient.GenerateAsync(
                        previewScene, previewProgress, activePreviewCancellationToken),
                    (WebPreviewGenerateProgressDto previewProgressUpdate) =>
                    {
                        if (previewProgressUpdate != null &&
                            activePreviewRequestVersion == _webPreviewRequestVersion)
                        {
                            SetLoadingState(
                                true,
                                previewProgressUpdate.Message ??
                                    "正在生成 Web 预览…");
                        }
                        return new CKBusbarBridgePreview.FeedbackProgress
                        {
                            Stage = previewProgressUpdate?.Message ?? "正在生成 Web 预览…"
                        };
                    });
                // 即使结果已经过期，也登记/回收Worker生成的目录，避免关闭竞态留下孤立预览。
                if (!string.IsNullOrWhiteSpace(previewGenerationResult?.PreviewDirectory))
                {
                    if (_isPreviewWindowClosing) TryDeleteOwnedPreviewDirectory(previewGenerationResult.PreviewDirectory);
                    else _ownedPreviewDirectories.Add(previewGenerationResult.PreviewDirectory);
                }
                activePreviewCancellationToken.ThrowIfCancellationRequested();
                if (activePreviewRequestVersion != _webPreviewRequestVersion ||
                    _isPreviewWindowClosing)
                {
                    return;
                }

                if (previewGenerationResult == null ||
                    string.IsNullOrWhiteSpace(
                        previewGenerationResult.EntryHtmlPath) ||
                    !File.Exists(previewGenerationResult.EntryHtmlPath))
                {
                    throw new FileNotFoundException(
                        "Web 预览 Worker 未返回可用的 HTML 文件。");
                }

                await NavigateToPreviewAsync(
                    previewGenerationResult.EntryHtmlPath,
                    activePreviewCancellationToken);
                if (activePreviewRequestVersion != _webPreviewRequestVersion ||
                    _isPreviewWindowClosing)
                {
                    return;
                }

                SetLoadingState(false, null);
                PreviewWebView.Visibility = Visibility.Visible;
                PreviewStatusTextBlock.Text =
                    "已显示完整场景：铜排 " +
                    previewGenerationResult.BusbarCount + " 根，元器件 " +
                    previewGenerationResult.ComponentCount + " 个。";
            }
            catch (OperationCanceledException)
            {
                // 用户切换了方案或所属窗口正在关闭，旧请求静默失效。
            }
            catch (Exception previewException)
            {
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Error("Web三维预览失败", previewException);
                if (activePreviewRequestVersion != _webPreviewRequestVersion ||
                    _isPreviewWindowClosing)
                {
                    return;
                }

                SetLoadingState(false, null);
                PreviewStatusTextBlock.Text =
                    "Web 预览失败：" + previewException.Message;
            }
        }

        internal void CloseFromOwnerWindow()
        {
            if (_isPreviewWindowClosing)
            {
                return;
            }

            _isOwnerWindowCloseRequested = true;
            Close();
        }

        private Task EnsureWebViewInitializedAsync()
        {
            if (_webViewInitializationTask == null)
            {
                _webViewInitializationTask = InitializeWebViewAsync();
            }

            return _webViewInitializationTask;
        }

        private async Task InitializeWebViewAsync()
        {
            var webViewUserDataDirectory = Path.Combine(
                System.Environment.GetFolderPath(
                    System.Environment.SpecialFolder.LocalApplicationData),
                "RD",
                "CKBusbarBridge",
                "WebView2");
            Directory.CreateDirectory(webViewUserDataDirectory);
            var webViewEnvironment = await CoreWebView2Environment.CreateAsync(
                null,
                webViewUserDataDirectory);
            await PreviewWebView.EnsureCoreWebView2Async(webViewEnvironment);
            PreviewWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            PreviewWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            PreviewWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            PreviewWebView.CoreWebView2.Settings.IsZoomControlEnabled = true;
        }

        private async Task NavigateToPreviewAsync(
            string previewHtmlPath,
            CancellationToken previewCancellationToken)
        {
            var navigationCompletionSource = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<CoreWebView2NavigationCompletedEventArgs>
                navigationCompletedHandler = null;
            navigationCompletedHandler = (eventSender, navigationEventArguments) =>
                navigationCompletionSource.TrySetResult(
                    navigationEventArguments.IsSuccess);
            PreviewWebView.NavigationCompleted += navigationCompletedHandler;
            try
            {
                PreviewWebView.CoreWebView2.Navigate(
                    new Uri(previewHtmlPath).AbsoluteUri);
                using (previewCancellationToken.Register(() =>
                    navigationCompletionSource.TrySetCanceled()))
                {
                    var navigationSucceeded = await navigationCompletionSource.Task;
                    if (!navigationSucceeded)
                    {
                        throw new InvalidOperationException(
                            "WebView2 无法加载生成的预览文件。");
                    }
                }
            }
            finally
            {
                PreviewWebView.NavigationCompleted -= navigationCompletedHandler;
            }
        }

        private void PlaceBesideOwnerWindow()
        {
            var ownerWindowHandle = new WindowInteropHelper(Owner).Handle;
            var ownerWindowScreen =
                System.Windows.Forms.Screen.FromHandle(ownerWindowHandle);
            var ownerWindowDpi = VisualTreeHelper.GetDpi(Owner);
            var horizontalDpiScale = Math.Max(ownerWindowDpi.DpiScaleX, 0.1d);
            var verticalDpiScale = Math.Max(ownerWindowDpi.DpiScaleY, 0.1d);
            var workingAreaLeft = ownerWindowScreen.WorkingArea.Left /
                horizontalDpiScale;
            var workingAreaTop = ownerWindowScreen.WorkingArea.Top /
                verticalDpiScale;
            var workingAreaRight = ownerWindowScreen.WorkingArea.Right /
                horizontalDpiScale;
            var workingAreaBottom = ownerWindowScreen.WorkingArea.Bottom /
                verticalDpiScale;

            Width = Math.Min(
                Math.Max(Width, MinWidth),
                workingAreaRight - workingAreaLeft);
            Height = Math.Min(
                Math.Max(Height, MinHeight),
                workingAreaBottom - workingAreaTop);
            var preferredRightPosition = Owner.Left +
                Owner.ActualWidth + PreviewWindowGap;
            Left = preferredRightPosition + Width <= workingAreaRight
                ? preferredRightPosition
                : Math.Max(
                    workingAreaLeft,
                    Owner.Left - Width - PreviewWindowGap);
            Top = Math.Max(
                workingAreaTop,
                Math.Min(Owner.Top, workingAreaBottom - Height));
        }

        private void SetLoadingState(bool isLoading, string loadingText)
        {
            if (isLoading)
            {
                if (_loadingToast == null) _loadingToast = RDToast.BeginProgress("母排方案预览");
                _loadingToast.Report(new RDToastProgress { Stage = loadingText ?? "正在准备三维预览" });
            }
            else if (_loadingToast != null)
            {
                _loadingToast.Finish();
                _loadingToast = null;
            }
            if (!string.IsNullOrWhiteSpace(loadingText))
            {
                PreviewStatusTextBlock.Text = loadingText;
            }
        }

        protected override void OnClosing(CancelEventArgs eventArguments)
        {
            _loadingToast?.Finish();
            _loadingToast = null;
            if (!_isOwnerWindowCloseRequested && !_allowUserClose)
            {
                eventArguments.Cancel = true;
                return;
            }

            if (_isPreviewWindowClosing)
            {
                base.OnClosing(eventArguments);
                return;
            }

            _isPreviewWindowClosing = true;
            Interlocked.Increment(ref _webPreviewRequestVersion);
            _webPreviewRequestCancellationSource?.Cancel();
            _webPreviewRequestCancellationSource?.Dispose();
            _webPreviewRequestCancellationSource = null;
            _webPreviewWorkerClient.Dispose();
            TryDisposePreviewWebView();

            foreach (var ownedPreviewDirectory in
                _ownedPreviewDirectories.ToArray())
            {
                TryDeleteOwnedPreviewDirectory(ownedPreviewDirectory);
            }

            _ownedPreviewDirectories.Clear();
            base.OnClosing(eventArguments);
        }

        private void TryDisposePreviewWebView()
        {
            try
            {
                PreviewWebView.Dispose();
            }
            catch (InvalidOperationException)
            {
            }
            catch (COMException)
            {
            }
        }

        private static void TryDeleteOwnedPreviewDirectory(
            string previewDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(previewDirectoryPath))
            {
                return;
            }

            var previewSessionsRootDirectory = Path.GetFullPath(Path.Combine(
                    System.Environment.GetFolderPath(
                        System.Environment.SpecialFolder.LocalApplicationData),
                    "RD",
                    "WebPreviewWorker",
                    "Sessions"))
                .TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var normalizedPreviewDirectoryPath = Path.GetFullPath(
                    previewDirectoryPath)
                .TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!normalizedPreviewDirectoryPath.StartsWith(
                previewSessionsRootDirectory,
                StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                Directory.Delete(
                    normalizedPreviewDirectoryPath.TrimEnd(
                        Path.DirectorySeparatorChar),
                    true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private void TitleBar_MouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs eventArguments)
        {
            if (eventArguments.ClickCount > 1)
            {
                eventArguments.Handled = true;
                return;
            }

            if (eventArguments.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void BusbarWebPreviewWindow_SizeChanged(
            object sender,
            SizeChangedEventArgs eventArguments)
        {
            if (!IsLoaded ||
                _isPreviewWindowSizeAdjustmentActive ||
                WindowState != WindowState.Normal)
            {
                return;
            }

            var previewWindowWidthChange = Math.Abs(
                eventArguments.NewSize.Width -
                eventArguments.PreviousSize.Width);
            var previewWindowHeightChange = Math.Abs(
                eventArguments.NewSize.Height -
                eventArguments.PreviousSize.Height);
            _isPreviewWindowSizeAdjustmentActive = true;
            try
            {
                if (previewWindowWidthChange >= previewWindowHeightChange)
                {
                    var constrainedPreviewWindowWidth = Math.Max(
                        MinWidth,
                        eventArguments.NewSize.Width);
                    Width = constrainedPreviewWindowWidth;
                    Height = Math.Max(
                        MinHeight,
                        constrainedPreviewWindowWidth +
                            PreviewViewportHeightOffset);
                }
                else
                {
                    var constrainedPreviewWindowHeight = Math.Max(
                        MinHeight,
                        eventArguments.NewSize.Height);
                    Height = constrainedPreviewWindowHeight;
                    Width = Math.Max(
                        MinWidth,
                        constrainedPreviewWindowHeight -
                            PreviewViewportHeightOffset);
                }
            }
            finally
            {
                _isPreviewWindowSizeAdjustmentActive = false;
            }
        }

        private void BusbarWebPreviewWindow_StateChanged(
            object sender,
            EventArgs eventArguments)
        {
            if (_isPreviewWindowStateRestorationActive ||
                WindowState == WindowState.Normal ||
                Owner?.WindowState == WindowState.Minimized)
            {
                return;
            }

            _isPreviewWindowStateRestorationActive = true;
            try
            {
                WindowState = WindowState.Normal;
            }
            finally
            {
                _isPreviewWindowStateRestorationActive = false;
            }
        }
    }
}
