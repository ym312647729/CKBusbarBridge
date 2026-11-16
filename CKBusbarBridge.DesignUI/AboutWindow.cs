using RD.UI.Wpf.Controls;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CKBusbarBridge.views
{
    /// <summary>项目说明窗口，不访问模型或 SolidWorks 对象。</summary>
    public sealed class AboutWindow : RDWindow
    {
        public AboutWindow(AboutInformation information)
        {
            if (information == null) throw new ArgumentNullException(nameof(information));
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/RD.UI.Wpf;component/Themes/Generic.xaml")
            });
            Title = "关于 · " + AboutInformation.ProductName;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 13;
            Background = Brushes.White;
            RightSidePanelLayout.Apply(this);

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = new StackPanel { Margin = new Thickness(0, 4, 0, 16) };
            heading.Children.Add(new TextBlock
            {
                Text = AboutInformation.ProductName,
                FontSize = 20, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            heading.Children.Add(new TextBlock
            {
                Text = "SOLIDWORKS 母线桥设计工具 · V" + information.Version,
                Foreground = Brushes.SlateGray,
                Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap
            });
            root.Children.Add(heading);

            var notes = new Grid();
            notes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            notes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            notes.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            notes.Children.Add(new TextBlock
            {
                Text = "版本更新", FontSize = 15, FontWeight = FontWeights.SemiBold
            });
            var caption = new TextBlock
            {
                Text = information.HasReleaseNotes ? information.ReleaseNotesMessage : "更新说明暂不可用",
                Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 12)
            };
            Grid.SetRow(caption, 1);
            notes.Children.Add(caption);
            var reader = new RichTextBox
            {
                Name = "ReleaseNotesReader", IsReadOnly = true, IsReadOnlyCaretVisible = false,
                BorderThickness = new Thickness(1), BorderBrush = Brushes.LightGray,
                Padding = new Thickness(8),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Document = ReleaseNotesDocument.Create(information.HasReleaseNotes
                    ? information.ReleaseNotes : information.ReleaseNotesMessage)
            };
            Grid.SetRow(reader, 2);
            notes.Children.Add(reader);
            Grid.SetRow(notes, 1);
            root.Children.Add(notes);

            var closeButton = new RDButton
            {
                Name = "CloseAbout", Content = "关闭", Width = 88, Height = 34,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0), IsCancel = true
            };
            closeButton.Click += (sender, args) => Close();
            Grid.SetRow(closeButton, 2);
            root.Children.Add(closeButton);
            Content = root;
            PreviewKeyDown += (sender, args) =>
            {
                if (args.Key != Key.Escape) return;
                Close();
                args.Handled = true;
            };
        }
    }
}