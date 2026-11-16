using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows;
using RD.UI.Wpf.Controls;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CKBusbarBridge.models;
using CKBusbarBridge.Models;
using CKBusbarBridge.Prototype;
using CKBusbarBridge.Drivers;
using CKBusbarBridgePreview;


namespace CKBusbarBridge.views
{
    /// <summary>设计窗口只编排交互；SolidWorks临时体由独立会话管理。</summary>
    public partial class FrameDriverWindow : RDWindow
    {
        private readonly BusbarBridgeDesignWindowVM _viewModel;
        private IBridgeDesignPreviewSession _nativePreview;
        private readonly IBridgeDesignHost _host;
        private readonly DispatcherTimer _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        private bool _previewBusy;
        private bool _closed;
        private bool _forceClose;
        private bool _syncingFrameSelection;
        private IReadOnlyList<BridgeDesignObject> _supportListObjects;
        private DataGridColumn _supportSortColumn;
        private ListSortDirection _supportSortDirection;
        private BusbarSchemeSelectionWindow _wallSchemeWindow;
        private DispatcherOperation _initialReadOperation;
        private DispatcherOperation _frameParameterOperation;
        private string _lastListHangerId;
        private string _frameParameterTargetId;
        private BridgeReadLoadingIndicator _readLoadingIndicator;
        private bool _readingUpstream;
        private bool _initialReadStarted;
        internal Action ValidateInitialContext { get; set; }
        internal Action ShowCabinetDesign { get; set; }
        private bool _draftClosingAccepted, _savingDraftSettings;

