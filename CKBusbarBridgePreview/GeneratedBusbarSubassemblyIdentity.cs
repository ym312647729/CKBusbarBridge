using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// 生成铜排子装配体的模型级身份标识，以及按组件父链判断归属的工具。
    /// </summary>
    public static class GeneratedBusbarSubassemblyIdentity
    {
        public const string RdTypePropertyName = "RD类型";
        public const string RdTypePropertyValue = "铜排子装配";

        public static bool IsGeneratedSubassembly(Component2 component)
        {
            return HasRdType(component, RdTypePropertyValue);
        }

        public static bool IsUnderGeneratedSubassembly(Component2 component)
        {
            var currentComponent = component;
            for (var parentDepth = 0;
                currentComponent != null && parentDepth < 64;
                parentDepth++)
            {
                if (IsGeneratedSubassembly(currentComponent))
                {
                    return true;
                }

                try
                {
                    currentComponent = currentComponent.GetParent() as Component2;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static bool HasRdType(
            Component2 component,
            string expectedRdTypeValue)
        {
            if (component == null ||
                string.IsNullOrWhiteSpace(expectedRdTypeValue))
            {
                return false;
            }

            ModelDoc2 componentModel;
            try
            {
                componentModel = component.IGetModelDoc() as ModelDoc2 ??
                    component.GetModelDoc2() as ModelDoc2;
            }
            catch
            {
                return false;
            }

            if (componentModel == null)
            {
                return false;
            }

            var configurationNames = new List<string>();
            try
            {
                if (!string.IsNullOrWhiteSpace(component.ReferencedConfiguration))
                {
                    configurationNames.Add(component.ReferencedConfiguration);
                }
            }
            catch
            {
            }

            try
            {
                var activeConfigurationName = componentModel.ConfigurationManager
                    ?.ActiveConfiguration?.Name;
                if (!string.IsNullOrWhiteSpace(activeConfigurationName))
                {
                    configurationNames.Add(activeConfigurationName);
                }
            }
            catch
            {
            }

            configurationNames.Add(string.Empty);
            foreach (var configurationName in configurationNames)
            {
                try
                {
                    var propertyManager = componentModel.Extension
                        ?.CustomPropertyManager[configurationName ?? string.Empty];
                    if (propertyManager == null)
                    {
                        continue;
                    }

                    propertyManager.Get2(
                        RdTypePropertyName,
                        out var rawPropertyValue,
                        out var resolvedPropertyValue);
                    var actualRdTypeValue = !string.IsNullOrWhiteSpace(
                            resolvedPropertyValue)
                        ? resolvedPropertyValue.Trim()
                        : (rawPropertyValue ?? string.Empty).Trim();
                    if (string.Equals(
                            actualRdTypeValue,
                            expectedRdTypeValue,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    // 某个配置不可读时继续检查文档级和其它配置属性。
                }
            }

            return false;
        }
    }
}
