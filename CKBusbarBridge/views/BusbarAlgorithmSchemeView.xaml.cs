using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CKBusbarBridge.views
{
    public partial class BusbarAlgorithmSchemeView : UserControl
    {
        public event RoutedEventHandler WebPreviewVisibilityChanged;
        public event MouseButtonEventHandler SchemeDoubleClick;
        public CheckBox WebPreviewToggle => ShowWebPreviewCheckBox;
        public BusbarAlgorithmSchemeView() { InitializeComponent(); }
        private void ShowWebPreviewCheckBox_Changed(object sender, RoutedEventArgs e) => WebPreviewVisibilityChanged?.Invoke(this, e);
        private void AlgorithmSchemeDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => SchemeDoubleClick?.Invoke(this, e);
        private void ParamTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { ((UIElement)sender).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); e.Handled = true; }
        }
    }
}