        public FrameDriverWindow() : this(null, null) { }
        internal FrameDriverWindow(IBridgeDesignHost host) : this(null, host) { }
        public FrameDriverWindow(BusbarBridgeDesignWindowVM viewModel) : this(viewModel, null) { }
        private FrameDriverWindow(BusbarBridgeDesignWindowVM viewModel, IBridgeDesignHost host)
        {
            InitializeComponent();
            _viewModel = viewModel ?? new BusbarBridgeDesignWindowVM(
                new PrototypeBusbarBridgeDesignProvider(), new BusbarBridgeDraftEditor(),
                message => UiFeedback.Show(this, message, "母线桥设计", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes, host?.UpstreamReader);
            DataContext = _viewModel;
            _host = host;
            InitializePreviewLayout();
            _viewModel.IsParameterDraftMode = _parameterDraftMode;
            if (host != null)
            {
                var owner = host.OwnerHandle;
                if (owner != IntPtr.Zero) new WindowInteropHelper(this).Owner = owner;
                _viewModel.RepairModel = PlanBridgeRepair;
                _viewModel.RestoreGeneratedModel = host.RestoreSavedDraft;
                _viewModel.ReadHangerContext = host.ReadHangerContext;
                _viewModel.DecodeWallPlanning = host.DecodeWallPlanning;
                _viewModel.ResolveWorkerAuthorization = host.ResolveWorkerAuthorization;
                _viewModel.ValidateWallSpanGeneration = host.ValidateWallSpanGeneration;
                if (host is IBridgeParameterDraftHost parameterHost)
                { parameterHost.AttachDraftEditor(_viewModel); parameterHost.ParameterDraftChanged += ParameterDraftChanged; }
            }
            if (!_parameterDraftMode) _previewTimer.Tick += PreviewTimerTick;
            _viewModel.PlanChanged += PlanChanged;
            if (!_parameterDraftMode) _viewModel.ModelPreviewReady += ModelPreviewReady;
            _viewModel.ViewChanged += SyncFrameSelection;
            _viewModel.PropertyChanged += HangerLayoutPropertyChanged;
            _viewModel.FrameParametersRequested += LocateFrameParameters;
            MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
            MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
            Width = Math.Min(Width, SystemParameters.WorkArea.Width);
            Height = Math.Min(Height, SystemParameters.WorkArea.Height);
            Loaded += InitializeWindowLayout;
            ContentRendered += Window_ContentRendered;
        }

        private void InitializeWindowLayout(object sender, RoutedEventArgs e)
        {
            Loaded -= InitializeWindowLayout;
            SyncFrameSelection(false);
        }
        private void Window_ContentRendered(object sender, EventArgs e)
        {
            ContentRendered -= Window_ContentRendered;
            if (_initialReadStarted) return;
            _initialReadStarted = true;
            if (!_viewModel.HasUpstreamReader) return;
            _viewModel.Message = "正在读取总装并规划母线桥方案，请稍候。";
            // 让首帧先呈现；COM 读取仍在 SolidWorks 宿主线程，不放入 Task.Run。
            _initialReadOperation = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _initialReadOperation = null;
                if (!_closed) ReloadFromActiveModel();
            }));
        }
        public void ActivateBusbarTab()
        {
            if (_viewModel.SelectedCopperSectionId == null && _viewModel.CopperSections.FirstOrDefault() is BridgeCopperSection first)
                _viewModel.SelectCopperSection(first.Id);
        }
        internal void ShowAndActivate()
        {
            if (_wallSchemeWindow != null)
            {
                if (_wallSchemeWindow.WindowState == WindowState.Minimized)
                    _wallSchemeWindow.WindowState = WindowState.Normal;
                UiFeedback.ActivateWindow(_wallSchemeWindow);
                return;
            }
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            if (!IsVisible) Show();
            UiFeedback.ActivateWindow(this);
        }
        private async void SelectWallScheme_Click(object sender, RoutedEventArgs e)
        {
            if (_wallSchemeWindow != null)
            {
                if (_wallSchemeWindow.WindowState == WindowState.Minimized)
                    _wallSchemeWindow.WindowState = WindowState.Normal;
                UiFeedback.ActivateWindow(_wallSchemeWindow);
                return;
            }
            try
            {
                using (var selection = _viewModel.CreateWallSchemeSelector())
                {
                    var draftHost = _parameterDraftMode ? _host as IBridgeParameterDraftHost : null;
                    var window = new BusbarSchemeSelectionWindow(this, selection, draftHost == null ? (Func<viewmodels.BusbarSchemeSelectionWindowVM, Task>)null :
                        candidate => draftHost.PreviewDraftScheme(_viewModel.PreviewWallSchemeSelection(candidate)));
                    var completion = new TaskCompletionSource<bool>();
                    window.Closed += (closedSender, closedArgs) =>
                    {
                        _wallSchemeWindow = null;
                        IsEnabled = true;
                        completion.TrySetResult(window.IsAccepted);
                    };
                    _wallSchemeWindow = window;
                    // ShowDialog会禁用宿主线程上的所有窗口；只暂停设计编辑，保留SW交互。
                    IsEnabled = false;
                    try
                    {
                        window.Show();
                        var accepted = await completion.Task;
                        if (!_closed && accepted) _viewModel.ApplyWallSchemeSelection(selection);
                    }
                    finally
                    {
                        draftHost?.RestoreDraftPreview();
                        IsEnabled = true;
                        if (ReferenceEquals(_wallSchemeWindow, window))
                        {
                            _wallSchemeWindow = null;
                            window.Close();
                        }
                    }
                }
            }
            catch (Exception ex)
            { FaceToWallPreviewTrace.Error("面对墙方案选择失败", ex); _viewModel.Message = "方案选择失败，当前草稿保留：" + ex.Message; }
        }
        /// <summary>只读反读上游，不初始化旧VM、不运行路径算法或生成模型。</summary>
        public bool ReloadFromActiveModel()
        {
            if (_closed || _readingUpstream) return false;
            if (_host is IBridgeParameterDraftHost draftHost && draftHost.HasParameterDraft)
            {
                ReloadParameterDraft(draftHost);
                return false;
            }
            _readingUpstream = true;
            try
            {
                return ReloadFromActiveModelCore();
            }
            finally
            {
                _readingUpstream = false;
                HideReadLoadingIndicator();
            }
        }
        private async void ReloadParameterDraft(IBridgeParameterDraftHost draftHost)
        {
            _readingUpstream = true;
            _viewModel.NativePreviewBusy = true;
            _viewModel.Message = "正在读取参数草稿…";
            try
            {
                await draftHost.EnsureParameterDraftReady(_viewModel.Editor.CurrentSample != null);
                if (!_closed)
                {
                    if (_viewModel.Editor.CurrentSample == null) ReloadFromActiveModelCore();
                    // The draft has already been rebased by EnsureParameterDraftReady. Do not
                    // enter the saved-model reread path or reset the editor a second time.
                    _viewModel.Message = _viewModel.Editor.CurrentSample?.ReadbackBaselineWarning ?? "已载入当前参数草稿方案。";
                    draftHost.ShowDraftView(BridgeDraftView.All);
                }
            }
            catch (Exception error)
            {
                FaceToWallPreviewTrace.Error("参数草稿重读失败", error);
                if (!_closed) _viewModel.Message = "参数草稿重读失败：" + error.Message;
            }
            finally
            {
                _readingUpstream = false;
                _viewModel.NativePreviewBusy = false;
                HideReadLoadingIndicator();
            }
        }
        private void ShowReadLoadingIndicator()
        {
            if (_readLoadingIndicator != null || !IsVisible) return;
            LayoutScroll.IsEnabled = false;
            _readLoadingIndicator = new BridgeReadLoadingIndicator(IntPtr.Zero, new Point());
        }
        private void HideReadLoadingIndicator()
        {
            _readLoadingIndicator?.Dispose();
            _readLoadingIndicator = null;
            LayoutScroll.IsEnabled = true;
            DraftProgressChanged(this, new PropertyChangedEventArgs(null));
        }
        private bool ReloadFromActiveModelCore()
        {
            _initialReadOperation?.Abort(); _initialReadOperation = null;
            if (_viewModel.GenerationBusy) { _viewModel.Message = "正式生成正在执行，请结束后重读。"; return false; }
            if (_previewBusy) { _viewModel.Message = "视口预览正在更新，请稍后重读。"; return false; }
            try
            {
                ValidateInitialContext?.Invoke(); ValidateInitialContext = null;
                if (_nativePreview == null && _host != null)
                {
                    _nativePreview = _host.CreatePreviewSession();
                    _nativePreview.Invalidated += PreviewInvalidated;
                }
            }
            catch (Exception error)
            {
                FaceToWallPreviewTrace.Error("母线桥设计初始上下文检查失败", error);
                _viewModel.ReportUpstreamFailure(error.Message);
                return false;
            }
            var readPrepared = false;
            try
            {
                var loaded = _viewModel.ReadUpstream(() =>
                {
                    ShowReadLoadingIndicator();
                    _previewTimer.Stop();
                    _nativePreview?.Clear();
                    _viewModel.NativePreviewAvailable = false;
                    _viewModel.NativePreviewStatus = null;
                    readPrepared = true;
                });
                if (loaded && _viewModel.IsModelPlannedPreview && _nativePreview != null)
                {
                    try { _nativePreview.Bind(_viewModel.Upstream); _viewModel.NativePreviewAvailable = true; }
                    catch (Exception error)
                    {
                        FaceToWallPreviewTrace.Error("视口预览上下文绑定失败", error);
                        _viewModel.Message = "参数草稿已载入；视口预览暂不可用：" + error.Message;
                        _viewModel.NativePreviewStatus = _viewModel.Message;
                    }
                }
                return loaded;
            }
            catch (Exception ex)
            {
                if (!readPrepared)
                {
                    FaceToWallPreviewTrace.Error("重读前准备失败", ex);
                    _viewModel.Message = "重读已取消，当前草稿保留：" + ex.Message;
                    _viewModel.NativePreviewStatus = _viewModel.Message;
                    return false;
                }
                FaceToWallPreviewTrace.Error("母线桥设计上游反读失败", ex);
                _viewModel.ReportUpstreamFailure(ex.Message);
                return false;
            }
        }
        private void ReloadUpstream_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.HasUnsavedChanges && UiFeedback.Show(this,
                "重读并规划将替换当前草稿、未提交输入、锁定和未生成的吊架修改。是否继续？", "母线桥设计",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            ReloadFromActiveModel();
        }
        internal void CloseForAddinShutdown() { _forceClose = true; Close(); }
        internal bool LeaveDraftForCabinet()
        {
            if (!_parameterDraftMode) return true;
            Keyboard.ClearFocus();
            if (_readingUpstream || _savingDraftSettings || _previewBusy || _wallSchemeWindow != null || _viewModel.HasPendingInput)
            {
                _viewModel.Message = "请等待当前操作结束，并提交参数输入后再返回柜体设计。";
                return false;
            }
            _forceClose = true;
            Close();
            return _closed;
        }
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_readingUpstream) { e.Cancel = true; return; }
            if (_viewModel.GenerationBusy) { e.Cancel = true; _viewModel.Message = "请等待正式生成及恢复结束后关闭。"; return; }
            if (_previewBusy) { e.Cancel = true; _viewModel.Message = "请等待本次视口预览结束后关闭。"; return; }
            if (_parameterDraftMode && !_forceClose && !_draftClosingAccepted)
            {
                e.Cancel = true;
                if (_savingDraftSettings) return;
                var answer = UiFeedback.Show(this, "是否保存母线桥参数和草稿设置？", "退出母线桥设计", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel) return;
                if (answer == MessageBoxResult.Yes) { SaveDraftSettingsAndClose(); return; }
                try
                {
                    ((IBridgeParameterDraftHost)_host).DiscardDraftSettings();
                    _draftClosingAccepted = true; _ = Dispatcher.BeginInvoke(new Action(Close));
                }
                catch (Exception error) { FaceToWallPreviewTrace.Error("放弃母线桥草稿设置失败", error); _viewModel.Message = "退出未完成：" + error.Message; }
                return;
            }
            if (!_parameterDraftMode && !_forceClose && !_viewModel.ConfirmClose()) { e.Cancel = true; return; }
            try { _nativePreview?.Clear(); }
            catch (Exception error)
            {
                FaceToWallPreviewTrace.Error("关闭前清理视口预览失败", error);
                _viewModel.Message = error.Message;
                if (!_forceClose) e.Cancel = true;
            }
        }
        private void DraftPrevious_Click(object sender, RoutedEventArgs e) => ShowCabinetDesign?.Invoke();
        private async void SaveDraftSettingsAndClose()
        {
            if (_savingDraftSettings || _viewModel.GenerationBusy) return;
            _savingDraftSettings = true;
            DraftPreviousButton.IsEnabled = false;
            try
            {
                System.Windows.Input.Keyboard.ClearFocus();
                if (_viewModel.PlanningBusy) await _viewModel.PlanningCompletion;
                if (_viewModel.HasPendingInput || _viewModel.Fields.Any(field => !string.IsNullOrEmpty(field.Error)))
                    throw new InvalidOperationException("请先修正并提交参数表中的输入。");
                LayoutScroll.IsEnabled = false;
                await ((IBridgeParameterDraftHost)_host).ConfirmDraftSettings();
                _draftClosingAccepted = true; _ = Dispatcher.BeginInvoke(new Action(Close));
            }
            catch (Exception error) { FaceToWallPreviewTrace.Error("保存母线桥草稿设置失败", error); _viewModel.Message = "保存失败：" + error.Message; }
            finally { LayoutScroll.IsEnabled = true; _savingDraftSettings = false; DraftPreviousButton.SetBinding(IsEnabledProperty, nameof(BusbarBridgeDesignWindowVM.IsEditingEnabled)); }
        }
        private void Window_Closed(object sender, EventArgs e)
        {
            if (_host is IBridgeParameterDraftHost parameterHost)
            { parameterHost.ParameterDraftChanged -= ParameterDraftChanged; parameterHost.DetachDraftEditor(_viewModel); }
            HideReadLoadingIndicator();
            _initialReadOperation?.Abort(); _initialReadOperation = null; ValidateInitialContext = null; ShowCabinetDesign = null;
            _closed = true;
            _viewModel.PropertyChanged -= DraftProgressChanged;
            _draftProgressToast?.Finish();
            _draftProgressToast = null;
            _frameParameterOperation?.Abort(); _frameParameterOperation = null;
            _viewModel.FrameParametersRequested -= LocateFrameParameters;
            _viewModel.PlanChanged -= PlanChanged; _viewModel.ModelPreviewReady -= ModelPreviewReady; _viewModel.ViewChanged -= SyncFrameSelection;
            _viewModel.PropertyChanged -= HangerLayoutPropertyChanged; _viewModel.Dispose();
            _viewModel.RestoreGeneratedModel = null; _viewModel.DecodeWallPlanning = null;
            _viewModel.RepairModel = null;
            _viewModel.ValidateWallSpanGeneration = null;
            _previewTimer.Stop(); _previewTimer.Tick -= PreviewTimerTick;
            if (_nativePreview != null)
            {
                _nativePreview.Invalidated -= PreviewInvalidated;
                try { _nativePreview.Dispose(); }
                catch (Exception error) { FaceToWallPreviewTrace.Error("释放视口预览会话失败", error); }
            }
        }
        private void PlanChanged(object sender, EventArgs e)
        {
            if (_parameterDraftMode) return;
            if (_nativePreview?.Enabled != true || _closed) return;
            _viewModel.Message = "草稿已改变，视口预览待刷新。";
            _viewModel.NativePreviewStatus = _viewModel.Message;
            _previewTimer.Stop(); _previewTimer.Start();
        }
        private void ParameterDraftChanged(object sender, EventArgs e)
        {
            if (_closed || _viewModel.GenerationBusy || _viewModel.HasPendingInput) return;
            if (_viewModel.Editor.CurrentSample == null) ReloadFromActiveModel();
            else { _viewModel.NativePreviewAvailable = true; _viewModel.NativePreviewStatus = "当前参数草稿预览已同步。"; }
        }
        private void ModelPreviewReady(object sender, EventArgs e)
        {
            if (_closed || _nativePreview == null) return;
            var refresh = _nativePreview.Enabled;
            try
            {
                _nativePreview.Bind(_viewModel.Upstream);
                _viewModel.NativePreviewAvailable = true;
                _viewModel.NativePreviewStatus = null;
                // Bind清除上一方案的临时体；等规划状态结束，再恢复已启用的三维预览。
                if (refresh) Dispatcher.BeginInvoke(new Action(() => RefreshNativePreview(true)));
            }
            catch (Exception error)
            {
                FaceToWallPreviewTrace.Error("面对墙规划完成后绑定视口上下文失败", error);
                _viewModel.NativePreviewAvailable = false;
                _viewModel.NativePreviewStatus = "参数草稿已载入；视口上下文需重读：" + error.Message;
            }
        }
        private void PreviewTimerTick(object sender, EventArgs e)
        {
            if (_previewBusy || _viewModel.HasPendingInput) return;
            _previewTimer.Stop();
            if (_nativePreview?.Enabled == true) RefreshNativePreview(false);
        }
        private void Preview3D_Click(object sender, RoutedEventArgs e) => RefreshNativePreview(true);
        private void Clear3D_Click(object sender, RoutedEventArgs e)
        {
            if (_previewBusy) return;
            try { _previewTimer.Stop(); _nativePreview?.Clear(); _viewModel.Message = "视口预览已清除，参数草稿保留。"; _viewModel.NativePreviewStatus = _viewModel.Message; }
            catch (Exception error) { FaceToWallPreviewTrace.Error("清除视口预览失败", error); _viewModel.Message = error.Message; _viewModel.NativePreviewStatus = _viewModel.Message; }
        }
        private void PreviewInvalidated(object sender, EventArgs e)
        {
            if (_viewModel.GenerationBusy) return;
            _previewTimer.Stop();
            _viewModel.NativePreviewAvailable = _nativePreview?.IsBound == true;
            _viewModel.Message = "目标文档已切换或关闭，视口预览已停止；返回原总装后可重新预览。";
            _viewModel.NativePreviewStatus = _viewModel.Message;
        }

        private void PlanBridgeRepair()
        {
            if (_previewBusy || _savingDraftSettings || _viewModel.GenerationBusy || _host == null) return;
            try
            {
                _viewModel.GenerationBusy = true; _previewTimer.Stop();
                _viewModel.Message = "正在按当前柜端接口规划贯通修复，优先保留实际铜排和基架。";
                Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Render);
                var editor = _viewModel.Editor;
                var result = editor.PlanAndApplyRepair(actual => _host.PlanRepair(_viewModel.Upstream, actual));
                if (!result.Success) throw new InvalidOperationException(result.Message);
                _viewModel.GenerationBlocked = false;
                _viewModel.Message = result.Message;
            }
            catch (Exception error)
            {
                FaceToWallPreviewTrace.Error("母线桥贯通修复规划失败", error);
                _viewModel.Message = "修复规划未完成，当前草稿保留：" + error.Message;
                // 原草稿的贯通问题仍会占用状态栏，单独呈现本次规划失败的实际原因。
                UiFeedback.Show(this, _viewModel.Message, "贯通修复未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _viewModel.GenerationBusy = false; }
        }

        private void GenerateUnavailable_Click(object sender, RoutedEventArgs e)
            => UiFeedback.Show(this, "实体生成暂未开发。当前可以继续编辑参数、查看预览或保存草稿。",
                "母线桥项目", MessageBoxButton.OK, MessageBoxImage.Information);

        private void RefreshNativePreview(bool activate)
        {
            if (_parameterDraftMode) return;
            if (_previewBusy || _closed || !_viewModel.CanNativePreview || _nativePreview == null) return;
            _previewTimer.Stop(); _previewBusy = true; _viewModel.NativePreviewBusy = true;
            _viewModel.Message = "正在创建SolidWorks 视口预览，请稍候……";
            _viewModel.NativePreviewStatus = _viewModel.Message;
            var revision = _viewModel.Editor.Revision;
            try
            {
                _nativePreview.Refresh(_viewModel.Editor.CurrentSample.WithHangers(_viewModel.Editor.HangerLayout)
                    .WithHangerRoof(_viewModel.Editor.Sample.HangerRoof, _viewModel.Editor.Sample.HangerRoofProblem), _viewModel.Objects, revision,
                    () => !_closed && revision == _viewModel.Editor.Revision, activate);
                _viewModel.Message = "视口预览已更新，草稿版本" + revision + "；显示铜排、基架、绝缘子和吊杆草稿，未写入模型。";
                _viewModel.NativePreviewStatus = "视口预览已更新，版本" + revision;
            }
            catch (Exception ex)
            {
                FaceToWallPreviewTrace.Error("SolidWorks 视口预览失败", ex);
                if (!_closed)
                {
                    _viewModel.Message = "视口预览未生效：" + ex.Message + "；参数草稿保留。";
                    _viewModel.NativePreviewStatus = "视口预览未生效，参数草稿保留。";
                }
            }
            finally
            {
                _previewBusy = false; _viewModel.NativePreviewBusy = false;
                if (_forceClose && !_closed) _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
        private void FitAll_Click(object sender, RoutedEventArgs e) => _viewModel.FitAll();
        private void FitSelected_Click(object sender, RoutedEventArgs e) => _viewModel.FitSelection();
        private void DraftView_Click(object sender, RoutedEventArgs e)
        {
            if (_host is IBridgeParameterDraftHost draftHost && Enum.TryParse((sender as FrameworkElement)?.Tag as string, out BridgeDraftView view))
                draftHost.ShowDraftView(view);
        }
        private void Field_KeyDown(object sender, KeyEventArgs e)
        {
            var field = (sender as FrameworkElement)?.DataContext as BridgeDesignFieldVM;
            if (field == null) return;
            if (e.Key == Key.Enter) { field.Commit(); e.Handled = true; }
            if (e.Key == Key.Escape) { field.Cancel(); e.Handled = true; }
        }
        private void Field_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            ((sender as FrameworkElement)?.DataContext as BridgeDesignFieldVM)?.Commit();
        }
        private void FieldChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var choice = sender as ComboBox;
            if (choice?.IsKeyboardFocusWithin != true) return;
            choice.GetBindingExpression(ComboBox.SelectedItemProperty)?.UpdateSource();
            (choice.DataContext as BridgeDesignFieldVM)?.Commit();
        }
        private void ToggleObjectListView_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is TabControl views)
                views.SetCurrentValue(TabControl.SelectedIndexProperty, views.SelectedIndex == 0 ? 1 : 0);
        }

        private void GenerationIssues_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!(sender is ScrollViewer scroll) || scroll.ScrollableWidth <= 0) return;
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        }

        private void CopperSectionGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, sender) || _syncingFrameSelection ||
                !(CopperSectionGrid.SelectedItem is BridgeCopperListRow row) || _viewModel == null) return;
            SelectCopperListRow(row);
            if (_viewModel.HasPendingInput) SyncFrameSelection(false);
        }

        private void CopperRowName_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Hyperlink)?.DataContext is BridgeCopperListRow row) SelectCopperListRow(row);
        }

        private void SelectCopperListRow(BridgeCopperListRow row)
        {
            if (row.Section != null) _viewModel.SelectCopperSection(row.Section.Id);
            else SelectCopperBreakpoint(row.EndJointId);
        }

        private void CopperBreakpoint_Click(object sender, RoutedEventArgs e)
        {
            var jointId = ((sender as Hyperlink)?.DataContext as BridgeCopperListRow)?.EndJointId ??
                (sender as MenuItem)?.Tag as string;
            SelectCopperBreakpoint(jointId);
        }

        private void SelectCopperBreakpoint(string jointId)
        {
            if (jointId == null) return;
            _viewModel.Select(jointId);
            if (!_viewModel.HasPendingInput && _viewModel.Editor.SelectedId == jointId)
                ObjectDetailsExpander.IsExpanded = true;
        }

        private void SupportTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, sender) || !(sender is TabControl tabs) || _viewModel == null || _closed)
                return;
            var index = tabs.SelectedIndex;
            if (index < 0 || index == _viewModel.SupportTabIndex) return;

            // 预览定位由视图模型同步页签；只处理用户切换，并等目标列表绑定后再读取当前排序的首行。
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (_closed || tabs.SelectedIndex != index || index == _viewModel.SupportTabIndex) return;
                var tab = index == 0 ? BusbarBridgeDesignWindowVM.FrameTab : BusbarBridgeDesignWindowVM.HangerTab;
                var grid = index == 0 ? FrameGrid : HangerGrid;
                var row = grid.Items.Count > 0 ? grid.Items[0] : null;
                var firstRowId = (row as SupportListRow)?.Id ?? (row as BridgeHangerRow)?.Id;
                if (_viewModel.TrySelectObjectListTab(tab, firstRowId))
                    FindObjectGridScroll(grid)?.ScrollToTop();
                else
                    tabs.GetBindingExpression(TabControl.SelectedIndexProperty)?.UpdateTarget();
            }));

        }

        private void SyncFrameSelection(object sender, EventArgs e) => SyncFrameSelection(false);

        private void SyncFrameSelection(bool refreshList)
        {
            if (_syncingFrameSelection || _viewModel == null) return;
            _syncingFrameSelection = true;
            try
            {
                if (refreshList || !ReferenceEquals(_supportListObjects, _viewModel.Objects)) RefreshSupportList();

                var selected = FrameGrid.Items.OfType<SupportListRow>().Where(row =>
                    _viewModel.SelectedFrameIds.Contains(row.Id) ||
                    (!_viewModel.HasMultipleFrames && row.Id == _viewModel.Editor.SelectedId)).ToArray();
                foreach (var item in FrameGrid.SelectedItems.Cast<object>().Where(o => !selected.Contains(o)).ToArray()) FrameGrid.SelectedItems.Remove(item);
                foreach (var item in selected) if (!FrameGrid.SelectedItems.Contains(item)) FrameGrid.SelectedItems.Add(item);
                CopperSectionGrid.SetCurrentValue(DataGrid.SelectedItemProperty, _viewModel.SelectedCopperListRow);
                var hangerId = _viewModel.SelectedHangerId;
                HangerGrid.SetCurrentValue(DataGrid.SelectedValueProperty, hangerId);
                if (hangerId != null && hangerId != _lastListHangerId)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                    {
                        if (_closed || _viewModel.SelectedHangerId != hangerId) return;
                        var row = HangerGrid.Items.OfType<BridgeHangerRow>().FirstOrDefault(item => item.Id == hangerId);
                        if (row != null) HangerGrid.ScrollIntoView(row);
                    }));
                }
                _lastListHangerId = hangerId;
            }
            finally { _syncingFrameSelection = false; }
        }

        private void RefreshSupportList()
        {
            _supportListObjects = _viewModel.Objects;
            var frames = _viewModel.FrameObjects;
            if (_supportSortColumn != null)
            {
                Func<BridgeDesignObject, double> key = frame => _supportSortColumn == FrameLengthColumn ? frame.Length : frame.FramePositionIndex;
                frames = _supportSortDirection == ListSortDirection.Ascending ? frames.OrderBy(key) : frames.OrderByDescending(key);
            }
            var orderedFrames = frames.ToArray();
            var insulators = _viewModel.InsulatorObjects.ToLookup(item => item.FrameId, StringComparer.Ordinal);
            var framesBySource = orderedFrames.Where(frame => !string.IsNullOrWhiteSpace(frame.SourceName))
                .ToLookup(frame => frame.SourceName, StringComparer.OrdinalIgnoreCase);
            var rows = new List<SupportListRow>(orderedFrames.Length);
            foreach (var frame in orderedFrames)
            {
                var children = insulators[frame.Id];
                var sources = framesBySource[frame.SourceName].Where(other => other.Id != frame.Id).ToArray();
                var linked = frame.FrameTemplateKey == "LargeOffsetCorner" || _viewModel.Editor.IsLargeOffsetConnector(frame.Id);
                rows.Add(new SupportListRow(frame, children, sources, linked));
            }

            var scroll = FindObjectGridScroll(FrameGrid);
            var offset = scroll?.VerticalOffset;
            FrameGrid.ItemsSource = rows;
            foreach (var column in FrameGrid.Columns)
                column.SortDirection = column == _supportSortColumn ? (ListSortDirection?)_supportSortDirection : null;
            if (offset.HasValue) scroll.ScrollToVerticalOffset(offset.Value);
        }

        private void FrameGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;
            _supportSortColumn = e.Column;
            _supportSortDirection = e.Column.SortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending : ListSortDirection.Ascending;
            SyncFrameSelection(true);
        }

        private void LocateGenerationIssue_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.Locate((sender as FrameworkElement)?.DataContext as BridgeDesignIssue);
        }

        private void LocateFrameParameters(object sender, EventArgs e)
        {
            var id = _viewModel.SelectedObject?.Id;
            _frameParameterOperation?.Abort();
            // 等基架列表完成绑定和布局，再按稳定ID寻找当前行。
            _frameParameterOperation = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _frameParameterOperation = null;
                if (_closed || _viewModel.SelectedObject?.Id != id || _viewModel.HasPendingInput) return;
                _frameParameterTargetId = id;
                try
                {
                    SyncFrameSelection(this, EventArgs.Empty);
                    var frame = FrameGrid.Items.OfType<SupportListRow>().FirstOrDefault(row => row.Id == id);
                    if (frame == null) return;
                    FrameGrid.ScrollIntoView(frame, FrameLengthColumn);
                    FrameGrid.UpdateLayout();
                }
                finally { _frameParameterTargetId = null; }
            }));
        }

        private void ObjectGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var grid = sender as DataGrid;
            var scroll = FindObjectGridScroll(grid);
            if (scroll == null) return;
            // 短列表一次滚动一行，保留高精度滚轮的小步输入；不沿用系统一次多行的跳动。
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta / 120d * grid.RowHeight);
            e.Handled = true;
        }

        private static ScrollViewer FindObjectGridScroll(DependencyObject node)
        {
            if (node == null) return null;
            if (node is ScrollViewer scroll) return scroll;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            {
                var found = FindObjectGridScroll(VisualTreeHelper.GetChild(node, index));
                if (found != null) return found;
            }
            return null;
        }

        private void ObjectRow_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            // 只有预览发起的明确定位允许滚动；普通选择和参数刷新继续保留浏览位置。
            var row = sender as DataGridRow;
            if (_frameParameterTargetId != null && (row?.DataContext as SupportListRow)?.Id == _frameParameterTargetId) return;
            e.Handled = true;
        }

        private void FrameGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingFrameSelection || _viewModel == null) return;
            if (_viewModel.HasPendingInput) { SyncFrameSelection(this, EventArgs.Empty); return; }

            var frames = FrameGrid.SelectedItems.OfType<SupportListRow>();
            var current = FrameGrid.CurrentItem as SupportListRow;
            var active = current != null && e.AddedItems.Contains(current) ? current : e.AddedItems.OfType<SupportListRow>().LastOrDefault();
            _viewModel.SetFrameSelection(frames.Select(row => row.Id).ToArray(), active?.Id ?? _viewModel.Editor.SelectedId);
        }

        private sealed class SupportListRow
        {
            internal SupportListRow(BridgeDesignObject frame, IEnumerable<BridgeInsulatorObject> children,
                IReadOnlyList<BridgeDesignObject> sourcePeers, bool linked)
            {
                Frame = frame;
                var insulators = BusbarBridgeDesignWindowVM.DescribeFrameInsulators(children);
                InsulatorCountText = insulators.Count;
                InsulatorPositionText = insulators.Positions;
                Relation = sourcePeers.Count > 0 ? (linked ? "同源/联动" : "同源") : linked ? "结构联动" : "独立";
                var sameSourceNote = sourcePeers.Count > 0 ? "同源基架：" + string.Join("、", sourcePeers.Select(peer => peer.PositionLabel)) : null;
                RelationToolTip = linked
                    ? (sameSourceNote == null ? "大偏移结构组联动" : sameSourceNote + "；大偏移结构组联动")
                    : sameSourceNote;
            }

            public BridgeDesignObject Frame { get; }
            public string Relation { get; }
            public string RelationToolTip { get; }
            public string InsulatorCountText { get; }
            public string InsulatorPositionText { get; }
            public string Id => Frame.Id;
            public int FramePositionIndex => Frame.FramePositionIndex;
        }
    }
}
