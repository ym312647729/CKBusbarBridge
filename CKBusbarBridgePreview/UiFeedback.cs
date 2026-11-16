using SolidWorks.Interop.swconst;
using RD.UI.Wpf.Controls;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// 插件及预览库共用的交互入口。业务只传入消息或执行方法，不创建提示窗口。
    /// SolidWorks COM 工作始终在调用线程执行，只有进度和通知在专用 STA 线程绘制。
    /// </summary>
    public static class UiFeedback
    {
        public const int NotificationSeconds = 4;

        /// <summary>插件连接时复位提示线程生命周期；按需启动，不在这里创建窗口。</summary>
        public static void InitializePresentation()
        {
            FeedbackRuntime.InitializePresentation();
            // ConnectToSW 在宿主 STA 调用，Application 随宿主存活，不归提示线程关闭。
            FeedbackRuntime.EnsureApplication();
        }

        /// <summary>仅执行纯界面操作，不能传入 SolidWorks COM 对象或业务操作。</summary>
        public static T InvokePresentation<T>(Func<T> presentationAction)
        {
            if (presentationAction == null) throw new ArgumentNullException(nameof(presentationAction));
            return FeedbackRuntime.InvokePresentation(presentationAction);
        }

        /// <summary>在插件复用的 STA 上异步绘制提示；卸载开始后忽略新提示。</summary>
        public static void PostPresentation(Action presentationAction)
        {
            if (presentationAction == null) throw new ArgumentNullException(nameof(presentationAction));
            FeedbackRuntime.PostPresentation(presentationAction);
        }

        /// <summary>在提示 STA 内显示窗口，并纳入插件卸载时的统一关闭清单。</summary>
        public static bool ShowPresentationWindow(Window presentationWindow)
        {
            if (presentationWindow == null) throw new ArgumentNullException(nameof(presentationWindow));
            return FeedbackRuntime.ShowPresentationWindow(presentationWindow);
        }

        /// <summary>停止接收新提示并关闭窗口，保留 Dispatcher 供宿主随后完成 GC。</summary>
        public static void PrepareForShutdown() => FeedbackRuntime.PrepareForShutdown();

        /// <summary>宿主完成 GC 后清理所属 COM 上下文，再关闭并等待提示线程。</summary>
        public static void ShutdownPresentation() => FeedbackRuntime.ShutdownPresentation();

        /// <summary>未指定 WPF 父窗时，使用当前宿主界面作为所有者。</summary>
        public static void AssignWindowOwner(Window window)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            FeedbackRuntime.AssignWindowOwner(window);
        }

        /// <summary>被模态子窗禁用时，激活子窗，避免宿主命令把父窗抬到前面。</summary>
        public static void ActivateWindow(Window window)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            FeedbackRuntime.ActivateWindow(window);
        }

        public static void Success(string message, string title = "操作完成")
        {
            FeedbackRuntime.Notify(message, title, true);
        }

        public static void Information(string message, string title = "提示")
        {
            FeedbackRuntime.Notify(message, title, false);
        }

        public static void Warning(string message, string title = "警告")
        {
            FeedbackRuntime.NotifyToast(message, title, RDMessageBoxKind.Warning);
        }

        public static void Error(string message, string title = "错误")
        {
            FeedbackRuntime.NotifyToast(message, title, RDMessageBoxKind.Error);
        }

        // 兼容现有 WPF 调用的参数，统一在这里决定通知/警告/错误/确认的呈现方式。
        public static MessageBoxResult Show(string message, string title = "提示",
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage icon = MessageBoxImage.Information,
            MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            return Show(null, message, title, buttons, icon, defaultResult);
        }

        public static MessageBoxResult Show(Window owner, string message, string title,
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage icon = MessageBoxImage.Information,
            MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            if (buttons == MessageBoxButton.OK)
            {
                if (icon == MessageBoxImage.Warning) Warning(message, title);
                else if (icon == MessageBoxImage.Error) Error(message, title);
                else Information(message, title);
                return MessageBoxResult.OK;
            }

            if (owner != null && !owner.Dispatcher.CheckAccess())
                return owner.Dispatcher.Invoke(() => Show(owner, message, title, buttons, icon, defaultResult));

            FeedbackRuntime.EnsureApplication();
            if (!Application.Current.Dispatcher.CheckAccess() && owner == null)
                return Application.Current.Dispatcher.Invoke(() => Show(null, message, title, buttons, icon, defaultResult));
            return FeedbackRuntime.ShowDialog(owner, message, title, buttons, icon, defaultResult);
        }

        public static bool Confirm(string message, string title = "操作确认", Action execute = null)
        {
            bool confirmed = Show(message, title, MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
            if (confirmed) execute?.Invoke();
            return confirmed;
        }

        public static void ShowSolidWorks(string message, int icon, int buttons)
        {
            // 当前插件原生提示均只有 OK；询问必须使用 Confirm 并处理返回结果。
            if (buttons != (int)swMessageBoxBtn_e.swMbOk)
                throw new ArgumentOutOfRangeException(nameof(buttons));
            Show(message, "母线桥设计", MessageBoxButton.OK,
                icon == (int)swMessageBoxIcon_e.swMbStop ? MessageBoxImage.Error :
                icon == (int)swMessageBoxIcon_e.swMbWarning ? MessageBoxImage.Warning :
                MessageBoxImage.Information);
        }

        public static void Run(string title, Action<IProgress<FeedbackProgress>> execute,
            string successMessage = null)
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));
            Run<object>(title, progress => { execute(progress); return null; }, successMessage);
        }

        public static T Run<T>(string title, Func<IProgress<FeedbackProgress>, T> execute,
            string successMessage = null)
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));
            T result;
            using (var progress = FeedbackRuntime.BeginProgress(title))
            {
                // 不使用 Task.Run：SolidWorks COM 对象和调用方窗口属于原线程。
                result = execute(progress);
            }
            // 异常继续交给原业务处理；失败或取消时绝不补发“执行完成”。
            if (!string.IsNullOrWhiteSpace(successMessage)) Success(successMessage, title);
            return result;
        }

        public static async Task<T> RunAsync<TUpdate, T>(string title,
            Func<IProgress<TUpdate>, Task<T>> execute, Func<TUpdate, FeedbackProgress> mapProgress)
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));
            if (mapProgress == null) throw new ArgumentNullException(nameof(mapProgress));
            using (var progress = FeedbackRuntime.BeginProgress(title))
            {
                // 在调用方同步上下文转发 Worker 回报，保留界面更新的线程约束。
                var updates = new Progress<TUpdate>(update => progress.Report(mapProgress(update)));
                return await execute(updates);
            }
        }
    }

    public sealed class FeedbackProgress
    {
        public string Stage { get; set; }
        public int Current { get; set; }
        public int Total { get; set; }
        public string Detail { get; set; }
        public long? EstimatedDurationMilliseconds { get; set; }
    }
}
