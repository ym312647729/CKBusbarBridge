using System.Windows;

namespace CKBusbarBridge.views
{
    public partial class SmallCornerWindowResources : ResourceDictionary
    {
        public static readonly DependencyProperty CaptionDetailsTemplateProperty = DependencyProperty.RegisterAttached(
            "CaptionDetailsTemplate", typeof(DataTemplate), typeof(SmallCornerWindowResources));

        public static DataTemplate GetCaptionDetailsTemplate(DependencyObject window) =>
            (DataTemplate)window.GetValue(CaptionDetailsTemplateProperty);

        public static void SetCaptionDetailsTemplate(DependencyObject window, DataTemplate template) =>
            window.SetValue(CaptionDetailsTemplateProperty, template);

        public SmallCornerWindowResources()
        {
            InitializeComponent();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow((DependencyObject)sender);
            if (window != null) SystemCommands.MinimizeWindow(window);
        }

        private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow((DependencyObject)sender);
            if (window == null) return;
            if (window.WindowState == WindowState.Maximized)
                SystemCommands.RestoreWindow(window);
            else
                SystemCommands.MaximizeWindow(window);
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow((DependencyObject)sender);
            if (window != null) SystemCommands.CloseWindow(window);
        }
    }
}
