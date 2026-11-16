using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CKBusbarBridge.views
{
    public sealed class DimensionHighlightVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length != 3 || !(values[0] is Visibility))
                return Visibility.Collapsed;

            var currentImageName = values[1] as string;
            var highlightImageName = values[2] as string;
            // 红框只属于激活时的图片，不能为了保留红框而覆盖当前示意图。
            return !string.IsNullOrWhiteSpace(currentImageName) &&
                string.Equals(currentImageName, highlightImageName, StringComparison.OrdinalIgnoreCase)
                ? values[0] : Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException("尺寸红框可见性只读。");
    }
}
