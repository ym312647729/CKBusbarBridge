using CKBusbarBridge.Drivers;
using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using CKBusbarBridge.Prototype;
using CKBusbarBridge.views;
using CKBusbarBridgePreview;
using CKBusbarBridgePreview.OpenHole;
using Microsoft.Win32;
using OpenHoleLib.Controls;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;
using SolidWorksTools.File;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using RD.UI.Wpf.Controls;
using UiFeedback = CKBusbarBridgePreview.UiFeedback;

namespace CKBusbarBridge
{
    [Guid("46af9620-9777-42ed-9df4-25a2ac4f860d"), ProgId("CKBusbarBridge.SwAddin"), ComVisible(true)]
    public sealed class SwAddin : ISwAddin
    {
        private const int CommandGroupId = 4601;
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private ISldWorks iSwApp;
        private ICommandManager _commands;
        private BitmapHandler _bitmaps;
        private Window _cabinetDriverWindow;
        private CabinetDraftWindowWorkflow _cabinetDraftWorkflow;
        private AboutWindow _aboutWindow;
        private BridgeDesignWindowController _bridgeDesignController;
        private OpenHoleLibraryWindow _openHoleLibSettingWindow;
        private AppDomain _copperDrawingRuntimeDomain;
        private CopperDrawingRuntimeHost _copperDrawingRuntimeHost;

