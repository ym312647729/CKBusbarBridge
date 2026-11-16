using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CKBusbarBridge.viewmodels;
using CKBusbarBridge.Helpers;
using SolidWorks.Interop.sldworks;

namespace CKBusbarBridge.views
{
    /// <summary>
    /// 侧对侧场景的独立柜体选配窗口。
    /// </summary>
    public partial class SideToSideCabinetDriverWindow : Window
    {
        /// <summary>
        /// 使用侧对侧场景的独立参数打开选配窗口。
        /// </summary>
        public SideToSideCabinetDriverWindow()
            : this(null)
        {
        }

        public SideToSideCabinetDriverWindow(ISldWorks swApp)
        {
            InitializeComponent();
            var viewModel = new SideToSideCabinetDriverWindowVM(swApp);
            var projectRootPath = ProjectRootPreferenceStore.Default
                .LoadExistingProjectRoot();
            if (!string.IsNullOrWhiteSpace(projectRootPath))
            {
                viewModel.ProjectPath = projectRootPath;
            }

            DataContext = viewModel;
            viewModel.BaseParametersRequested +=
                message => CabinetBaseParameterNavigation.Locate(this, message);
        }


        private void DimensionTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            var textBox = sender as TextBox;
            var dimensionName = textBox == null ? null : textBox.Tag as string;
            var viewModel = DataContext as SideToSideCabinetDriverWindowVM;
            if (viewModel == null)
            {
                return;
            }

            if (!viewModel.ActivateDimensionHighlight(dimensionName, PreviewImage.Tag as string))
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded,
                    new Action(() =>
                    {
                        if (viewModel.ActivateDimensionHighlight(
                            dimensionName,
                            PreviewImage.Tag as string))
                        {
                            UpdateDimensionHighlightLayout();
                        }
                    }));
                return;
            }

            UpdateDimensionHighlightLayout();
            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(UpdateDimensionHighlightLayout));
        }

        private void ParameterTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || !(e.OriginalSource is TabControl))
            {
                return;
            }

            (DataContext as SideToSideCabinetDriverWindowVM)?.ClearDimensionHighlight();
        }

        private void PreviewImage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateDimensionHighlightLayout();
        }

        private void UpdateDimensionHighlightLayout()
        {
            var viewModel = DataContext as SideToSideCabinetDriverWindowVM;
            var bitmapSource = PreviewImage == null ? null : PreviewImage.Source as BitmapSource;
            if (viewModel == null || bitmapSource == null || DimensionHighlightCanvas == null)
            {
                return;
            }

            var imageOrigin = PreviewImage.TranslatePoint(
                new Point(0, 0),
                DimensionHighlightCanvas);
            viewModel.UpdateDimensionHighlightLayout(
                imageOrigin.X,
                imageOrigin.Y,
                PreviewImage.ActualWidth,
                PreviewImage.ActualHeight,
                bitmapSource.PixelWidth,
                bitmapSource.PixelHeight);
        }

        private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            var textBox = FindAncestor<TextBox>(e.OriginalSource as DependencyObject);
            if (textBox == null)
            {
                return;
            }

            var bindingExpression = BindingOperations.GetBindingExpression(textBox, TextBox.TextProperty);
            if (bindingExpression != null)
            {
                bindingExpression.UpdateSource();
            }

            textBox.Select(textBox.Text.Length, 0);
            e.Handled = true;
        }

        private static T FindAncestor<T>(DependencyObject dependencyObject) where T : DependencyObject
        {
            while (dependencyObject != null)
            {
                if (dependencyObject is T typedObject)
                {
                    return typedObject;
                }

                dependencyObject = VisualTreeHelper.GetParent(dependencyObject);
            }

            return null;
        }

        /// <summary>
        /// 打开侧对侧柜体项目的“图号生成”弹窗。
        /// </summary>
        private void GenerateDrawingNumber_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = DataContext as SideToSideCabinetDriverWindowVM;
            if (viewModel == null)
            {
                return;
            }

            DrawingNumberDialogLauncher.Show(
                this,
                viewModel.DrawingNumberMetadataSession);
        }

    }
}

