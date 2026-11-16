using CKBusbarBridge.Prototype;

namespace CKBusbarBridge.DesignPreview
{
    internal static class BridgeViewportAppearance
    {
        internal static bool IsEnvironment(string role) => role == "roof" || role == "beam" || role == "support";
        internal static bool IsBaseOrFrame(string category) => category == "base" || category == "raisedBase" || category == "frame";
        internal static float Opacity(BridgeMeshInstance instance, int? tab)
        {
            if (IsEnvironment(instance.AppearanceRole)) return .25f;
            if (instance.AppearanceRole == "cabinet") return 1f;
            return IsBaseOrFrame(instance.Category) && IsCopperEditing(tab) ? .25f : 1f;
        }
        private static bool IsCopperEditing(int? tab) => tab == BusbarBridgeDesignWindowVM.CopperTab ||
            tab == BusbarBridgeDesignWindowVM.BreakpointTab || tab == BusbarBridgeDesignWindowVM.InsulatorTab;
        internal static bool CanSelect(string category, int? tab) => tab == BusbarBridgeDesignWindowVM.FrameTab
            ? category == "frame" : !IsCopperEditing(tab) || !IsBaseOrFrame(category);
    }
}
