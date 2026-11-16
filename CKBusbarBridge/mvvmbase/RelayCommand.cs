using System;
using System.Diagnostics;
using System.Windows.Input;

namespace CKBusbarBridge
{
    /// <summary>
    /// RelayCommand类实现了ICommand接口，适用于无参数的命令绑定。
    /// 可用于MVVM模式下将ViewModel中的方法绑定到UI控件的Command属性。
    /// </summary>
    public class RelayCommand : ICommand
    {
        #region private fields
        /// <summary>
        /// 命令执行的委托，无参数。
        /// </summary>
        private readonly Action execute;
        /// <summary>
        /// 判断命令是否可执行的委托，无参数，返回bool。
        /// </summary>
        private readonly Func<bool> canExecute;
        #endregion

        /// <summary>
        /// 当命令可执行状态发生变化时触发的事件。
        /// 通常用于通知UI控件刷新命令的可用状态。
        /// </summary>
        public event EventHandler CanExecuteChanged
        {
            add
            {
                // 只有canExecute不为空时，才订阅CommandManager的RequerySuggested事件
                // 这样可以在外部调用CommandManager.InvalidateRequerySuggested()时自动刷新命令状态
                if (this.canExecute != null)
                {
                    CommandManager.RequerySuggested += value;
                }
            }
            remove
            {
                if (this.canExecute != null)
                {
                    CommandManager.RequerySuggested -= value;
                }
            }
        }

        /// <summary>
        /// 构造函数，仅指定执行逻辑，不指定可执行条件，命令始终可用。
        /// </summary>
        /// <param name="execute">命令执行的委托。</param>
        public RelayCommand(Action execute)
            : this(execute, null)
        {
        }

        /// <summary>
        /// 构造函数，指定执行逻辑和可执行条件。
        /// </summary>
        /// <param name="execute">命令执行的委托。</param>
        /// <param name="canExecute">命令是否可执行的委托。</param>
        public RelayCommand(Action execute, Func<bool> canExecute)
        {
            if (execute == null)
            {
                // 如果未传入执行委托，则抛出异常
                throw new ArgumentNullException("execute");
            }

            this.execute = execute;
            this.canExecute = canExecute;
        }

        /// <summary>
        /// 执行命令的具体方法，调用execute委托。
        /// </summary>
        /// <param name="parameter">命令参数，未使用。</param>
        public void Execute(object parameter)
        {
            var sourceWindow = CommandWindowForegroundRestorer.CaptureSourceWindow();
            try
            {
                this.execute();
            }
            finally
            {
                CommandWindowForegroundRestorer.Restore(sourceWindow);
            }
        }

        /// <summary>
        /// 判断命令是否可执行，调用canExecute委托。
        /// </summary>
        /// <param name="parameter">命令参数，未使用。</param>
        /// <returns>如果canExecute为空则始终可执行，否则返回canExecute的结果。</returns>
        public bool CanExecute(object parameter)
        {
            return this.canExecute == null ? true : this.canExecute();
        }
    }

    /// <summary>
    /// RelayCommand泛型类，实现了ICommand接口，适用于带参数的命令绑定。
    /// 可用于MVVM模式下将带参数的方法绑定到UI控件的Command属性。
    /// </summary>
    /// <typeparam name="T">命令参数类型。</typeparam>
    public class RelayCommand<T> : ICommand
    {
        #region private fields
        /// <summary>
        /// 命令执行的委托，带参数T。
        /// </summary>
        private readonly Action<T> execute;
        /// <summary>
        /// 判断命令是否可执行的委托，带参数T，返回bool。
        /// </summary>
        private readonly Predicate<T> canExecute;
        #endregion

        /// <summary>
        /// 当命令可执行状态发生变化时触发的事件。
        /// 通常用于通知UI控件刷新命令的可用状态。
        /// </summary>
        public event EventHandler CanExecuteChanged
        {
            add
            {
                // 只有canExecute不为空时，才订阅CommandManager的RequerySuggested事件
                if (this.canExecute != null)
                {
                    CommandManager.RequerySuggested += value;
                }
            }
            remove
            {
                if (this.canExecute != null)
                {
                    CommandManager.RequerySuggested -= value;
                }
            }
        }

        /// <summary>
        /// 构造函数，仅指定执行逻辑，不指定可执行条件，命令始终可用。
        /// </summary>
        /// <param name="execute">命令执行的委托。</param>
        public RelayCommand(Action<T> execute)
            : this(execute, null)
        {
        }

        /// <summary>
        /// 构造函数，指定执行逻辑和可执行条件。
        /// </summary>
        /// <param name="execute">命令执行的委托。</param>
        /// <param name="canExecute">命令是否可执行的委托。</param>
        public RelayCommand(Action<T> execute, Predicate<T> canExecute)
        {
            if (execute == null)
            {
                // 如果未传入执行委托，则抛出异常
                throw new ArgumentNullException("execute");
            }

            this.execute = execute;
            this.canExecute = canExecute;
        }

        /// <summary>
        /// 执行命令的具体方法，调用execute委托并传入参数。
        /// </summary>
        /// <param name="parameter">命令参数，需为T类型。</param>
        public void Execute(object parameter)
        {
            var sourceWindow = CommandWindowForegroundRestorer.CaptureSourceWindow();
            try
            {
                this.execute((T)parameter);
            }
            finally
            {
                CommandWindowForegroundRestorer.Restore(sourceWindow);
            }
        }

        /// <summary>
        /// 判断命令是否可执行，调用canExecute委托并传入参数。
        /// 若参数类型不匹配，则在DEBUG模式下输出调试信息，默认返回true。
        /// </summary>
        /// <param name="parameter">命令参数，需为T类型。</param>
        /// <returns>如果canExecute为空则始终可执行，否则返回canExecute的结果。</returns>
        public bool CanExecute(object parameter)
        {
            if (parameter is T)
            {
                return this.canExecute == null ? true : this.canExecute((T)parameter);
            }

#if DEBUG
            if (parameter == null)
            {
                // Debug.WriteLine("RelayCommand: parameter is null");
            }
            else
            {
                Debug.WriteLine("RelayCommand: the type of the parameter does not match current type " +
                    parameter.GetType() + " excepted type " + typeof(T));
            }
#endif

            // 参数类型不匹配时，默认命令可执行
            return true;
        }
    }
}
