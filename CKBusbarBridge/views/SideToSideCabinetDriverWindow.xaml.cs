using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CKBusbarBridge.viewmodels;
using CKBusbarBridge.Helpers;
using SolidWorks.Interop.sldworks;
using RD.UI.Wpf.Controls;

namespace CKBusbarBridge.views
{
    /// <summary>
    /// 侧对侧场景的独立柜体选配窗口。
    /// </summary>
    public partial class SideToSideCabinetDriverWindow : RDWindow
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
            RightSidePanelLayout.Apply(this);
            CompactParameterGridLayout.Apply(this);
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


        private void ParameterTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || !(e.OriginalSource is TabControl))
            {
                return;
            }

            (DataContext as SideToSideCabinetDriverWindowVM)?.ClearDimensionHighlight();
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

