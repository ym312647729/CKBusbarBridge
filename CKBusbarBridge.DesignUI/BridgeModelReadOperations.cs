using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
namespace CKBusbarBridge.Drivers
{
    internal static class BridgeModelReadOperations
    {
        internal const string OrdinaryBaseInstallationCoordinateName = "安装坐标系";
        internal const string ElevatedBaseBottomCoordinateName = "下";
        internal const string ElevatedBaseTopCoordinateName = "上";
        internal static void EnsureAssemblyFullyResolved(IModelDoc2 model)
        {
            if (model == null || model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY
                || !(model is IAssemblyDoc assembly))
                throw new InvalidOperationException("无法检查总装轻量化状态：当前文档不是装配体。");

            var lightweight = FindLightweightComponent(assembly);
            if (lightweight == null) return;

            int resolveStatus;
            try
            {
                resolveStatus = assembly.ResolveAllLightWeightComponents(false);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("总装轻量化组件还原失败：" + lightweight, error);
            }

            lightweight = FindLightweightComponent(assembly);
            if (lightweight != null)
                throw new InvalidOperationException("总装组件仍处于轻量化状态：" + lightweight
                    + "；SolidWorks 解析状态码：" + resolveStatus + "。");
        }
        private static string FindLightweightComponent(IAssemblyDoc assembly)
        {
            var components = assembly.GetComponents(false) as object[];
            if (components == null)
                throw new InvalidOperationException("无法读取总装组件，不能确认轻量化状态。");
            foreach (var item in components)
            {
                if (!(item is Component2 component)) continue;
                var state = component.GetSuppression();
                if (state == (int)swComponentSuppressionState_e.swComponentLightweight
                    || state == (int)swComponentSuppressionState_e.swComponentFullyLightweight)
                    return component.Name2;
            }
            return null;
        }
        internal static string GetCustomProperty(IModelDoc2 model, string propertyName)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (string.IsNullOrWhiteSpace(propertyName)) throw new ArgumentException(nameof(propertyName));

            var extension = model.Extension;
            if (extension == null)
            {
                return null;
            }

