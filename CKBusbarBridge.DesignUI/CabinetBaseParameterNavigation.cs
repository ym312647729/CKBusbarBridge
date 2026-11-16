using System.Windows;
using System.Windows.Controls;

namespace CKBusbarBridge.views
{
    internal static class CabinetBaseParameterNavigation
    {
        internal static void Locate(Window window, string validationMessage)
        {
            if (string.IsNullOrEmpty(validationMessage)
                || !validationMessage.Contains("基架高度"))
            {
                return;
            }

            var cabinet = validationMessage.Contains("A柜") ? "A"
                : validationMessage.Contains("B柜") ? "B" : null;
            if (cabinet == null) return;

            if (window.FindName(cabinet + "TabItem") is TabItem cabinetTab)
                cabinetTab.IsSelected = true;
            if (window.FindName(cabinet + "BaseParamTabItem") is TabItem baseTab)
                baseTab.IsSelected = true;
        }
    }
}
