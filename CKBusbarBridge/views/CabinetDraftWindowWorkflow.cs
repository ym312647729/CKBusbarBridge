using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CKBusbarBridge.Drivers;
using CKBusbarBridge.Models;
using CKBusbarBridge.viewmodels;
using CKBusbarBridgePreview;
using RD.UI.Wpf.Controls;

namespace CKBusbarBridge.views
{
    internal sealed class CabinetDraftWindowWorkflow
    {
        private readonly Window _window;
        private ICabinetDraftWorkflow _model;
        private DesignScenarioId _scenario;
        private FaceToWallCabinetOrientation _wallOrientation;
        private readonly Func<DesignScenarioId, UserControl> _createPage;
        private readonly Action<BridgeParameterDraftDocument> _validateSwitch;
        private readonly Action<BridgeParameterDraftDocument> _next;
        private readonly RDTabControl _scenarioTabs = new RDTabControl { HeaderWrap = true };
        private readonly RDComboBox _orientationPicker = new RDComboBox { MinWidth = 105, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly RDButton _confirm;
        private readonly TextBlock _status;
        private readonly ScrollViewer _statusScroll;
        private DispatcherOperation _previewOperation, _switchOperation;
        private bool _closing, _saving, _syncingSelection;
        private Action _discardOpeningSettings;
        private BridgeParameterDraftDocument _cabinetDesignDraft;

        internal CabinetDraftWindowWorkflow(Window window, DesignScenarioId scenario, FaceToWallCabinetOrientation? orientation,
            Func<DesignScenarioId, UserControl> createPage, Action<BridgeParameterDraftDocument> validateSwitch,
            Action<BridgeParameterDraftDocument> next)
        {
            _window = window; _scenario = scenario; _createPage = createPage; _validateSwitch = validateSwitch; _next = next;
            _wallOrientation = orientation ?? FaceToWallCabinetOrientation.CabinetFrontToWall;
            var layout = new DockPanel();
            var footer = new Grid { Margin = new Thickness(12, 4, 12, 8) };
            DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
            footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = 40 });
            _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkRed };
            _statusScroll = new ScrollViewer { Content = _status, MaxHeight = 80,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            footer.Children.Add(_statusScroll);
            _confirm = new RDButton { Content = "确定参数，下一步", Variant = RDButtonVariant.Primary,
                Height = 36, MinWidth = 148, Padding = new Thickness(16, 0, 16, 0),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            _confirm.Click += Confirm;
            Grid.SetRow(_confirm, 1); footer.Children.Add(_confirm);
            foreach (var id in new[] { DesignScenarioId.FaceToFace, DesignScenarioId.FaceToWall, DesignScenarioId.SideToSide })
                _scenarioTabs.Items.Add(new TabItem { Header = DesignScenarioDefaults.GetDisplayName(id), Tag = id });
            _orientationPicker.Items.Add(new ComboBoxItem { Tag = FaceToWallCabinetOrientation.CabinetFrontToWall, Content = "柜前向墙" });
            _orientationPicker.Items.Add(new ComboBoxItem { Tag = FaceToWallCabinetOrientation.CabinetRearToWall, Content = "柜后向墙" });
            _orientationPicker.Margin = new Thickness(12, 8, 0, 0);
            layout.Children.Add(_scenarioTabs); _window.Content = layout;
            AlignSelection();
            _scenarioTabs.SelectionChanged += ScenarioSelectionChanged;
            _orientationPicker.SelectionChanged += ScenarioSelectionChanged;
            _window.Closing += Closing; _window.Closed += Closed;
            _window.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(InputFocusChanged), true);
            _window.AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(InputFocusChanged), true);
            _window.Deactivated += (sender, args) => _cabinetDesignDraft?.HighlightCabinetInput(null, null);
        }

        internal void CloseWithoutPrompt() { _closing = true; _window.Close(); }

        // Keep the same window and SW document; only the tab's parameter view is exchanged.
        internal void PreparePreview(BridgeParameterDraftDocument activeDraft)
        {
            var model = GetPage(_scenario);
            if (model is FaceToWallCabinetDriverWindowVM wall) wall.SetOrientation(_wallOrientation);
            if (activeDraft != null) model.RestoreDraftInputs(activeDraft);
            model.PreviewCabinetInputs(activeDraft, false);
            SetModel(model);
        }

        private ICabinetDraftWorkflow GetPage(DesignScenarioId scenario)
        {
            var tab = _scenarioTabs.Items.Cast<TabItem>().Single(item => Equals(item.Tag, scenario));
            if (!(tab.Content is Grid slot))
            {
                var page = _createPage(scenario);
                slot = new Grid { DataContext = page.DataContext };
                if (scenario == DesignScenarioId.FaceToWall)
                {
                    slot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    slot.RowDefinitions.Add(new RowDefinition());
                    slot.Children.Add(_orientationPicker);
                    Grid.SetRow(page, 1);
                }
                slot.Children.Add(page);
                tab.Content = slot;
            }
            return (ICabinetDraftWorkflow)slot.DataContext;
        }

        private void SetModel(ICabinetDraftWorkflow model)
        {
            if (!ReferenceEquals(_model, model))
            {
                ReleaseModel();
                _model = model;
                _model.DraftPreviewChanged += Changed;
                if (_model is INotifyPropertyChanged changes) changes.PropertyChanged += ModelChanged;
            }
            Changed(this, EventArgs.Empty);
        }

        private void ReleaseModel()
        {
            if (_model == null) return;
            if (_model is INotifyPropertyChanged changes) changes.PropertyChanged -= ModelChanged;
            _model.DraftPreviewChanged -= Changed;
            _model.ReleaseDraftWindow();
        }

        private void AlignSelection()
        {
            _syncingSelection = true;
            try
            {
                _scenarioTabs.SelectedItem = _scenarioTabs.Items.Cast<TabItem>().Single(item => Equals(item.Tag, _scenario));
                _orientationPicker.SelectedItem = _orientationPicker.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Tag, _wallOrientation));
            }
            finally { _syncingSelection = false; }
        }

        private void ScenarioSelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (_syncingSelection || !ReferenceEquals(args.OriginalSource, sender)) return;
            if (_saving || _closing) { AlignSelection(); return; }
            if (_switchOperation != null) return;
            _switchOperation = _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _switchOperation = null;
                if (_closing || _saving || !((_scenarioTabs.SelectedItem as TabItem)?.Tag is DesignScenarioId selected)) return;
                var orientation = _wallOrientation;
                if (selected == DesignScenarioId.FaceToWall &&
                    (_orientationPicker.SelectedItem as ComboBoxItem)?.Tag is FaceToWallCabinetOrientation chosen) orientation = chosen;
                if (selected == _scenario && orientation == _wallOrientation) return;
                FaceToWallCabinetDriverWindowVM wall = null;
                var previousOrientation = _wallOrientation;
                try
                {
                    _validateSwitch(_cabinetDesignDraft);
                    _previewOperation?.Abort(); _previewOperation = null;
                    _cabinetDesignDraft.HighlightCabinetInput(null, null);
                    var nextModel = GetPage(selected);
                    if (!ReferenceEquals(nextModel, _model))
                        nextModel.CarryDraftProject(_model.ProjectPath, _model.DrawingNumberSession.CommittedMetadataJson);
                    wall = nextModel as FaceToWallCabinetDriverWindowVM;
                    wall?.SetOrientation(orientation);
                    nextModel.PreviewCabinetInputs(_cabinetDesignDraft, true);
                    _scenario = selected; _wallOrientation = orientation;
                    SetModel(nextModel);
                }
                catch (Exception error)
                {
                    wall?.SetOrientation(previousOrientation);
                    AlignSelection();
                    FaceToWallPreviewTrace.Error("切换柜体设计场景失败", error);
                    SetStatus("场景未切换：" + error.Message);
                }
            }));
        }

        private void InputFocusChanged(object sender, KeyboardFocusChangedEventArgs args)
        {
            var input = args.NewFocus as TextBox;
            var page = (_scenarioTabs.SelectedContent as Grid)?.Children.OfType<UserControl>().FirstOrDefault();
            var binding = input != null && !input.IsReadOnly && page?.IsAncestorOf(input) == true
                ? input.GetBindingExpression(TextBox.TextProperty) : null;
            _cabinetDesignDraft?.HighlightCabinetInput(binding?.ResolvedSource, binding?.ResolvedSourcePropertyName);
        }


        private void ModelChanged(object sender, PropertyChangedEventArgs args)
        {
            if (!ReferenceEquals(sender, _model) || args.PropertyName != "Parameters" || _closing || _saving || _previewOperation != null) return;
            _previewOperation = _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _previewOperation = null;
                if (!_closing && !_saving) StartPreview(_cabinetDesignDraft, false);
            }));
        }

        private void StartPreview(BridgeParameterDraftDocument expectedDraft, bool resetScenario)
        {
            try { _model.PreviewCabinetInputs(expectedDraft, resetScenario); }
            catch (Exception error)
            {
                FaceToWallPreviewTrace.Error("启动柜体参数预览失败", error);
                SetStatus("预览未开始：" + error.Message);
            }
        }

        private void Changed(object sender, EventArgs args)
        {
            var draft = _model.DraftDocument;
            if (draft != null && !ReferenceEquals(_cabinetDesignDraft, draft))
            {
                if (_cabinetDesignDraft != null) _discardOpeningSettings = null;
                _cabinetDesignDraft?.CabinetDesignClosed();
                _cabinetDesignDraft = draft;
                if (_discardOpeningSettings == null) _discardOpeningSettings = draft.CaptureSettings();
                draft.CabinetDesignOpened();
            }
            SetStatus(draft == null ? "" : draft.Session.Failure != null ? "预览失败：" + draft.Session.Failure :
                draft.Session.IsReady ? "" : "正在更新柜体预览…");
        }

        private void SetStatus(string text)
        {
            _status.Text = text;
            _statusScroll.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void Confirm(object sender, RoutedEventArgs args) { await SaveAndClose(true); }

        private Task SaveAndClose(bool advance)
        {
            if (_saving || _switchOperation != null) return Task.CompletedTask;
            _saving = true; _confirm.IsEnabled = false;
            return _window.Dispatcher.InvokeAsync(() => SaveAndCloseCore(advance), DispatcherPriority.Background).Task.Unwrap();
        }
        private async Task SaveAndCloseCore(bool advance)
        {
            try
            {
                System.Windows.Input.Keyboard.ClearFocus();
                if (HasInvalidInput(_window)) throw new InvalidOperationException("请先修正窗口中标出的输入错误。");
                if (!Directory.Exists(_model.ProjectPath)) throw new InvalidOperationException("请先设置有效的项目根目录。");
                var drawingSession = _model.DrawingNumberSession;
                if (!drawingSession.HasCommittedMetadata)
                {
                    DrawingNumberDialogLauncher.Show(_window, drawingSession);
                    if (!drawingSession.HasCommittedMetadata) return;
                }
                var draft = _model.SubmitCabinetInputs();
                _window.IsEnabled = false;
                await draft.ConfirmCabinetInputs();
                _discardOpeningSettings = draft.CaptureSettings();
                if (advance) _next(draft);
                _closing = true;
                _ = _window.Dispatcher.BeginInvoke(new Action(_window.Close));
            }
            catch (Exception error)
            {
                FaceToWallPreviewTrace.Error("柜体草稿确认失败", error);
                SetStatus("保存失败：" + error.Message);
            }
            finally { _window.IsEnabled = true; _saving = false; if (!_closing) _confirm.IsEnabled = true; }
        }

        private async void Closing(object sender, CancelEventArgs args)
        {
            if (_closing) return;
            args.Cancel = true;
            if (_saving || _switchOperation != null) return;
            var answer = UiFeedback.Show(_window, "是否保存柜体参数和草稿记录？", "退出柜体设计", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes) { await SaveAndClose(false); return; }
            try
            {
                _discardOpeningSettings?.Invoke();
                _closing = true; _ = _window.Dispatcher.BeginInvoke(new Action(_window.Close));
            }
            catch (Exception error)
            {
                FaceToWallPreviewTrace.Error("放弃柜体输入失败", error);
                SetStatus("退出未完成：" + error.Message);
            }
        }

        private static bool HasInvalidInput(DependencyObject item)
        {
            if (Validation.GetHasError(item)) return true;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(item); index++)
                if (HasInvalidInput(VisualTreeHelper.GetChild(item, index))) return true;
            return false;
        }

        private void Closed(object sender, EventArgs args)
        {
            _previewOperation?.Abort();
            _switchOperation?.Abort();
            ReleaseModel();
            _cabinetDesignDraft?.HighlightCabinetInput(null, null);
            _cabinetDesignDraft?.CabinetDesignClosed();
            _cabinetDesignDraft = null;
        }
    }
}
