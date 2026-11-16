using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 描述普通基座内一种侧封板的图号前缀和目标压缩状态。
    /// </summary>
    internal sealed class FaceToFaceBaseSidePanelRule
    {
        internal FaceToFaceBaseSidePanelRule(
            string drawingNumberPrefix,
            bool suppress)
        {
            DrawingNumberPrefix = drawingNumberPrefix;
            Suppress = suppress;
        }

        internal string DrawingNumberPrefix { get; }

        internal bool Suppress { get; }
    }

    /// <summary>
    /// 隔离基座驱动器所需的 SolidWorks 操作，便于在不启动 SolidWorks 时验证事务顺序。
    /// </summary>
    internal interface IFaceToFaceBaseModelOperations
    {
        void ApplyModelChangesAndSave(
            string assemblyPath,
            IReadOnlyDictionary<string, object> variables,
            bool? hasElevatedBase);

        void PackAndGoWithSuffix(
            string assemblyPath,
            string outputDirectory,
            string suffix);
    }

    internal interface IFaceToFaceBaseNewModelOperations
    {
        void ApplyNewModelChangesAndSave(string assemblyPath,
            IReadOnlyDictionary<string, object> variables, bool? hasElevatedBase);
    }

    /// <summary>
    /// 普通基座与增高基座均从模板 PackAndGo 到最终目录；默认参数的增高基座跳过驱动，
    /// 默认参数的普通基座仍切侧封板并重建。
    /// </summary>
    internal sealed class FaceToFaceBaseDriver : IFaceToFaceBaseDriver
    {
        private readonly IFaceToFaceBaseModelOperations _operations;

        internal FaceToFaceBaseDriver(IFaceToFaceBaseModelOperations operations)
        {
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        }

        public string PrepareModel(
            FaceToFaceBaseModelPlan plan,
            string sourceAssemblyPath,
            string outputDirectory,
            IDrawingNumberGenerator drawingNumbers)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            if (string.IsNullOrWhiteSpace(sourceAssemblyPath)
                || !File.Exists(sourceAssemblyPath))
            {
                throw new FileNotFoundException(
                    $"待驱动基座总装配体不存在：{sourceAssemblyPath}",
                    sourceAssemblyPath);
            }

            return PrepareModelFromTemplate(
                plan,
                sourceAssemblyPath,
                outputDirectory,
                drawingNumbers);
        }

        /// <summary>
        /// 先从模板 PackAndGo 到最终目录（非默认参数取号重命名），再按基座类型决定是否驱动。
        /// </summary>
        private string PrepareModelFromTemplate(
            FaceToFaceBaseModelPlan plan,
            string templateAssemblyPath,
            string outputDirectory,
            IDrawingNumberGenerator drawingNumbers)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new ArgumentException("基座驱动输出目录不能为空。", nameof(outputDirectory));
            }

            var nameAction = BaseModelNamingPolicy.Resolve(
                hasDrawingNumberSuffix: false,
                targetIsTemplateDefault: plan.IsTemplateDefault);
            var needsDrawingNumber =
                nameAction == BaseModelNameActionKind.AddDrawingNumber;
            string suffix;
            string drawingNumber = null;
            if (!needsDrawingNumber)
            {
                suffix = string.Empty;
            }
            else
            {
                if (drawingNumbers == null)
                {
                    throw new ArgumentNullException(nameof(drawingNumbers));
                }

                drawingNumber = drawingNumbers.PeekNextDrawingNumber();
                suffix = drawingNumbers.PeekNextFileNameSuffix();
            }

            _operations.PackAndGoWithSuffix(
                templateAssemblyPath,
                outputDirectory,
                suffix);

            var outputAssemblyPath = BuildIndependentAssemblyPath(
                templateAssemblyPath,
                outputDirectory,
                suffix);
            if (!File.Exists(outputAssemblyPath))
            {
                throw new FileNotFoundException(
                    $"基座 PackAndGo 后未找到总装配体：{outputAssemblyPath}",
                    outputAssemblyPath);
            }

            if (needsDrawingNumber)
            {
                drawingNumbers.CommitNext(drawingNumber);
            }

            var isBase = plan.Kind == FaceToFaceBaseModelKind.Base;
            // 默认参数的增高基座 PackAndGo 后即可使用；普通基座仍需切侧封板并重建。
            if (!plan.IsTemplateDefault || isBase)
            {
                var newModelOperations = _operations as IFaceToFaceBaseNewModelOperations;
                if (newModelOperations != null)
                    newModelOperations.ApplyNewModelChangesAndSave(outputAssemblyPath,
                        plan.IsTemplateDefault ? null : plan.GlobalVariables, isBase ? plan.HasElevatedBase : null);
                else
                    _operations.ApplyModelChangesAndSave(outputAssemblyPath,
                        plan.IsTemplateDefault ? null : plan.GlobalVariables, isBase ? plan.HasElevatedBase : null);
            }

            return outputAssemblyPath;
        }

        /// <summary>
        /// 对已有基座装配体写入全局变量和侧封板状态，不 PackAndGo、不取号。
        /// </summary>
        public void DriveExistingModel(
            string assemblyPath,
            FaceToFaceBaseModelPlan plan)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            {
                throw new FileNotFoundException(
                    $"待二次驱动的基座装配体不存在：{assemblyPath}",
                    assemblyPath);
            }

            var isBase = plan.Kind == FaceToFaceBaseModelKind.Base;
            _operations.ApplyModelChangesAndSave(
                assemblyPath,
                plan.GlobalVariables,
                isBase ? plan.HasElevatedBase : null);
        }

        internal static string BuildIndependentAssemblyPath(
            string sourceAssemblyPath,
            string outputDirectory,
            string suffix)
        {
            return Path.Combine(
                Path.GetFullPath(outputDirectory),
                Path.GetFileNameWithoutExtension(sourceAssemblyPath)
                    + suffix
                    + Path.GetExtension(sourceAssemblyPath));
        }
    }

    /// <summary>
    /// 基座驱动器的 SolidWorks 实现。
    /// </summary>
    internal sealed class SolidWorksFaceToFaceBaseModelOperations
        : IFaceToFaceBaseModelOperations, IFaceToFaceBaseNewModelOperations
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private const string NoElevatedBaseSidePanelDrawingNumber = "8GX.017.216";
        private const string ElevatedBaseSidePanelDrawingNumber = "8GX.017.217";
        internal const string SidePanelLengthVariableName = "板长";
        internal const string SidePanelWidthVariableName = "板宽";
        internal const double SidePanelEdgeOffset = 2;
        private readonly ISldWorks _swApp;
        private readonly string _keepOpenAssemblyFullPath;

        internal SolidWorksFaceToFaceBaseModelOperations(ISldWorks swApp)
            : this(swApp, null)
        {
        }

        internal SolidWorksFaceToFaceBaseModelOperations(
            ISldWorks swApp,
            string keepOpenAssemblyPath)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
            _keepOpenAssemblyFullPath = NormalizeKeepOpenAssemblyPath(keepOpenAssemblyPath);
        }

        private static string NormalizeKeepOpenAssemblyPath(string keepOpenAssemblyPath)
        {
            if (string.IsNullOrWhiteSpace(keepOpenAssemblyPath))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(keepOpenAssemblyPath);
            }
            catch
            {
                return keepOpenAssemblyPath;
            }
        }

        public void ApplyModelChangesAndSave(
            string assemblyPath,
            IReadOnlyDictionary<string, object> variables,
            bool? hasElevatedBase)
        {
            ApplyModelChangesAndSave(
                assemblyPath,
                variables,
                hasElevatedBase,
                keepModelOpenAfterSave: false);
        }

        internal void ApplyModelChangesAndSave(
            string assemblyPath,
            IReadOnlyDictionary<string, object> variables,
            bool? hasElevatedBase,
            bool keepModelOpenAfterSave,
            bool discardNewDocumentsOnFailure = false)
        {
            Dictionary<string, object> variableMap = null;
            if (variables != null)
            {
                variableMap = new Dictionary<string, object>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var pair in variables)
                {
                    variableMap[pair.Key] = pair.Value;
                }
            }

            var driver = new SolidWorksDriver(_swApp);
            var persistence = new SolidWorksDocumentPersistence(_swApp);
            var documentConflicts = new SolidWorksDocumentConflictGuard(_swApp);
            var previouslyOpenPaths = discardNewDocumentsOnFailure ? documentConflicts.CaptureOpenPaths() : null;
            IModelDoc2 model = null;
            var savedSuccessfully = false;
            Exception driveFailure = null;
            try
            {
                model = driver.OpenOrActivateDocument(assemblyPath);
                persistence.EnsureWritable(model, assemblyPath);
                if (hasElevatedBase.HasValue)
                {
                    // 普通基座根据是否配置增高基座切换两种侧封板；
                    // 只匹配当前普通基座的顶层子组件，避免误改更深层引用。
                    var uncompressedPanels = ApplySidePanelSuppression(
                        model,
                        hasElevatedBase.Value,
                        assemblyPath);
                    try
                    {
                        DriveUncompressedSidePanels(driver, model, uncompressedPanels, variableMap, assemblyPath);
                    }
                    finally
                    {
                        foreach (var panel in uncompressedPanels) SolidWorksFileHelper.ReleaseComReference(panel);
                    }
                }

                if (variableMap != null)
                {
                    // 六个全局变量作为一个整体写入，避免部分参数沿用模板旧值。
                    // 六个基座参数缺一不可；禁止模板不匹配时静默跳过并继续改名。
                    driver.ApplyGlobalVariablesRequired(
                        model,
                        variableMap,
                        rebuildAfterChange: false);
                }

                FaceToFaceBaseOperationGuard.EnsureRebuildSucceeded(
                    model.EditRebuild3(),
                    "基座模型参数驱动",
                    assemblyPath);

                var directory = Path.GetDirectoryName(model.GetPathName());
                var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(_keepOpenAssemblyFullPath) && SolidWorksBaseBridgeAngleDriver.IsOrdinaryBasePath(assemblyPath))
                    references.UnionWith(SolidWorksParentDimensionDriver.ProjectReferences(model, Path.GetDirectoryName(_keepOpenAssemblyFullPath)));
                new SolidWorksParentDimensionDriver(_swApp, path => BridgeGenerationFileBackup.Under(directory, path) ||
                    references.Contains(Path.GetFullPath(path))).Synchronize(model);

                persistence.SaveModel(model, assemblyPath);
                savedSuccessfully = true;
            }
            catch (Exception exception)
            {
                driveFailure = exception;
                Logger.Error(exception, "基座驱动或保存失败：{0}", assemblyPath);
                throw;
            }
            finally
            {
                // 二次驱动失败交给既有事务恢复；只有明确的新建模型可以在本阶段丢弃修改。
                CloseModelAfterDrive(model, assemblyPath, !savedSuccessfully || keepModelOpenAfterSave, driver);
                if (driveFailure != null && discardNewDocumentsOnFailure)
                    TryDiscardNewDocuments(documentConflicts, assemblyPath, previouslyOpenPaths, driveFailure);
            }
        }

        private void CloseModelAfterDrive(IModelDoc2 model, string assemblyPath, bool leaveModelOpen,
            SolidWorksDriver driver)
        {
            SolidWorksFileHelper.ReleaseComReference(model);

            var directory = Path.GetDirectoryName(assemblyPath);
            if (leaveModelOpen || string.IsNullOrWhiteSpace(directory)) return;
            IReadOnlyCollection<string> excludedPaths = string.IsNullOrWhiteSpace(_keepOpenAssemblyFullPath)
                ? null : new[] { _keepOpenAssemblyFullPath };
            driver.CloseOpenDocumentsUnderDirectory(directory, excludedPaths);
        }

        private void TryDiscardNewDocuments(SolidWorksDocumentConflictGuard documentConflicts,
            string assemblyPath, IReadOnlyCollection<string> previouslyOpenPaths, Exception driveFailure)
        {
            try
            {
                documentConflicts.DiscardNewDocuments(Path.GetDirectoryName(assemblyPath), previouslyOpenPaths);
            }
            catch (Exception cleanupException)
            {
                Logger.Error(cleanupException, "基座新建文档回滚关闭失败：{0}", assemblyPath);
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Error("基座新建文档回滚关闭失败", cleanupException, assemblyPath);
                driveFailure.Data["BaseDocumentCleanupWarning"] = cleanupException.ToString();
            }
        }

        public void ApplyNewModelChangesAndSave(string assemblyPath,
            IReadOnlyDictionary<string, object> variables, bool? hasElevatedBase)
        {
            ApplyModelChangesAndSave(assemblyPath, variables, hasElevatedBase,
                keepModelOpenAfterSave: false, discardNewDocumentsOnFailure: true);
        }

        /// <summary>
        /// 根据是否配置增高基座，返回两种侧封板的目标压缩状态。
        /// </summary>
        internal static IReadOnlyList<FaceToFaceBaseSidePanelRule> BuildSidePanelRules(
            bool hasElevatedBase)
        {
            return new[]
            {
                new FaceToFaceBaseSidePanelRule(
                    NoElevatedBaseSidePanelDrawingNumber,
                    suppress: hasElevatedBase),
                new FaceToFaceBaseSidePanelRule(
                    ElevatedBaseSidePanelDrawingNumber,
                    suppress: !hasElevatedBase)
            };
        }

        /// <summary>
        /// 仅按图号前缀匹配组件名称或组件文件名，允许实例号和图号后缀。
        /// </summary>
        internal static bool SidePanelIdentifierMatches(
            string componentIdentifier,
            string drawingNumberPrefix)
        {
            if (string.IsNullOrWhiteSpace(componentIdentifier)
                || string.IsNullOrWhiteSpace(drawingNumberPrefix))
            {
                return false;
            }

            var normalizedIdentifier = componentIdentifier.Trim();
            // SolidWorks 组件实例名可能包含“<1>”，不能交给 Path API 解析；
            // 仅手动去掉目录部分，既兼容实例名，也兼容组件磁盘路径。
            var lastSeparatorIndex = normalizedIdentifier.LastIndexOfAny(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            if (lastSeparatorIndex >= 0 && lastSeparatorIndex < normalizedIdentifier.Length - 1)
            {
                normalizedIdentifier = normalizedIdentifier.Substring(lastSeparatorIndex + 1);
            }

            return !string.IsNullOrWhiteSpace(normalizedIdentifier)
                && normalizedIdentifier.StartsWith(
                    drawingNumberPrefix,
                    StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 侧封板板长 = 基座宽 − 2，板宽 = 基座高 − 基座开孔高度 − 2。
        /// </summary>
        internal static Dictionary<string, object> BuildSidePanelPlateVariables(
            double baseWidth,
            double baseHeight,
            double openingHeight)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                [SidePanelLengthVariableName] = NormalizeSidePanelSize(baseWidth - SidePanelEdgeOffset),
                [SidePanelWidthVariableName] = NormalizeSidePanelSize(
                    baseHeight - openingHeight - SidePanelEdgeOffset)
            };
        }

        /// <summary>
        /// 当前侧封板全局变量已与目标板长/板宽一致时跳过驱动。
        /// </summary>
        internal static bool ShouldSkipSidePanelDrive(
            double currentLength,
            double currentWidth,
            double targetLength,
            double targetWidth)
        {
            return NormalizeSidePanelSize(currentLength) == NormalizeSidePanelSize(targetLength)
                && NormalizeSidePanelSize(currentWidth) == NormalizeSidePanelSize(targetWidth);
        }

        /// <summary>
        /// 从普通基座全局变量读取侧封板公式所需的基座宽、基座高和基座开孔高度。
        /// </summary>
        internal static bool TryResolveSidePanelSourceSizes(
            IReadOnlyDictionary<string, object> variables,
            out double baseWidth,
            out double baseHeight,
            out double openingHeight)
        {
            baseWidth = 0;
            baseHeight = 0;
            openingHeight = 0;
            return TryGetNumericVariable(variables, "基座宽", out baseWidth)
                && TryGetNumericVariable(variables, "基座高", out baseHeight)
                && TryGetNumericVariable(
                    variables,
                    CabinetHoleParameters.BaseOpeningHeightVariableName,
                    out openingHeight);
        }

        private static bool TryGetNumericVariable(
            IReadOnlyDictionary<string, object> variables,
            string name,
            out double value)
        {
            value = 0;
            if (variables == null || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            object raw;
            if (!variables.TryGetValue(name, out raw) || raw == null)
            {
                return false;
            }

            try
            {
                value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static double NormalizeSidePanelSize(double value)
        {
            return Math.Round(value, 3, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// 两类侧封板缺少任意一种时中止生成，避免保存外观不完整的普通基座。
        /// </summary>
        internal static void EnsureRequiredSidePanelsFound(
            int noElevatedPanelCount,
            int elevatedPanelCount,
            string assemblyPath)
        {
            var missingDrawingNumbers = new List<string>();
            if (noElevatedPanelCount <= 0)
            {
                missingDrawingNumbers.Add(NoElevatedBaseSidePanelDrawingNumber);
            }

            if (elevatedPanelCount <= 0)
            {
                missingDrawingNumbers.Add(ElevatedBaseSidePanelDrawingNumber);
            }

            if (missingDrawingNumbers.Count > 0)
            {
                throw new InvalidOperationException(
                    $"普通基座侧封板切换失败，未找到组件：{string.Join("、", missingDrawingNumbers)}；"
                    + $"基座装配体={assemblyPath}");
            }
        }

        private IReadOnlyList<IComponent2> ApplySidePanelSuppression(
            IModelDoc2 model,
            bool hasElevatedBase,
            string assemblyPath)
        {
            var assembly = model as IAssemblyDoc;
            if (assembly == null)
            {
                throw new InvalidOperationException(
                    $"普通基座侧封板切换失败，目标模型不是装配体：{assemblyPath}");
            }

            var components = assembly.GetComponents(true) as object[];
            var uncompressedPanels = new List<IComponent2>();
            var transferred = false;
            try
            {
                var rules = BuildSidePanelRules(hasElevatedBase);
                var matchedComponents = new List<IComponent2>[rules.Count];
                for (var ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
                {
                    matchedComponents[ruleIndex] = new List<IComponent2>();
                }

                if (components != null)
                {
                    foreach (var component in components)
                    {
                        var child = component as IComponent2;
                        if (child == null)
                        {
                            continue;
                        }

                        for (var ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
                        {
                            if (ComponentMatchesSidePanelRule(child, rules[ruleIndex]))
                            {
                                matchedComponents[ruleIndex].Add(child);
                                break;
                            }
                        }
                    }
                }

                EnsureRequiredSidePanelsFound(
                    matchedComponents[0].Count,
                    matchedComponents[1].Count,
                    assemblyPath);

                // 两类侧封板均确认存在后再统一修改，避免缺件时留下部分修改。
                for (var ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
                {
                    var targetState = rules[ruleIndex].Suppress
                        ? (int)swComponentSuppressionState_e.swComponentSuppressed
                        : (int)swComponentSuppressionState_e.swComponentResolved;
                    foreach (var component in matchedComponents[ruleIndex])
                    {
                        component.SetSuppression2(targetState);
                        if (!rules[ruleIndex].Suppress)
                        {
                            uncompressedPanels.Add(component);
                        }
                    }
                }

                transferred = true;
                return uncompressedPanels;
            }
            finally
            {
                foreach (var component in components ?? new object[0])
                    if (!transferred || !uncompressedPanels.Exists(panel => ReferenceEquals(panel, component)))
                        SolidWorksFileHelper.ReleaseComReference(component);
            }
        }

        private void DriveUncompressedSidePanels(
            SolidWorksDriver driver,
            IModelDoc2 rootModel,
            IReadOnlyList<IComponent2> uncompressedPanels,
            IReadOnlyDictionary<string, object> baseVariables,
            string assemblyPath)
        {
            if (uncompressedPanels == null || uncompressedPanels.Count == 0)
            {
                throw new InvalidOperationException(
                    $"普通基座侧封板驱动失败，未找到已解压侧封板：{assemblyPath}");
            }

            double baseWidth;
            double baseHeight;
            double openingHeight;
            if (!TryResolveSidePanelSourceSizes(
                    baseVariables,
                    out baseWidth,
                    out baseHeight,
                    out openingHeight)
                && !TryResolveSidePanelSourceSizesFromModel(
                    rootModel,
                    out baseWidth,
                    out baseHeight,
                    out openingHeight))
            {
                throw new InvalidOperationException(
                    "普通基座侧封板驱动失败，无法读取基座宽、基座高或基架高度；"
                    + $"基座装配体={assemblyPath}");
            }

            var plateVariables = BuildSidePanelPlateVariables(baseWidth, baseHeight, openingHeight);
            var targetLength = (double)plateVariables[SidePanelLengthVariableName];
            var targetWidth = (double)plateVariables[SidePanelWidthVariableName];
            var drivenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var panel in uncompressedPanels)
            {
                if (panel == null)
                {
                    continue;
                }

                string panelPath = null;
                try
                {
                    panelPath = panel.GetPathName();
                }
                catch
                {
                    // 个别组件无法读取路径时仍按实例驱动。
                }

                if (!string.IsNullOrWhiteSpace(panelPath) && !drivenPaths.Add(panelPath))
                {
                    continue;
                }

                if (SidePanelAlreadyMatches(panel, targetLength, targetWidth)) continue;

                driver.DriveComponentAssemblyGlobalVariables(
                    rootModel,
                    panel,
                    plateVariables,
                    null,
                    "普通基座侧封板");
            }
        }
        private static bool SidePanelAlreadyMatches(IComponent2 panel, double targetLength, double targetWidth)
        {
            var plateModel = panel.GetModelDoc2() as IModelDoc2;
            try
            {
                double currentLength;
                double currentWidth;
                return plateModel != null
                    && global::CKBusbarBridge.CabinetAssemblyDimensionReader.TryGetGlobalVariableValue(
                        plateModel, SidePanelLengthVariableName, out currentLength)
                    && global::CKBusbarBridge.CabinetAssemblyDimensionReader.TryGetGlobalVariableValue(
                        plateModel, SidePanelWidthVariableName, out currentWidth)
                    && ShouldSkipSidePanelDrive(currentLength, currentWidth, targetLength, targetWidth);
            }
            finally { SolidWorksFileHelper.ReleaseComReference(plateModel); }
        }

        private static bool TryResolveSidePanelSourceSizesFromModel(
            IModelDoc2 model,
            out double baseWidth,
            out double baseHeight,
            out double openingHeight)
        {
            baseWidth = 0;
            baseHeight = 0;
            openingHeight = 0;
            return global::CKBusbarBridge.CabinetAssemblyDimensionReader.TryGetGlobalVariableValue(model, "基座宽", out baseWidth)
                && global::CKBusbarBridge.CabinetAssemblyDimensionReader.TryGetGlobalVariableValue(model, "基座高", out baseHeight)
                && global::CKBusbarBridge.CabinetAssemblyDimensionReader.TryGetGlobalVariableValue(
                    model,
                    CabinetHoleParameters.BaseOpeningHeightVariableName,
                    out openingHeight);
        }

        private static bool ComponentMatchesSidePanelRule(
            IComponent2 component,
            FaceToFaceBaseSidePanelRule rule)
        {
            if (component == null || rule == null)
            {
                return false;
            }

            if (SidePanelIdentifierMatches(component.Name2, rule.DrawingNumberPrefix))
            {
                return true;
            }

            string componentPath = null;
            try
            {
                componentPath = component.GetPathName();
            }
            catch
            {
                // 某些轻量化组件无法读取路径；仍可依赖组件名称匹配。
            }

            return SidePanelIdentifierMatches(
                componentPath,
                rule.DrawingNumberPrefix);
        }

        public void PackAndGoWithSuffix(
            string assemblyPath,
            string outputDirectory,
            string suffix)
        {
            var concreteApp = _swApp as SldWorks;
            if (concreteApp == null)
            {
                throw new InvalidOperationException(
                    "当前 SolidWorks 应用对象不支持 PackAndGo。");
            }

            // 源文档与引用缓存的清理由共享 PackAndGo 实现按实际清单统一处理。
            new SolidWorksFileHelper(concreteApp).CopyFileUsePackAndGoWithSuffix(
                assemblyPath, outputDirectory, suffix, new List<string>());
        }

    }
}
