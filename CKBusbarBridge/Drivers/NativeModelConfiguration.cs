using System;
using System.Collections.Generic;

namespace CKBusbarBridge.Drivers
{
    internal sealed class NativeModelConfiguration
    {
        internal string Resolve(IReadOnlyList<string> creationOrder, Func<string, bool> isDerived, string identity)
        {
            if (isDerived == null) throw new ArgumentNullException(nameof(isDerived));
            if (creationOrder == null || creationOrder.Count == 0 || string.IsNullOrWhiteSpace(creationOrder[0]))
                throw new InvalidOperationException("模型缺少原生默认配置：" + identity);

            // IModelDoc2.GetConfigurationNames 按创建顺序返回。保留初始配置的实际名称，
            // 包括焊件自动改名后的“默认<按加工>”，不依赖语言、当前活动配置或名称排序。
            var name = creationOrder[0];
            if (name.StartsWith("__DQ", StringComparison.Ordinal) || isDerived(name))
                throw new InvalidOperationException("模型初始配置不是可驱动的原生默认父配置：" + identity + "/" + name);
            return name;
        }

        internal void RequireReference(string actual, string expected, string identity)
        {
            if (string.IsNullOrWhiteSpace(expected) || !string.Equals(actual, expected, StringComparison.Ordinal))
                throw new InvalidOperationException("模型必须引用原生默认配置：" + identity
                    + "；期望=" + expected + "；实际=" + actual);
        }

        internal void Activate(string expected, Func<string> active, Func<string, bool> show,
            Action rebuild, string identity)
        {
            if (string.Equals(active(), expected, StringComparison.Ordinal)) return;
            // ShowConfiguration2 的返回值不能代替实际后置条件。
            show(expected);
            RequireReference(active(), expected, identity);
            rebuild();
            RequireReference(active(), expected, identity);
        }
    }
}
