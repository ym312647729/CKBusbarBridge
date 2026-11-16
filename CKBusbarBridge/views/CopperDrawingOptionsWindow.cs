using CKBusbarBridgePreview;
using RD.UI.Wpf.Controls;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CKBusbarBridge.views
{
    /// <summary>复用铜排出图 DLL 的设置数据与保存方法，界面使用 RD.UI.Wpf。</summary>
    internal sealed class CopperDrawingOptionsWindow : RDWindow
    {
        private readonly Type _optionsUtilityType;
        private readonly object _optionsModel;
        private readonly Dictionary<string, RDCheckBox> _booleanInputs = new Dictionary<string, RDCheckBox>();
        private readonly Dictionary<string, RDComboBox> _choiceInputs = new Dictionary<string, RDComboBox>();
        private readonly RDComboBox _templateChoice = new RDComboBox();
        private readonly IList _templates;
        private readonly Dictionary<int, RDCheckBox> _phaseInputs = new Dictionary<int, RDCheckBox>();

        internal CopperDrawingOptionsWindow(Assembly drawingAssembly)
        {
            if (drawingAssembly == null) throw new ArgumentNullException(nameof(drawingAssembly));
            _optionsUtilityType = drawingAssembly.GetType("PartDrawingDoc.DrawingsDimDataUtils3", true);
            _optionsModel = _optionsUtilityType.GetMethod("GetDimOption", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, null);
            _templates = (IList)_optionsUtilityType.GetMethod("GetModuleList", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, null);
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/RD.UI.Wpf;component/Themes/Generic.xaml")
            });
            Title = "铜排出图设置";
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 13;
            Background = Brushes.White;
            RightSidePanelLayout.Apply(this);
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var form = new StackPanel();
            form.Children.Add(new TextBlock
            {
                Text = "出图模板与标注", FontSize = 17, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            });
            form.Children.Add(new TextBlock { Text = "出图模板", Margin = new Thickness(0, 0, 0, 4) });
            for (int templateIndex = 0; templateIndex < _templates.Count; templateIndex++)
                _templateChoice.Items.Add("模板 " + (templateIndex + 1));
            _templateChoice.SelectedIndex = Math.Max(0, Math.Min(_templates.Count - 1, ReadInt("moduleId") - 1));
            _templateChoice.Margin = new Thickness(0, 0, 0, 12);
            form.Children.Add(_templateChoice);

            AddHeading(form, "视图");
            AddBoolean(form, "三视图", "threeViewShowType");
            AddBoolean(form, "展开图", "zhankaituShowType");
            AddBoolean(form, "轴测图", "ZhouceShowType");
            AddBoolean(form, "显示折弯文字", "showBendText");
            AddHeading(form, "三视图标注");
            AddBoolean(form, "总长标注", "MaxLengthShowType");
            AddBoolean(form, "总宽标注", "MaxHeightShowType");
            AddBoolean(form, "孔标注", "showHoleDim");
            AddBoolean(form, "斜边标注", "xiebianShowType");
            AddBoolean(form, "斜边投影标注", "xiebianProjectionShowType");
            AddBoolean(form, "角度标注", "showAngle");
            AddHeading(form, "展开图标注");
            AddBoolean(form, "总长标注", "MaxLengthShowType_unfoldView");
            AddBoolean(form, "总宽标注", "MaxHeightShowType_unfoldView");
            AddBoolean(form, "孔标注", "UnfoldViewShowHoleInfo");
            AddBoolean(form, "端头孔标注", "UnfoldViewShowHoleInfo_duantou");
            AddBoolean(form, "折弯线标注", "UnfoldViewShowBendLine");
            AddHeading(form, "其他");
            AddBoolean(form, "绝缘木出图", "supportInsulatorWood");
            AddChoice(form, "斜边开孔标注", "xieshituDimHoleType", "显示", "隐藏");
            AddChoice(form, "尺寸标注位置", "sizeDimType", "内侧", "外侧", "同侧内", "同侧外");
            AddChoice(form, "叠排信息显示", "FoldPartShow", "显示", "隐藏");
            AddChoice(form, "热缩套管信息显示", "TaoguanShow", "显示", "隐藏");
            AddChoice(form, "镀银显示", "DuyinShowType", "显示", "隐藏");
            AddHeading(form, "镀银相序");
            AddPhase(form, 1, "A 相");
            AddPhase(form, 2, "B 相");
            AddPhase(form, 3, "C 相");
            AddPhase(form, 6, "ABC 相");
            AddPhase(form, 4, "N 相");
            AddPhase(form, 0, "空相序");
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
            var save = new RDButton { Content = "保存", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            save.Click += Save;
            var cancel = new RDButton { Content = "取消", Width = 80, IsCancel = true };
            cancel.Click += (sender, args) => { DialogResult = false; Close(); };
            footer.Children.Add(save);
            footer.Children.Add(cancel);
            Grid.SetRow(footer, 1);
            root.Children.Add(footer);
            Content = root;
        }

        private static void AddHeading(StackPanel parent, string caption)
        {
            parent.Children.Add(new TextBlock
            {
                Text = caption, FontSize = 14, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 14, 0, 8)
            });
        }

        private void AddBoolean(StackPanel parent, string caption, string property)
        {
            var input = new RDCheckBox
            {
                Content = caption, IsChecked = Convert.ToBoolean(Read(property)),
                Margin = new Thickness(0, 0, 0, 7)
            };
            _booleanInputs.Add(property, input);
            parent.Children.Add(input);
        }

        private void AddChoice(StackPanel parent, string caption, string property, params string[] options)
        {
            parent.Children.Add(new TextBlock { Text = caption, Margin = new Thickness(0, 0, 0, 4) });
            var input = new RDComboBox { Margin = new Thickness(0, 0, 0, 10) };
            foreach (string option in options) input.Items.Add(option);
            input.SelectedIndex = Math.Max(0, Math.Min(options.Length - 1, ReadInt(property)));
            _choiceInputs.Add(property, input);
            parent.Children.Add(input);
        }

        private void AddPhase(StackPanel parent, int phase, string caption)
        {
            var phases = (IList)Read("duyinPhaseLs");
            var selected = false;
            foreach (object item in phases)
                if (Convert.ToInt32(item) == phase) selected = true;
            var input = new RDCheckBox
            {
                Content = caption, IsChecked = selected, Margin = new Thickness(0, 0, 0, 7)
            };
            _phaseInputs.Add(phase, input);
            parent.Children.Add(input);
        }

        private object Read(string property) =>
            _optionsModel.GetType().GetProperty(property)?.GetValue(_optionsModel, null) ??
            _optionsModel.GetType().GetField(property)?.GetValue(_optionsModel);

        private int ReadInt(string property) => Convert.ToInt32(Read(property));

        private void Write(string property, object value)
        {
            var type = _optionsModel.GetType();
            var member = type.GetProperty(property);
            if (member != null) member.SetValue(_optionsModel, value, null);
            else type.GetField(property)?.SetValue(_optionsModel, value);
        }

        private void Save(object sender, RoutedEventArgs args)
        {
            if (_booleanInputs["threeViewShowType"].IsChecked != true &&
                _booleanInputs["zhankaituShowType"].IsChecked != true &&
                _booleanInputs["ZhouceShowType"].IsChecked != true)
            {
                UiFeedback.Warning("至少选择一种视图类型。", Title);
                return;
            }
            if (_templateChoice.SelectedIndex < 0)
            {
                UiFeedback.Warning("请选择出图模板。", Title);
                return;
            }
            try
            {
                Write("moduleId", _templateChoice.SelectedIndex + 1);
                foreach (var input in _booleanInputs) Write(input.Key, input.Value.IsChecked == true);
                foreach (var input in _choiceInputs) Write(input.Key, input.Value.SelectedIndex);
                // 本项目不输出叠排图片。
                Write("jpgFoldPartShowType", 1);
                var phases = (IList)Read("duyinPhaseLs");
                phases.Clear();
                var phaseType = phases.GetType().GetGenericArguments()[0];
                foreach (var input in _phaseInputs)
                    if (input.Value.IsChecked == true)
                        phases.Add(Enum.ToObject(phaseType, input.Key));
                _optionsUtilityType.GetMethod("SaveDataToJsonFile", BindingFlags.Public | BindingFlags.Static)
                    .Invoke(null, null);
                // 原设置窗口保存后同步模板类型标志；出图后台会读取该静态字段。
                var componentType = _optionsUtilityType.Assembly.GetType(
                    "PartDrawingDoc.ExtensionMethord.ComponentInfo", true);
                var templateType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType(
                        "BusbarTools.ProjectManagerInfo.StaticDrawingsSetting", false))
                    .FirstOrDefault(type => type != null);
                if (templateType == null)
                    throw new TypeLoadException("未找到图纸模板设置组件。");
                var templateFlag = templateType.GetField("IsWanRondModule",
                    BindingFlags.Public | BindingFlags.Static);
                if (templateFlag == null)
                    throw new MissingFieldException(templateType.FullName, "IsWanRondModule");
                templateFlag.SetValue(null, componentType.GetMethod("IsWanRondModule",
                    BindingFlags.Public | BindingFlags.Static).Invoke(null, null));
                DialogResult = true;
                Close();
                UiFeedback.Success("出图设置已保存。", Title);
            }
            catch (Exception exception)
            {
                var cause = exception is TargetInvocationException invocation && invocation.InnerException != null
                    ? invocation.InnerException : exception;
                UiFeedback.Error(cause.Message, Title);
            }
        }
    }
}