            var value = ReadCustomPropertyFromManager(extension.CustomPropertyManager[string.Empty], propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            var activeConfigurationName = model.ConfigurationManager?.ActiveConfiguration?.Name;
            if (string.IsNullOrWhiteSpace(activeConfigurationName))
            {
                return null;
            }

            return ReadCustomPropertyFromManager(extension.CustomPropertyManager[activeConfigurationName], propertyName);
        }
        internal static string ReadCustomPropertyFromManager(CustomPropertyManager customPropertyManager, string propertyName)
        {
            if (customPropertyManager == null)
            {
                return null;
            }

            string rawValue = null;
            string resolvedValue = null;

            try
            {
                bool wasResolved;
                bool linkToProperty;
                var getResult = customPropertyManager.Get6(
                    propertyName,
                    false,
                    out rawValue,
                    out resolvedValue,
                    out wasResolved,
                    out linkToProperty);

                if (getResult == (int)swCustomInfoGetResult_e.swCustomInfoGetResult_NotPresent)
                {
                    return null;
                }
            }
            catch
            {
                customPropertyManager.Get2(propertyName, out rawValue, out resolvedValue);
            }

            return SelectCustomPropertyText(rawValue, resolvedValue);
        }
        internal static IReadOnlyList<T> CollectFirstLevelHostedInsulatorCandidates<T>(
            T root,
            Func<T, IEnumerable<T>> childSelector,
            Func<T, string> nameSelector)
            where T : class
        {
            if (root == null)
            {
                return Array.Empty<T>();
            }

            if (childSelector == null)
            {
                throw new ArgumentNullException(nameof(childSelector));
            }

            if (nameSelector == null)
            {
                throw new ArgumentNullException(nameof(nameSelector));
            }

            // 宿主绝缘子是基座的第一层子件；不递归进入绝缘子装配体，避免其内部零件被重复计数。
            return (childSelector(root) ?? Enumerable.Empty<T>())
                .Where(component => component != null
                    && IsHostedInsulatorComponentName(nameSelector(component)))
                .ToArray();
        }
        internal static T ResolveSingleHostedInsulatorCandidate<T>(IEnumerable<T> candidates)
            where T : class
        {
            if (candidates == null)
            {
                throw new ArgumentNullException(nameof(candidates));
            }

            var values = candidates.ToArray();
            if (values.Length > 1)
            {
                throw new InvalidOperationException(
                    "宿主基座内存在多个绝缘子组件，可能有替换残留。");
            }

            return values.SingleOrDefault();
        }
        internal static bool MateEndpointsMatch(
            IEnumerable<Tuple<string, string>> endpoints,
            string expectedParentComponent,
            string expectedParentCoordinate,
            string expectedChildComponent,
            string expectedChildCoordinate)
        {
            if (endpoints == null)
            {
                return false;
            }

            var values = endpoints.ToArray();
            return values.Any(endpoint => EndpointMatches(
                    endpoint,
                    expectedParentComponent,
                    expectedParentCoordinate))
                && values.Any(endpoint => EndpointMatches(
                    endpoint,
                    expectedChildComponent,
                    expectedChildCoordinate));
        }
        internal static bool PositioningMateSetIsExact(
            IEnumerable<IEnumerable<Tuple<string, string>>> mateEndpointSets,
            string expectedParentComponent,
            string expectedParentCoordinate,
            string expectedChildComponent,
            string expectedChildCoordinate)
        {
            if (mateEndpointSets == null)
            {
                return false;
            }

            var positioningMates = mateEndpointSets
                .Where(endpoints => endpoints != null)
                .Select(endpoints => endpoints.ToArray())
                .Where(endpoints => endpoints.Any(endpoint => EndpointMatches(
                    endpoint,
                    expectedChildComponent,
                    expectedChildCoordinate)))
                .ToArray();
            return positioningMates.Length == 1
                && MateEndpointsMatch(
                    positioningMates[0],
                    expectedParentComponent,
                    expectedParentCoordinate,
                    expectedChildComponent,
                    expectedChildCoordinate);
        }
        internal static bool IsHostedInsulatorComponentName(string componentName)
        {
            if (string.IsNullOrWhiteSpace(componentName))
            {
                return false;
            }

            var localName = componentName.Trim();
            var atIndex = localName.IndexOf('@');
            if (atIndex >= 0)
            {
                localName = localName.Substring(0, atIndex);
            }

            // Name2 可能包含“父组件/子组件”层级，仅使用最后一级组件名称匹配。
            var slashIndex = localName.LastIndexOf('/');
            if (slashIndex >= 0)
            {
                localName = localName.Substring(slashIndex + 1);
            }

            return localName.IndexOf(
                "绝缘子+垫片+支架",
                StringComparison.OrdinalIgnoreCase) >= 0;
        }
        internal static bool EndpointMatches(
            Tuple<string, string> endpoint,
            string expectedComponent,
            string expectedCoordinate)
        {
            if (endpoint == null
                || !string.Equals(endpoint.Item2, expectedCoordinate, StringComparison.Ordinal))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(expectedComponent)
                ? string.IsNullOrWhiteSpace(endpoint.Item1)
                : FaceToFaceBaseModelMappingHelper.ComponentFullNameEquals(
                    endpoint.Item1,
                    expectedComponent);
        }
        internal static string NormalizeMateComponentInstanceId(
            string componentInstanceId,
            string componentModelPath,
            string totalAssemblyModelPath)
        {
            if (string.IsNullOrWhiteSpace(componentInstanceId))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(componentModelPath)
                && !string.IsNullOrWhiteSpace(totalAssemblyModelPath)
                && PathsEqual(componentModelPath, totalAssemblyModelPath))
            {
                return null;
            }

