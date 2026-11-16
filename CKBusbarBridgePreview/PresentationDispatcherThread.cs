using NLog;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace CKBusbarBridgePreview
{
    // 只接收界面操作；宿主 COM 对象及业务委托不得传入这个线程。
    internal sealed class PresentationDispatcherThread
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
        private readonly List<Window> _presentationWindows = new List<Window>();
        private readonly Thread _presentationThread;
        private readonly Dispatcher _presentationDispatcher;
        private volatile bool _stopping;

        internal bool IsAlive => _presentationThread.IsAlive;
        internal bool IsStopping => _stopping;

        internal PresentationDispatcherThread()
        {
            Dispatcher startedDispatcher = null;
            Exception startupFailure = null;
            using (var startupSignal = new ManualResetEventSlim())
            {
                _presentationThread = new Thread(() =>
                {
                    try
                    {
                        startedDispatcher = Dispatcher.CurrentDispatcher;
                    }
                    catch (Exception exception)
                    {
                        startupFailure = exception;
                    }
                    finally
                    {
                        startupSignal.Set();
                    }
                    if (startedDispatcher != null) RunDispatcher();
                }) { IsBackground = true, Name = "CKBusbarBridgeFeedbackUI" };
                _presentationThread.SetApartmentState(ApartmentState.STA);
                _presentationThread.Start();
                startupSignal.Wait();
            }
            if (startupFailure != null)
                throw new InvalidOperationException("提示界面线程启动失败。", startupFailure);
            _presentationDispatcher = startedDispatcher;
        }

        private void RunDispatcher()
        {
            try
            {
                Logger.Debug("提示界面 STA 线程启动：{0}", Thread.CurrentThread.ManagedThreadId);
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                // 专用 UI 线程的最外层；不让提示组件的异常退出 SolidWorks 进程。
                Logger.Error(exception, "提示界面 STA 线程意外退出。");
            }
            finally
            {
                _stopping = true;
                try
                {
                    var endingDispatcher = Dispatcher.CurrentDispatcher;
                    if (!endingDispatcher.HasShutdownStarted) endingDispatcher.InvokeShutdown();
                }
                catch (Exception exception) { Logger.Warn(exception, "提示线程退出时关闭 Dispatcher 失败。"); }
                CleanupCurrentContext();
                Logger.Debug("提示界面 STA 线程结束：{0}", Thread.CurrentThread.ManagedThreadId);
            }
        }

        internal T Invoke<T>(Func<T> presentationAction)
        {
            if (_stopping) throw new ObjectDisposedException(nameof(PresentationDispatcherThread));
            return _presentationDispatcher.Invoke(new Func<T>(() =>
            {
                if (_stopping) throw new ObjectDisposedException(nameof(PresentationDispatcherThread));
                return presentationAction();
            }));
        }

        internal void Post(Action presentationAction)
        {
            if (_stopping) return;
            _presentationDispatcher.BeginInvoke(new Action(() =>
            {
                if (_stopping) return;
                try { presentationAction(); }
                catch (Exception exception) { Logger.Error(exception, "提示界面操作失败。"); }
            }));
        }

        internal bool ShowWindow(Window presentationWindow)
        {
            _presentationDispatcher.VerifyAccess();
            if (_stopping)
            {
                presentationWindow.Close();
                return false;
            }
            _presentationWindows.Add(presentationWindow);
            presentationWindow.Closed += (sender, args) =>
            {
                _presentationWindows.Remove(presentationWindow);
                // 断开已关闭窗口对控件、动画及其系统资源的持有。
                presentationWindow.Content = null;
            };
            try
            {
                presentationWindow.Show();
                return true;
            }
            catch
            {
                try { presentationWindow.Close(); }
                catch (Exception exception) { Logger.Warn(exception, "回收显示失败的提示窗口时关闭失败。"); }
                throw;
            }
        }

        internal void PrepareForShutdown()
        {
            _stopping = true;
            if (!IsAlive || _presentationDispatcher.HasShutdownStarted) return;
            try
            {
                _presentationDispatcher.Invoke(new Action(CloseWindows), DispatcherPriority.Send, ShutdownTimeout);
            }
            catch (Exception exception)
            {
                Logger.Warn(exception, "卸载插件时关闭提示窗口失败，将保留界面线程以便清理 COM 上下文。");
            }
        }

        private void CloseWindows()
        {
            foreach (var presentationWindow in _presentationWindows.ToArray())
            {
                try { presentationWindow.Close(); }
                catch (Exception exception) { Logger.Warn(exception, "关闭提示窗口失败：{0}", presentationWindow.Title); }
            }
        }

        internal void Shutdown()
        {
            _stopping = true;
            if (IsAlive && !_presentationDispatcher.HasShutdownStarted)
            {
                try
                {
                    _presentationDispatcher.Invoke(new Action(() =>
                    {
                        CloseWindows();
                        if (_presentationWindows.Count != 0)
                            throw new InvalidOperationException("仍有提示窗口未关闭，不能结束所属 STA 线程。");
                        if (!CleanupCurrentContext())
                            throw new InvalidOperationException("提示线程的 COM 清理未完成，不能结束 STA 上下文。");
                        _presentationDispatcher.InvokeShutdown();
                    }), DispatcherPriority.Send, ShutdownTimeout);
                }
                catch (Exception exception)
                {
                    Logger.Warn(exception, "提示界面线程关闭失败，保留仍存活的 COM 上下文。");
                    return;
                }
            }
            // 不在工作线程内 Join 自身，也不持有 FeedbackRuntime 的启动锁等待。
            if (Thread.CurrentThread != _presentationThread && !_presentationThread.Join(ShutdownTimeout))
                Logger.Warn("等待提示界面 STA 线程结束超时，未强制终止线程。");
        }

        private bool CleanupCurrentContext()
        {
            try { Marshal.CleanupUnusedObjectsInCurrentContext(); return true; }
            catch (Exception exception)
            {
                Logger.Warn(exception, "清理提示界面 STA 的 COM 上下文失败。");
                return false;
            }
        }
    }
}
