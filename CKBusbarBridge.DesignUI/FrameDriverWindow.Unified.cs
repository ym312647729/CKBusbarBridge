using System;
using System.Windows;
using System.Windows.Controls;
using RD.UI.Wpf.Controls;

namespace CKBusbarBridge.views
{
    public partial class FrameDriverWindow
    {
        internal Action PrepareCabinetSave { get; set; }
        internal ContentControl AddCabinetDesignPanel()
        {
            Title = "中压母线桥设计";
            var bridge = (FrameworkElement)Content;
            Content = null;
            var layout = new Grid();
            var left = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 270 };
            layout.ColumnDefinitions.Add(left);
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 430 });
            var toggle = new RDButton { Content = "折叠柜体设计", MinHeight = 26,
                Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            CabinetPanelToggleHost.Content = toggle;
            CabinetPanelToggleHost.Visibility = Visibility.Visible;
            var host = new ContentControl();
            layout.Children.Add(host);
            var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch };
            Grid.SetColumn(splitter, 1); layout.Children.Add(splitter);
            Grid.SetColumn(bridge, 2); layout.Children.Add(bridge);
            var expanded = true; var previous = left.Width;
            toggle.Click += (sender, args) =>
            {
                expanded = !expanded;
                if (!expanded) previous = left.Width;
                host.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                splitter.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                left.MinWidth = expanded ? 270 : 0;
                left.Width = expanded ? previous : new GridLength(0);
                layout.ColumnDefinitions[1].Width = new GridLength(expanded ? 6 : 0);
                toggle.Content = expanded ? "折叠柜体设计" : "柜体 ▸ 展开";
            };
            Content = layout;
            var workArea = SystemParameters.WorkArea;
            Width = Math.Min(workArea.Width - RightSidePanelLayout.EdgeSpacing * 2, Math.Max(900, workArea.Width * .75));
            MinWidth = Math.Min(760, Width);
            Left = workArea.Right - Width - RightSidePanelLayout.EdgeSpacing;
            DraftPreviousButton.Visibility = Visibility.Collapsed;
            return host;
        }
        internal void RefreshUnifiedScenario()
        {
            if (_closed) return;
            ReloadFromActiveModel();
        }
    }
}
