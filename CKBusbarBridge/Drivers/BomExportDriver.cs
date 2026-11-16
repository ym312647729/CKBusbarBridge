using CKBusbarBridgePreview;
using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ISheet = NPOI.SS.UserModel.ISheet;

namespace CKBusbarBridge.Drivers
{
    public sealed class BomExportResult
    {
        public string OutputPath { get; internal set; }

        public int HostAssemblyInstanceCount { get; internal set; }

        public int ModelCount { get; internal set; }

        public int FrameCategoryCount { get; internal set; }

        public int BaseCategoryCount { get; internal set; }

        public int SheetCount { get; internal set; }

        public int ComponentOccurrenceCount { get; internal set; }

        public int RowCount { get; internal set; }

        public IList<string> Warnings { get; } = new List<string>();
    }

    /// <summary>
    /// 从当前母线桥总装配体全局查找基架和基座，按标准模板及模型图号后缀
    /// 分组后导出递归明细。5GX 机架/基座装配体自身先写入清单，其组件树中的
    /// 子装配体和零件再依次写入，其中子装配体自身写入后继续向下展开。
    /// </summary>
    public sealed class BomExportDriver
    {
        private const string TemplateFileName = "母线桥标准清单模板.xlsx";
        private const string DrawingNumberPropertyName = "图号";
        private const string VersionPropertyName = "版本";
        private const int MaximumAssemblyDepth = 128;

        internal static readonly string[] ItemPropertyNames =
        {
            DrawingNumberPropertyName,
            VersionPropertyName,
            "名称",
            "零部件类型",
            "规格型号",
            "材质",
            "净重",
            "毛重",
            "展开长",
            "展开宽",
            "厚度",
            "图纸文件名称",
            "对应标准图号"
        };

        private readonly ISldWorks _swApp;

        public BomExportDriver(ISldWorks swApp)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
        }

