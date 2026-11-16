using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CKBusbarBridge.viewmodels;
using CKBusbarBridgePreview;
using RD.UI.Wpf.Controls;

namespace CKBusbarBridge.views
{
    public partial class BusbarSchemeSelectionWindow : RDWindow
    {
        private readonly BusbarSchemeSelectionWindowVM _model;
        private BusbarWebPreviewWindow _preview;
        private bool _closed;
        private int _previewVersion;
        private readonly Func<BusbarSchemeSelectionWindowVM, Task> _viewportPreview;
        internal bool IsAccepted { get; private set; }
        internal BusbarSchemeSelectionWindow(Window owner, BusbarSchemeSelectionWindowVM model,
            Func<BusbarSchemeSelectionWindowVM, Task> viewportPreview = null)
        {
            _model = model; Owner = owner; InitializeComponent(); DataContext = model;
            _viewportPreview = viewportPreview;
            if (viewportPreview != null)
            {
                Title = "面对墙 · 方案列表（SolidWorks视口预览）";
                SchemePanel.WebPreviewToggle.Visibility = Visibility.Collapsed;
                model.UseSolidWorksViewportPreview = true;
            }
            model.SelectedSchemeChanged += SelectionChanged; model.PreviewRequested += PreviewRequested;
            Loaded += SelectionChanged; Closed += WindowClosed;
            RightSidePanelLayout.Apply(this);
        }
        private async void SelectionChanged(object sender, EventArgs e) { await RefreshPreviewAsync(); }
        private async void PreviewRequested(object sender, EventArgs e)
        {
            if (_viewportPreview != null) { await RefreshPreviewAsync(); return; }
            if (SchemePanel.WebPreviewToggle.IsChecked != true) SchemePanel.WebPreviewToggle.IsChecked = true;
            else await RefreshPreviewAsync();
        }
        private async void PreviewChanged(object sender, RoutedEventArgs e) { await RefreshPreviewAsync(); }
        private async void SchemeDoubleClick(object sender, MouseButtonEventArgs e) { e.Handled = true; await RefreshPreviewAsync(); }
        private async Task RefreshPreviewAsync()
        {
            if (_closed || !IsLoaded) return;
            var version = ++_previewVersion;
            try
            {
                if (_viewportPreview != null)
                {
                    if (_model.SelectedAlgorithmScheme == null) return;
                    _model.SetStatus("正在准备视口方案预览…", false);
                    await _viewportPreview(_model);
                    if (!_closed && version == _previewVersion) _model.SetStatus("已在 SolidWorks 视口显示选中方案，确认后采用。", false);
                    return;
                }
                if (SchemePanel.WebPreviewToggle.IsChecked != true || _model.SelectedAlgorithmScheme == null)
                { ClosePreview(); return; }
                if (_preview == null) _preview = new BusbarWebPreviewWindow(this);
                await _preview.ShowSceneAsync(_model.CreateWebScene(), CancellationToken.None);
            }
            catch (OperationCanceledException) { /* 切换方案和关闭窗口会取消旧预览。 */ }
            catch (Exception ex)
            { FaceToWallPreviewTrace.Error("独立方案窗口预览失败", ex); if (!_closed && version == _previewVersion) _model.SetStatus("方案预览失败：" + ex.Message, true); }
        }
        private void Accept_Click(object sender, RoutedEventArgs e)
        {
            if (!_model.CanAccept) return;
            IsAccepted = true;
            Close();
        }
        private void Cancel_Click(object sender, RoutedEventArgs e) { Close(); }
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        }
        private void ClosePreview() { var preview = _preview; _preview = null; preview?.CloseFromOwnerWindow(); }
        private void WindowClosed(object sender, EventArgs e)
        {
            _closed = true; _model.SelectedSchemeChanged -= SelectionChanged; _model.PreviewRequested -= PreviewRequested;
            try { ClosePreview(); }
            catch (Exception ex) { FaceToWallPreviewTrace.Error("关闭方案Web预览失败", ex); }
            try { _model.Dispose(); }
            catch (Exception ex) { FaceToWallPreviewTrace.Error("释放独立方案窗口Worker失败", ex); }
        }
    }
}
