using System;
using System.Windows;
using CKBusbarBridge.views;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
namespace CKBusbarBridge.Prototype
{
    // 每个插件实例持有一个窗口；主程序集仅转发命令，不承担窗口初始化编排。
    internal sealed class BridgeDesignWindowController
    {
        private readonly ISldWorks _app;
        private readonly Func<IBridgeDesignHost> _createHost;
        private readonly Action _showCabinet;
        private IModelDoc2 _assembly;
        internal FrameDriverWindow Window { get; private set; }
        internal BridgeDesignWindowController(ISldWorks app, Func<IBridgeDesignHost> createHost, Action showCabinet)
        { _app = app ?? throw new ArgumentNullException(nameof(app)); _createHost = createHost ?? throw new ArgumentNullException(nameof(createHost)); _showCabinet = showCabinet; }
        internal void Show(bool activateBusbarTab)
        {
            var assembly = _app.IActiveDoc2;
            if (assembly == null || assembly.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                throw new InvalidOperationException("请先打开并激活装配体文档。");
            if (Window == null)
            {
                var configuration = assembly.ConfigurationManager.ActiveConfiguration.Name;
                var window = new FrameDriverWindow(_createHost());
                window.ShowCabinetDesign = _showCabinet;
                window.ValidateInitialContext = () =>
                {
                    if (!ReferenceEquals(_app.IActiveDoc2, assembly) ||
                        assembly.ConfigurationManager.ActiveConfiguration.Name != configuration)
                        throw new InvalidOperationException("活动装配体或配置已改变，请关闭并重新打开母线桥设计。");
                };
                Window = window; _assembly = assembly;
                window.Closed += (sender, args) => { if (ReferenceEquals(Window, window)) { Window = null; _assembly = null; } };
            }
            else if (!ReferenceEquals(_assembly, assembly))
                throw new InvalidOperationException("请先关闭另一份总装的母线桥设计窗口，再打开当前总装的设计。");
            if (activateBusbarTab && Window.IsEnabled) Window.ActivateBusbarTab();
            Window.ShowAndActivate();
        }
        internal bool LeaveDraftForCabinet(IModelDoc2 assembly)
            => Window == null || !ReferenceEquals(_assembly, assembly) || Window.LeaveDraftForCabinet();
        internal void CloseForAddinShutdown() => Window?.CloseForAddinShutdown();
    }
}
