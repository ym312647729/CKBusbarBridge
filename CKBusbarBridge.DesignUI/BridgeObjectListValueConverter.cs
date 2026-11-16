using System;
using System.Globalization;
using System.Windows.Data;
using CKBusbarBridge.Prototype;

namespace CKBusbarBridge.views
{
    // 列只读取现有草稿；绑定编辑器版本，使锁定、撤销和重做后重新显示当前值。
    public sealed class BridgeObjectListValueConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (Equals(parameter, "NumberedIssue"))
                return values.Length >= 2 && values[0] is int index && values[1] is string message
                    ? (index + 1).ToString(CultureInfo.InvariantCulture) + "：" +
                      message.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ') + "；"
                    : string.Empty;

            var vm = values.Length > 1 ? values[1] as BusbarBridgeDesignWindowVM : null;
            if (vm == null || values.Length == 0) return Equals(parameter, "Locked") ? (object)false : "—";
            var node = values[0] as BridgeDesignObject;
            return Equals(parameter, "Locked") && node?.Kind == DesignObjectKind.Frame && vm.Editor.IsLocked(node.Id);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException("对象列表新增列只读。");
    }
}
