using RD.UI.Wpf.Controls;
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
        private static readonly Logger ProgressToastLogger = LogManager.GetCurrentClassLogger();
        private static readonly object Gate = new object();
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
            var rdButtons = (RDMessageBoxButton)Enum.Parse(typeof(RDMessageBoxButton), buttons.ToString());
            var rdKind = icon == MessageBoxImage.Warning ? RDMessageBoxKind.Warning :
                icon == MessageBoxImage.Error ? RDMessageBoxKind.Error :
                icon == MessageBoxImage.Question ? RDMessageBoxKind.Question : RDMessageBoxKind.Info;
            var rdOptions = new RDMessageBoxOptions { ShowLogo = false };
            if (defaultResult != MessageBoxResult.None)
            {
                var preferredResult = (RDMessageBoxResult)Enum.Parse(
                    typeof(RDMessageBoxResult), defaultResult.ToString());
                rdOptions.DefaultResult = preferredResult;
                rdOptions.CloseResult = preferredResult;
            }
            var rdResult = RDMessageBox.Show(owner, message ?? string.Empty,
                string.IsNullOrWhiteSpace(title) ? "提示" : title, rdButtons, rdKind,
                rdOptions);
            return (MessageBoxResult)Enum.Parse(typeof(MessageBoxResult), rdResult.ToString());
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
                return InvokePresentation(() => new FeedbackProgressScope(
                    RDToast.BeginProgress(string.IsNullOrWhiteSpace(title) ? "正在处理" : title)));
            }
            catch (Exception exception)
            {
                ProgressToastLogger.Error(exception, "进度组件创建失败，不影响业务执行。");
                return new FeedbackProgressScope();
            }
        }

        internal static void Notify(string message, string title, bool success)
        {
            NotifyToast(message, title, success ? RDMessageBoxKind.Success : RDMessageBoxKind.Info);
        }

        internal static void NotifyToast(string message, string title, RDMessageBoxKind kind)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            try
            {
                PostPresentation(() =>
                {
                    if (kind == RDMessageBoxKind.Warning)
                        RDToast.Warning(message, title);
                    else if (kind == RDMessageBoxKind.Error)
                        RDToast.Error(message, title, null, null);
                    else
                        RDToast.Success(message, title);
                });
            }
            catch (Exception exception)
            {
                ProgressToastLogger.Error(exception, "RD 提示投递失败。");
            }
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
        private readonly RDProgressToast _toast;
        private int _disposed;

        internal FeedbackProgressScope() { }
        internal FeedbackProgressScope(RDProgressToast toast) { _toast = toast; }

        public void Report(FeedbackProgress update)
        {
            if (_toast == null || update == null || Volatile.Read(ref _disposed) != 0) return;
            var stage = update.Stage;
            var current = update.Current;
            var total = update.Total;
            var detail = update.Detail;
            FeedbackRuntime.PostPresentation(() =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                _toast.Report(new RDToastProgress
                {
                    Stage = stage, Current = current, Total = total, Detail = detail
                });
            });
        }

        public void Dispose()
        {
            if (_toast == null || Interlocked.Exchange(ref _disposed, 1) != 0) return;
            FeedbackRuntime.PostPresentation(() => _toast.Finish());
        }
    }
}
