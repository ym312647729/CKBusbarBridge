using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
        private readonly ICabinetDraftWorkflow _model;
        private readonly DesignScenarioId _scenario;
        private readonly FaceToWallCabinetOrientation? _orientation;
        private readonly Action<CabinetDraftWindowWorkflow, DesignScenarioId, FaceToWallCabinetOrientation?> _switchScenario;
        private readonly Action<BridgeParameterDraftDocument> _next;
        private readonly bool _resetScenario;
        private readonly BridgeParameterDraftDocument _expectedDraft;
        private readonly RDButton _confirm;
        private readonly TextBlock _status;
        private readonly ScrollViewer _statusScroll;
        private DispatcherOperation _previewOperation;
        private bool _closing, _saving, _changingScenario;
        private Action _discardOpeningSettings;
        private BridgeParameterDraftDocument _cabinetDesignDraft;

        internal CabinetDraftWindowWorkflow(Window window, DesignScenarioId scenario, FaceToWallCabinetOrientation? orientation,
            Action<CabinetDraftWindowWorkflow, DesignScenarioId, FaceToWallCabinetOrientation?> switchScenario,
            Action<BridgeParameterDraftDocument> next, CabinetDraftWindowWorkflow previous)
        {
            _window = window;
            _model = (ICabinetDraftWorkflow)window.DataContext;
            _scenario = scenario; _orientation = orientation; _switchScenario = switchScenario; _next = next;
            _expectedDraft = previous?._cabinetDesignDraft;
            _resetScenario = previous != null;
            _discardOpeningSettings = previous?._discardOpeningSettings;
            if (previous != null) _model.CarryDraftProject(previous._model.ProjectPath, previous._model.DrawingNumberSession.CommittedMetadataJson);
            var choices = RequireLayoutHost<WrapPanel>("CabinetScenarioSelectorHost");
            var footer = RequireLayoutHost<Grid>("CabinetDraftFooter");
            InstallScenarioSelector(choices);
            footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = 40 });
            _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkRed };
            _statusScroll = new ScrollViewer { Content = _status, MaxHeight = 80, Margin = new Thickness(8, 0, 8, 8),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            footer.Children.Add(_statusScroll);
            _confirm = new RDButton
            {
                Content = "确定参数，下一步", Variant = RDButtonVariant.Primary, Height = 36, MinWidth = 148,
                Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(0, 2, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
            };
            _confirm.Click += Confirm;
            Grid.SetRow(_confirm, 1); footer.Children.Add(_confirm);
            _window.Closing += Closing; _window.Closed += Closed;
            _model.DraftPreviewChanged += Changed;
            if (_model is INotifyPropertyChanged changes) changes.PropertyChanged += ModelChanged;
            Changed(this, EventArgs.Empty);
        }

        internal BridgeParameterDraftDocument DraftDocument => _cabinetDesignDraft;
        internal void CloseWithoutPrompt() { _closing = true; _window.Close(); }

        // 首次入口必须绑定活动总装才显示面板，不能依赖窗口 Loaded 的时机建文档。
        internal void PreparePreview(BridgeParameterDraftDocument activeDraft)
            => _model.PreviewCabinetInputs(_expectedDraft ?? activeDraft, _resetScenario);

        private T RequireLayoutHost<T>(string name) where T : FrameworkElement
            => _window.FindName(name) as T ?? throw new InvalidOperationException("柜体窗口缺少布局区域：" + name + "。");

        private void InstallScenarioSelector(WrapPanel choices)
        {
            var scenarioPicker = new RDComboBox { Height = 28, MinWidth = 105, Margin = new Thickness(0, 0, 8, 4),
                SelectedValuePath = "Tag", ToolTip = "选择柜体设计场景" };
            foreach (var scenario in new[] { DesignScenarioId.FaceToFace, DesignScenarioId.SideToSide, DesignScenarioId.FaceToWall })
                scenarioPicker.Items.Add(new ComboBoxItem { Tag = scenario, Content = DesignScenarioDefaults.GetDisplayName(scenario) });
            scenarioPicker.SelectedValue = _scenario;
            choices.Children.Add(scenarioPicker);
            scenarioPicker.SelectionChanged += (sender, args) =>
            {
                if (!(scenarioPicker.SelectedValue is DesignScenarioId nextScenario) || nextScenario == _scenario) return;
                var nextOrientation = nextScenario == DesignScenarioId.FaceToWall
                    ? (FaceToWallCabinetOrientation?)FaceToWallCabinetOrientation.CabinetFrontToWall : null;
                SwitchScenario(nextScenario, nextOrientation, () => scenarioPicker.SelectedValue = _scenario);
            };
            if (_scenario == DesignScenarioId.FaceToWall)
            {
                var orientationPicker = new RDComboBox { Height = 28, MinWidth = 94, Margin = new Thickness(0, 0, 0, 4), SelectedValuePath = "Tag" };
                orientationPicker.Items.Add(new ComboBoxItem { Tag = FaceToWallCabinetOrientation.CabinetFrontToWall, Content = "柜前向墙" });
                orientationPicker.Items.Add(new ComboBoxItem { Tag = FaceToWallCabinetOrientation.CabinetRearToWall, Content = "柜后向墙" });
                orientationPicker.SelectedValue = _orientation;
                choices.Children.Add(orientationPicker);
                orientationPicker.SelectionChanged += (sender, args) =>
                {
                    if (orientationPicker.SelectedValue is FaceToWallCabinetOrientation nextOrientation && nextOrientation != _orientation)
                        SwitchScenario(_scenario, nextOrientation, () => orientationPicker.SelectedValue = _orientation);
                };
            }
        }

        private void SwitchScenario(DesignScenarioId scenario, FaceToWallCabinetOrientation? orientation, Action restoreSelection)
        {
            if (_saving || _closing || _changingScenario) return;
            _changingScenario = true;
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try { if (!_closing && !_saving) _switchScenario(this, scenario, orientation); else restoreSelection(); }
                catch (Exception error)
                {
                    restoreSelection();
                    FaceToWallPreviewTrace.Error("切换柜体设计场景失败", error);
                    SetStatus("场景未切换：" + error.Message);
                }
                finally { _changingScenario = false; }
            }));
        }

        private void ModelChanged(object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != "Parameters" || _closing || _saving || _previewOperation != null) return;
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
            if (_saving || _changingScenario) return Task.CompletedTask;
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
            if (_saving || _changingScenario) return;
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
            if (_model is INotifyPropertyChanged changes) changes.PropertyChanged -= ModelChanged;
            _model.DraftPreviewChanged -= Changed;
            _model.ReleaseDraftWindow();
            _cabinetDesignDraft?.CabinetDesignClosed();
            _cabinetDesignDraft = null;
        }
    }
}
