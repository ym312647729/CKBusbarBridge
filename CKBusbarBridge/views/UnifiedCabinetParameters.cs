using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using CKBusbarBridge.Models;
using Newtonsoft.Json.Linq;
using RD.UI.Wpf.Controls;

namespace CKBusbarBridge.views
{
    internal static class UnifiedCabinetParameters
    {
        internal static void Apply(UserControl page, DesignScenarioId scenario)
        {
            var scenarioKey = scenario == DesignScenarioId.FaceToWall ? "face-to-wall" :
                scenario == DesignScenarioId.SideToSide ? "side-to-side" : "face-to-face";
            var runtime = Path.Combine(Path.GetDirectoryName(typeof(UnifiedCabinetParameters).Assembly.Location), "Worker", "PreviewRuntime");
            var schema = JObject.Parse(File.ReadAllText(Path.Combine(runtime, "catalog", "original-cabinet-ui.json")));
            var fields = schema["scenarios"].Single(row => (string)row["key"] == scenarioKey)["fields"].OfType<JObject>();
            var root = new DockPanel { Margin = new Thickness(8) };
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
            var project = new RDInput { ToolTip = "项目根目录", Margin = new Thickness(0, 0, 0, 6) };
            project.SetBinding(TextBox.TextProperty, new Binding("ProjectPath") { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            header.Children.Add(project);
            var actions = new WrapPanel();
            var folder = new RDButton { Content = "项目目录", Margin = new Thickness(0, 0, 6, 6) };
            folder.SetBinding(Button.CommandProperty, new Binding("SelectProjectPathCommand")); actions.Children.Add(folder);
            var drawing = new RDButton { Content = "图号", Margin = new Thickness(0, 0, 6, 6) };
            var originalDrawing = page.FindName("GenerateDrawingNumberButton") as Button;
            drawing.Click += (sender, args) => originalDrawing?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            actions.Children.Add(drawing); header.Children.Add(actions);
            var groups = new RDComboBox { Margin = new Thickness(0, 0, 0, 6), MinWidth = 120 };
            header.Children.Add(groups);
            var rowsByGroup = new Dictionary<string, List<ParameterRow>>();
            foreach (var field in fields)
            {
                var path = (string)field["path"];
                if (path == "ProjectPath" || path.EndsWith(".L1", StringComparison.Ordinal) || path.EndsWith(".L2", StringComparison.Ordinal)) continue;
                var parameters = page.DataContext.GetType().GetProperty("Parameters")?.GetValue(page.DataContext);
                object owner = parameters; var components = path.Split('.');
                foreach (var part in components.Take(components.Length - 1)) owner = owner?.GetType().GetProperty(part)?.GetValue(owner);
                if (owner?.GetType().GetProperty(components.Last()) == null) continue;
                var group = string.Join(" / ", field["tabs"].Values<string>());
                if (string.IsNullOrEmpty(group)) group = "整体参数";
                if (!rowsByGroup.TryGetValue(group, out var rows)) rowsByGroup.Add(group, rows = new List<ParameterRow>());
                FrameworkElement editor;
                var binding = new Binding("Parameters." + path) { Source = page.DataContext, ValidatesOnExceptions = true,
                    NotifyOnValidationError = true, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus };
                if ((string)field["kind"] == "ToggleButton")
                {
                    var toggle = new RDCheckBox { VerticalAlignment = VerticalAlignment.Center };
                    binding.UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged;
                    toggle.SetBinding(ToggleButton.IsCheckedProperty, binding); editor = toggle;
                }
                else if (field["choices"] is JArray choices && choices.Count != 0)
                {
                    var propertyType = owner.GetType().GetProperty(components.Last()).PropertyType;
                    var valueType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
                    object ChoiceValue(JToken choice)
                    {
                        var text = (string)choice["value"];
                        return valueType.IsEnum ? Enum.Parse(valueType, text, true) :
                            System.Convert.ChangeType(text, valueType, CultureInfo.InvariantCulture);
                    }
                    var combo = new RDComboBox { ItemsSource = choices.Select(choice =>
                        new ComboBoxItem { Content = (string)choice["label"], Tag = ChoiceValue(choice) }).ToArray(),
                        SelectedValuePath = "Tag", MinWidth = 65 };
                    binding.UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged;
                    combo.SetBinding(Selector.SelectedValueProperty, binding); editor = combo;
                }
                else
                {
                    var input = new RDInput { IsReadOnly = (bool?)field["readOnly"] == true, MinWidth = 65 };
                    input.SetBinding(TextBox.TextProperty, binding); editor = input;
                }
                var label = path.EndsWith(".L3", StringComparison.Ordinal) ? "柜顶通口半宽(L3)" :
                    path.EndsWith(".FixedHoleSpacing", StringComparison.Ordinal) ? "基座深度定位尺寸" : (string)field["label"];
                editor.ToolTip = label; editor.Tag = path; editor.Margin = new Thickness(0, 2, 2, 2);
                if (path.Contains(".FrontHole.") || path.Contains(".BackHole."))
                {
                    label = (path.Contains(".FrontHole.") ? "前 · " : "后 · ") + label;
                    if (!path.EndsWith(".IsEnabled", StringComparison.Ordinal))
                    {
                        var hole = path.Substring(0, path.IndexOf(path.Contains(".FrontHole.") ? ".FrontHole." : ".BackHole.", StringComparison.Ordinal));
                        hole += path.Contains(".FrontHole.") ? ".FrontHole.IsEnabled" : ".BackHole.IsEnabled";
                        editor.SetBinding(UIElement.IsEnabledProperty, new Binding("Parameters." + hole) { Source = page.DataContext });
                    }
                }
                rows.Add(new ParameterRow(label, editor, path));
                if (path.EndsWith(".BaseHeight", StringComparison.Ordinal) && !path.Contains(".Base1.") && !path.Contains(".Base2."))
                {
                    var cabinet = components[0];
                    var hole = path.Substring(0, path.Length - ".BaseHeight".Length);
                    var width = new RDInput { IsReadOnly = true, Margin = new Thickness(0, 2, 2, 2) };
                    width.SetBinding(TextBox.TextProperty, new Binding("Parameters." + cabinet + ".CabinetWidth") { Source = page.DataContext });
                    var depth = new RDInput { IsReadOnly = true, Margin = new Thickness(0, 2, 2, 2),
                        ToolTip = "按原基座规则：柜顶深度定位尺寸 + 41.5" };
                    depth.SetBinding(TextBox.TextProperty, new Binding("Parameters." + hole + ".FixedHoleSpacing")
                        { Source = page.DataContext, Converter = new BaseDepthConverter() });
                    rows.Add(new ParameterRow("基座宽度（自动）", width));
                    rows.Add(new ParameterRow("基座深度（自动）", depth));
                }
            }
            var grid = new RDDataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
                CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, RowHeight = 40, MinHeight = 140 };
            grid.Columns.Add(new DataGridTextColumn { Header = "参数", Binding = new Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            var cell = new FrameworkElementFactory(typeof(ContentControl));
            cell.SetBinding(ContentControl.ContentProperty, new Binding("Editor"));
            grid.Columns.Add(new DataGridTemplateColumn { Header = "当前值", CellTemplate = new DataTemplate { VisualTree = cell },
                Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            groups.ItemsSource = rowsByGroup.Keys;
            void FilterRows()
            {
                if (!(groups.SelectedItem is string selected)) { grid.ItemsSource = null; return; }
                var currentParameters = page.DataContext.GetType().GetProperty("Parameters").GetValue(page.DataContext);
                var general = currentParameters.GetType().GetProperty("General").GetValue(currentParameters);
                var mode = general.GetType().GetProperty("CabinetTopOpeningMode")?.GetValue(general)?.ToString();
                grid.ItemsSource = rowsByGroup[selected].Where(row => scenario != DesignScenarioId.SideToSide ||
                    !(row.Path?.Contains(".Base1.") == true && mode != "LeftRight" ||
                      row.Path?.Contains(".Base2.") == true && mode != "FrontRear")).ToArray();
            }
            groups.SelectionChanged += (sender, args) => FilterRows();
            var parameterModel = page.DataContext.GetType().GetProperty("Parameters").GetValue(page.DataContext);
            if (parameterModel.GetType().GetProperty("General").GetValue(parameterModel) is INotifyPropertyChanged generalChanges)
                generalChanges.PropertyChanged += (sender, args) => { if (args.PropertyName == "CabinetTopOpeningMode") FilterRows(); };
            root.Children.Add(grid); page.Content = root;
            groups.SelectedIndex = 0;
        }
        private sealed class ParameterRow
        {
            public string Name { get; }
            public FrameworkElement Editor { get; }
            public string Path { get; }
            internal ParameterRow(string name, FrameworkElement editor, string path = null) { Name = name; Editor = editor; Path = path; }
        }
        private sealed class BaseDepthConverter : IValueConverter
        {
            public object Convert(object value, Type type, object parameter, CultureInfo culture)
                => (System.Convert.ToDouble(value, CultureInfo.InvariantCulture) + 41.5).ToString("0.###", culture);
            public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => Binding.DoNothing;
        }
    }
}
