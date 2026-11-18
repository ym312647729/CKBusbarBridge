using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RD.UI.Wpf.Controls;

namespace CKBusbarBridgePreview
{
    /// <summary>只替换本项目窗口的品牌图标，保留 RD 窗体及其标题栏交互。</summary>
    public static class SystemWindowAppearance
    {
        private static readonly BitmapSource ApplicationIcon = CreateApplicationIcon();
        private static readonly DependencyProperty AppliedProperty = DependencyProperty.RegisterAttached(
            "Applied", typeof(bool), typeof(SystemWindowAppearance), new PropertyMetadata(false));

        private static BitmapSource CreateApplicationIcon()
        {
            var applicationIcon = Imaging.CreateBitmapSourceFromHIcon(System.Drawing.SystemIcons.Application.Handle,
                Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            applicationIcon.Freeze();
            return applicationIcon;
        }

        public static void Apply(Window projectWindow)
        {
            if (projectWindow == null) throw new ArgumentNullException(nameof(projectWindow));
            projectWindow.Icon = ApplicationIcon;
            if (!(projectWindow is RDWindow) || (bool)projectWindow.GetValue(AppliedProperty)) return;
            projectWindow.SetValue(AppliedProperty, true);
            projectWindow.Loaded += ReplaceTitleBarBrand;
            if (projectWindow.IsLoaded) ReplaceTitleBarBrand(projectWindow, null);
        }

        private static void ReplaceTitleBarBrand(object sender, RoutedEventArgs args)
        {
            var projectWindow = (Window)sender;
            if (!(projectWindow.Template?.FindName("PART_TitleBar", projectWindow) is DependencyObject titleBar)) return;
            ReplaceBrandChildren(titleBar);
        }

        private static void ReplaceBrandChildren(DependencyObject titleBarPart)
        {
            // 兼容此项目使用过的 RD 主题：旧版为图片，新版为固定品牌文字。
            var titleBarChildren = Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(titleBarPart))
                .Select(index => VisualTreeHelper.GetChild(titleBarPart, index)).ToArray();
            foreach (var titleBarChild in titleBarChildren)
            {
                if (titleBarChild is Image titleBarImage) titleBarImage.Source = ApplicationIcon;
                else if (titleBarChild is TextBlock brandText &&
                    (brandText.Text == "铜排大师" || brandText.Text == "榕迪") && brandText.Parent is Panel brandPanel)
                {
                    var systemImage = new Image { Source = ApplicationIcon, Width = 20, Height = 20,
                        Margin = brandText.Margin, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(systemImage, Grid.GetColumn(brandText));
                    Grid.SetRow(systemImage, Grid.GetRow(brandText));
                    DockPanel.SetDock(systemImage, DockPanel.GetDock(brandText));
                    var brandIndex = brandPanel.Children.IndexOf(brandText);
                    brandPanel.Children.RemoveAt(brandIndex);
                    brandPanel.Children.Insert(brandIndex, systemImage);
                }
                else ReplaceBrandChildren(titleBarChild);
            }
        }
    }
}
