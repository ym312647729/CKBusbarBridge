using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CKBusbarBridgePreview
{
    /// <summary>当前活动总装配体的可用组件及系统零件分类。</summary>
    public sealed class AssemblyPartCatalog
    {
        private const string ModelMappingPropertyName = "模型对照映射";
        private const string FrameMappingNodeName = "基架";
        private const string FramePositionKeyPrefix = "位置";
        private const string ComponentFullNamePropertyName = "零部件全名";
        private readonly Dictionary<SystemPartType_e, List<Component2>> _componentsByType;

        private AssemblyPartCatalog(
            AssemblyDoc assemblyDocument,
            IModelDoc2 assemblyModel,
            IEnumerable<Component2> availableComponents)
        {
            AssemblyDocument = assemblyDocument;
            AssemblyModel = assemblyModel;
            Components = availableComponents.ToList().AsReadOnly();
            _componentsByType = Components
                .GroupBy(component => component.GetSystemPartType())
                .ToDictionary(group => group.Key, group => group.ToList());
        }

        public AssemblyDoc AssemblyDocument { get; private set; }
        public IModelDoc2 AssemblyModel { get; private set; }
        public IReadOnlyList<Component2> Components { get; private set; }

        public static AssemblyPartCatalog Create(ISldWorks swApp)
        {
            if (swApp == null)
            {
                throw new ArgumentNullException(nameof(swApp));
            }

            var activeModel = swApp.IActiveDoc2;
            FaceToWallPreviewTrace.Info(
                "装配体扫描-活动文档",
                "文档存在=" + (activeModel != null) +
                "，文档类型=" + (activeModel == null ? "null" : activeModel.GetType().ToString()) +
                "，运行时类型=" +
                (activeModel == null ? "null" : ((object)activeModel).GetType().FullName));
            if (activeModel == null ||
                activeModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY ||
                !(activeModel is AssemblyDoc assemblyDocument))
            {
                throw new InvalidOperationException("母排模型预览需要在母线桥总装配体中执行。");
            }

            var allComponents = (assemblyDocument.GetComponents(false) as object[])?
                .OfType<Component2>()
                .Where(IsAvailable)
                .ToList() ?? new List<Component2>();
            if (allComponents.Count == 0)
            {
                throw new InvalidOperationException("当前总装配体中没有可用于铜排预览的已解析组件。");
            }

            var assemblyPartCatalog = new AssemblyPartCatalog(
                assemblyDocument,
                activeModel,
                allComponents);
            FaceToWallPreviewTrace.Info(
                "装配体扫描-完成",
                "有效组件数=" + assemblyPartCatalog.Components.Count +
                "，基座=" + assemblyPartCatalog.GetTopLevelComponentsByConfiguredRdType(
                    SystemPartType_e.Base).Count +
                "，基架=" + assemblyPartCatalog.GetTopLevelComponentsByConfiguredRdType(
                    SystemPartType_e.Frame).Count +
                "，柜顶伸出排=" +
                assemblyPartCatalog.GetComponents(SystemPartType_e.CabinetTopExtensionBusbar).Count +
                "，绝缘子=" + assemblyPartCatalog.GetComponents(SystemPartType_e.Insulator).Count +
                "，穿墙套管=" + assemblyPartCatalog.GetComponents(SystemPartType_e.WallBushing).Count);
            return assemblyPartCatalog;
        }

        public IReadOnlyList<Component2> GetComponents(SystemPartType_e systemPartType)
        {
            if (IsStructuralAssemblyType(systemPartType))
            {
                return GetAvailableTopLevelComponents()
                    .Where(component =>
                        component.GetConfiguredRdSystemPartType() == systemPartType)
                    .ToList()
                    .AsReadOnly();
            }

            return _componentsByType.TryGetValue(systemPartType, out var components)
                ? components.AsReadOnly()
                : new List<Component2>().AsReadOnly();
        }

        public IReadOnlyList<Component2> GetTopLevelComponents(
            SystemPartType_e systemPartType)
        {
            var matchingTopLevelComponents = IsStructuralAssemblyType(systemPartType)
                ? GetAvailableTopLevelComponents()
                    .Where(component =>
                        component.GetConfiguredRdSystemPartType() == systemPartType)
                    .ToList()
                : GetAvailableTopLevelComponents()
                    .Where(component =>
                        component.GetSystemPartType() == systemPartType)
                    .ToList();
            FaceToWallPreviewTrace.Info(
                "装配体扫描-顶层类型",
                "目标类型=" + systemPartType +
                "，匹配数=" + matchingTopLevelComponents.Count +
                "，匹配=" + string.Join(
                    ",",
                    matchingTopLevelComponents.Select(
                        BusbarGeometryUtility.SafeName)));
            return matchingTopLevelComponents.AsReadOnly();
        }

        /// <summary>
        /// 仅按引用配置的“RD类型”筛选顶层组件，不允许名称或 Description 回退。
        /// </summary>
        public IReadOnlyList<Component2> GetTopLevelComponentsByConfiguredRdType(
            SystemPartType_e systemPartType)
        {
            var matchingTopLevelComponents = GetAvailableTopLevelComponents()
                .Where(component =>
                    component.GetConfiguredRdSystemPartType() == systemPartType)
                .ToList();
            FaceToWallPreviewTrace.Info(
                "装配体扫描-顶层RD类型",
                "目标类型=" + systemPartType +
                "，匹配数=" + matchingTopLevelComponents.Count +
                "，匹配=" + string.Join(
                    ",",
                    matchingTopLevelComponents.Select(
                        BusbarGeometryUtility.SafeName)));
            return matchingTopLevelComponents.AsReadOnly();
        }

        public Component2 FindUniqueTopLevelComponentContaining(string nameToken)
        {
            if (string.IsNullOrWhiteSpace(nameToken))
            {
                throw new ArgumentException(
                    "顶层组件名称匹配标记不能为空。",
                    nameof(nameToken));
            }

            var normalizedNameToken = nameToken.Trim();
            var availableTopLevelComponents = GetAvailableTopLevelComponents();
            var matchingComponents = availableTopLevelComponents
                .Where(component =>
                    BusbarGeometryUtility.SafeName(component).IndexOf(
                        normalizedNameToken,
                        StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            if (matchingComponents.Count == 0)
            {
                throw new InvalidOperationException(
                    "总装配体顶层设计树中没有找到名称包含“" +
                    normalizedNameToken + "”的已解析组件。");
            }

            if (matchingComponents.Count > 1)
            {
                throw new InvalidOperationException(
                    "总装配体顶层设计树中名称包含“" +
                    normalizedNameToken + "”的已解析组件不唯一，匹配数量=" +
                    matchingComponents.Count + "，组件=" +
                    string.Join(
                        ",",
                        matchingComponents.Select(
                            BusbarGeometryUtility.SafeName)) + "。");
            }

            return matchingComponents[0];
        }

        public bool IsComponentInSubtree(
            Component2 component,
            Component2 hostComponent)
        {
            return component != null &&
                   hostComponent != null &&
                   IsDescendantOf(component, hostComponent);
        }

        /// <summary>
        /// 返回组件在当前总装配体顶层设计树中的直接装配体。
        /// 面对墙基座归属必须使用该总装配体 Box 中心，不能用柜顶排的轴向投影代替。
        /// </summary>
        public Component2 GetTopLevelComponent(Component2 component)
        {
            if (component == null)
            {
                return null;
            }

            var currentComponent = component;
            for (var parentDepth = 0; parentDepth < 64; parentDepth++)
            {
                Component2 parentComponent;
                try
                {
                    parentComponent = currentComponent.GetParent() as Component2;
                }
                catch (Exception parentReadException)
                {
                    FaceToWallPreviewTrace.Warning(
                        "读取总装配体顶层柜体",
                        "组件=" + BusbarGeometryUtility.SafeName(component) +
                        "；读取父级失败=" + parentReadException.Message);
                    return null;
                }

                if (parentComponent == null)
                {
                    return currentComponent;
                }

                currentComponent = parentComponent;
            }

            throw new InvalidOperationException(
                "组件层级超过最大深度，无法确定总装配体顶层装配体：" +
                BusbarGeometryUtility.SafeName(component));
        }

        public IReadOnlyList<Component2> GetDescendants(
            Component2 hostComponent,
            SystemPartType_e descendantType)
        {
            if (hostComponent == null)
            {
                return new List<Component2>().AsReadOnly();
            }

            return GetComponents(descendantType)
                .Where(component => IsDescendantOf(component, hostComponent))
                .ToList()
                .AsReadOnly();
        }

        public IReadOnlyList<Component2> GetAllDescendants(Component2 hostComponent)
        {
            if (hostComponent == null)
            {
                return new List<Component2>().AsReadOnly();
            }

            return Components
                .Where(component => !ReferenceEquals(component, hostComponent) &&
                                    IsDescendantOf(component, hostComponent))
                .ToList()
                .AsReadOnly();
        }

        public Component2 FindNearestComponent(
            SystemPartType_e systemPartType,
            GeometRi.Point3d referencePoint)
        {
            var candidateComponents = IsStructuralAssemblyType(systemPartType)
                ? GetTopLevelComponentsByConfiguredRdType(systemPartType)
                : GetComponents(systemPartType);
            return candidateComponents
                .Select(component => new
                {
                    Component = component,
                    Center = BusbarGeometryUtility.GetComponentCenter(component)
                })
                .Where(candidate => candidate.Center != null && referencePoint != null)
                .OrderBy(candidate => candidate.Center.DistanceTo(referencePoint))
                .Select(candidate => candidate.Component)
                .FirstOrDefault();
        }

        public Component2 FindLastGeneratedFrameComponent()
        {
            return GetGeneratedFrameComponentsInOrder().Last();
        }

        /// <summary>
        /// 按总装配体“模型对照映射”中“基架/位置N/零部件全名”的物理顺序返回基架。
        /// 面对墙末基架及普通面对面仍沿用该装配顺序；大偏移使用独立二维几何顺序。
        /// </summary>
        public IReadOnlyList<Component2> GetGeneratedFrameComponentsInOrder()
        {
            var frameComponents = GetTopLevelComponentsByConfiguredRdType(
                SystemPartType_e.Frame).ToList();
            if (frameComponents.Count == 0)
            {
                throw new InvalidOperationException("当前总装配体中没有识别到基架组件。");
            }

            var modelMappingText = ReadCustomProperty(
                AssemblyModel,
                ModelMappingPropertyName);
            if (string.IsNullOrWhiteSpace(modelMappingText))
            {
                throw new InvalidOperationException(
                    "总装配体缺少“模型对照映射”，无法确定已生成基架的物理顺序。");
            }

            var orderedFrameMapEntries = ParseOrderedFrameModelMapping(
                modelMappingText);

            var orderedFrameComponents = new List<Component2>();
            var matchedFrameComponentNames = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var orderedFrameMapEntry in orderedFrameMapEntries)
            {
                var matchingFrameComponents = frameComponents
                    .Where(frameComponent => ComponentFullNameEquals(
                        BusbarGeometryUtility.SafeName(frameComponent),
                        orderedFrameMapEntry.Value))
                    .ToList();
                if (matchingFrameComponents.Count != 1)
                {
                    throw new InvalidOperationException(
                        "无法根据“" + FramePositionKeyPrefix + orderedFrameMapEntry.Key +
                        "”唯一匹配基架组件“" +
                        orderedFrameMapEntry.Value + "”，匹配数量=" +
                        matchingFrameComponents.Count + "。");
                }

                var matchedFrameComponent = matchingFrameComponents[0];
                var matchedFrameComponentName =
                    BusbarGeometryUtility.SafeName(matchedFrameComponent);
                if (!matchedFrameComponentNames.Add(
                        StripSelectPathSuffix(matchedFrameComponentName)))
                {
                    throw new InvalidOperationException(
                        "总装配体“模型对照映射”中的多个位置指向同一基架组件“" +
                        matchedFrameComponentName + "”。");
                }

                orderedFrameComponents.Add(matchedFrameComponent);
            }

            FaceToWallPreviewTrace.Info(
                "基架-模型对照映射物理顺序",
                string.Join(
                    "；",
                    orderedFrameMapEntries.Select((frameMapEntry, frameIndex) =>
                        FramePositionKeyPrefix + frameMapEntry.Key + "=" +
                        BusbarGeometryUtility.SafeName(
                            orderedFrameComponents[frameIndex]))));
            return orderedFrameComponents.AsReadOnly();
        }

        internal static IReadOnlyList<KeyValuePair<int, string>>
            ParseOrderedFrameModelMapping(string modelMappingText)
        {
            if (string.IsNullOrWhiteSpace(modelMappingText))
            {
                throw new InvalidOperationException(
                    "总装配体缺少“模型对照映射”，无法确定已生成基架的物理顺序。");
            }

            JObject modelMapping;
            try
            {
                modelMapping = JObject.Parse(modelMappingText);
            }
            catch (JsonException modelMappingException)
            {
                throw new InvalidOperationException(
                    "总装配体“模型对照映射”不是有效 JSON，无法确定已生成基架的物理顺序。",
                    modelMappingException);
            }

            var frameMappingToken = modelMapping[FrameMappingNodeName];
            if (frameMappingToken == null || frameMappingToken.Type != JTokenType.Object)
            {
                throw new InvalidOperationException(
                    "总装配体“模型对照映射”中缺少有效的“基架”映射。");
            }

            var orderedFrameMapEntries = new List<KeyValuePair<int, string>>();
            foreach (var frameMappingProperty in ((JObject)frameMappingToken).Properties())
            {
                var positionIndex = ParseFramePositionIndex(frameMappingProperty.Name);
                if (positionIndex <= 0)
                {
                    throw new InvalidOperationException(
                        "总装配体“模型对照映射”的“基架”映射中存在无效位置键“" +
                        frameMappingProperty.Name + "”。");
                }

                var frameEntry = frameMappingProperty.Value as JObject;
                var componentFullNameToken = frameEntry?[ComponentFullNamePropertyName];
                var componentFullName = componentFullNameToken?.Type == JTokenType.String
                    ? componentFullNameToken.Value<string>()
                    : null;
                if (string.IsNullOrWhiteSpace(componentFullName))
                {
                    throw new InvalidOperationException(
                        "总装配体“模型对照映射”的“" + frameMappingProperty.Name +
                        "”中缺少有效的“零部件全名”。");
                }

                orderedFrameMapEntries.Add(
                    new KeyValuePair<int, string>(positionIndex, componentFullName.Trim()));
            }

            if (orderedFrameMapEntries.Count == 0)
            {
                throw new InvalidOperationException(
                    "总装配体“模型对照映射”的“基架”映射中没有位置记录。");
            }

            var duplicatePositionIndex = orderedFrameMapEntries
                .GroupBy(frameMapEntry => frameMapEntry.Key)
                .FirstOrDefault(frameMapEntryGroup => frameMapEntryGroup.Count() > 1);
            if (duplicatePositionIndex != null)
            {
                throw new InvalidOperationException(
                    "总装配体“模型对照映射”中基架位置" +
                    duplicatePositionIndex.Key + "存在重复记录。");
            }

            return orderedFrameMapEntries
                .OrderBy(frameMapEntry => frameMapEntry.Key)
                .ToList()
                .AsReadOnly();
        }

        private static int ParseFramePositionIndex(string mapKey)
        {
            if (string.IsNullOrWhiteSpace(mapKey) ||
                !mapKey.StartsWith(FramePositionKeyPrefix, StringComparison.Ordinal))
            {
                return 0;
            }

            return int.TryParse(
                mapKey.Substring(FramePositionKeyPrefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var positionIndex)
                ? positionIndex
                : 0;
        }

        private static bool ComponentFullNameEquals(
            string actualComponentFullName,
            string mappedComponentFullName)
        {
            return string.Equals(
                StripSelectPathSuffix(actualComponentFullName),
                StripSelectPathSuffix(mappedComponentFullName),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string StripSelectPathSuffix(string componentFullName)
        {
            var normalizedComponentFullName = (componentFullName ?? string.Empty).Trim();
            var selectPathSuffixIndex = normalizedComponentFullName.IndexOf('@');
            return selectPathSuffixIndex >= 0
                ? normalizedComponentFullName.Substring(0, selectPathSuffixIndex)
                : normalizedComponentFullName;
        }

        private static string ReadCustomProperty(
            IModelDoc2 model,
            string propertyName)
        {
            if (model?.Extension == null || string.IsNullOrWhiteSpace(propertyName))
            {
                return null;
            }

            var generalValue = ReadCustomProperty(
                model.Extension.CustomPropertyManager[string.Empty],
                propertyName);
            if (!string.IsNullOrWhiteSpace(generalValue))
            {
                return generalValue;
            }

            var activeConfigurationName =
                model.ConfigurationManager?.ActiveConfiguration?.Name;
            return string.IsNullOrWhiteSpace(activeConfigurationName)
                ? null
                : ReadCustomProperty(
                    model.Extension.CustomPropertyManager[activeConfigurationName],
                    propertyName);
        }

        private static string ReadCustomProperty(
            CustomPropertyManager propertyManager,
            string propertyName)
        {
            if (propertyManager == null)
            {
                return null;
            }

            string rawValue;
            string resolvedValue;
            try
            {
                bool wasResolved;
                bool linkToProperty;
                var getResult = propertyManager.Get6(
                    propertyName,
                    false,
                    out rawValue,
                    out resolvedValue,
                    out wasResolved,
                    out linkToProperty);
                if (getResult ==
                    (int)swCustomInfoGetResult_e.swCustomInfoGetResult_NotPresent)
                {
                    return null;
                }
            }
            catch
            {
                propertyManager.Get2(propertyName, out rawValue, out resolvedValue);
            }

            return !string.IsNullOrWhiteSpace(rawValue)
                ? rawValue.Trim()
                : resolvedValue?.Trim();
        }

        private static bool IsAvailable(Component2 component)
        {
            if (component == null)
            {
                return false;
            }

            try
            {
                if (component.IsSuppressed())
                {
                    return false;
                }
            }
            catch (Exception componentStateException)
            {
                FaceToWallPreviewTrace.Error(
                    "装配体扫描-检查组件状态",
                    componentStateException,
                    "组件=" + BusbarGeometryUtility.SafeName(component));
                throw new InvalidOperationException("模型反读失败：无法读取组件状态：" +
                    BusbarGeometryUtility.SafeName(component), componentStateException);
            }

            try
            {
                if (component.IGetModelDoc() == null)
                    throw new InvalidOperationException("模型反读失败：未压缩组件的文档不可访问：" +
                        BusbarGeometryUtility.SafeName(component));
                return true;
            }
            catch (Exception componentDocumentException)
            {
                FaceToWallPreviewTrace.Error(
                    "装配体扫描-检查组件文档",
                    componentDocumentException,
                    "组件=" + BusbarGeometryUtility.SafeName(component));
                throw new InvalidOperationException("模型反读失败：无法读取组件文档：" +
                    BusbarGeometryUtility.SafeName(component), componentDocumentException);
            }
        }

        private List<Component2> GetAvailableTopLevelComponents()
        {
            return (AssemblyDocument.GetComponents(true) as object[])?
                .OfType<Component2>()
                .Where(IsAvailable)
                .ToList() ?? new List<Component2>();
        }

        private static bool IsStructuralAssemblyType(SystemPartType_e systemPartType)
        {
            return systemPartType == SystemPartType_e.Base
                || systemPartType == SystemPartType_e.Frame;
        }

        private static bool IsDescendantOf(Component2 component, Component2 hostComponent)
        {
            var hostName = BusbarGeometryUtility.SafeName(hostComponent);
            var currentComponent = component;
            for (var parentDepth = 0; currentComponent != null && parentDepth < 64; parentDepth++)
            {
                var currentName = BusbarGeometryUtility.SafeName(currentComponent);
                if (ReferenceEquals(currentComponent, hostComponent) ||
                    !string.IsNullOrWhiteSpace(hostName) &&
                    string.Equals(currentName, hostName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                try
                {
                    currentComponent = currentComponent.GetParent() as Component2;
                }
                catch
                {
                    currentComponent = null;
                }
            }

            return false;
        }
    }
}
