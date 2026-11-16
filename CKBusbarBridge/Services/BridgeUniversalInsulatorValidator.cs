using System;
using System.Collections.Generic;
using System.Globalization;
using CKBusbarBridge.Drivers;

namespace CKBusbarBridge.Prototype
{
    // 正式生成与驱动使用同一距边契约，同时独立核验实际几何，避免参数正确而配合未更新。
    internal sealed class BridgeUniversalInsulatorValidator
    {
        internal void Validate(UniversalInsulatorLayout expected, bool offsetLayout, string actualOffsetLayout,
            IReadOnlyDictionary<string, double> variables, IReadOnlyDictionary<int, double?> groupLocalZ,
            double bracketSketchLength, string label)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            var expectedFlag = offsetLayout ? "1" : "0";
            if (actualOffsetLayout != expectedFlag)
                throw new InvalidOperationException(label + "通用绝缘子偏移布局标记不符：期望=" + expectedFlag +
                    "，实际=" + (actualOffsetLayout ?? "<缺失>") + "。");

            CheckVariable(variables, "支架长度", expected.BracketLength, label);
            for (var index = 0; index < 3; index++)
                CheckVariable(variables, UniversalInsulatorLayout.DistanceNames[index], expected.Distances[index], label);

            if (groupLocalZ == null || groupLocalZ.Count != 3)
                throw new InvalidOperationException(label + "通用绝缘子必须具有三个唯一的原安装组。");
            for (var index = 1; index <= 3; index++)
            {
                if (!groupLocalZ.TryGetValue(index, out var actual) || !actual.HasValue)
                    throw new InvalidOperationException(label + "绝缘子组" + index + "缺失、已压缩或实际变换不可读取。");
                CheckValue("绝缘子组" + index + "实际Z", actual.Value,
                    expected.BracketLength / 2d - expected.Distances[index - 1], label);
            }
            CheckValue("支架实际草图长度", bracketSketchLength, expected.BracketLength, label);
        }

        private void CheckVariable(IReadOnlyDictionary<string, double> variables, string name, double expected, string label)
        {
            if (variables == null || !variables.TryGetValue(name, out var actual))
                throw new InvalidOperationException(label + "通用绝缘子缺少原驱动变量：" + name + "。");
            CheckValue(name, actual, expected, label);
        }

        private void CheckValue(string name, double actual, double expected, string label)
        {
            if (double.IsNaN(actual) || double.IsInfinity(actual) ||
                Math.Abs(actual - expected) > UniversalInsulatorLayout.ToleranceMm)
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "{0}通用绝缘子校验不符：{1}，期望={2:0.###} mm，实际={3:0.###} mm。",
                    label, name, expected, actual));
        }
    }
}