            return componentInstanceId.Trim();
        }
        internal static bool PathsEqual(string firstPath, string secondPath)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(firstPath ?? string.Empty),
                    Path.GetFullPath(secondPath ?? string.Empty),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }
        private const string InsulatorTemplateFolderName = "绝缘子模板";
        private const string ConventionalInsulatorTemplateFolderName = "常规绝缘子模版";
        private const string LBracketInsulatorTemplateFolderName = "L型支架绝缘子模板";
        private const string OddSegmentInsulatorAsmFileName = "绝缘子+垫片+支架（3垫块）.SLDASM";
        private const string LBracketOddSegmentInsulatorAsmFileName =
            "绝缘子+垫片+支架（3垫块）-L.SLDASM";
        internal static string SelectCustomPropertyText(string rawValue, string resolvedValue)
        {
            var normalizedRaw = NormalizeCustomPropertyStoredText(rawValue);
            if (!string.IsNullOrWhiteSpace(normalizedRaw))
            {
                if (normalizedRaw.StartsWith("{", StringComparison.Ordinal) ||
                    normalizedRaw.StartsWith("[", StringComparison.Ordinal))
                {
                    return normalizedRaw;
                }

                return rawValue?.Trim();
            }

            var normalizedResolved = NormalizeCustomPropertyStoredText(resolvedValue);
            if (!string.IsNullOrWhiteSpace(normalizedResolved))
            {
                return normalizedResolved;
            }

            return null;
        }
        internal static string NormalizeCustomPropertyStoredText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            if (trimmed.Length >= 2 && trimmed.StartsWith("\"", StringComparison.Ordinal) && trimmed.EndsWith("\"", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(1, trimmed.Length - 2);
            }

            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }
        internal static string ReadConfigurationCustomProperty(
            IModelDoc2 model,
            string configurationName,
            string propertyName)
        {
            if (model?.Extension == null ||
                string.IsNullOrWhiteSpace(configurationName) ||
                string.IsNullOrWhiteSpace(propertyName))
            {
                return null;
            }

            var customPropertyManager = model.Extension.CustomPropertyManager[configurationName];
            if (customPropertyManager == null)
            {
                return null;
            }

            string rawValue = null;
            string resolvedValue = null;
            try
            {
                bool wasResolved;
                bool linkToProperty;
                var result = customPropertyManager.Get6(
                    propertyName,
                    false,
                    out rawValue,
                    out resolvedValue,
                    out wasResolved,
                    out linkToProperty);
                if (result == (int)swCustomInfoGetResult_e.swCustomInfoGetResult_NotPresent)
                {
                    return null;
                }
            }
            catch
            {
                try { customPropertyManager.Get2(propertyName, out rawValue, out resolvedValue); }
                catch { return null; }
            }

            if (!string.IsNullOrWhiteSpace(rawValue))
            {
                return rawValue;
            }

            return string.IsNullOrWhiteSpace(resolvedValue) ? null : resolvedValue;
        }
        internal static SideToSideBaseMateRoute ResolveBase1Route(string positionName)
        {
            return new SideToSideBaseMateRoute(
                ResolveCabinetCoordinateName(positionName, "前后开孔"),
                // 基座1用于换相不偏移：A柜反装，B柜正装。
                positionName == "A柜前" || positionName == "A柜后"
                    ? "反装坐标系"
                    : "安装坐标系");
        }
        internal static SideToSideBaseMateRoute ResolveElevatedRoute(string positionName)
        {
            return new SideToSideBaseMateRoute(
                ResolveCabinetCoordinateName(positionName, "前后开孔"),
                "下");
        }
        internal static SideToSideBaseMateRoute ResolveBase2Route(bool isCabinetA)
        {
            return new SideToSideBaseMateRoute(
                "上",
                isCabinetA ? "安装坐标系(A)" : "安装坐标系(B)");
        }
        internal static string ResolveCabinetCoordinateName(
            string positionName,
            string openingModeName)
        {
            // 总装模板按柜号、前后位置和开孔方式区分基座安装坐标系。
            switch (positionName)
            {
                case "A柜前":
                    return "A柜-前（" + openingModeName + "）";
                case "A柜后":
                    return "A柜-后（" + openingModeName + "）";
                case "B柜前":
                    return "B柜-前（" + openingModeName + "）";
                case "B柜后":
                    return "B柜-后（" + openingModeName + "）";
                default:
                    throw new ArgumentException(
                        "不支持的侧对侧基座位置：" + positionName,
                        nameof(positionName));
            }
        }
        internal static string ResolveConventionalInsulatorAssemblyPath(string templateRoot)
        {
            if (string.IsNullOrWhiteSpace(templateRoot))
            {
                throw new ArgumentException("共享模板根目录不能为空。", nameof(templateRoot));
            }

            var directory = Path.GetFullPath(
                Path.Combine(
                    templateRoot,
                    InsulatorTemplateFolderName,
                    "绝缘子通用模板"));
            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException("未找到通用绝缘子模板目录：" + directory);
            }

            var path = Path.GetFullPath(Path.Combine(directory, OddSegmentInsulatorAsmFileName));
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("未找到通用绝缘子模板装配体：" + path, path);
            }

            return path;
        }
        internal static string ResolveLBracketInsulatorAssemblyPath(string templateRoot)
        {
            // 保留合法兼容入口；支架类型由驱动计划明确传入，不再由模板文件名决定。
            return ResolveConventionalInsulatorAssemblyPath(templateRoot);
        }
    }
    internal sealed class SideToSideBaseMateRoute
    {
        internal SideToSideBaseMateRoute(
            string parentCoordinateName,
            string childCoordinateName)
        {
            ParentCoordinateName = string.IsNullOrWhiteSpace(parentCoordinateName)
                ? throw new ArgumentException("父坐标系名称不能为空。", nameof(parentCoordinateName))
                : parentCoordinateName;
            ChildCoordinateName = string.IsNullOrWhiteSpace(childCoordinateName)
                ? throw new ArgumentException("子坐标系名称不能为空。", nameof(childCoordinateName))
                : childCoordinateName;
        }

        internal string ParentCoordinateName { get; }

        internal string ChildCoordinateName { get; }
    }
}
