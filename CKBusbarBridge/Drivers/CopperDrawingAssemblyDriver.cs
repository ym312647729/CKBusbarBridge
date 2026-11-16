using CKBusbarBridge.Models;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    internal sealed class CopperDrawingAssemblyDriver
    {
        private readonly ISldWorks _swApp;

        internal CopperDrawingAssemblyDriver(ISldWorks swApp)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
        }

        internal void OpenBusbarSubassembly(ModelDoc2 parentModel)
        {
            var parentAssembly = parentModel as AssemblyDoc;
            if (parentModel == null ||
                parentModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY ||
                parentAssembly == null)
            {
                throw new InvalidOperationException("请先打开包含铜排子装配体的装配体，再使用铜排图纸功能。");
            }

            parentAssembly.ResolveAllLightWeightComponents(false);
            // 包含嵌套组件；显式排序，避免多个匹配项时结果随 COM 枚举顺序变化。
            var components = (parentAssembly.GetComponents(false) as object[] ?? new object[0])
                .OfType<Component2>()
                .OrderBy(component => component.Name2, StringComparer.OrdinalIgnoreCase)
                .ThenBy(component => component.Name2, StringComparer.Ordinal);

            foreach (var component in components)
            {
                if (component.IsSuppressed())
                {
                    continue;
                }

                var model = component.GetModelDoc2() as ModelDoc2;
                if (model == null)
                {
                    if (string.Equals(Path.GetExtension(component.GetPathName()), ".sldasm", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("无法读取子装配体“" + component.Name2 + "”，请完全解析后重试。");
                    }
                    continue;
                }

                if (model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    continue;
                }

                // RD类型为文档级标识，不能使用父装配体或配置属性替代。
                var properties = model.Extension?.CustomPropertyManager[string.Empty];
                if (properties == null)
                {
                    continue;
                }
                properties.Get4(RdTypeMetadata.PropertyName, false, out var rawValue, out var resolvedValue);
                var rdType = string.IsNullOrWhiteSpace(resolvedValue) ? rawValue : resolvedValue;
                if (string.Equals(rdType, "铜排子装配", StringComparison.Ordinal))
                {
                    OpenAndActivate(model.GetPathName(), component.ReferencedConfiguration);
                    return;
                }
            }

            throw new InvalidOperationException("当前装配体下未找到自定义属性“RD类型”为“铜排子装配”的子装配体。");
        }

        private void OpenAndActivate(string assemblyPath, string configurationName)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            {
                throw new InvalidOperationException("铜排子装配体没有可打开的文件，请先将其保存为独立装配体文件。");
            }

            var errors = 0;
            var warnings = 0;
            var model = _swApp.OpenDoc6(
                assemblyPath,
                (int)swDocumentTypes_e.swDocASSEMBLY,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                configurationName ?? string.Empty,
                ref errors,
                ref warnings) as ModelDoc2;
            if (model == null)
            {
                throw new InvalidOperationException("打开铜排子装配体失败，错误码：" + errors + "，文件：" + assemblyPath);
            }

            errors = 0;
            var activated = _swApp.ActivateDoc3(
                assemblyPath,
                false,
                (int)swRebuildOnActivation_e.swDontRebuildActiveDoc,
                ref errors) as ModelDoc2;
            var activeModel = _swApp.ActiveDoc as ModelDoc2;
            if (activated == null || activeModel == null ||
                activeModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY ||
                !string.Equals(activeModel.GetPathName(), assemblyPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("激活铜排子装配体失败，错误码：" + errors + "，文件：" + assemblyPath);
            }

            // 已加载的子装配体也要使用父装配体所引用的配置，避免扫描到另一配置的零件。
            if (!string.IsNullOrWhiteSpace(configurationName) &&
                !string.Equals(activeModel.ConfigurationManager.ActiveConfiguration.Name, configurationName, StringComparison.Ordinal) &&
                !activeModel.ShowConfiguration2(configurationName))
            {
                throw new InvalidOperationException("无法激活铜排子装配体引用的配置：" + configurationName);
            }
        }
    }
}
