using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace CKBusbarBridge
{
	public class DelegateCommand : ICommand
	{
		public Action<object> ExecuteCommand = null;
		public Func<object, Task> ExecuteCommandAsync = null;
		public Func<object, bool> CanExecuteCommand = null;
		public event EventHandler CanExecuteChanged;

		public bool CanExecute(object parameter)
		{
			if (CanExecuteCommand != null)
			{
				return this.CanExecuteCommand(parameter);
			}
			else
			{
				return true;
			}
		}

		public async void Execute(object parameter)
		{
			var sourceWindow = CommandWindowForegroundRestorer.CaptureSourceWindow();
			try
			{
				if (this.ExecuteCommand != null) this.ExecuteCommand(parameter);
				if (this.ExecuteCommandAsync != null) await this.ExecuteCommandAsync(parameter);
			}
			finally
			{
				CommandWindowForegroundRestorer.Restore(sourceWindow);
			}
		}

		public void RaiseCanExecuteChanged()
		{
			if (CanExecuteChanged != null)
			{
				CanExecuteChanged(this, EventArgs.Empty);
			}
		}

        /// <summary>
        /// 得到委托按钮
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        public static DelegateCommand GetDelegateCommand(Action<object> obj)
		{
			//声明委托对象
			DelegateCommand delCom = new DelegateCommand();
			delCom.ExecuteCommand = obj;
			return delCom;
		}

		/// <summary>
		/// 得到异步委托按钮
		/// </summary>
		/// <param name="obj"></param>
		/// <returns></returns>
		public static DelegateCommand GetDelegateCommand(Func<object, Task> obj)
		{
			//声明委托对象
			DelegateCommand delCom = new DelegateCommand();
			delCom.ExecuteCommandAsync = obj;
			return delCom;
		}
	}

	/// <summary>
	/// 在命令完成后把发起命令的 WPF 窗口恢复到 SolidWorks 前面。
		/// 保持所有者层级，存在确认框时优先恢复确认框。
	/// </summary>
	internal static class CommandWindowForegroundRestorer
	{
		internal static Window CaptureSourceWindow()
		{
			var inputSource = Keyboard.FocusedElement as DependencyObject ??
				Mouse.DirectlyOver as DependencyObject;
			var sourceWindow = inputSource == null
				? null
				: Window.GetWindow(inputSource);
			if (sourceWindow != null)
			{
				return sourceWindow;
			}

			var application = Application.Current;
			if (application == null)
			{
				return null;
			}

			foreach (Window window in application.Windows)
			{
				if (window.IsActive)
				{
					return window;
				}
			}

			return null;
		}

		internal static void Restore(Window sourceWindow)
		{
			if (sourceWindow == null ||
				sourceWindow.Dispatcher.HasShutdownStarted ||
				sourceWindow.Dispatcher.HasShutdownFinished)
			{
				return;
			}

			try
			{
				sourceWindow.Dispatcher.BeginInvoke(
					DispatcherPriority.ApplicationIdle,
					new Action(() => RestoreOnDispatcher(sourceWindow)));
			}
			catch (InvalidOperationException)
			{
				// 窗口可能在命令结束和调度恢复之间已经关闭。
			}
		}

		private static void RestoreOnDispatcher(Window sourceWindow)
		{
			if (!sourceWindow.IsLoaded || !sourceWindow.IsVisible)
			{
				return;
			}

			try
			{
				if (sourceWindow.WindowState == WindowState.Minimized)
				{
					sourceWindow.WindowState = WindowState.Normal;
				}

					CKBusbarBridgePreview.UiFeedback.ActivateWindow(sourceWindow);
			}
			catch (InvalidOperationException)
			{
				// 关闭中的窗口无需继续争取前台焦点。
			}
		}
	}
}