        public BomExportResult ExportBomFromCurrentAssembly(string outputPath)
        {
            var assemblyModel = _swApp.ActiveDoc as ModelDoc2;
            if (assemblyModel == null)
            {
                throw new InvalidOperationException("当前没有活动文档，无法导出 BOM。");
            }

            if (assemblyModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new InvalidOperationException("当前活动文档不是装配体，无法导出 BOM。");
            }

            var assemblyPath = assemblyModel.GetPathName();
            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            {
                throw new InvalidOperationException("请先保存当前总装配体，再执行导出 BOM。");
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("BOM 输出路径不能为空。", nameof(outputPath));
            }

            var fullOutputPath = Path.GetFullPath(outputPath.Trim());
            if (!string.Equals(Path.GetExtension(fullOutputPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("BOM 输出文件必须是 .xlsx 文件。", nameof(outputPath));
            }

            var outputDirectory = Path.GetDirectoryName(fullOutputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new InvalidOperationException("无法确定 BOM 输出目录。");
            }

            Directory.CreateDirectory(outputDirectory);
            var templatePath = ResolveTemplatePath();
            if (string.Equals(
                    Path.GetFullPath(templatePath),
                    fullOutputPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("BOM 输出文件不能覆盖标准清单模板。");
            }

            var assemblyDocument = assemblyModel as AssemblyDoc;
            if (assemblyDocument == null)
            {
                throw new InvalidOperationException("无法读取当前总装配体对象。");
            }

            ResolveAndValidateLightweightComponents(assemblyDocument, assemblyModel);
            try
            {
                assemblyModel.EditRebuild3();
            }
            catch
            {
                // BOM 读取不依赖强制重建成功；后续仍会严格校验每个组件是否可读。
            }

            var result = new BomExportResult { OutputPath = fullOutputPath };
            var projectPath = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
            var hosts = BomHostAssemblyCollector.Collect(assemblyModel, projectPath);
            if (hosts.Count == 0)
            {
                throw new InvalidOperationException(
                    "未在总装配体全局找到使用受支持模板的基架或基座装配体。");
            }

            result.HostAssemblyInstanceCount = hosts.Count;
            var propertyReader = new BomCustomPropertyReader(result);
            var sheetGroups = BuildSheetGroups(hosts, propertyReader, result);
            if (sheetGroups.Count == 0)
            {
                throw new InvalidOperationException("没有可写入清单的基架或基座组件。");
            }

            result.ModelCount = sheetGroups.Sum(group => group.Models.Count);
            result.FrameCategoryCount = sheetGroups.Count(group => group.Template.Kind == BomHostKind.Frame);
            result.BaseCategoryCount = sheetGroups.Count(group => group.Template.Kind == BomHostKind.Base);
            result.SheetCount = sheetGroups.Count;
            result.RowCount = sheetGroups.Sum(group => group.Models.Sum(model => model.Items.Count));

            BomWorkbookWriter.Write(templatePath, fullOutputPath, sheetGroups);
            return result;
        }

        private static List<BomSheetGroup> BuildSheetGroups(
            IList<BomHostAssembly> hosts,
            BomCustomPropertyReader propertyReader,
            BomExportResult result)
        {
            var sheetGroups = new List<BomSheetGroup>();
            foreach (var templateGroup in hosts
                .GroupBy(host => host.Template.TemplateDrawingNumber, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.First().Template.SortOrder))
            {
                var template = templateGroup.First().Template;
                var sheetGroup = new BomSheetGroup(template);
                var modelGroups = templateGroup
                    .GroupBy(host => host.ModelGroupKey, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new
                    {
                        FirstOrder = group.Min(host => host.GlobalOrder),
                        Hosts = group.OrderBy(host => host.GlobalOrder).ToList()
                    })
                    .OrderBy(group => group.FirstOrder)
                    .ToList();

                foreach (var groupedModel in modelGroups)
                {
                    var firstHost = groupedModel.Hosts[0];
                    var model = new BomModelGroup(
                        firstHost.ModelSuffix,
                        firstHost.ModelGroupKey,
                        groupedModel.FirstOrder);
                    var aggregator = new BomLineItemAggregator(
                        template,
                        firstHost.ModelSuffix,
                        propertyReader,
                        result);

                    foreach (var host in groupedModel.Hosts)
                    {
                        if (!aggregator.AcceptComponent(host.Component))
                        {
                            throw new InvalidOperationException(
                                "机架/基座装配体被清单过滤规则排除：" + host.ModelPath);
                        }

                        var children = BomComponentTreeCollector.GetChildrenInFeatureTreeOrder(host.Component);
                        foreach (var child in children)
                        {
                            BomComponentTreeCollector.Traverse(
                                child,
                                1,
                                aggregator.AcceptComponent);
                        }
                    }

                    foreach (var item in aggregator.GetItems())
                    {
                        model.Items.Add(item);
                    }
                    if (model.Items.Count == 0)
                    {
                        throw new InvalidOperationException(
                            "模型的有效清单项为空：模板=" + template.TemplateDrawingNumber +
                            "，后缀=" + DisplaySuffix(firstHost.ModelSuffix));
                    }

                    sheetGroup.Models.Add(model);
                }

                sheetGroups.Add(sheetGroup);
            }

            return sheetGroups;
        }

        private static string DisplaySuffix(string suffix)
        {
            return string.IsNullOrWhiteSpace(suffix) ? "（无后缀）" : suffix;
        }

        private static void ResolveAndValidateLightweightComponents(
            AssemblyDoc assemblyDocument,
            ModelDoc2 assemblyModel)
        {
            try
            {
                assemblyDocument.ResolveAllLightWeightComponents(false);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("解析轻化组件失败：" + exception.Message, exception);
            }

            var activeConfiguration = assemblyModel.ConfigurationManager?.ActiveConfiguration;
            var root = activeConfiguration?.GetRootComponent3(true);
            if (root == null)
            {
                throw new InvalidOperationException("无法读取当前总装配体的活动配置组件树。");
            }

            ValidateResolvedTree(root, 0);
        }

        private static void ValidateResolvedTree(Component2 parent, int depth)
        {
            if (depth > MaximumAssemblyDepth)
            {
                throw new InvalidOperationException("装配体层级超过允许的最大深度，无法安全导出 BOM。");
            }

            foreach (var component in BomComponentTreeCollector.GetChildrenInFeatureTreeOrder(parent))
            {
                if (BomComponentTreeCollector.ShouldSkipBranch(component))
                {
                    continue;
                }

                var suppressionState = BomComponentTreeCollector.GetSuppressionState(component);
                if (suppressionState == (int)swComponentSuppressionState_e.swComponentLightweight ||
                    suppressionState == (int)swComponentSuppressionState_e.swComponentFullyLightweight)
                {
                    throw new InvalidOperationException(
                        "组件仍处于轻化状态，无法保证 BOM 完整：" +
                        BomComponentTreeCollector.GetComponentLabel(component));
                }

                var model = BomComponentTreeCollector.GetModel(component);
                if (model == null)
                {
                    throw new InvalidOperationException(
                        "组件模型无法读取：" + BomComponentTreeCollector.GetComponentLabel(component));
                }

                if (model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    ValidateResolvedTree(component, depth + 1);
                }
            }
        }

        private static string ResolveTemplatePath()
        {
            var addinDirectory = SolidWorksDriver.RegDllPath();
            if (string.IsNullOrWhiteSpace(addinDirectory))
            {
                throw new FileNotFoundException(
                    "无法定位插件目录，无法找到 BOM 模板：" + TemplateFileName);
            }

            var templatePath = Path.Combine(addinDirectory, "Settings", TemplateFileName);
            if (!File.Exists(templatePath))
            {
                throw new FileNotFoundException(
                    "未找到 BOM 模板：" + templatePath + "。请检查程序 Settings 目录。",
                    templatePath);
            }

            return templatePath;
        }

        private sealed class BomLineItemAggregator
        {
            private readonly BomTemplateDefinition _template;
            private readonly string _modelSuffix;
            private readonly BomCustomPropertyReader _propertyReader;
            private readonly BomExportResult _result;
            private readonly Dictionary<string, BomLineItem> _itemsByKey =
                new Dictionary<string, BomLineItem>(StringComparer.OrdinalIgnoreCase);
            private readonly List<BomLineItem> _orderedItems = new List<BomLineItem>();
            private int _nextOrder;

            internal BomLineItemAggregator(
                BomTemplateDefinition template,
                string modelSuffix,
                BomCustomPropertyReader propertyReader,
                BomExportResult result)
            {
                _template = template;
                _modelSuffix = modelSuffix ?? string.Empty;
                _propertyReader = propertyReader;
                _result = result;
            }

            internal bool AcceptComponent(Component2 component)
            {
                var snapshot = _propertyReader.Read(component);
                if (BomExportExclusionRules.ShouldExclude(snapshot))
                {
                    return false;
                }

                _propertyReader.ReportMissingProperties(snapshot);
                _result.ComponentOccurrenceCount++;
                var key = BomMergeKeyBuilder.Build(
                    snapshot.DrawingNumber,
                    snapshot.Version,
                    snapshot.ModelPath,
                    snapshot.ReferencedConfiguration,
                    snapshot.ComponentLabel);

                if (string.IsNullOrWhiteSpace(snapshot.DrawingNumber))
                {
                    _propertyReader.AddWarning(
                        "图号缺失，已按模型路径和引用配置合并：" + snapshot.ComponentLabel);
                }

                if (!_itemsByKey.TryGetValue(key, out var existing))
                {
                    snapshot.Quantity = 1;
                    snapshot.FirstOrder = _nextOrder++;
                    _itemsByKey.Add(key, snapshot);
                    _orderedItems.Add(snapshot);
                    return true;
                }

                EnsureCompatible(existing, snapshot);
                checked
                {
                    existing.Quantity++;
                }

                return true;
            }

            internal IList<BomLineItem> GetItems()
            {
                return _orderedItems
                    .OrderBy(item => item.FirstOrder)
                    .ToList();
            }

            private void EnsureCompatible(BomLineItem existing, BomLineItem candidate)
            {
                foreach (var propertyName in ItemPropertyNames)
                {
                    var existingValue = existing.GetPropertyValue(propertyName);
                    var candidateValue = candidate.GetPropertyValue(propertyName);
                    if (string.Equals(
                            NormalizeValue(existingValue),
                            NormalizeValue(candidateValue),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    throw new InvalidOperationException(
                        "同一模型内的相同清单项存在属性冲突。模板=" +
                        _template.TemplateDrawingNumber +
                        "，模型后缀=" + DisplaySuffix(_modelSuffix) +
                        "，图号=" + existing.DrawingNumber +
                        "，版本=" + existing.Version +
                        "，冲突字段=" + propertyName +
                        "，值1=" + existingValue +
                        "，值2=" + candidateValue +
                        "，模型1=" + existing.ModelPath +
                        "，模型2=" + candidate.ModelPath);
                }
            }

            private static string NormalizeValue(string value)
            {
                return string.IsNullOrWhiteSpace(value)
                    ? string.Empty
                    : value.Trim().Replace('　', ' ');
            }
        }
    }

    internal enum BomHostKind
    {
        Frame,
        Base
    }

    internal sealed class BomTemplateDefinition
    {
        internal BomTemplateDefinition(
            string templateDrawingNumber,
            string categoryName,
            BomHostKind kind,
            int sortOrder)
        {
            TemplateDrawingNumber = templateDrawingNumber;
            CategoryName = categoryName;
            Kind = kind;
            SortOrder = sortOrder;
        }

        internal string TemplateDrawingNumber { get; }

        internal string CategoryName { get; }

        internal BomHostKind Kind { get; }

        internal int SortOrder { get; }

        internal string SheetName => TemplateDrawingNumber + (Kind == BomHostKind.Frame ? " 机架" : " 基座");

        internal string ProjectDirectoryName => Kind == BomHostKind.Frame ? "基架" : "基座";
    }

    internal static class BomTemplateCatalog
    {
        private static readonly BomTemplateDefinition[] Definitions =
        {
            new BomTemplateDefinition("5GX.355.024", "常规基架", BomHostKind.Frame, 0),
            new BomTemplateDefinition("5GX.355.026", "大基架1", BomHostKind.Frame, 1),
            new BomTemplateDefinition("5GX.355.027", "大基架2", BomHostKind.Frame, 2),
            new BomTemplateDefinition("5GX.355.028", "大偏移基架", BomHostKind.Frame, 3),
            new BomTemplateDefinition("5GX.355.029", "侧对侧换相基架", BomHostKind.Frame, 4),
            new BomTemplateDefinition("5GX.355.032", "小偏移基架", BomHostKind.Frame, 5),
            new BomTemplateDefinition("5GX.355.020", "普通基座", BomHostKind.Base, 100),
            new BomTemplateDefinition("5GX.355.021", "增高基座", BomHostKind.Base, 101),
            new BomTemplateDefinition("5GX.355.030", "侧对侧基座1", BomHostKind.Base, 102),
            new BomTemplateDefinition("5GX.355.031", "侧对侧基座2", BomHostKind.Base, 103)
        };

        internal static BomTemplateDefinition FindByAssemblyFileName(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath) ||
                !string.Equals(Path.GetExtension(modelPath), ".SLDASM", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var fileName = Path.GetFileNameWithoutExtension(modelPath);
            return Definitions.FirstOrDefault(definition =>
                HasDrawingNumberPrefix(fileName, definition.TemplateDrawingNumber));
        }

        private static bool HasDrawingNumberPrefix(string fileName, string prefix)
        {
            if (string.IsNullOrWhiteSpace(fileName) ||
                !fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return fileName.Length == prefix.Length || fileName[prefix.Length] == '.';
        }
    }

    internal static class BomModelSuffixResolver
    {
        private static readonly Regex SuffixPattern = new Regex(
            @"^[A-Za-z0-9]+\.\d{3,4}$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        internal static bool TryExtract(
            string assemblyBaseName,
            string templateDrawingNumber,
            out string suffix)
        {
            suffix = string.Empty;
            if (string.IsNullOrWhiteSpace(assemblyBaseName) ||
                string.IsNullOrWhiteSpace(templateDrawingNumber))
            {
                return false;
            }

            var normalizedName = assemblyBaseName.Trim();
            var normalizedTemplate = templateDrawingNumber.Trim();
            if (string.Equals(normalizedName, normalizedTemplate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var prefix = normalizedTemplate + ".";
            if (!normalizedName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var candidate = normalizedName.Substring(prefix.Length).Trim().ToUpperInvariant();
            if (!SuffixPattern.IsMatch(candidate))
            {
                return false;
            }

            suffix = candidate;
            return true;
        }
    }

    internal sealed class BomHostAssembly
    {
        internal Component2 Component { get; set; }

        internal BomTemplateDefinition Template { get; set; }

        internal string ModelPath { get; set; }

        internal string ModelSuffix { get; set; }

        internal string ModelGroupKey { get; set; }

        internal int GlobalOrder { get; set; }
    }

    internal static class BomHostAssemblyCollector
    {
        internal static IList<BomHostAssembly> Collect(ModelDoc2 assemblyModel, string projectPath)
        {
            if (assemblyModel == null)
            {
                throw new ArgumentNullException(nameof(assemblyModel));
            }

            if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
            {
                throw new DirectoryNotFoundException("无法确定总装配体所在项目目录：" + projectPath);
            }

            var root = assemblyModel.ConfigurationManager?.ActiveConfiguration?.GetRootComponent3(true);
            if (root == null)
            {
                throw new InvalidOperationException("无法读取总装配体活动配置的根组件。");
            }

            var hosts = new List<BomHostAssembly>();
            var order = 0;
            foreach (var child in BomComponentTreeCollector.GetChildrenInFeatureTreeOrder(root))
            {
                TraverseGlobal(child, projectPath, 1, ref order, hosts);
            }

            return hosts;
        }

        private static void TraverseGlobal(
            Component2 component,
            string projectPath,
            int depth,
            ref int order,
            ICollection<BomHostAssembly> hosts)
        {
            if (depth > 128)
            {
                throw new InvalidOperationException("总装配体层级超过允许的最大深度。");
            }

            if (BomComponentTreeCollector.ShouldSkipBranch(component))
            {
                return;
            }

            var model = BomComponentTreeCollector.GetModel(component);
            if (model == null)
            {
                throw new InvalidOperationException(
                    "组件模型无法读取：" + BomComponentTreeCollector.GetComponentLabel(component));
            }

            var currentOrder = order++;
            if (model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                var modelPath = BomComponentTreeCollector.GetModelPath(component, model);
                var template = BomTemplateCatalog.FindByAssemblyFileName(modelPath);
                if (template != null)
                {
                    hosts.Add(CreateHost(component, modelPath, template, projectPath, currentOrder));
                    return;
                }

                foreach (var child in BomComponentTreeCollector.GetChildrenInFeatureTreeOrder(component))
                {
                    TraverseGlobal(child, projectPath, depth + 1, ref order, hosts);
                }
            }
        }

        private static BomHostAssembly CreateHost(
            Component2 component,
            string modelPath,
            BomTemplateDefinition template,
            string projectPath,
            int globalOrder)
        {
            var rdType = component.GetConfiguredRdSystemPartType();
            var expectedRdType = template.Kind == BomHostKind.Frame
                ? SystemPartType_e.Frame
                : SystemPartType_e.Base;
            if (rdType != expectedRdType)
            {
                throw new InvalidOperationException(
                    "模板宿主的 RD类型 与模板类别不一致：组件=" +
                    BomComponentTreeCollector.GetComponentLabel(component) +
                    "，模板=" + template.TemplateDrawingNumber +
                    "，期望=" + expectedRdType +
                    "，实际=" + rdType);
            }

            var expectedDirectory = Path.Combine(projectPath, template.ProjectDirectoryName);
            if (!IsPathUnderDirectory(expectedDirectory, modelPath))
            {
                throw new InvalidOperationException(
                    template.ProjectDirectoryName + "模板宿主不在项目" +
                    template.ProjectDirectoryName + "目录内：" + modelPath);
            }

            var assemblyBaseName = Path.GetFileNameWithoutExtension(modelPath);
            if (!BomModelSuffixResolver.TryExtract(
                    assemblyBaseName,
                    template.TemplateDrawingNumber,
                    out var suffix))
            {
                throw new InvalidOperationException(
                    "无法从模板宿主名称提取模型图号后缀：模板=" +
                    template.TemplateDrawingNumber + "，模型=" + assemblyBaseName);
            }

            var modelGroupKey = string.IsNullOrWhiteSpace(suffix)
                ? "__DEFAULT_PATH__:" + NormalizePath(modelPath)
                : "__SUFFIX__:" + suffix;
            return new BomHostAssembly
            {
                Component = component,
                Template = template,
                ModelPath = modelPath,
                ModelSuffix = suffix,
                ModelGroupKey = modelGroupKey,
                GlobalOrder = globalOrder
            };
        }

        private static bool IsPathUnderDirectory(string directoryPath, string candidatePath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || string.IsNullOrWhiteSpace(candidatePath))
            {
                return false;
            }

            var root = Path.GetFullPath(directoryPath).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(candidatePath);
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path).Trim().ToUpperInvariant();
        }
    }

    internal static class BomComponentTreeCollector
    {
        internal static void Traverse(
            Component2 component,
            int depth,
            Func<Component2, bool> acceptComponent)
        {
            if (component == null || acceptComponent == null)
            {
                return;
            }

            if (depth > 128)
            {
                throw new InvalidOperationException("基架/基座组件层级超过允许的最大深度。");
            }

            if (ShouldSkipBranch(component))
            {
                return;
            }

            if (IsInsulatorBranch(component))
            {
                return;
            }

            var model = GetModel(component);
            if (model == null)
            {
                throw new InvalidOperationException("组件模型无法读取：" + GetComponentLabel(component));
            }

            if (!acceptComponent(component))
            {
                return;
            }

            if (model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                return;
            }

            foreach (var child in GetChildrenInFeatureTreeOrder(component))
            {
                Traverse(child, depth + 1, acceptComponent);
            }
        }

        internal static IList<Component2> GetChildrenInFeatureTreeOrder(Component2 parent)
        {
            if (parent == null)
            {
                return new List<Component2>();
            }

            try
            {
                return (parent.GetChildren() as object[])?
                    .OfType<Component2>()
                    .ToList() ?? new List<Component2>();
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "读取组件特征树失败：" + GetComponentLabel(parent) +
                    "。" + exception.Message,
                    exception);
            }
        }

        internal static bool ShouldSkipBranch(Component2 component)
        {
            if (component == null || IsSuppressed(component))
            {
                return true;
            }

            try
            {
                if (component.IsEnvelope())
                {
                    return true;
                }
            }
            catch
            {
                // 某些旧模型不支持读取封套状态，继续按普通组件处理。
            }

            try
            {
                return component.ExcludeFromBOM;
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsInsulatorBranch(Component2 component)
        {
            if (component == null)
            {
                return false;
            }

            try
            {
                if (component.GetConfiguredRdSystemPartType() == SystemPartType_e.Insulator)
                {
                    return true;
                }
            }
            catch
            {
                // 旧模型可能没有 RD 类型，继续按模型路径识别。
            }

            return BomExportExclusionRules.IsInsulatorModelPath(GetModelPath(component));
        }

        internal static bool IsSuppressed(Component2 component)
        {
            try
            {
                return component == null || component.IsSuppressed();
            }
            catch
            {
                return true;
            }
        }

        internal static int GetSuppressionState(Component2 component)
        {
            try
            {
                return component.GetSuppression();
            }
            catch
            {
                return (int)swComponentSuppressionState_e.swComponentSuppressed;
            }
        }

        internal static ModelDoc2 GetModel(Component2 component)
        {
            try
            {
                return component?.IGetModelDoc() as ModelDoc2;
            }
            catch
            {
                return null;
            }
        }

        internal static string GetModelPath(Component2 component, ModelDoc2 model = null)
        {
            try
            {
                var path = component?.GetPathName();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }
            catch
            {
                // 尝试从模型文档读取路径。
            }

            try
            {
                return model?.GetPathName() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        internal static string GetComponentLabel(Component2 component)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(component?.Name2))
                {
                    return component.Name2;
                }
            }
            catch
            {
                // 使用磁盘路径作为后备显示名。
            }

            var path = GetModelPath(component);
            return string.IsNullOrWhiteSpace(path) ? "（未知组件）" : path;
        }
    }

    internal enum BomDocumentKind
    {
        Assembly,
        Part
    }

    internal readonly struct BomEvaluatedPropertyValue
    {
        internal static readonly BomEvaluatedPropertyValue Missing =
            new BomEvaluatedPropertyValue(false, string.Empty);

        internal BomEvaluatedPropertyValue(bool exists, string evaluatedValue)
        {
            Exists = exists;
            EvaluatedValue = evaluatedValue ?? string.Empty;
        }

        internal bool Exists { get; }

        internal string EvaluatedValue { get; }
    }

    internal static class BomPropertyMapping
    {
        private static readonly Regex StandardDrawingNumberPattern = new Regex(
            @"^\s*([A-Za-z0-9]+)\s*\.\s*([A-Za-z0-9]+)\s*\.\s*([A-Za-z0-9]+)",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        internal static BomDocumentKind ResolveDocumentKind(int documentType)
        {
            if (documentType == (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                return BomDocumentKind.Assembly;
            }

            if (documentType == (int)swDocumentTypes_e.swDocPART)
            {
                return BomDocumentKind.Part;
            }

            throw new InvalidOperationException(
                "BOM 仅支持装配体和零件文档，当前文档类型=" + documentType);
        }

        internal static string ResolveSourcePropertyName(
            string outputPropertyName,
            BomDocumentKind documentKind)
        {
            switch (outputPropertyName)
            {
                case "零部件类型":
                    return documentKind == BomDocumentKind.Assembly ? "部件类型" : "零件类型";
                case "净重":
                    return documentKind == BomDocumentKind.Assembly
                        ? "重量(kg)仅供参考"
                        : "净重(kg)";
                case "材质":
                    return documentKind == BomDocumentKind.Part ? "中文材质" : null;
                case "展开长":
                    return documentKind == BomDocumentKind.Part ? "展开长(mm)" : null;
                case "展开宽":
                    return documentKind == BomDocumentKind.Part ? "展开宽(mm)" : null;
                case "厚度":
                    return documentKind == BomDocumentKind.Part ? "板厚" : null;
                case "图纸文件名称":
                case "对应标准图号":
                    return null;
                default:
                    return outputPropertyName;
            }
        }

        internal static bool IsApplicableOutputProperty(
            string outputPropertyName,
            BomDocumentKind documentKind)
        {
            return outputPropertyName == "图纸文件名称" ||
                   outputPropertyName == "对应标准图号" ||
                   !string.IsNullOrWhiteSpace(
                       ResolveSourcePropertyName(outputPropertyName, documentKind));
        }

        internal static string GetStandardDrawingNumber(string drawingNumber)
        {
            if (string.IsNullOrWhiteSpace(drawingNumber))
            {
                return string.Empty;
            }

            var match = StandardDrawingNumberPattern.Match(drawingNumber);
            if (!match.Success)
            {
                return string.Empty;
            }

            return string.Join(
                ".",
                match.Groups[1].Value.Trim(),
                match.Groups[2].Value.Trim(),
                match.Groups[3].Value.Trim());
        }
    }

    internal sealed class BomCustomPropertyReader
    {
        private readonly BomExportResult _result;
        private readonly Dictionary<string, BomLineItem> _cache =
            new Dictionary<string, BomLineItem>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _warnings =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal BomCustomPropertyReader(BomExportResult result)
        {
            _result = result ?? throw new ArgumentNullException(nameof(result));
        }

        internal BomLineItem Read(Component2 component)
        {
            var model = BomComponentTreeCollector.GetModel(component);
            if (model == null)
            {
                throw new InvalidOperationException(
                    "组件模型无法读取：" + BomComponentTreeCollector.GetComponentLabel(component));
            }

            var referencedConfiguration = GetReferencedConfiguration(component, model);
            var modelPath = BomComponentTreeCollector.GetModelPath(component, model);
            var cacheKey = BuildCacheKey(modelPath, referencedConfiguration, component);
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                return cached.CopyForOccurrence(BomComponentTreeCollector.GetComponentLabel(component));
            }

            var item = new BomLineItem
            {
                ModelPath = modelPath,
                ReferencedConfiguration = referencedConfiguration,
                ComponentLabel = BomComponentTreeCollector.GetComponentLabel(component),
                DocumentKind = BomPropertyMapping.ResolveDocumentKind(model.GetType())
            };

            foreach (var propertyName in BomExportDriver.ItemPropertyNames)
            {
                var sourcePropertyName = BomPropertyMapping.ResolveSourcePropertyName(
                    propertyName,
                    item.DocumentKind);
                var value = string.IsNullOrWhiteSpace(sourcePropertyName)
                    ? string.Empty
                    : ReadProperty(model, referencedConfiguration, sourcePropertyName);
                item.SetPropertyValue(propertyName, value);
            }

            var standardDrawingNumber = BomPropertyMapping.GetStandardDrawingNumber(
                item.DrawingNumber);
            item.SetPropertyValue("图纸文件名称", standardDrawingNumber);
            item.SetPropertyValue("对应标准图号", standardDrawingNumber);

            _cache[cacheKey] = item.CopyForOccurrence(item.ComponentLabel);
            return item;
        }

        internal void ReportMissingProperties(BomLineItem item)
        {
            if (item == null)
            {
                return;
            }

            foreach (var propertyName in BomExportDriver.ItemPropertyNames)
            {
                if (BomPropertyMapping.IsApplicableOutputProperty(
                        propertyName,
                        item.DocumentKind) &&
                    string.IsNullOrWhiteSpace(item.GetPropertyValue(propertyName)))
                {
                    AddWarning(item.ComponentLabel + "：自定义属性“" + propertyName + "”为空。");
                }
            }
        }

        internal void AddWarning(string warning)
        {
            if (string.IsNullOrWhiteSpace(warning) || !_warnings.Add(warning.Trim()))
            {
                return;
            }

            _result.Warnings.Add(warning.Trim());
        }

        private static string ReadProperty(
            ModelDoc2 model,
            string referencedConfiguration,
            string propertyName)
        {
            var configuredValue = ReadProperty(
                model.Extension?.CustomPropertyManager[referencedConfiguration ?? string.Empty],
                propertyName);
            if (configuredValue.Exists)
            {
                return configuredValue.EvaluatedValue;
            }

            return ReadProperty(
                model.Extension?.CustomPropertyManager[string.Empty],
                propertyName).EvaluatedValue;
        }

        private static BomEvaluatedPropertyValue ReadProperty(
            CustomPropertyManager propertyManager,
            string propertyName)
        {
            if (propertyManager == null)
            {
                return BomEvaluatedPropertyValue.Missing;
            }

            string rawValue;
            string resolvedValue;
            try
            {
                bool wasResolved;
                bool linkToProperty;
                var result = propertyManager.Get6(
                    propertyName,
                    false,
                    out rawValue,
                    out resolvedValue,
                    out wasResolved,
                    out linkToProperty);
                if (result == (int)swCustomInfoGetResult_e.swCustomInfoGetResult_NotPresent)
                {
                    return BomEvaluatedPropertyValue.Missing;
                }
            }
            catch
            {
                propertyManager.Get2(propertyName, out rawValue, out resolvedValue);
                if (rawValue == null && resolvedValue == null)
                {
                    return BomEvaluatedPropertyValue.Missing;
                }
            }

            return new BomEvaluatedPropertyValue(
                true,
                SelectEvaluatedValueForExport(rawValue, resolvedValue));
        }

        internal static string SelectEvaluatedValueForExport(
            string rawValue,
            string resolvedValue)
        {
            // BOM 严格使用 SolidWorks 评估值；即使原始表达式非空，
            // 评估结果为空时也不回退导出原始表达式。
            return (resolvedValue ?? string.Empty).Trim();
        }

        private static string GetReferencedConfiguration(Component2 component, ModelDoc2 model)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(component?.ReferencedConfiguration))
                {
                    return component.ReferencedConfiguration.Trim();
                }
            }
            catch
            {
                // 回退到活动配置。
            }

            try
            {
                return model.ConfigurationManager?.ActiveConfiguration?.Name ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string BuildCacheKey(
            string modelPath,
            string referencedConfiguration,
            Component2 component)
        {
            if (!string.IsNullOrWhiteSpace(modelPath))
            {
                return Path.GetFullPath(modelPath).Trim().ToUpperInvariant() + "|" +
                       (referencedConfiguration ?? string.Empty).Trim().ToUpperInvariant();
            }

            return "__VIRTUAL__|" + BomComponentTreeCollector.GetComponentLabel(component) + "|" +
                   (referencedConfiguration ?? string.Empty).Trim().ToUpperInvariant();
        }
    }

    internal static class BomExportExclusionRules
    {
        private const string DrawingNamePlaceholder = "图样名称";
        private const string InsulatorKeyword = "绝缘子";

        internal static bool ShouldExclude(BomLineItem item)
        {
            if (item == null)
            {
                return false;
            }

            if (string.Equals(
                    Normalize(item.GetPropertyValue("名称")),
                    DrawingNamePlaceholder,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return ContainsInsulatorKeyword(item.GetPropertyValue("名称")) ||
                   ContainsInsulatorKeyword(item.GetPropertyValue("零部件类型")) ||
                   ContainsInsulatorKeyword(item.GetPropertyValue("规格型号"));
        }

        internal static bool IsInsulatorModelPath(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                return false;
            }

            var segments = modelPath.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            return segments.Any(ContainsInsulatorKeyword);
        }

        private static bool ContainsInsulatorKeyword(string value)
        {
            return Normalize(value).IndexOf(
                InsulatorKeyword,
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().Replace('　', ' ');
        }
    }

    internal static class BomMergeKeyBuilder
    {
        internal static string Build(
            string drawingNumber,
            string version,
            string modelPath,
            string referencedConfiguration,
            string componentLabel)
        {
            var normalizedDrawingNumber = Normalize(drawingNumber);
            if (!string.IsNullOrWhiteSpace(normalizedDrawingNumber))
            {
                return "__DRAWING__|" + normalizedDrawingNumber + "|" + Normalize(version);
            }

            if (!string.IsNullOrWhiteSpace(modelPath))
            {
                return "__PATH__|" + Path.GetFullPath(modelPath).Trim().ToUpperInvariant() + "|" +
                       Normalize(referencedConfiguration);
            }

            return "__COMPONENT__|" + Normalize(componentLabel) + "|" +
                   Normalize(referencedConfiguration);
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : Regex.Replace(value.Trim().Replace('　', ' '), @"\s+", " ").ToUpperInvariant();
        }
    }

    internal sealed class BomLineItem
    {
        private readonly Dictionary<string, string> _properties =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal string DrawingNumber => GetPropertyValue("图号");

        internal string Version => GetPropertyValue("版本");

        internal string ModelPath { get; set; }

        internal string ReferencedConfiguration { get; set; }

        internal string ComponentLabel { get; set; }

        internal BomDocumentKind DocumentKind { get; set; }

        internal int Quantity { get; set; }

        internal int FirstOrder { get; set; }

        internal string GetPropertyValue(string propertyName)
        {
            return _properties.TryGetValue(propertyName, out var value)
                ? value ?? string.Empty
                : string.Empty;
        }

        internal void SetPropertyValue(string propertyName, string value)
        {
            _properties[propertyName] = value ?? string.Empty;
        }

        internal BomLineItem CopyForOccurrence(string componentLabel)
        {
            var copy = new BomLineItem
            {
                ModelPath = ModelPath,
                ReferencedConfiguration = ReferencedConfiguration,
                ComponentLabel = componentLabel,
                DocumentKind = DocumentKind,
                Quantity = Quantity,
                FirstOrder = FirstOrder
            };
            foreach (var property in _properties)
            {
                copy._properties[property.Key] = property.Value;
            }

            return copy;
        }

        internal object[] ToExcelRow(int serialNumber)
        {
            return new object[]
            {
                serialNumber,
                DrawingNumber,
                Version,
                Quantity,
                GetPropertyValue("名称"),
                GetPropertyValue("零部件类型"),
                GetPropertyValue("规格型号"),
                GetPropertyValue("材质"),
                GetPropertyValue("净重"),
                GetPropertyValue("毛重"),
                GetPropertyValue("展开长"),
                GetPropertyValue("展开宽"),
                GetPropertyValue("厚度"),
                GetPropertyValue("图纸文件名称"),
                GetPropertyValue("对应标准图号")
            };
        }
    }

    internal sealed class BomModelGroup
    {
        internal BomModelGroup(string modelSuffix, string groupKey, int firstOrder)
        {
            ModelSuffix = modelSuffix ?? string.Empty;
            GroupKey = groupKey;
            FirstOrder = firstOrder;
        }

        internal string ModelSuffix { get; }

        internal string GroupKey { get; }

        internal int FirstOrder { get; }

        internal IList<BomLineItem> Items { get; } = new List<BomLineItem>();
    }

    internal sealed class BomSheetGroup
    {
        internal BomSheetGroup(BomTemplateDefinition template)
        {
            Template = template;
        }

        internal BomTemplateDefinition Template { get; }

        internal IList<BomModelGroup> Models { get; } = new List<BomModelGroup>();
    }

    internal static class BomWorkbookWriter
    {
        private const int HeaderRowIndex = 0;
        private const int DataStartRowIndex = 1;

        private static readonly string[] ExpectedHeaders =
        {
            "序号", "图号", "版本", "数量", "名称", "零部件类型", "规格型号", "材质",
            "净重", "毛重", "展开长", "展开宽", "厚度", "图纸文件名称", "对应标准图号"
        };

        internal static void Write(
            string templatePath,
            string outputPath,
            IList<BomSheetGroup> sheetGroups)
        {
            if (sheetGroups == null || sheetGroups.Count == 0)
            {
                throw new ArgumentException("BOM 子表数据不能为空。", nameof(sheetGroups));
            }

            var tempPath = Path.Combine(
                Path.GetDirectoryName(outputPath),
                "." + Path.GetFileNameWithoutExtension(outputPath) + "." +
                Guid.NewGuid().ToString("N") + ".tmp.xlsx");
            try
            {
                using (var input = new FileStream(
                    templatePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite))
                {
                    var workbook = new XSSFWorkbook(input);
                    try
                    {
                        var templateSheet = ValidateTemplate(workbook);
                        var originalSheetCount = workbook.NumberOfSheets;
                        var serialBySheet = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                        foreach (var sheetGroup in sheetGroups)
                        {
                            var clonedSheet = workbook.CloneSheet(workbook.GetSheetIndex(templateSheet));
                            var clonedIndex = workbook.GetSheetIndex(clonedSheet);
                            workbook.SetSheetName(clonedIndex, sheetGroup.Template.SheetName);
                            ClearDataRows(clonedSheet);
                            var bodyStyles = CreateBodyStyles(workbook, clonedSheet);

                            var serial = 1;
                            foreach (var model in sheetGroup.Models.OrderBy(item => item.FirstOrder))
                            {
                                foreach (var item in model.Items)
                                {
                                    WriteRow(
                                        clonedSheet,
                                        bodyStyles,
                                        DataStartRowIndex + serial - 1,
                                        item,
                                        serial);
                                    serial++;
                                }
                            }

                            var lastRowIndex = Math.Max(HeaderRowIndex, DataStartRowIndex + serial - 2);
                            clonedSheet.SetAutoFilter(new CellRangeAddress(
                                HeaderRowIndex,
                                lastRowIndex,
                                0,
                                ExpectedHeaders.Length - 1));
                            serialBySheet[sheetGroup.Template.SheetName] = serial - 1;
                        }

                        for (var index = originalSheetCount - 1; index >= 0; index--)
                        {
                            workbook.RemoveSheetAt(index);
                        }

                        workbook.SetActiveSheet(0);
                        workbook.SetSelectedTab(0);
                        using (var output = new FileStream(
                            tempPath,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None))
                        {
                            workbook.Write(output);
                            output.Flush(true);
                        }

                        ValidateSavedWorkbook(tempPath, sheetGroups, serialBySheet);
                    }
                    finally
                    {
                        workbook.Close();
                    }
                }

                ReplaceOutput(tempPath, outputPath);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        private static ISheet ValidateTemplate(IWorkbook workbook)
        {
            if (workbook == null || workbook.NumberOfSheets == 0)
            {
                throw new InvalidDataException("母线桥标准清单模板没有工作表。");
            }

            var sheet = workbook.GetSheetAt(0);
            var headerRow = sheet.GetRow(HeaderRowIndex);
            if (headerRow == null)
            {
                throw new InvalidDataException("母线桥标准清单模板缺少列头行。");
            }

            for (var column = 0; column < ExpectedHeaders.Length; column++)
            {
                var actual = GetCellText(headerRow.GetCell(column));
                if (!string.Equals(actual, ExpectedHeaders[column], StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "母线桥标准清单模板列头不正确：列=" +
                        (column + 1).ToString(CultureInfo.InvariantCulture) +
                        "，期望=" + ExpectedHeaders[column] +
                        "，实际=" + actual);
                }
            }

            return sheet;
        }

        private static void ClearDataRows(ISheet sheet)
        {
            for (var rowIndex = sheet.LastRowNum; rowIndex >= DataStartRowIndex; rowIndex--)
            {
                var row = sheet.GetRow(rowIndex);
                if (row != null)
                {
                    sheet.RemoveRow(row);
                }
            }
        }

        private static void WriteRow(
            ISheet sheet,
            ICellStyle[] bodyStyles,
            int rowIndex,
            BomLineItem item,
            int serialNumber)
        {
            var row = sheet.CreateRow(rowIndex);
            var headerRow = sheet.GetRow(HeaderRowIndex);
            if (headerRow != null && headerRow.Height > 0)
            {
                row.Height = headerRow.Height;
            }

            var values = item.ToExcelRow(serialNumber);
            for (var column = 0; column < values.Length; column++)
            {
                var cell = row.CreateCell(column);
                cell.CellStyle = bodyStyles[column];
                if (values[column] is int integerValue)
                {
                    cell.SetCellValue(integerValue);
                }
                else
                {
                    cell.SetCellValue(values[column]?.ToString() ?? string.Empty);
                }
            }
        }

        private static ICellStyle[] CreateBodyStyles(IWorkbook workbook, ISheet sheet)
        {
            var styles = new ICellStyle[ExpectedHeaders.Length];
            var headerRow = sheet.GetRow(HeaderRowIndex);
            for (var column = 0; column < styles.Length; column++)
            {
                styles[column] = CreateBodyStyle(workbook, headerRow?.GetCell(column)?.CellStyle);
            }

            return styles;
        }

        private static ICellStyle CreateBodyStyle(IWorkbook workbook, ICellStyle headerStyle)
        {
            var style = workbook.CreateCellStyle();
            if (headerStyle != null)
            {
                style.CloneStyleFrom(headerStyle);
            }

            style.Alignment = HorizontalAlignment.Center;
            style.VerticalAlignment = VerticalAlignment.Center;
            style.WrapText = false;

            var font = workbook.CreateFont();
            if (headerStyle != null)
            {
                var sourceFont = workbook.GetFontAt(headerStyle.FontIndex);
                if (sourceFont != null)
                {
                    font.FontName = sourceFont.FontName;
                    font.FontHeight = sourceFont.FontHeight;
                    font.Charset = sourceFont.Charset;
                    font.Color = sourceFont.Color;
                    font.IsItalic = sourceFont.IsItalic;
                    font.IsStrikeout = sourceFont.IsStrikeout;
                    font.TypeOffset = sourceFont.TypeOffset;
                    font.Underline = sourceFont.Underline;
                }
            }

            font.IsBold = false;
            style.SetFont(font);
            return style;
        }

        private static void ValidateSavedWorkbook(
            string tempPath,
            IList<BomSheetGroup> sheetGroups,
            IDictionary<string, int> rowCountBySheet)
        {
            using (var input = new FileStream(
                tempPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                var workbook = new XSSFWorkbook(input);
                try
                {
                    if (workbook.NumberOfSheets != sheetGroups.Count)
                    {
                        throw new InvalidDataException("BOM 临时文件子表数量校验失败。");
                    }

                    foreach (var group in sheetGroups)
                    {
                        var sheet = workbook.GetSheet(group.Template.SheetName);
                        if (sheet == null)
                        {
                            throw new InvalidDataException(
                                "BOM 临时文件缺少子表：" + group.Template.SheetName);
                        }

                        var expectedRows = rowCountBySheet[group.Template.SheetName];
                        if (sheet.LastRowNum != expectedRows)
                        {
                            throw new InvalidDataException(
                                "BOM 临时文件数据行数校验失败：子表=" +
                                group.Template.SheetName +
                                "，期望=" + expectedRows +
                                "，实际=" + sheet.LastRowNum);
                        }

                        ValidateTemplateHeader(sheet);
                    }
                }
                finally
                {
                    workbook.Close();
                }
            }
        }

        private static void ValidateTemplateHeader(ISheet sheet)
        {
            var row = sheet.GetRow(HeaderRowIndex);
            for (var column = 0; column < ExpectedHeaders.Length; column++)
            {
                if (!string.Equals(
                        GetCellText(row?.GetCell(column)),
                        ExpectedHeaders[column],
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException("BOM 临时文件列头校验失败：" + sheet.SheetName);
                }
            }
        }

        private static string GetCellText(ICell cell)
        {
            if (cell == null)
            {
                return string.Empty;
            }

            return cell.CellType == CellType.String
                ? (cell.StringCellValue ?? string.Empty).Trim()
                : cell.ToString().Trim();
        }

        private static void ReplaceOutput(string tempPath, string outputPath)
        {
            try
            {
                if (File.Exists(outputPath))
                {
                    File.Replace(tempPath, outputPath, null, true);
                }
                else
                {
                    File.Move(tempPath, outputPath);
                }
            }
            catch (IOException exception)
            {
                throw new IOException(
                    "无法保存 BOM 文件，请确认目标文件未被 Excel 或 WPS 占用：" + outputPath,
                    exception);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 临时文件清理失败不覆盖原始导出异常。
            }
        }
    }
}
