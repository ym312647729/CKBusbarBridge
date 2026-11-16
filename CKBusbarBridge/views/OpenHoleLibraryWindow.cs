using BusbarTools.DataModel;
using BusbarTools.Shared;
using CKBusbarBridgePreview;
using Microsoft.Win32;
using OpenHoleLib.Controls;
using RD.UI.Wpf.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace CKBusbarBridge.views
{
    /// <summary>复用现有工艺库读写契约的 RD.UI.Wpf 设置窗口。</summary>
    internal sealed class OpenHoleLibraryWindow : RDWindow
    {
        private List<MatingFaceOpenHoleInfo> _libraryItems = new List<MatingFaceOpenHoleInfo>();
        private readonly RDDataGrid _libraryGrid = new RDDataGrid();
        private readonly RDInput _mainWidthFilter = new RDInput();
        private readonly RDInput _linkWidthFilter = new RDInput();
        private readonly RDInput _directionFilter = new RDInput();
        private readonly RDInput _storeFilter = new RDInput();

        internal OpenHoleLibraryWindow()
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/RD.UI.Wpf;component/Themes/Generic.xaml")
            });
            Title = "开孔库设置";
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 13;
            Background = Brushes.White;
            RightSidePanelLayout.Apply(this);

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(new TextBlock
            {
                Text = "搭接开孔工艺库", FontSize = 17, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            });

            var filters = new StackPanel();
            filters.Children.Add(CreateField("主排宽度", _mainWidthFilter));
            filters.Children.Add(CreateField("搭接排宽度", _linkWidthFilter));
            filters.Children.Add(CreateField("搭接方向", _directionFilter));
            filters.Children.Add(CreateField("工艺库名", _storeFilter));
            foreach (var filter in new[] { _mainWidthFilter, _linkWidthFilter, _directionFilter, _storeFilter })
                filter.TextChanged += (sender, args) => RefreshGrid();
            Grid.SetRow(filters, 1);
            root.Children.Add(filters);

            var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 12) };
            actions.Children.Add(ActionButton("添加", (sender, args) => EditItem(null)));
            actions.Children.Add(ActionButton("编辑", (sender, args) => EditItem(_libraryGrid.SelectedItem as MatingFaceOpenHoleInfo)));
            actions.Children.Add(ActionButton("删除", (sender, args) => DeleteSelected()));
            actions.Children.Add(ActionButton("全部切换默认状态", (sender, args) => ToggleDefaults()));
            Grid.SetRow(actions, 2);
            root.Children.Add(actions);

            _libraryGrid.AutoGenerateColumns = false;
            _libraryGrid.IsReadOnly = true;
            _libraryGrid.CanUserAddRows = false;
            _libraryGrid.CanUserDeleteRows = false;
            _libraryGrid.SelectionMode = DataGridSelectionMode.Single;
            _libraryGrid.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _libraryGrid.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _libraryGrid.MouseDoubleClick += (sender, args) => EditItem(_libraryGrid.SelectedItem as MatingFaceOpenHoleInfo);
            AddColumn("主排宽度", nameof(MatingFaceOpenHoleInfo.MainBusbarWidth), 88);
            AddColumn("搭接排宽", nameof(MatingFaceOpenHoleInfo.LinkBusbarWidth), 88);
            AddColumn("方向", nameof(MatingFaceOpenHoleInfo.Direction), 66);
            AddColumn("搭接长度", nameof(MatingFaceOpenHoleInfo.DajieLength), 88);
            AddColumn("搭接宽度", nameof(MatingFaceOpenHoleInfo.DajieWidth), 88);
            AddColumn("孔型", nameof(MatingFaceOpenHoleInfo.HoleStyle), 76);
            AddColumn("孔数", nameof(MatingFaceOpenHoleInfo.HoleQty), 56);
            AddColumn("孔径", nameof(MatingFaceOpenHoleInfo.HoleD), 56);
            AddColumn("工艺库", nameof(MatingFaceOpenHoleInfo.Comments), 110);
            AddColumn("默认", nameof(MatingFaceOpenHoleInfo.IsDefault), 56);
            Grid.SetRow(_libraryGrid, 3);
            root.Children.Add(_libraryGrid);

            var footer = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            footer.Children.Add(ActionButton("导入 Excel", (sender, args) => Import(false)));
            if (CurrentSettings.CurrentSettingsBase.IsUserNewVersionHole)
                footer.Children.Add(ActionButton("导入旧版", (sender, args) => Import(true)));
            footer.Children.Add(ActionButton("导出 Excel", (sender, args) => Export()));
            Grid.SetRow(footer, 4);
            root.Children.Add(footer);
            Content = root;
            Loaded += (sender, args) => LoadItems();
        }

        private static StackPanel CreateField(string label, RDInput input)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) });
            input.Height = 32;
            panel.Children.Add(input);
            return panel;
        }

        private static RDButton ActionButton(string caption, RoutedEventHandler handler)
        {
            var button = new RDButton { Content = caption, Margin = new Thickness(0, 0, 8, 8), MinWidth = 78 };
            button.Click += handler;
            return button;
        }

        private void AddColumn(string caption, string property, double width)
        {
            _libraryGrid.Columns.Add(new DataGridTextColumn
            {
                Header = caption, Binding = new Binding(property), Width = width
            });
        }

        private void LoadItems()
        {
            try
            {
                bool migrated;
                _libraryItems = DataHelper.GetBusbarMatingFaceHoleList(out migrated) ??
                    new List<MatingFaceOpenHoleInfo>();
                RefreshGrid();
            }
            catch (Exception exception) { UiFeedback.Error(exception.Message, Title); }
        }

        private void RefreshGrid()
        {
            IEnumerable<MatingFaceOpenHoleInfo> filtered = _libraryItems;
            if (double.TryParse(_mainWidthFilter.Text, out var mainWidth) && mainWidth > 0)
                filtered = filtered.Where(item => item.MainBusbarWidth == mainWidth);
            if (double.TryParse(_linkWidthFilter.Text, out var linkWidth) && linkWidth > 0)
                filtered = filtered.Where(item => item.LinkBusbarWidth == linkWidth);
            if (!string.IsNullOrWhiteSpace(_directionFilter.Text))
                filtered = filtered.Where(item => string.Equals(item.Direction, _directionFilter.Text.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(_storeFilter.Text))
                filtered = filtered.Where(item => string.Equals(item.Comments, _storeFilter.Text.Trim(), StringComparison.OrdinalIgnoreCase));
            _libraryGrid.ItemsSource = filtered.ToList();
        }

        private void SaveItems()
        {
            if (!DataHelper.SaveBusbarMatingFaceOpenHoleInfo(_libraryItems))
                throw new IOException("开孔库保存失败。");
            RefreshGrid();
        }

        private void EditItem(MatingFaceOpenHoleInfo selected)
        {
            var working = selected == null ? new MatingFaceOpenHoleInfo
            {
                Id = _libraryItems.Count == 0 ? 1 : _libraryItems.Max(item => item.Id) + 2
            } : Clone(selected);
            var editor = new OpenHoleItemEditorWindow(working) { Owner = this };
            if (editor.ShowDialog() != true) return;
            if (selected == null)
            {
                if (_libraryItems.Any(item => SameSpecification(item, working)))
                {
                    UiFeedback.Warning("当前列表中已经存在该规格，请检查。", Title);
                    return;
                }
                _libraryItems.Add(working);
            }
            else
            {
                int index = _libraryItems.IndexOf(selected);
                if (index < 0) return;
                _libraryItems[index] = working;
                foreach (var reverse in _libraryItems.Where(item =>
                    item.MainBusbarWidth == working.LinkBusbarWidth &&
                    item.LinkBusbarWidth == working.MainBusbarWidth &&
                    item.Direction == working.Direction && item.HoleStyle == working.HoleStyle))
                {
                    reverse.HoleD = working.HoleD;
                    reverse.HoleX = working.HoleX;
                    reverse.HoleY = working.HoleY;
                    reverse.HoleLocation = reverse.Direction == "垂直" &&
                        Math.Abs(reverse.MainBusbarWidth - reverse.LinkBusbarWidth) > 0.001
                        ? (CurrentSettings.CurrentSettingsBase.IsUserNewVersionHole
                            ? OpenHoleLocationHelper.HoleLocationExchangeRotationXY(working.HoleLocation)
                            : OpenHoleLocationHelper.HoleLocationExchangeXY(working.HoleLocation))
                        : working.HoleLocation;
                }
            }
            try { SaveItems(); }
            catch (Exception exception) { UiFeedback.Error(exception.Message, Title); }
        }

        private static MatingFaceOpenHoleInfo Clone(MatingFaceOpenHoleInfo original)
        {
            var copy = new MatingFaceOpenHoleInfo();
            foreach (PropertyInfo property in typeof(MatingFaceOpenHoleInfo).GetProperties().Where(item => item.CanRead && item.CanWrite))
                property.SetValue(copy, property.GetValue(original, null), null);
            return copy;
        }

        private static bool SameSpecification(MatingFaceOpenHoleInfo first, MatingFaceOpenHoleInfo second) =>
            first.MainBusbarWidth == second.MainBusbarWidth &&
            first.LinkBusbarWidth == second.LinkBusbarWidth &&
            first.Direction == second.Direction && first.Type1 == second.Type1 &&
            first.Type2 == second.Type2 && first.DajieLength == second.DajieLength &&
            first.DajieWidth == second.DajieWidth && first.HoleStyle == second.HoleStyle &&
            first.Comments == second.Comments && first.IsDefault == second.IsDefault;

        private void DeleteSelected()
        {
            if (!(_libraryGrid.SelectedItem is MatingFaceOpenHoleInfo selected)) return;
            if (UiFeedback.Show(this, "删除此项？\n" + selected.MainBusbarWidth + "-" +
                selected.LinkBusbarWidth + "-" + selected.Direction + "-" + selected.HoleStyle,
                "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _libraryItems.Remove(selected);
            try { SaveItems(); }
            catch (Exception exception) { UiFeedback.Error(exception.Message, Title); }
        }

        private void ToggleDefaults()
        {
            foreach (var item in _libraryItems) item.IsDefault = !item.IsDefault;
            try { SaveItems(); }
            catch (Exception exception) { UiFeedback.Error(exception.Message, Title); }
        }

        private void Import(bool oldFormat)
        {
            var picker = new OpenFileDialog { Filter = "Excel (*.xls)|*.xls", Title = "选择母排工艺列表" };
            if (picker.ShowDialog(this) != true) return;
            try
            {
                var imported = OpenHoleWorkBook.ReadOpenHoleInfoFromExcelFile(picker.FileName);
                if (imported == null) return;
                if (oldFormat)
                    foreach (var item in imported.Where(item => item.Direction == "垂直" &&
                        Math.Abs(item.MainBusbarWidth - item.LinkBusbarWidth) > 0.001))
                        item.HoleLocation = OpenHoleLocationHelper.HoleLocationExchangeRotationXY(item.HoleLocation);
                _libraryItems = imported;
                SaveItems();
                UiFeedback.Success("开孔库导入完成。", Title);
            }
            catch (Exception exception) { UiFeedback.Error(exception.Message, Title); }
        }

        private void Export()
        {
            var picker = new SaveFileDialog
            {
                Filter = "Excel (*.xls)|*.xls", FileName = "母排工艺列表.xls",
                Title = "保存母排工艺列表"
            };
            if (picker.ShowDialog(this) != true) return;
            try
            {
                var workbook = OpenHoleWorkBook.BuildMatingFaceOpenHoleInfoData("母排工艺列表", _libraryItems);
                using (var stream = new FileStream(picker.FileName, FileMode.Create, FileAccess.Write))
                    workbook.Write(stream);
                UiFeedback.Success("开孔库导出完成。", Title);
            }
            catch (Exception exception) { UiFeedback.Error(exception.Message, Title); }
        }
    }
}
