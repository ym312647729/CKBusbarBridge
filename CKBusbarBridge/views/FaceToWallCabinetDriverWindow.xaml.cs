using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CKBusbarBridge.Models;
using CKBusbarBridge.Helpers;
using CKBusbarBridge.viewmodels;
using SolidWorks.Interop.sldworks;
using RD.UI.Wpf.Controls;

namespace CKBusbarBridge.views
{
    /// <summary>
    /// 面对墙场景的柜体驱动窗口。
    /// </summary>
    public partial class FaceToWallCabinetDriverWindow : UserControl
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        /// <summary>
        /// 使用默认参数打开面对墙柜体驱动窗口。
        /// </summary>
        public FaceToWallCabinetDriverWindow(
            ISldWorks swApp,
            FaceToWallCabinetOrientation cabinetOrientation)
        {
            InitializeComponent();
            CompactParameterGridLayout.Apply(this);
            var viewModel = new FaceToWallCabinetDriverWindowVM(
                swApp,
                cabinetOrientation);
            var projectRootPath = ProjectRootPreferenceStore.Default
                .LoadExistingProjectRoot();
            if (!string.IsNullOrWhiteSpace(projectRootPath))
            {
                viewModel.ProjectPath = projectRootPath;
            }

            DataContext = viewModel;
            Loaded += FaceToWallCabinetDriverWindow_Loaded;
        }

        /// <summary>
        /// 使用已识别的活动总装配体上下文打开面对墙柜体驱动窗口。
        /// </summary>

        private void FaceToWallCabinetDriverWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateExtensionBaseVisibility("Front");
            UpdateExtensionBaseVisibility("Back");
        }


        private void ParameterTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || !(e.OriginalSource is TabControl))
            {
                return;
            }

            (DataContext as FaceToWallCabinetDriverWindowVM)?.ClearDimensionHighlight();
        }

        private void SynchronizeWallBaseParameters_Click(object sender, RoutedEventArgs e)
        {
            var parameters = (DataContext as FaceToWallCabinetDriverWindowVM)?.Parameters;
            if (parameters == null) return;
            try
            {
                parameters.SynchronizeBaseParameters();
            }
            catch (InvalidOperationException error)
            {
                Logger.Warn(error, "墙孔安装参数同步未完成");
                CKBusbarBridgePreview.UiFeedback.Show(error.Message, "墙孔安装参数无效",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }


        private void BaseHeightTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var prefix = GetHolePrefix(sender, "BaseHeightTextBox");
            if (prefix != null)
            {
                UpdateExtensionBaseVisibility(prefix);
            }
        }

        private void ExtensionBaseTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var prefix = GetHolePrefix(sender, "ExtensionBaseTextBox");
            if (prefix != null)
            {
                UpdateBasicBaseHeightText(prefix);
            }
        }

        private static string GetHolePrefix(object sender, string suffix)
        {
            var textBox = sender as TextBox;
            if (textBox == null || string.IsNullOrEmpty(textBox.Name) ||
                !textBox.Name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return null;
            }

            return textBox.Name.Substring(0, textBox.Name.Length - suffix.Length);
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

        private void UpdateExtensionBaseVisibility(string prefix)
        {
            var baseHeightTextBox = FindName(prefix + "BaseHeightTextBox") as TextBox;
            var hintElement = FindName(prefix + "ExtensionBaseHintTextBlock") as UIElement;
            var extensionElement = FindName(prefix + "ExtensionBasePanel") as UIElement;
            var extensionTextBox = FindName(prefix + "ExtensionBaseTextBox") as TextBox;

            if (baseHeightTextBox == null || hintElement == null || extensionElement == null || extensionTextBox == null)
            {
                return;
            }

            var hasBaseHeight = double.TryParse(baseHeightTextBox.Text, out var baseHeight);
            var shouldShow = hasBaseHeight && baseHeight > FaceToWallCabinetHoleParameters.ExtensionBaseHeightThreshold;
            var wasShown = extensionElement.Visibility == Visibility.Visible;

            hintElement.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
            extensionElement.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;

            if (shouldShow && !wasShown)
            {
                extensionTextBox.Text = FaceToWallCabinetHoleParameters.CalculateExtensionBaseHeight(baseHeight).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                extensionTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            }

            UpdateBasicBaseHeightText(prefix);
        }

        private void UpdateBasicBaseHeightText(string prefix)
        {
            var basicBaseTextBlock = FindName(prefix + "BasicBaseHeightTextBlock") as TextBlock;
            if (basicBaseTextBlock == null)
            {
                return;
            }

            var baseHeightTextBox = FindName(prefix + "BaseHeightTextBox") as TextBox;
            var extensionTextBox = FindName(prefix + "ExtensionBaseTextBox") as TextBox;
            if (baseHeightTextBox == null || extensionTextBox == null)
            {
                basicBaseTextBlock.Text = string.Empty;
                return;
            }

            if (double.TryParse(baseHeightTextBox.Text, out var baseHeight) &&
                double.TryParse(extensionTextBox.Text, out var extensionHeight))
            {
                var basicBaseHeight = baseHeight - extensionHeight;
                basicBaseTextBlock.Text = "基础基座高" + basicBaseHeight.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                basicBaseTextBlock.Text = string.Empty;
            }
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
        /// 打开面对墙柜体项目的“图号生成”弹窗。
        /// </summary>
        private void GenerateDrawingNumber_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = DataContext as FaceToWallCabinetDriverWindowVM;
            if (viewModel == null)
            {
                return;
            }

            DrawingNumberDialogLauncher.Show(
                Window.GetWindow(this),
                viewModel.DrawingNumberMetadataSession);
        }
    }
}
