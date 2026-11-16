using CKBusbarBridgePreview;
using Microsoft.Win32;
using RD.UI.Wpf.Controls;
using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace CKBusbarBridge.views
{
    /// <summary>隔离运行域中的铜排出图界面，调用原 DLL 的无界面出图入口。</summary>
    internal sealed class CopperDrawingWindow : RDWindow
    {
        private readonly Forms.Form _drawingBackend;
        private readonly ISldWorks _solidWorksApplication;
        private readonly ObservableCollection<CopperDrawingFile> _selectedFiles =
            new ObservableCollection<CopperDrawingFile>();
        private readonly RDDataGrid _fileGrid = new RDDataGrid();
        private readonly RDInput _outputFolderInput = new RDInput();
        private readonly RDInput _pdfFolderInput = new RDInput();
        private readonly RDCheckBox _assemblyModeCheckBox = new RDCheckBox { Content = "从当前装配体出图" };
        private readonly RDCheckBox _samePathCheckBox = new RDCheckBox { Content = "图纸与铜排同路径" };
        private readonly RDCheckBox _mergePdfCheckBox = new RDCheckBox { Content = "输出并合并 PDF" };
        private readonly RDCheckBox _ignoreExistingCheckBox = new RDCheckBox { Content = "跳过已有图纸" };
        private readonly RDCheckBox _checkAttributesCheckBox = new RDCheckBox { Content = "检查零件属性" };
        private readonly TextBlock _resultText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _emptyListHint = new TextBlock
        {
            Text = "添加零件，或选择当前装配体后开始出图。",
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12)
        };

        internal CopperDrawingWindow(Forms.Form drawingBackend, ISldWorks solidWorksApplication)
        {
            _drawingBackend = drawingBackend ?? throw new ArgumentNullException(nameof(drawingBackend));
            _solidWorksApplication = solidWorksApplication ?? throw new ArgumentNullException(nameof(solidWorksApplication));
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/RD.UI.Wpf;component/Themes/Generic.xaml")
            });
            Title = "铜排出图";
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 13;
            Background = Brushes.White;
            RightSidePanelLayout.Apply(this);

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            heading.Children.Add(new TextBlock
            {
                Text = "铜排图纸", FontSize = 17, FontWeight = FontWeights.SemiBold
            });
            heading.Children.Add(new TextBlock
            {
                Text = "选择当前装配体或指定零件文件，设置输出路径后开始出图。",
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray,
                Margin = new Thickness(0, 4, 0, 0)
            });
            heading.Children.Add(ActionButton("出图设置", (sender, args) =>
            {
                var drawingOptionsWindow = new CopperDrawingOptionsWindow(_drawingBackend.GetType().Assembly)
                {
                    Owner = this
                };
                drawingOptionsWindow.ShowDialog();
            }));
            root.Children.Add(heading);

            var selection = new StackPanel();
            _assemblyModeCheckBox.IsChecked = true;
            _assemblyModeCheckBox.Margin = new Thickness(0, 0, 0, 10);
            _assemblyModeCheckBox.Checked += (sender, args) => UpdateFileControls();
            _assemblyModeCheckBox.Unchecked += (sender, args) => UpdateFileControls();
            selection.Children.Add(_assemblyModeCheckBox);
            var fileActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            fileActions.Children.Add(ActionButton("添加零件", AddFiles));
            fileActions.Children.Add(ActionButton("添加文件夹", AddFolder));
            fileActions.Children.Add(ActionButton("移除选中", RemoveSelected));
            fileActions.Children.Add(ActionButton("清空", (sender, args) => _selectedFiles.Clear()));
            selection.Children.Add(fileActions);
            fileActions.Tag = "FileActions";
            Grid.SetRow(selection, 1);
            root.Children.Add(selection);

            _fileGrid.ItemsSource = _selectedFiles;
            _fileGrid.AutoGenerateColumns = false;
            _fileGrid.IsReadOnly = true;
            _fileGrid.CanUserAddRows = false;
            _fileGrid.CanUserDeleteRows = false;
            _fileGrid.SelectionMode = DataGridSelectionMode.Extended;
            _fileGrid.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _fileGrid.Columns.Add(new DataGridTextColumn { Header = "文件名", Binding = new Binding(nameof(CopperDrawingFile.Name)), Width = 140 });
            _fileGrid.Columns.Add(new DataGridTextColumn { Header = "路径", Binding = new Binding(nameof(CopperDrawingFile.Path)), Width = 280 });
            _fileGrid.Columns.Add(new DataGridTextColumn { Header = "结果", Binding = new Binding(nameof(CopperDrawingFile.Result)), Width = 110 });
            var fileArea = new Grid();
            fileArea.Children.Add(_fileGrid);
            fileArea.Children.Add(_emptyListHint);
            _selectedFiles.CollectionChanged += (sender, args) => UpdateEmptyListHint();
            Grid.SetRow(fileArea, 2);
            root.Children.Add(fileArea);

            var options = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            _samePathCheckBox.IsChecked = true;
            _samePathCheckBox.Margin = new Thickness(0, 0, 0, 6);
            _mergePdfCheckBox.Margin = new Thickness(0, 0, 0, 6);
            _ignoreExistingCheckBox.IsChecked = true;
            _ignoreExistingCheckBox.Margin = new Thickness(0, 0, 0, 10);
            _checkAttributesCheckBox.Margin = new Thickness(0, 0, 0, 10);
            options.Children.Add(_samePathCheckBox);
            options.Children.Add(_mergePdfCheckBox);
            options.Children.Add(_ignoreExistingCheckBox);
            options.Children.Add(_checkAttributesCheckBox);
            options.Children.Add(FolderField("图纸输出目录", _outputFolderInput));
            options.Children.Add(FolderField("PDF 输出目录", _pdfFolderInput));
            _resultText.Margin = new Thickness(0, 4, 0, 8);
            _resultText.Foreground = Brushes.SlateGray;
            options.Children.Add(_resultText);
            var startButton = new RDButton { Content = "开始出图", MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Right };
            startButton.Click += StartDrawing;
            options.Children.Add(startButton);
            Grid.SetRow(options, 3);
            root.Children.Add(options);
            Content = root;
            UpdateFileControls();
            UpdateEmptyListHint();
            Closed += (sender, args) => _drawingBackend.Dispose();
        }

        private static RDButton ActionButton(string caption, RoutedEventHandler action)
        {
            var button = new RDButton { Content = caption, Margin = new Thickness(0, 0, 8, 8), MinWidth = 72 };
            button.Click += action;
            return button;
        }

        private static Grid FolderField(string caption, RDInput input)
        {
            var group = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            group.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            group.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            group.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            group.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            group.Children.Add(new TextBlock { Text = caption, Margin = new Thickness(0, 0, 0, 4) });
            input.Height = 32;
            Grid.SetRow(input, 1);
            group.Children.Add(input);
            var browse = new RDButton { Content = "浏览", Margin = new Thickness(8, 0, 0, 0) };
            browse.Click += (sender, args) =>
            {
                using (var picker = new Forms.FolderBrowserDialog { Description = caption })
                    if (picker.ShowDialog() == Forms.DialogResult.OK) input.Text = picker.SelectedPath;
            };
            Grid.SetRow(browse, 1);
            Grid.SetColumn(browse, 1);
            group.Children.Add(browse);
            return group;
        }

        private void UpdateFileControls()
        {
            if (Content is Grid root && root.Children.OfType<StackPanel>().FirstOrDefault(panel => Grid.GetRow(panel) == 1) is StackPanel selection)
                foreach (var actions in selection.Children.OfType<WrapPanel>())
                    actions.IsEnabled = _assemblyModeCheckBox.IsChecked != true;
        }

        private void UpdateEmptyListHint() =>
            _emptyListHint.Visibility = _selectedFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void AddFiles(object sender, RoutedEventArgs args)
        {
            var picker = new OpenFileDialog
            {
                Filter = "SolidWorks 零件 (*.sldprt)|*.sldprt",
                Multiselect = true, Title = "选择铜排零件"
            };
            if (picker.ShowDialog(this) != true) return;
            foreach (var path in picker.FileNames) AddPath(path);
        }

        private void AddFolder(object sender, RoutedEventArgs args)
        {
            using (var picker = new Forms.FolderBrowserDialog { Description = "选择铜排零件文件夹" })
            {
                if (picker.ShowDialog() != Forms.DialogResult.OK) return;
                foreach (var path in Directory.GetFiles(picker.SelectedPath, "*.sldprt", SearchOption.TopDirectoryOnly))
                    AddPath(path);
            }
        }

        private void AddPath(string path)
        {
            if (System.IO.Path.GetFileName(path).StartsWith("~$")) return;
            if (_selectedFiles.Any(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            _selectedFiles.Add(new CopperDrawingFile { Path = path, Name = System.IO.Path.GetFileName(path) });
        }

        private void RemoveSelected(object sender, RoutedEventArgs args)
        {
            var selected = _fileGrid.SelectedItems.Cast<CopperDrawingFile>().ToList();
            if (selected.Count == 0) return;
            if (UiFeedback.Show(this, "确认移除选中的 " + selected.Count + " 个零件吗？",
                "移除确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            foreach (var file in selected) _selectedFiles.Remove(file);
        }

        private void StartDrawing(object sender, RoutedEventArgs args)
        {
            var outputDirectory = _outputFolderInput.Text?.Trim();
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                UiFeedback.Warning("请填写图纸输出目录。", Title);
                return;
            }
            var isAssembly = _assemblyModeCheckBox.IsChecked == true;
            var assemblyPath = isAssembly ? (_solidWorksApplication.IActiveDoc2 as IModelDoc2)?.GetPathName() : null;
            if (isAssembly && (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath)))
            {
                UiFeedback.Warning("请先打开并保存要出图的装配体。", Title);
                return;
            }
            if (!isAssembly && _selectedFiles.Count == 0)
            {
                UiFeedback.Warning("请先添加铜排零件。", Title);
                return;
            }
            if (!isAssembly && _selectedFiles.Any(file => !File.Exists(file.Path)))
            {
                UiFeedback.Warning("部分零件文件已不存在，请从列表中移除后重试。", Title);
                return;
            }
            var pdfDirectory = _pdfFolderInput.Text?.Trim();
            if (_mergePdfCheckBox.IsChecked == true && string.IsNullOrWhiteSpace(pdfDirectory))
            {
                UiFeedback.Warning("输出 PDF 时请填写 PDF 目录。", Title);
                return;
            }
            try
            {
                var methodName = isAssembly ? "RunByAssemblyForRpc" : "RunByPartPathsForRpc";
                object source = isAssembly ? (object)assemblyPath : _selectedFiles.Select(file => file.Path).ToList();
                var runArguments = new object[]
                {
                    source, outputDirectory, _samePathCheckBox.IsChecked == true,
                    _mergePdfCheckBox.IsChecked == true, pdfDirectory ?? string.Empty,
                    _ignoreExistingCheckBox.IsChecked == true
                };
                var runResult = UiFeedback.Run(Title, progress =>
                {
                    progress.Report(new FeedbackProgress { Stage = "正在生成铜排图纸" });
                    if (_checkAttributesCheckBox.IsChecked == true)
                        return RunWithAttributeChecking(isAssembly, source, outputDirectory,
                            _samePathCheckBox.IsChecked == true, _mergePdfCheckBox.IsChecked == true,
                            pdfDirectory ?? string.Empty, _ignoreExistingCheckBox.IsChecked == true);
                    var runMethod = _drawingBackend.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
                    if (runMethod == null) throw new MissingMethodException(_drawingBackend.GetType().FullName, methodName);
                    return runMethod.Invoke(_drawingBackend, runArguments);
                });
                var success = Convert.ToInt32(ReadResult(runResult, "SuccessCount"));
                var failed = Convert.ToInt32(ReadResult(runResult, "FailCount"));
                _resultText.Text = "已完成：成功 " + success + "，失败 " + failed + "。";
                if (failed > 0) UiFeedback.Warning(_resultText.Text, Title);
                else UiFeedback.Success(_resultText.Text, Title);
                if (ReadResult(runResult, "Items") is System.Collections.IEnumerable resultItems)
                {
                    foreach (var item in resultItems)
                    {
                        var itemPath = ReadResult(item, "FilePath") as string;
                        if (string.IsNullOrWhiteSpace(itemPath)) continue;
                        var row = _selectedFiles.FirstOrDefault(file =>
                            string.Equals(file.Path, itemPath, StringComparison.OrdinalIgnoreCase));
                        if (row == null)
                        {
                            row = new CopperDrawingFile { Path = itemPath, Name = System.IO.Path.GetFileName(itemPath) };
                            _selectedFiles.Add(row);
                        }
                        row.Result = Convert.ToString(ReadResult(item, "DrawResult")) + " " +
                            Convert.ToString(ReadResult(item, "OutputCheck"));
                    }
                }
                _fileGrid.Items.Refresh();
            }
            catch (Exception exception)
            {
                var cause = exception is TargetInvocationException invocation && invocation.InnerException != null
                    ? invocation.InnerException : exception;
                UiFeedback.Error(cause.Message, Title);
            }
        }

        private static object ReadResult(object result, string property) =>
            result?.GetType().GetProperty(property)?.GetValue(result, null) ?? 0;

        private object RunWithAttributeChecking(bool isAssembly, object source, string outputDirectory,
            bool samePath, bool mergePdf, string pdfDirectory, bool ignoreExisting)
        {
            var backendType = _drawingBackend.GetType();
            void SetBackendField(string name, object value)
            {
                var field = backendType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
                if (field == null) throw new MissingFieldException(backendType.FullName, name);
                field.SetValue(_drawingBackend, value);
            }
            object InvokeBackend(string name, params object[] arguments)
            {
                var method = backendType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
                if (method == null) throw new MissingMethodException(backendType.FullName, name);
                return method.Invoke(_drawingBackend, arguments);
            }
            SetBackendField("samePathWithBusbar", samePath);
            SetBackendField("outputPDF", mergePdf);
            SetBackendField("pdfOutputPath", pdfDirectory);
            SetBackendField("IgnoreIfExistDrawing", ignoreExisting);
            InvokeBackend("PrepareForRpc", outputDirectory);
            var attributeToggle = (Forms.CheckBox)backendType.GetField("cb_check_attr",
                BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(_drawingBackend);
            if (attributeToggle == null) throw new MissingFieldException(backendType.FullName, "cb_check_attr");
            attributeToggle.Checked = true;
            if (isAssembly)
            {
                InvokeBackend("OpenOrActivateAssemblyForRpc", source);
                InvokeBackend("ScanCurrentAssemblyForRpc");
            }
            else InvokeBackend("AddPartPathsForRpc", source);
            var backendGrid = (Forms.DataGridView)backendType.GetField("dataGridView1",
                BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(_drawingBackend);
            if (backendGrid == null) throw new MissingFieldException(backendType.FullName, "dataGridView1");
            if (backendGrid.Rows.Count > 0) InvokeBackend("StartDrawingForRpc");
            return InvokeBackend("BuildRpcResult", isAssembly ? "assembly" : "parts", outputDirectory);
        }

        private sealed class CopperDrawingFile
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public string Result { get; set; }
        }
    }
}
