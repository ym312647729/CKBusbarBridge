using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using CKBusbarBridge.Prototype;
using System.Windows.Input;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.views
{
    public partial class FrameDriverWindow
    {
        private Window _detachedPreviewWindow;
        private Grid PreviewPane;
        private ContentControl TopPreviewHost;
        private BusbarBridgeJointPreviewControl JointPreview;
        private Button DetachPreviewButton;
        private Border ReadLoadingShade;
        private bool _parameterDraftMode;
        private double _embeddedPreviewToListsRatio;
        private double _embeddedLeftMinWidth;
        private double _embeddedLayoutMinWidth;

        private void InitializePreviewLayout()
        {
            _parameterDraftMode = _host is Prototype.IBridgeParameterDraftHost draftHost && draftHost.HasParameterDraft;
            if (_parameterDraftMode)
            {
                DraftViewportToolbar.Visibility = Visibility.Visible;
                DraftPreviousButton.Visibility = Visibility.Visible;
                GenerateModelButton.Content = "生成";
                PhysicalPreviewContent.Visibility = PreviewColumnSplitter.Visibility = Visibility.Collapsed;
                DesignLayout.MinWidth = 0;
                DesignLayout.ColumnDefinitions[0].MinWidth = 0; DesignLayout.ColumnDefinitions[0].Width = new GridLength(0);
                DesignLayout.ColumnDefinitions[1].Width = new GridLength(0);
                DesignLayout.ColumnDefinitions[2].MinWidth = 0;
                DesignLayout.ColumnDefinitions[4].MinWidth = 0;
                ObjectDetailsExpander.IsExpanded = true;
                var workArea = SystemParameters.WorkArea;
                MinWidth = Math.Min(480, workArea.Width / 2);
                Width = workArea.Width / 2;
                MinHeight = Math.Min(580, workArea.Height);
                Height = workArea.Height;
                Left = workArea.Right - Width; Top = workArea.Top;
                WindowState = WindowState.Normal;
                WindowStartupLocation = WindowStartupLocation.Manual;
                LayoutScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                DesignStatus.Height = double.NaN; DesignStatus.TextWrapping = TextWrapping.Wrap;
                _viewModel.PropertyChanged += DraftProgressChanged;
                DraftProgressChanged(this, new System.ComponentModel.PropertyChangedEventArgs(null));
                return;
            }
            var template = (DataTemplate)Resources["PhysicalPreviewTemplate"];
            PreviewPane = (Grid)template.LoadContent();
            JointPreview = (BusbarBridgeJointPreviewControl)PreviewPane.FindName("JointPreview");
            TopPreviewHost = (ContentControl)PreviewPane.FindName("TopPreviewHost");
            DetachPreviewButton = (Button)PreviewPane.FindName("DetachPreviewButton");
            ReadLoadingShade = (Border)PreviewPane.FindName("ReadLoadingShade");
            PhysicalPreviewContent.Content = PreviewPane;
        }
        private void DraftProgressChanged(object sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (!_parameterDraftMode || _closed) return;
            DraftProgressStatus.Visibility = _readingUpstream || _viewModel.PlanningBusy || _viewModel.NativePreviewBusy ? Visibility.Visible : Visibility.Collapsed;
        }
        private void DetachPreview_Click(object sender, RoutedEventArgs e)
        {
            if (_parameterDraftMode) return;
            if (_detachedPreviewWindow != null)
            {
                if (_detachedPreviewWindow.WindowState == WindowState.Minimized)
                    _detachedPreviewWindow.WindowState = WindowState.Normal;
                _detachedPreviewWindow.Activate();
                return;
            }
            if (_closed || _readingUpstream || !_viewModel.IsEditingEnabled) return;
            try
            {
                var window = new Window
                {
                    Title = "母线桥二维预览", Owner = this, DataContext = _viewModel,
                    Resources = Resources, Style = Style, Background = Background,
                    FontFamily = FontFamily, FontSize = FontSize,
                    Width = Math.Min(1200, SystemParameters.WorkArea.Width),
                    Height = Math.Min(850, SystemParameters.WorkArea.Height),
                    MinWidth = Math.Min(760, SystemParameters.WorkArea.Width),
                    MinHeight = Math.Min(520, SystemParameters.WorkArea.Height),
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                // 复用主窗口的禁用状态，读取或选择方案时不能从另一窗口继续编辑。
                window.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = LayoutScroll });
                window.Closed += DetachedPreview_Closed;
                var columns = DesignLayout.ColumnDefinitions;
                _embeddedPreviewToListsRatio = columns[0].Width.Value / (columns[2].Width.Value + columns[4].Width.Value);
                _embeddedLeftMinWidth = columns[0].MinWidth;
                _embeddedLayoutMinWidth = DesignLayout.MinWidth;
                _detachedPreviewWindow = window;
                PhysicalPreviewContent.Content = null;
                SetPreviewDetachedLayout(true);
                window.Content = PreviewPane;
                window.Show();
            }
            catch (Exception error)
            {
                FaceToWallPreviewTrace.Error("拆出二维预览窗口失败", error);
                CloseDetachedPreview();
                _viewModel.Message = "预览窗口未能打开：" + error.Message;
            }
        }

        private void SetPreviewDetachedLayout(bool detached)
        {
            DetachPreviewButton.Visibility = PreviewColumnSplitter.Visibility = detached ? Visibility.Collapsed : Visibility.Visible;
            DetachedPreviewActions.Visibility = detached ? Visibility.Visible : Visibility.Collapsed;
            PreviewPane.Margin = new Thickness(detached ? 12 : 0);

            var columns = DesignLayout.ColumnDefinitions;
            columns[0].MinWidth = detached ? 0 : _embeddedLeftMinWidth;
            columns[0].Width = detached ? new GridLength(0) :
                new GridLength(_embeddedPreviewToListsRatio * (columns[2].Width.Value + columns[4].Width.Value), GridUnitType.Star);
            columns[1].Width = new GridLength(detached ? 0 : PreviewColumnSplitter.Width);
            DesignLayout.MinWidth = detached ? columns[2].MinWidth + columns[3].Width.Value + columns[4].MinWidth : _embeddedLayoutMinWidth;
        }

        private void DetachedPreview_Closed(object sender, EventArgs e)
        {
            var window = (Window)sender;
            if (!ReferenceEquals(_detachedPreviewWindow, window)) return;
            window.Closed -= DetachedPreview_Closed;
            window.Content = null;
            BindingOperations.ClearBinding(window, IsEnabledProperty);
            window.DataContext = null;
            _detachedPreviewWindow = null;
            SetPreviewDetachedLayout(false);
            if (!_closed)
            {
                PhysicalPreviewContent.Content = PreviewPane;
                SyncFrameSelection(this, EventArgs.Empty);
            }
        }

        private void CloseDetachedPreview() => _detachedPreviewWindow?.Close();
        private void DockPreview_Click(object sender, RoutedEventArgs e) => CloseDetachedPreview();

        private void ApplyHangerCount_Click(object sender, RoutedEventArgs e) => _viewModel.ApplyHangerCount();
        private void HangerLayoutPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(_viewModel.IsReducingHangers))
                HangerRemoveColumn.Visibility = _viewModel.IsReducingHangers ? Visibility.Visible : Visibility.Collapsed;
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(_viewModel.HasHangerSections))
                HangerSectionColumn.Visibility = _viewModel.HasHangerSections ? Visibility.Visible : Visibility.Collapsed;
        }
        private void ConfirmHangerStart_Click(object sender, RoutedEventArgs e) => _viewModel.ConfirmHangerStart();

        private void HangerCount_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) _viewModel.ApplyHangerCount();
            else if (e.Key == Key.Escape) _viewModel.CancelHangerCount();
            else return;
            e.Handled = true;
        }

        private void HangerDistance_KeyDown(object sender, KeyEventArgs e)
        {
            if (!((sender as TextBox)?.DataContext is Prototype.BridgeHangerRow row)) return;
            if (e.Key == Key.Enter) _viewModel.CommitHangerDistance(row);
            else if (e.Key == Key.Escape) row.DistanceText = row.SavedDistance;
            else if (e.Key == Key.Up || e.Key == Key.Down)
                _viewModel.StepHangerDistance(row, e.Key == Key.Up ? 1 : -1);
            else return;
            e.Handled = true;
        }

        private void HangerDistance_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if ((sender as TextBox)?.DataContext is Prototype.BridgeHangerRow row)
                _viewModel?.CommitHangerDistance(row);
        }

        private void HangerFrame_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var combo = (ComboBox)sender;
            if (combo.IsKeyboardFocusWithin && combo.DataContext is Prototype.BridgeHangerRow row && combo.SelectedValue is string frameId)
                _viewModel.ChooseHangerFrame(row, frameId);
        }
    }
}
