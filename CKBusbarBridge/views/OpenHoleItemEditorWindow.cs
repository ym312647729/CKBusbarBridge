using BusbarTools.DataModel;
using BusbarTools.Shared;
using CKBusbarBridgePreview;
using OpenHoleLib.Controls;
using RD.UI.Wpf.Controls;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CKBusbarBridge.views
{
    /// <summary>开孔规格编辑器。坐标由参数计算，也可直接输入，无图片预览。</summary>
    internal sealed class OpenHoleItemEditorWindow : RDWindow
    {
        private readonly MatingFaceOpenHoleInfo _workingItem;
        private readonly Dictionary<string, RDInput> _fields = new Dictionary<string, RDInput>();
        private readonly RDCheckBox _defaultCheckBox = new RDCheckBox { Content = "作为默认规格" };

        internal OpenHoleItemEditorWindow(MatingFaceOpenHoleInfo workingItem)
        {
            _workingItem = workingItem ?? throw new ArgumentNullException(nameof(workingItem));
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/RD.UI.Wpf;component/Themes/Generic.xaml")
            });
            Title = "编辑开孔规格";
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 13;
            Background = Brushes.White;
            RightSidePanelLayout.Apply(this);

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var form = new StackPanel();
            AddField(form, "主排宽度 (mm)", nameof(MatingFaceOpenHoleInfo.MainBusbarWidth), workingItem.MainBusbarWidth);
            AddField(form, "搭接排宽度 (mm)", nameof(MatingFaceOpenHoleInfo.LinkBusbarWidth), workingItem.LinkBusbarWidth);
            AddField(form, "搭接方向（水平 / 垂直）", nameof(MatingFaceOpenHoleInfo.Direction), workingItem.Direction);
            AddField(form, "搭接长度 (mm)", nameof(MatingFaceOpenHoleInfo.DajieLength), workingItem.DajieLength);
            AddField(form, "搭接宽度 (mm)", nameof(MatingFaceOpenHoleInfo.DajieWidth), workingItem.DajieWidth);
            AddField(form, "开孔形状（圆孔 / 长方形 / 腰孔）", nameof(MatingFaceOpenHoleInfo.HoleStyle), workingItem.HoleStyle);
            AddField(form, "开孔数量（1 / 2 / 4 / 6 / 9）", nameof(MatingFaceOpenHoleInfo.HoleQty), workingItem.HoleQty);
            AddField(form, "开孔尺寸 D (mm)", nameof(MatingFaceOpenHoleInfo.HoleD), workingItem.HoleD);
            AddField(form, "开孔尺寸 X (mm)", nameof(MatingFaceOpenHoleInfo.HoleX), workingItem.HoleX);
            AddField(form, "开孔尺寸 Y (mm)", nameof(MatingFaceOpenHoleInfo.HoleY), workingItem.HoleY);
            AddField(form, "开孔位置", nameof(MatingFaceOpenHoleInfo.HoleLocation), workingItem.HoleLocation);
            AddField(form, "工艺库名", nameof(MatingFaceOpenHoleInfo.Comments), workingItem.Comments);
            AddField(form, "螺栓大小", nameof(MatingFaceOpenHoleInfo.BoltSize), workingItem.BoltSize);
            AddField(form, "类型 1", nameof(MatingFaceOpenHoleInfo.Type1), workingItem.Type1);
            AddField(form, "类型 2", nameof(MatingFaceOpenHoleInfo.Type2), workingItem.Type2);
            _defaultCheckBox.IsChecked = workingItem.IsDefault;
            _defaultCheckBox.Margin = new Thickness(0, 4, 0, 12);
            form.Children.Add(_defaultCheckBox);
            var generateButton = new RDButton { Content = "按搭接尺寸生成孔位坐标", Margin = new Thickness(0, 0, 0, 12) };
            generateButton.Click += (sender, args) => GenerateCoordinates();
            form.Children.Add(generateButton);
            root.Children.Add(new ScrollViewer
            {
                Content = form,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var saveButton = new RDButton { Content = "保存", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            saveButton.Click += Save;
            var cancelButton = new RDButton { Content = "取消", Width = 80, IsCancel = true };
            cancelButton.Click += (sender, args) => { DialogResult = false; Close(); };
            footer.Children.Add(saveButton);
            footer.Children.Add(cancelButton);
            Grid.SetRow(footer, 1);
            root.Children.Add(footer);
            Content = root;
        }

        private void AddField(StackPanel parent, string caption, string propertyName, object value)
        {
            var field = new RDInput
            {
                Height = 32,
                Text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty
            };
            _fields.Add(propertyName, field);
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            panel.Children.Add(new TextBlock { Text = caption, Margin = new Thickness(0, 0, 0, 4) });
            panel.Children.Add(field);
            parent.Children.Add(panel);
        }

        private void GenerateCoordinates()
        {
            if (!double.TryParse(Value(nameof(MatingFaceOpenHoleInfo.DajieLength)), out var lapLength) ||
                !double.TryParse(Value(nameof(MatingFaceOpenHoleInfo.DajieWidth)), out var lapWidth) ||
                !int.TryParse(Value(nameof(MatingFaceOpenHoleInfo.HoleQty)), out var holeQuantity))
            {
                UiFeedback.Warning("请先填写有效的搭接长宽和孔数。", Title);
                return;
            }
            var lengthQuarter = lapLength / 4d;
            var widthQuarter = lapWidth / 4d;
            var points = new List<Point>();
            switch (holeQuantity)
            {
                case 1: points.Add(new Point(0, 0)); break;
                case 2:
                    if (lapLength > lapWidth) { points.Add(new Point(0, lengthQuarter)); points.Add(new Point(0, -lengthQuarter)); }
                    else { points.Add(new Point(widthQuarter, 0)); points.Add(new Point(-widthQuarter, 0)); }
                    break;
                case 4:
                    points.AddRange(new[] { new Point(widthQuarter, lengthQuarter), new Point(-widthQuarter, lengthQuarter),
                        new Point(-widthQuarter, -lengthQuarter), new Point(widthQuarter, -lengthQuarter) });
                    break;
                case 6:
                    if (lengthQuarter > widthQuarter)
                        points.AddRange(new[] { new Point(widthQuarter, 0), new Point(widthQuarter, lengthQuarter),
                            new Point(-widthQuarter, lengthQuarter), new Point(-widthQuarter, 0),
                            new Point(-widthQuarter, -lengthQuarter), new Point(widthQuarter, -lengthQuarter) });
                    else
                        points.AddRange(new[] { new Point(widthQuarter, lengthQuarter), new Point(0, lengthQuarter),
                            new Point(-widthQuarter, lengthQuarter), new Point(-widthQuarter, -lengthQuarter),
                            new Point(0, -lengthQuarter), new Point(widthQuarter, -lengthQuarter) });
                    break;
                case 9:
                    foreach (double y in new[] { lengthQuarter, 0d, -lengthQuarter })
                        foreach (double x in new[] { widthQuarter, 0d, -widthQuarter })
                            points.Add(new Point(x, y));
                    break;
                default: UiFeedback.Warning("孔数只支持 1、2、4、6、9。", Title); return;
            }
            var coordinates = string.Join(";", points.Select(point =>
                point.X.ToString(CultureInfo.CurrentCulture) + "," +
                point.Y.ToString(CultureInfo.CurrentCulture)));
            if (CurrentSettings.CurrentSettingsBase.IsUserNewVersionHole)
                coordinates = OpenHoleLocationHelper.HoleLocationExchangeRotationXY(coordinates);
            _fields[nameof(MatingFaceOpenHoleInfo.HoleLocation)].Text = coordinates;
        }

        private string Value(string propertyName) => _fields[propertyName].Text?.Trim() ?? string.Empty;

        private void Save(object sender, RoutedEventArgs args)
        {
            if (!TryPositive(nameof(MatingFaceOpenHoleInfo.MainBusbarWidth), out var mainWidth) ||
                !TryPositive(nameof(MatingFaceOpenHoleInfo.LinkBusbarWidth), out var linkWidth) ||
                !TryPositive(nameof(MatingFaceOpenHoleInfo.DajieLength), out var lapLength) ||
                !TryPositive(nameof(MatingFaceOpenHoleInfo.DajieWidth), out var lapWidth) ||
                !TryNonNegative(nameof(MatingFaceOpenHoleInfo.HoleD), out var holeDiameter) ||
                !TryNonNegative(nameof(MatingFaceOpenHoleInfo.HoleX), out var holeX) ||
                !TryNonNegative(nameof(MatingFaceOpenHoleInfo.HoleY), out var holeY) ||
                !int.TryParse(Value(nameof(MatingFaceOpenHoleInfo.HoleQty)), out var holeQuantity) ||
                !new[] { 1, 2, 4, 6, 9 }.Contains(holeQuantity))
            {
                UiFeedback.Warning("请检查宽度、搭接尺寸、开孔尺寸和孔数。", Title);
                return;
            }
            if (string.IsNullOrWhiteSpace(Value(nameof(MatingFaceOpenHoleInfo.HoleLocation))))
                GenerateCoordinates();
            _workingItem.MainBusbarWidth = mainWidth;
            _workingItem.LinkBusbarWidth = linkWidth;
            _workingItem.Direction = Value(nameof(MatingFaceOpenHoleInfo.Direction));
            _workingItem.DajieLength = lapLength;
            _workingItem.DajieWidth = lapWidth;
            _workingItem.HoleStyle = Value(nameof(MatingFaceOpenHoleInfo.HoleStyle));
            _workingItem.HoleQty = holeQuantity;
            _workingItem.HoleD = holeDiameter;
            _workingItem.HoleX = holeX;
            _workingItem.HoleY = holeY;
            _workingItem.HoleLocation = Value(nameof(MatingFaceOpenHoleInfo.HoleLocation));
            _workingItem.Comments = Value(nameof(MatingFaceOpenHoleInfo.Comments));
            _workingItem.BoltSize = Value(nameof(MatingFaceOpenHoleInfo.BoltSize));
            _workingItem.Type1 = Value(nameof(MatingFaceOpenHoleInfo.Type1));
            _workingItem.Type2 = Value(nameof(MatingFaceOpenHoleInfo.Type2));
            _workingItem.IsDefault = _defaultCheckBox.IsChecked == true;
            DialogResult = true;
            Close();
        }

        private bool TryPositive(string propertyName, out double value) =>
            double.TryParse(Value(propertyName), out value) && value > 0;

        private bool TryNonNegative(string propertyName, out double value) =>
            double.TryParse(Value(propertyName), out value) && value >= 0;
    }
}