        [ComRegisterFunction]
        public static void RegisterFunction(Type addinType)
        {
            using (var installed = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\SolidWorks\Addins\" + addinType.GUID.ToString("B")))
            {
                installed.SetValue(null, 0, RegistryValueKind.DWord);
                installed.SetValue("Title", AboutInformation.ProductName);
                installed.SetValue("Description", AboutInformation.ProductName);
            }
            using (var startup = Registry.CurrentUser.CreateSubKey(@"Software\SolidWorks\AddInsStartup\" + addinType.GUID.ToString("B")))
                startup.SetValue(null, 0, RegistryValueKind.DWord);
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type addinType)
        {
            Registry.LocalMachine.DeleteSubKey(@"SOFTWARE\SolidWorks\Addins\" + addinType.GUID.ToString("B"), false);
            Registry.CurrentUser.DeleteSubKey(@"Software\SolidWorks\AddInsStartup\" + addinType.GUID.ToString("B"), false);
        }

        public bool ConnectToSW(object application, int cookie)
        {
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
            try
            {
                iSwApp = (ISldWorks)application;
                if (!iSwApp.SetAddinCallbackInfo2(0, this, cookie))
                    throw new InvalidOperationException("未能向 SolidWorks 登记本项目命令回调。");
                UiFeedback.InitializePresentation();
                _commands = iSwApp.GetCommandManager(cookie);
                RegisterCommands();
                BridgeParameterDraftDocuments.For(iSwApp);
                return true;
            }
            catch (Exception error)
            {
                Logger.Error(error, "加载母线桥项目插件失败。");
                DisconnectFromSW();
                return false;
            }
        }

        public bool DisconnectFromSW()
        {
            var success = true;
            void Release(string operation, Action action)
            {
                try { action(); }
                catch (Exception error) { success = false; Logger.Error(error, operation); }
            }
            Release("关闭本项目柜体草稿窗口", () => _cabinetDraftWorkflow?.CloseWithoutPrompt());
            Release("关闭本项目桥体草稿窗口", () => _bridgeDesignController?.CloseForAddinShutdown());
            Release("关闭本项目关于窗口", () => _aboutWindow?.Close());
            Release("关闭本项目设置窗口", () => _openHoleLibSettingWindow?.Close());
            Release("释放本项目铜排图纸运行域", ReleaseCopperDrawingRuntime);
            if (iSwApp != null) Release("释放本项目预览", () => BridgeParameterDraftDocuments.Release(iSwApp));
            Release("卸载本项目命令", () => _commands?.RemoveCommandGroup(CommandGroupId));
            Release("释放本项目命令图标", () => _bitmaps?.Dispose());
            Release("关闭本项目消息提示", () => { UiFeedback.PrepareForShutdown(); UiFeedback.ShutdownPresentation(); });
            _commands = null; iSwApp = null;
            AppDomain.CurrentDomain.AssemblyResolve -= CurrentDomain_AssemblyResolve;
            return success;
        }

        private void RegisterCommands()
        {
            var status = 0;
            var group = _commands.CreateCommandGroup2(CommandGroupId, AboutInformation.ProductName,
                AboutInformation.ProductName, "", -1, false, ref status)
                ?? throw new InvalidOperationException("无法建立母线桥项目命令组：" + status);
            var sizes = new[] { 20, 32, 40, 64, 96, 128 };
            var assembly = typeof(SwAddin).Assembly;
            _bitmaps = new BitmapHandler();
            group.MainIconList = sizes.Select(size => _bitmaps.CreateFileFromResourceBitmap("CKBusbarBridge.icons.mainicon_" + size + ".png", assembly)).ToArray();
            group.IconList = sizes.Select(size => _bitmaps.CreateFileFromResourceBitmap("CKBusbarBridge.icons.toolbar" + size + "x.png", assembly)).ToArray();
            int Add(string label, int icon, string action, string enable, int id)
                => group.AddCommandItem2(label, -1, label, label, icon, action, enable, id,
                    (int)swCommandItemType_e.swMenuItem | (int)swCommandItemType_e.swToolbarItem);
            var indexes = new[]
            {
                Add("柜体设计", 0, nameof(ShowCabinetDriverWindow), nameof(EnableCabinetDriverWindow), 1),
                Add("母线桥设计", 1, nameof(ShowFrameDriverWindow), nameof(EnableFrameDriverWindow), 2),
                Add("出工程图", 4, nameof(ShowExportDrawingWindow), nameof(EnableExportDrawingWindow), 3),
                Add("铜排出图", 5, nameof(ShowCopperDrawingWindow), nameof(EnableCopperDrawingWindow), 4),
                Add("项目设置", 8, nameof(ShowOpenHoleLibSettingWindow), nameof(EnableOpenHoleLibSettingWindow), 5),
                Add("关于", 9, nameof(ShowAboutWindow), nameof(EnableAboutWindow), 6)
            };
            if (indexes.Any(index => index < 0)) throw new InvalidOperationException("母线桥项目命令未全部登记。");
            group.HasMenu = true;
            group.HasToolbar = true;
            if (!group.Activate()) throw new InvalidOperationException("母线桥项目命令组激活失败。");
            foreach (var type in new[] { swDocumentTypes_e.swDocASSEMBLY, swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocDRAWING })
            {
                var tab = _commands.GetCommandTab((int)type, AboutInformation.ProductName);
                if (tab != null) continue;
                tab = _commands.AddCommandTab((int)type, AboutInformation.ProductName);
                var display = Enumerable.Repeat((int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow, indexes.Length).ToArray();
                tab.AddCommandTabBox().AddCommands(indexes.Select(index => group.get_CommandID(index)).ToArray(), display);
            }
        }

        private void RequireDrawingAssembly()
        {
            var assembly = iSwApp.IActiveDoc2;
            if (!(assembly is AssemblyDoc) ||
                !(((AssemblyDoc)assembly).GetComponents(false) is object[] components) || components.Length == 0 ||
                !string.IsNullOrWhiteSpace(BridgeParameterDraftDocument.ReadProperty(assembly, BridgeParameterDraftDocument.IdentityProperty)))
                throw new InvalidOperationException("出图需要打开已有实体装配体。参数草稿只有预览，不能出图。");
        }

        private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            AssemblyName assemblyName = new AssemblyName(args.Name);
            string requestedAssemblyFile = assemblyName.Name + ".dll";
            string addinDirectory = Path.GetDirectoryName(typeof(SwAddin).Assembly.Location);

            if (string.IsNullOrEmpty(addinDirectory))
            {
                return null;
            }

            string candidatePath = Path.Combine(addinDirectory, requestedAssemblyFile);
            if (!File.Exists(candidatePath))
            {
                return null;
            }

            try
            {
                AssemblyName candidateName = AssemblyName.GetAssemblyName(candidatePath);
                if (!string.Equals(candidateName.Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase) ||
                    assemblyName.Version != null && candidateName.Version != assemblyName.Version)
                {
                    return null;
                }

                return Assembly.LoadFrom(candidatePath);
            }
            catch (BadImageFormatException)
            {
                // Ignore native or incompatible files and let the CLR continue resolving.
            }
            catch (FileLoadException)
            {
                // A different version may already be loaded; let the CLR continue resolving.
            }

            return null;
        }

        public void ShowAboutWindow()
        {
            try
            {
                if (_aboutWindow == null)
                {
                    var information = AboutInformation.Load(typeof(SwAddin).Assembly);
                    if (information.ReleaseNotesError != null)
                        Logger.Warn(information.ReleaseNotesError, "关于窗口未能读取插件目录中的版本更新文档。");
                    _aboutWindow = new AboutWindow(information);
                    _aboutWindow.Closed += (sender, args) => _aboutWindow = null;
                    UiFeedback.AssignWindowOwner(_aboutWindow);
                }
                if (_aboutWindow.WindowState == WindowState.Minimized)
                    _aboutWindow.WindowState = WindowState.Normal;
                _aboutWindow.Show();
                UiFeedback.ActivateWindow(_aboutWindow);
            }
            catch (Exception error)
            {
                Logger.Error(error, "打开关于窗口失败。");
                UiFeedback.Error("无法打开关于窗口：" + error.Message);
            }
        }

        // 版本与更新说明不依赖活动文档，空白会话及零件、装配、图纸中均可查看。
        public int EnableAboutWindow() => 1;

        public void ShowCabinetDriverWindow()
        {
            try
            {
                if (_cabinetDriverWindow != null)
                {
                    if (_cabinetDriverWindow.WindowState == WindowState.Minimized)
                        _cabinetDriverWindow.WindowState = WindowState.Normal;
                    UiFeedback.ActivateWindow(_cabinetDriverWindow);
                    return;
                }

                var draft = BridgeParameterDraftDocuments.For(iSwApp).Active;
                if (draft != null && _bridgeDesignController?.LeaveDraftForCabinet(draft.Document) == false)
                    return;
                var scenario = draft == null ? DesignScenarioId.FaceToFace :
                    draft.Session.Scenario == "face-to-wall" ? DesignScenarioId.FaceToWall :
                    draft.Session.Scenario == "side-to-side" ? DesignScenarioId.SideToSide : DesignScenarioId.FaceToFace;
                FaceToWallCabinetOrientation? orientation = scenario == DesignScenarioId.FaceToWall
                    ? (FaceToWallCabinetOrientation?)Enum.Parse(typeof(FaceToWallCabinetOrientation), draft.Orientation) : null;
                OpenCabinetDesign(scenario, orientation, activeDraft: draft);
            }
            catch (Exception ex)
            {
                FaceToWallPreviewTrace.Error("打开柜体设计失败", ex);
                UiFeedback.ShowSolidWorks(ex.Message, (int)swMessageBoxIcon_e.swMbStop, (int)swMessageBoxBtn_e.swMbOk);
            }
        }

        private void OpenCabinetDesign(DesignScenarioId scenario, FaceToWallCabinetOrientation? orientation,
            BridgeParameterDraftDocument activeDraft)
        {
            var window = new RDWindow { Title = "柜体设计", WindowKey = "CabinetDriverWindow",
                IsPlacementPersistenceEnabled = false, UseLayoutRounding = true };
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/RD.UI.Wpf;component/Themes/Generic.xaml")
            });
            RightSidePanelLayout.Apply(window);
            UserControl CreatePage(DesignScenarioId target)
            {
                if (target == DesignScenarioId.FaceToWall)
                    return new FaceToWallCabinetDriverWindow(iSwApp, FaceToWallCabinetOrientation.CabinetFrontToWall);
                return target == DesignScenarioId.SideToSide ? (UserControl)new SideToSideCabinetDriverWindow(iSwApp) : new CabinetDriverWindow(iSwApp);
            }
            var workflow = new CabinetDraftWindowWorkflow(window, scenario, orientation, CreatePage, ValidateCabinetScenarioSwitch, OpenBridgeAfterCabinetDraft);
            window.Closed += (sender, args) =>
            {
                if (!ReferenceEquals(_cabinetDriverWindow, window)) return;
                _cabinetDriverWindow = null; _cabinetDraftWorkflow = null;
            };
            try
            {
                workflow.PreparePreview(activeDraft);
                UiFeedback.AssignWindowOwner(window);
                window.Show();
            }
            catch
            {
                workflow.CloseWithoutPrompt();
                throw;
            }
            _cabinetDriverWindow = window; _cabinetDraftWorkflow = workflow;
            UiFeedback.ActivateWindow(window);
        }

        private void ValidateCabinetScenarioSwitch(BridgeParameterDraftDocument draft)
        {
            if (draft == null || !ReferenceEquals(iSwApp.IActiveDoc2, draft.Document))
                throw new InvalidOperationException("请先激活当前柜体设计对应的总装。");
            if (_bridgeDesignController?.LeaveDraftForCabinet(draft.Document) == false)
                throw new InvalidOperationException("请先结束当前的母线桥设计操作。");
        }

        public int EnableCabinetDriverWindow()
        {
            return 1;
        }

        public int EnableFrameDriverWindow() => 1;
        public void ShowFrameDriverWindow()
        {
            try
            {
                if (_cabinetDraftWorkflow != null || BridgeParameterDraftDocuments.For(iSwApp).Active == null)
                { ShowCabinetDriverWindow(); return; }
                ShowUnifiedBridgeDesign(false);
            }
            catch (Exception error)
            {
                Logger.Error(error, "无法打开本项目预览草稿。");
                UiFeedback.Error(error.Message, "母线桥项目");
            }
        }

        private void ShowUnifiedBridgeDesign(bool activateBusbarTab)
        {
            try
            {
                BridgeDesignController().Show(activateBusbarTab);
            }
            catch (Exception ex)
            {
                FaceToWallPreviewTrace.Error("打开母线桥设计失败", ex);
                SolidWorksProgressToast.ShowError("母线桥设计", "母线桥设计界面打开失败：" + ex.Message);
            }
        }
        private BridgeDesignWindowController BridgeDesignController()
            => _bridgeDesignController ?? (_bridgeDesignController = new BridgeDesignWindowController(iSwApp,
                () => new SolidWorksBridgeDesignHost(iSwApp), ShowCabinetDriverWindow));

        private void OpenBridgeAfterCabinetDraft(BridgeParameterDraftDocument draft)
        {
            if (!ReferenceEquals(iSwApp.IActiveDoc2, draft.Document))
                throw new InvalidOperationException("柜体参数已保存，请激活对应的草稿总装后再进入母线桥设计。");
            BridgeDesignController().Show(false);
        }

        public void ShowExportDrawingWindow()
        {
            try
            {
                RequireDrawingAssembly();

                var exportDrawingDriver = new ExportDrawingDriver(iSwApp);
                exportDrawingDriver.OpenCabinetAssemblyDrawing(null);
            }
            catch (Exception ex)
            {
                UiFeedback.ShowSolidWorks(ex.Message, (int)swMessageBoxIcon_e.swMbStop, (int)swMessageBoxBtn_e.swMbOk);
            }
        }

        public int EnableExportDrawingWindow()
        {
            ModelDoc2 activeDoc = (ModelDoc2)iSwApp.ActiveDoc;
            if (activeDoc != null && activeDoc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                return 1;

            return 0;
        }

        public void ShowCopperDrawingWindow()
        {
            try
            {
                RequireDrawingAssembly();
                var assemblyDriver = new CopperDrawingAssemblyDriver(iSwApp);
                assemblyDriver.OpenBusbarSubassembly(iSwApp?.ActiveDoc as ModelDoc2);

                if (TryActivateCopperDrawingWindow())
                {
                    return;
                }

                string addinDirectory = Path.GetDirectoryName(typeof(SwAddin).Assembly.Location);
                if (string.IsNullOrWhiteSpace(addinDirectory))
                {
                    throw new InvalidOperationException("无法定位铜排图纸运行目录。");
                }

                string runtimeDirectory = Path.Combine(addinDirectory, "CopperDrawing");
                if (!Directory.Exists(runtimeDirectory))
                {
                    throw new DirectoryNotFoundException(
                        "未找到铜排图纸运行目录：" + runtimeDirectory);
                }

                Directory.CreateDirectory(Path.Combine(addinDirectory, "Settings", "Custom"));
                Directory.CreateDirectory(Path.Combine(addinDirectory, "DrawingsFiles"));

                var runtimeDomainSetup = new AppDomainSetup
                {
                    ApplicationBase = runtimeDirectory,
                    LoaderOptimization = LoaderOptimization.MultiDomainHost
                };
                string runtimeDomainName =
                    "CKBusbarBridge.CopperDrawing." + Guid.NewGuid().ToString("N");
                AppDomain createdRuntimeDomain = AppDomain.CreateDomain(
                    runtimeDomainName,
                    null,
                    runtimeDomainSetup);

                try
                {
                    var createdRuntimeHost =
                        (CopperDrawingRuntimeHost)createdRuntimeDomain
                            .CreateInstanceFromAndUnwrap(
                                typeof(CopperDrawingRuntimeHost).Assembly.Location,
                                typeof(CopperDrawingRuntimeHost).FullName);
                    createdRuntimeHost.ShowWindow(
                        addinDirectory,
                        runtimeDirectory,
                        iSwApp);

                    _copperDrawingRuntimeDomain = createdRuntimeDomain;
                    _copperDrawingRuntimeHost = createdRuntimeHost;
                }
                catch
                {
                    AppDomain.Unload(createdRuntimeDomain);
                    throw;
                }
            }
            catch (TargetInvocationException ex)
            {
                Exception actualException = ex.InnerException ?? ex;
                UiFeedback.ShowSolidWorks(actualException.Message, (int)swMessageBoxIcon_e.swMbStop, (int)swMessageBoxBtn_e.swMbOk);
            }
            catch (Exception ex)
            {
                UiFeedback.ShowSolidWorks(ex.Message, (int)swMessageBoxIcon_e.swMbStop, (int)swMessageBoxBtn_e.swMbOk);
            }
        }

        public int EnableCopperDrawingWindow()
        {
            var activeDoc = iSwApp?.ActiveDoc as ModelDoc2;
            return activeDoc != null &&
                activeDoc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY
                ? 1
                : 0;
        }

        private bool TryActivateCopperDrawingWindow()
        {
            if (_copperDrawingRuntimeHost == null ||
                _copperDrawingRuntimeDomain == null)
            {
                return false;
            }

            try
            {
                if (_copperDrawingRuntimeHost.IsWindowOpen())
                {
                    _copperDrawingRuntimeHost.ActivateWindow();
                    return true;
                }
            }
            catch (AppDomainUnloadedException)
            {
                // 运行域已释放时，清理会话并重新创建窗口。
            }
            catch (System.Runtime.Remoting.RemotingException)
            {
                // 跨域代理失效时，清理会话并重新创建窗口。
            }

            ReleaseCopperDrawingRuntime();
            return false;
        }

        private void ReleaseCopperDrawingRuntime()
        {
            CopperDrawingRuntimeHost runtimeHostToRelease =
                _copperDrawingRuntimeHost;
            AppDomain runtimeDomainToRelease =
                _copperDrawingRuntimeDomain;
            _copperDrawingRuntimeHost = null;
            _copperDrawingRuntimeDomain = null;

            if (runtimeHostToRelease != null)
            {
                try
                {
                    runtimeHostToRelease.CloseWindow();
                }
                catch (AppDomainUnloadedException)
                {
                    // 运行域已释放，无需重复关闭窗口。
                }
                catch (System.Runtime.Remoting.RemotingException)
                {
                    // 跨域代理已失效，继续释放运行域。
                }
            }

            if (runtimeDomainToRelease == null)
            {
                return;
            }

            try
            {
                AppDomain.Unload(runtimeDomainToRelease);
            }
            catch (AppDomainUnloadedException)
            {
                // 运行域已释放，无需重复处理。
            }
            catch (CannotUnloadAppDomainException)
            {
                // SolidWorks 退出时不因第三方窗口域释放失败而中断卸载流程。
            }
        }

        public void ShowOpenHoleLibSettingWindow()
        {
            try
            {
                // 可选：首次启动 V1→V2 转换
                OpenHoleLocationHelper.AutoConvertBusbarMatingFaceOpenHoleInfoToV2();
                if (_openHoleLibSettingWindow != null)
                {
                    _openHoleLibSettingWindow.Show();
                    _openHoleLibSettingWindow.Activate();
                    return;
                }

                var createdOpenHoleLibSettingWindow =
                    new OpenHoleLibraryWindow();
                _openHoleLibSettingWindow =
                    createdOpenHoleLibSettingWindow;
                createdOpenHoleLibSettingWindow.Closed +=
                    (closedWindowSender, closedWindowEventArgs) =>
                    {
                        if (ReferenceEquals(
                                _openHoleLibSettingWindow,
                                createdOpenHoleLibSettingWindow))
                        {
                            _openHoleLibSettingWindow = null;
                        }
                    };
                UiFeedback.AssignWindowOwner(createdOpenHoleLibSettingWindow);
                createdOpenHoleLibSettingWindow.Show();
                createdOpenHoleLibSettingWindow.Activate();
            }
            catch (Exception ex)
            {
                FaceToWallPreviewTrace.Error(
                    "打开开孔库设置界面失败",
                    ex);
                SolidWorksProgressToast.ShowError(
                    "开孔库设置",
                    "打开开孔库设置界面失败，请检查开孔库文件。"
                );
            }
        }

        public int EnableOpenHoleLibSettingWindow()
        {
            return 1;
        }

    }
}
