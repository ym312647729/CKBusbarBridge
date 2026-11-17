using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using CKBusbarBridge.Prototype;
using System.Windows.Input;
using CKBusbarBridgePreview;
using RD.UI.Wpf.Controls;

namespace CKBusbarBridge.views
{
    public partial class FrameDriverWindow
    {
        private bool _parameterDraftMode;
        private RDProgressToast _draftProgressToast;
        private const double SideBySideListMinimumWidth = 850d;

        private void InitializePreviewLayout()
        {
            _parameterDraftMode = _host is Prototype.IBridgeParameterDraftHost draftHost && draftHost.HasParameterDraft;
            DraftViewportToolbar.Visibility = Visibility.Visible;
            DraftPreviousButton.Visibility = _parameterDraftMode ? Visibility.Visible : Visibility.Collapsed;
            GenerateModelButton.Content = "生成";
            ObjectDetailsExpander.IsExpanded = true;
            RightSidePanelLayout.Apply(this, halfWidth: true);
            UpdateObjectListLayout(Width - LayoutScroll.Margin.Left - LayoutScroll.Margin.Right);
            DesignStatus.Height = double.NaN;
            DesignStatus.TextWrapping = TextWrapping.Wrap;
            _viewModel.PropertyChanged += DraftProgressChanged;
            DraftProgressChanged(this, new System.ComponentModel.PropertyChangedEventArgs(null));
        }

        private void DesignLayout_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged) UpdateObjectListLayout(e.NewSize.Width);
        }

        private void UpdateObjectListLayout(double width)
        {
            bool stacked = width < SideBySideListMinimumWidth;
            Grid.SetColumnSpan(CopperListPanel, stacked ? 3 : 1);
            Grid.SetColumn(SupportListPanel, stacked ? 0 : 2);
            Grid.SetColumnSpan(SupportListPanel, stacked ? 3 : 1);
            Grid.SetRow(SupportListPanel, stacked ? 2 : 1);
            DesignLayout.RowDefinitions[2].Height = stacked ? DesignLayout.RowDefinitions[1].Height : new GridLength(0);
        }
        private void DraftProgressChanged(object sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (_closed || _readingUpstream) return;
            bool isBusy = _viewModel.PlanningBusy || _viewModel.NativePreviewBusy || _viewModel.GenerationBusy;
            if (isBusy)
            {
                if (_draftProgressToast == null) _draftProgressToast = RDToast.BeginProgress("母线桥设计");
                _draftProgressToast.Report(new RDToastProgress
                {
                    Stage = _viewModel.PlanningBusy ? "正在规划方案" :
                        _viewModel.NativePreviewBusy ? "正在更新视口预览" : "正在处理",
                    Detail = _viewModel.Message
                });
            }
            else if (_draftProgressToast != null)
            {
                _draftProgressToast.Finish();
                _draftProgressToast = null;
            }
        }
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
