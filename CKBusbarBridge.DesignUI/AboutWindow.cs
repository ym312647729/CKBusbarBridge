using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CKBusbarBridge.views
{
    /// <summary>HandyControl阅读窗口，无模型访问和宿主反向引用。</summary>
    public sealed class AboutWindow : HandyControl.Controls.Window
    {
        public AboutWindow(AboutInformation information)
        {
            if (information == null) throw new ArgumentNullException(nameof(information));
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/HandyControl;component/Themes/SkinDefault.xaml")
            });
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/HandyControl;component/Themes/Theme.xaml")
            });
            Resources.MergedDictionaries.Add(new SmallCornerWindowResources());
            Style = (Style)FindResource("SmallCornerWindowStyle");
            // HandyControl加载时设置自己的WindowChrome；移除本地值以采用共享外框样式。
            Loaded += (sender, args) => ClearValue(System.Windows.Shell.WindowChrome.WindowChromeProperty);
            Title = "关于 · " + AboutInformation.ProductName;
            Width = 860; Height = 720; MinWidth = 640; MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14;
            Background = Brush("#F3F6FA"); UseLayoutRounding = true;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new Grid { Margin = new Thickness(28, 24, 28, 20) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new Border
            {
                Width = 54, Height = 54, CornerRadius = new CornerRadius(14),
                Background = Brush("#163F60"), Margin = new Thickness(0, 0, 16, 0),
                Child = new TextBlock
                {
                    Text = "i", FontFamily = new FontFamily("Georgia"), FontSize = 38,
                    FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            });
            var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            identity.Children.Add(new TextBlock
            {
                Text = AboutInformation.ProductName, FontSize = 24, FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#153B59")
            });
            identity.Children.Add(new TextBlock
            {
                Text = "SOLIDWORKS 中压母线桥设计工具", FontSize = 12,
                Foreground = Brush("#718397"), Margin = new Thickness(0, 5, 0, 0)
            });
            Grid.SetColumn(identity, 1); header.Children.Add(identity);
            var version = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            version.Children.Add(new TextBlock
            {
                Text = "当前版本", FontSize = 12, Foreground = Brush("#718397"),
                HorizontalAlignment = HorizontalAlignment.Right
            });
            version.Children.Add(new TextBlock
            {
                Name = "CurrentVersion", Text = "V" + information.Version,
                FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brush("#237CB4"),
                Margin = new Thickness(0, 5, 0, 0)
            });
            Grid.SetColumn(version, 2); header.Children.Add(version); root.Children.Add(header);

            var card = new Grid();
            card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var caption = new StackPanel { Margin = new Thickness(28, 18, 28, 16) };
            caption.Children.Add(new TextBlock
            {
                Text = "版本更新", FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Brush("#163A59")
            });
            caption.Children.Add(new TextBlock
            {
                Text = information.HasReleaseNotes ? information.ReleaseNotesMessage : "更新说明暂不可用",
                FontSize = 12, Foreground = Brush("#718397"), Margin = new Thickness(0, 6, 0, 0)
            });
            card.Children.Add(caption);
            var reader = new RichTextBox
            {
                Name = "ReleaseNotesReader", IsReadOnly = true, IsReadOnlyCaretVisible = false,
                BorderThickness = new Thickness(0), Background = Brushes.White,
                Padding = new Thickness(28, 12, 28, 24),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Document = ReleaseNotesDocument.Create(information.HasReleaseNotes
                    ? information.ReleaseNotes : information.ReleaseNotesMessage)
            };
            Grid.SetRow(reader, 1); card.Children.Add(reader);
            var surface = new Border
            {
                Background = Brushes.White, BorderBrush = Brush("#DFE7EF"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Margin = new Thickness(28, 0, 28, 0), Child = card
            };
            Grid.SetRow(surface, 1); root.Children.Add(surface);

            var footer = new Grid { Margin = new Thickness(28, 16, 28, 20) };
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var close = new Button
            {
                Name = "CloseAbout", Content = "关闭", Width = 92, Height = 34,
                Style = (Style)FindResource("ButtonPrimary"), IsCancel = true
            };
            close.Click += (sender, args) => Close();
            Grid.SetColumn(close, 1); footer.Children.Add(close);
            Grid.SetRow(footer, 2); root.Children.Add(footer);
            Content = root;
            PreviewKeyDown += (sender, args) =>
            {
                if (args.Key != Key.Escape) return;
                Close(); args.Handled = true;
            };
        }

        private static SolidColorBrush Brush(string color) =>
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }
}
