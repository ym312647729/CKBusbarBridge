using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace CKBusbarBridge.views
{
    /// <summary>将旧版双栏参数行排列为适合右侧窄窗的单栏参数行。</summary>
    internal static class CompactParameterGridLayout
    {
        internal static void Apply(DependencyObject root)
        {
            if (root == null) return;
            if (root is Grid formGrid && IsPairedFormGrid(formGrid))
                Reflow(formGrid);

            foreach (object child in LogicalTreeHelper.GetChildren(root))
                if (child is DependencyObject logicalChild)
                    Apply(logicalChild);
        }

        private static bool IsPairedFormGrid(Grid formGrid)
        {
            return formGrid.ColumnDefinitions.Count == 5 &&
                   formGrid.ColumnDefinitions[2].Width.GridUnitType == GridUnitType.Pixel &&
                   Math.Abs(formGrid.ColumnDefinitions[2].Width.Value - 12d) < 0.01d;
        }

        private static void Reflow(Grid formGrid)
        {
            var positions = new List<Tuple<UIElement, int, int, int>>();
            int largestRow = 0;
            foreach (UIElement element in formGrid.Children)
            {
                int oldRow = Grid.GetRow(element);
                int oldColumn = Grid.GetColumn(element);
                int oldSpan = Grid.GetColumnSpan(element);
                largestRow = Math.Max(largestRow, oldRow);
                positions.Add(Tuple.Create(element, oldRow, oldColumn, oldSpan));
            }

            formGrid.ColumnDefinitions.Clear();
            formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1d, GridUnitType.Star) });
            int sourceRows = Math.Max(formGrid.RowDefinitions.Count, largestRow + 1);
            formGrid.RowDefinitions.Clear();
            for (int rowIndex = 0; rowIndex < sourceRows * 2; rowIndex++)
                formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            foreach (var position in positions)
            {
                UIElement element = position.Item1;
                int oldColumn = position.Item3;
                if (oldColumn == 2)
                {
                    element.Visibility = Visibility.Collapsed;
                    continue;
                }

                bool secondField = oldColumn >= 3;
                if (secondField && element is FrameworkElement fieldElement)
                {
                    var originalMargin = fieldElement.Margin;
                    fieldElement.Margin = new Thickness(originalMargin.Left,
                        originalMargin.Top + 8d, originalMargin.Right, originalMargin.Bottom);
                }
                Grid.SetRow(element, position.Item2 * 2 + (secondField ? 1 : 0));
                Grid.SetColumn(element, secondField ? oldColumn - 3 : Math.Min(oldColumn, 1));
                Grid.SetColumnSpan(element, position.Item4 > 1
                    ? (oldColumn == 0 ? 2 : 1)
                    : 1);
            }
        }
    }
}
