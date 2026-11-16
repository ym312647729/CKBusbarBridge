using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// 在独立 AppDomain 中承载铜排图纸运行包，避免与主插件中同名但内容不同的依赖互相覆盖。
    /// </summary>
    public sealed class CopperDrawingRuntimeHost : MarshalByRefObject
    {
        private string _addinDirectory;
        private string _runtimeDirectory;
        private Form _drawingWindow;
        private bool _assemblyResolverRegistered;

        public override object InitializeLifetimeService()
        {
            return null;
        }

        public bool IsWindowOpen()
        {
            return _drawingWindow != null && !_drawingWindow.IsDisposed;
        }

        public void ShowWindow(
            string addinDirectory,
            string runtimeDirectory,
            object solidWorksApplication)
        {
            if (IsWindowOpen())
            {
                ActivateWindow();
                return;
            }

            if (string.IsNullOrWhiteSpace(addinDirectory))
            {
                throw new ArgumentException("插件目录不能为空。", "addinDirectory");
            }

            if (string.IsNullOrWhiteSpace(runtimeDirectory))
            {
                throw new ArgumentException("铜排图纸运行目录不能为空。", "runtimeDirectory");
            }

            if (solidWorksApplication == null)
            {
                throw new ArgumentNullException("solidWorksApplication");
            }

            _addinDirectory = addinDirectory;
            _runtimeDirectory = runtimeDirectory;
            EnsureAssemblyResolverRegistered();

            string utilityAssemblyPath = Path.Combine(
                _runtimeDirectory,
                "BusbarTools.BuMathUtility.dll");
            if (!File.Exists(utilityAssemblyPath))
            {
                throw new FileNotFoundException(
                    "未找到铜排图纸模型检查组件。",
                    utilityAssemblyPath);
            }

            Assembly utilityAssembly = Assembly.LoadFrom(utilityAssemblyPath);
            Type extensionType = utilityAssembly.GetType(
                "BusbarTools.BuMathUtility.SwExtention",
                true);
            MethodInfo saveMethod = null;
            MethodInfo[] publicStaticExtensionMethods = extensionType.GetMethods(
                BindingFlags.Public | BindingFlags.Static);
            foreach (MethodInfo extensionMethodCandidate in publicStaticExtensionMethods)
            {
                ParameterInfo[] extensionMethodParameters =
                    extensionMethodCandidate.GetParameters();
                if (string.Equals(
                        extensionMethodCandidate.Name,
                        "CheckModelNeedSave",
                        StringComparison.Ordinal) &&
                    extensionMethodParameters.Length == 1 &&
                    extensionMethodParameters[0].ParameterType.IsInstanceOfType(
                        solidWorksApplication))
                {
                    saveMethod = extensionMethodCandidate;
                    break;
                }
            }

            if (saveMethod == null)
            {
                throw new MissingMethodException(
                    extensionType.FullName,
                    "CheckModelNeedSave");
            }

            saveMethod.Invoke(null, new[] { solidWorksApplication });

            string drawingAssemblyPath = Path.Combine(
                _runtimeDirectory,
                "PartDrawingDoc.dll");
            if (!File.Exists(drawingAssemblyPath))
            {
                throw new FileNotFoundException(
                    "未找到铜排图纸功能组件。",
                    drawingAssemblyPath);
            }

            Assembly drawingAssembly = Assembly.LoadFrom(drawingAssemblyPath);
            Type drawingWindowType = drawingAssembly.GetType(
                "PartDrawingDoc.ui.PickBusbarForm",
                true);
            var createdDrawingWindow = Activator.CreateInstance(
                drawingWindowType,
                new[] { solidWorksApplication }) as Form;
            if (createdDrawingWindow == null)
            {
                throw new InvalidOperationException("铜排图纸窗口创建失败。");
            }

            _drawingWindow = createdDrawingWindow;
            createdDrawingWindow.FormClosed += OnDrawingWindowClosed;
            createdDrawingWindow.Show();
            createdDrawingWindow.Activate();
            createdDrawingWindow.BringToFront();
        }

        public void ActivateWindow()
        {
            if (!IsWindowOpen())
            {
                return;
            }

            _drawingWindow.Show();
            _drawingWindow.Activate();
            _drawingWindow.BringToFront();
        }

        public void CloseWindow()
        {
            if (!IsWindowOpen())
            {
                _drawingWindow = null;
                return;
            }

            Form drawingWindowToClose = _drawingWindow;
            _drawingWindow = null;
            drawingWindowToClose.FormClosed -= OnDrawingWindowClosed;
            drawingWindowToClose.Close();
        }

        private void EnsureAssemblyResolverRegistered()
        {
            if (_assemblyResolverRegistered)
            {
                return;
            }

            AppDomain.CurrentDomain.AssemblyResolve += ResolveRuntimeAssembly;
            _assemblyResolverRegistered = true;
        }

        private Assembly ResolveRuntimeAssembly(
            object resolverSender,
            ResolveEventArgs resolverEventArgs)
        {
            var requestedAssemblyName = new AssemblyName(resolverEventArgs.Name);
            string requestedAssemblyFile = requestedAssemblyName.Name + ".dll";
            string[] candidateDirectories =
            {
                _runtimeDirectory,
                _addinDirectory
            };

            foreach (string candidateDirectory in candidateDirectories)
            {
                string candidateAssemblyPath = Path.Combine(
                    candidateDirectory,
                    requestedAssemblyFile);
                if (!File.Exists(candidateAssemblyPath))
                {
                    continue;
                }

                try
                {
                    AssemblyName candidateAssemblyName =
                        AssemblyName.GetAssemblyName(candidateAssemblyPath);
                    bool assemblyIdentityMatches = string.Equals(
                        candidateAssemblyName.Name,
                        requestedAssemblyName.Name,
                        StringComparison.OrdinalIgnoreCase);
                    bool assemblyVersionMatches = requestedAssemblyName.Version == null ||
                        candidateAssemblyName.Version == requestedAssemblyName.Version;
                    if (!assemblyIdentityMatches || !assemblyVersionMatches)
                    {
                        continue;
                    }

                    return Assembly.LoadFrom(candidateAssemblyPath);
                }
                catch (BadImageFormatException)
                {
                    // 非托管或位数不匹配的文件不参与托管程序集解析。
                }
                catch (FileLoadException)
                {
                    // 继续交给 CLR 处理其他可能的解析来源。
                }
            }

            return null;
        }

        private void OnDrawingWindowClosed(
            object closedWindowSender,
            FormClosedEventArgs closedWindowEventArgs)
        {
            if (ReferenceEquals(_drawingWindow, closedWindowSender))
            {
                _drawingWindow = null;
            }
        }
    }
}
