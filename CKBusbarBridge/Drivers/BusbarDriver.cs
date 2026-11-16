using AutoOpeningHoleBase;
using AutoOpeningHoleDataModel;
using CKBusbarBridge.Helpers;
using CKBusbarBridge.models;
using CKBusbarBridgePreview;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using static CKBusbarBridge.Helpers.SWHelper;

namespace CKBusbarBridge.Drivers
{
    public class BusbarDriver : IFaceToWallSpecialBusbarPhysicalSpecExecutor
    {
        private const string BusbarWidthGlobalVariableName = "排宽";
        private const string BusbarThicknessGlobalVariableName = "排厚";
        private const string BusbarOverlapGlobalVariableName = "排搭接尺寸";
        private const string PhaseSpacingGlobalVariableName = "相间距";
        private const string ExtensionBusbarPatternQuantityGlobalVariableName = "阵列数量";
        private const string ExtensionBusbarPatternFeatureName = "局部线性阵列1";
        private const string ExtensionBusbarPatternDimensionName = "D1@" + ExtensionBusbarPatternFeatureName;
        private const string ExtensionBusbarPatternEquation = "\"" + ExtensionBusbarPatternDimensionName + "\"=\"" + ExtensionBusbarPatternQuantityGlobalVariableName + "\"";
        private const string FaceToWallPhaseOffsetDirectionGlobalVariableName = "偏移方向";
        private const string FaceToWallPhaseOffsetSizeGlobalVariableName = "偏移尺寸";

        private const string BusbarBaseFeatureName = "铜排基体";
        private const string BusbarWidthDimensionName = "W@" + BusbarBaseFeatureName;
        private static readonly Regex ExtensionBusbarSourcePartNamePattern = new Regex(
            @"^(?:[AB]柜内伸出排-[ABC]-[AB]柜|柜内伸出排-[ABC]-柜)(前|后)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ExtensionBusbarAssemblyNamePattern = new Regex(
            @"^(?:[AB]柜内伸出排-[AB]柜|柜内伸出排-柜)(前|后)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private const string SpecificationPropertyName = "铜排规格";
        private const string ExtensionBusbarPhaseSequencePropertyName = "伸出排相序";
        private const string MaterialGradeBelowPropertyName = "材料牌号下";
        private const string MaterialGradeStandardSuffix = "GB/T 5585.1";
        private const string MaterialPropertyName = "材质";
        private const string ChineseMaterialPropertyName = "中文材质";
        private const string RightAngleBusbarChineseMaterial = "TMY";
        private const string FullRoundEdgeBusbarChineseMaterial = "TMQY";
        private const string NetWeightPropertyName = "净重(Kg)";
        private const string PartTypePropertyName = "零件类型";
        private const string UnfoldedLengthPropertyName = "铜排展开长度";
        private const string BusbarPartTypeValue = "铜排";

        private readonly ISldWorks swApp;

        private sealed class ExtensionBusbarPhasePartTarget
        {
            public Component2 Component { get; set; }

            public string SourcePhase { get; set; }

            public string ReferencedConfiguration { get; set; }

            public string PartPath { get; set; }
        }

        /// <summary>
        /// 创建铜排驱动器。
        /// </summary>
        /// <param name="_swApp">当前 SolidWorks 应用程序对象（由外部创建/注入）。</param>
        public BusbarDriver(ISldWorks _swApp)
        {
            swApp = _swApp;
        }

        internal static Dictionary<string, object> BuildFaceToWallSpecialAssemblyGlobalVariables(
            FaceToWallSpecialBusbarPhasePlan plan,
            double width,
            double thickness,
            double overlapLength)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            var variableMap = new Dictionary<string, object>
            {
                { BusbarThicknessGlobalVariableName, thickness },
                { BusbarOverlapGlobalVariableName, overlapLength }
            };

            switch (plan.TemplateKind)
            {
                case FaceToWallPhaseSpecialTemplateKind.VerticalBend:
                    variableMap.Add(
                        FaceToWallPhaseOffsetSizeGlobalVariableName,
                        plan.OffsetSize);
                    variableMap.Add(
                        FaceToWallPhaseOffsetDirectionGlobalVariableName,
                        plan.OffsetDirection);
                    break;
                case FaceToWallPhaseSpecialTemplateKind.OffsetConnection:
                    variableMap.Add(BusbarWidthGlobalVariableName, width);
                    variableMap.Add(
                        FaceToWallPhaseOffsetSizeGlobalVariableName,
                        plan.OffsetSize);
                    variableMap.Add(
                        FaceToWallPhaseOffsetDirectionGlobalVariableName,
                        plan.OffsetDirection);
                    break;
                case FaceToWallPhaseSpecialTemplateKind.MilledBar:
                    variableMap.Add(BusbarWidthGlobalVariableName, width);
                    break;
                case FaceToWallPhaseSpecialTemplateKind.StraightRaiseOnly:
                    variableMap.Add(BusbarWidthGlobalVariableName, width);
                    break;
                default:
                    throw new InvalidOperationException(
                        string.Format("不支持的面对墙特殊铜排模板类型：{0}。", plan.TemplateKind));
            }

            return variableMap;
        }

        /// <summary>关闭前调用指定文档的原生保存，关联子件由 SolidWorks 处理。</summary>
        /// <param name="model">待保存并关闭的文档。</param>
        /// <param name="saveReferenced">保留参数以兼容既有调用；装配体统一采用原生关联保存。</param>
        /// <param name="optimizeReferencedSave">保留参数以兼容既有调用，不再手动逐层保存。</param>
        public void SaveAndCloseModelDocument(
            IModelDoc2 model,
            bool saveReferenced = true,
            bool optimizeReferencedSave = false)
        {
            if (swApp == null || model == null) return;
            try { new SolidWorksDriver(swApp).TryCloseDocumentByPath(model.GetPathName()); }
            catch (Exception exception)
            {
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Error("铜排文档保存关闭未完成", exception);
            }
        }

        /// <summary>
        /// 从组件名称中提取“基础名称”（去掉最后一个 '-' 及其后缀）。
        /// 常用于判断/构造重命名后的内部文档名。
        /// </summary>
        /// <param name="component">SolidWorks 组件对象。</param>
        /// <returns>基础名称；若不包含 '-' 则返回原名称。</returns>
        private static string GetBasePartName(Component2 component)
        {
            var name = component?.Name2 ?? string.Empty;
            var lastDashIndex = name.LastIndexOf('-');
            return lastDashIndex > 0 ? name.Substring(0, lastDashIndex) : name;
        }

        /// <summary>
        /// 将铜排装配体的排宽、排厚和相间距全局变量同步为当前值。
        /// </summary>
        /// <param name="asmModel">铜排装配体模型。</param>
        /// <param name="width">铜排宽度（mm）。</param>
        /// <param name="thickness">铜排厚度（mm）。</param>
        /// <param name="phaseSpacing">相间距（mm）。</param>
        private void ApplyBusbarAssemblyGlobalVariables(
            IModelDoc2 asmModel,
            double width,
            double thickness,
            double phaseSpacing,
            bool rebuildAndSave = true)
        {
            if (swApp == null) return;
            if (asmModel == null) return;
            if (asmModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY) return;

            // 注意：此处写入的是装配体“全局变量”（Equations/Global Variables），用于驱动装配体级方程式。
            var solidWorksDriver = new SolidWorksDriver(swApp);
            solidWorksDriver.ApplyGlobalVariables(
                asmModel,
                new Dictionary<string, object>
                {
                    { BusbarWidthGlobalVariableName, width },
                    { BusbarThicknessGlobalVariableName, thickness },
                    { PhaseSpacingGlobalVariableName, phaseSpacing }
                },
                rebuildAndSave);
        }

        /// <summary>
        /// 合并写入伸出排装配体全局变量，统一重建并核验实际根数。
        /// </summary>
        /// <param name="asmModel">伸出排容器装配体模型。</param>
        /// <param name="thickness">铜排厚度（mm）。</param>
        /// <param name="phaseSpacing">相间距（mm）。</param>
        /// <param name="patternQuantity">阵列数量（= 单相根数 - 1），每次均写入。</param>
        /// <param name="driveDimensions">是否同步排厚和相间距；二次驱动未修改时跳过。</param>
        private void ApplyExtensionBusbarAssemblyGlobalVariables(
            IModelDoc2 asmModel,
            double thickness,
            double phaseSpacing,
            int patternQuantity,
            bool driveDimensions)
        {
            ExtensionBusbarPhaseDrivePlan.ValidatePatternQuantity(patternQuantity);
            if (!(asmModel is AssemblyDoc)) throw new InvalidOperationException("伸出排方程式驱动需要有效装配体。");
            EnsureAssemblyEditingContext(swApp, asmModel);
            RequireExtensionBusbarPattern(asmModel);
            var equationManager = asmModel.GetEquationMgr() as IEquationMgr;
            if (equationManager == null) throw new InvalidOperationException("伸出排方程式驱动无法读取方程管理器。");

            var variableMap = new Dictionary<string, object>
            {
                { ExtensionBusbarPatternQuantityGlobalVariableName, patternQuantity }
            };
            if (driveDimensions)
            {
                variableMap.Add(BusbarThicknessGlobalVariableName, thickness);
                variableMap.Add(PhaseSpacingGlobalVariableName, phaseSpacing);
            }

            var missingVariables = new List<string>();
            foreach (var variableItem in variableMap)
            {
                if (!TrySetAssemblyGlobalVariable(equationManager, variableItem.Key, variableItem.Value))
                {
                    missingVariables.Add(variableItem.Key);
                }
            }

            if (missingVariables.Count > 0)
            {
                throw new InvalidOperationException(
                    $"伸出排装配体方程式驱动失败，未找到以下全局变量：{string.Join("、", missingVariables)}");
            }

            if (FindExtensionBusbarEquation(equationManager, ExtensionBusbarPatternDimensionName) < 0)
            {
                // 旧B柜模型只补一次缺失绑定，已有方程保持不变。
                var bindingIndex = asmModel.GetConfigurationCount() > 1
                    ? equationManager.Add3(-1, ExtensionBusbarPatternEquation, false, (int)swInConfigurationOpts_e.swThisConfiguration, null)
                    : equationManager.Add2(-1, ExtensionBusbarPatternEquation, false);
                if (bindingIndex < 0) throw new InvalidOperationException("无法建立伸出排阵列根数与“阵列数量”的方程绑定。");
            }

            // 全局变量合并写入后统一求解、重建；实际根数核验不受二次驱动脏字段影响。
            equationManager.EvaluateAll();
            if (!asmModel.EditRebuild3())
                throw new InvalidOperationException("伸出排全局变量已写入，但装配体重建失败。");
            VerifyExtensionBusbarRootCount(asmModel, patternQuantity);
        }

        void IFaceToWallSpecialBusbarPhysicalSpecExecutor.DriveStandard(
            IModelDoc2 assemblyModel,
            double width,
            double thickness)
        {
            ModifyUnsuppressedBusbarsInAssembly(
                assemblyModel,
                width,
                thickness,
                resolveLightweight: false,
                finalizeAssembly: false);
        }

        /// <summary>
        /// 在当前铜排装配体中，遍历所有未压缩铜排并驱动其排宽和排厚。
        /// </summary>
        /// <param name="resolveLightweight">为 false 时跳过轻量化解析（调用方已解析时使用）。</param>
        /// <param name="finalizeAssembly">为 false 时不在此方法内重建装配体；为 true 时仅重建，保存推迟到关闭文档前。</param>
        private void ModifyUnsuppressedBusbarsInAssembly(
            IModelDoc2 asmModel,
            double width,
            double thickness,
            bool resolveLightweight = true,
            bool finalizeAssembly = true)
        {
            if (swApp == null) return;

            var asm = asmModel as AssemblyDoc;

            if (asmModel == null || asm == null || asmModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new InvalidOperationException("请先打开并激活铜排装配体(.SLDASM)，再驱动铜排子零部件。");
            }

            // 避免 SolidWorks 当前激活文档不一致导致部分 API（如 Parameter/特征访问）失败。
            var activePath = string.Empty;
            try
            {
                activePath = swApp.IActiveDoc2?.GetPathName() ?? string.Empty;
            }
            catch
            {
                activePath = string.Empty;
            }

            var targetPath = string.Empty;
            try
            {
                targetPath = asmModel.GetPathName() ?? string.Empty;
            }
            catch
            {
                targetPath = string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(targetPath) &&
                !string.Equals(activePath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                try { swApp.ActivateDoc(targetPath); } catch { }
            }

            if (resolveLightweight)
            {
                TryResolveLightweightComponents(asm);
            }

            var allBusbarComponents = asm.GetAllBusbarComponents(false);
            if (allBusbarComponents == null || allBusbarComponents.Count == 0) return;

            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var component in allBusbarComponents)
            {
                if (component == null) continue;

                try
                {
                    if (component.IsSuppressed()) continue;
                }
                catch
                {
                    // ignore
                }

                string compPath = null;
                try { compPath = component.GetPathName(); } catch { }

                var compModel = component.IGetModelDoc();
                if (string.IsNullOrWhiteSpace(compPath))
                {
                    try { compPath = compModel?.GetPathName(); } catch { }
                }

                if (string.IsNullOrWhiteSpace(compPath) || compModel == null) continue;

                string compFullPath;
                try { compFullPath = Path.GetFullPath(compPath); }
                catch { compFullPath = compPath; }

                string referencedConfig = null;
                try { referencedConfig = component.ReferencedConfiguration; } catch { }

                var processKey = compFullPath + "|" + (referencedConfig ?? string.Empty);
                if (processed.Contains(processKey)) continue;

                string originalConfig = null;
                try { originalConfig = compModel.ConfigurationManager.ActiveConfiguration.Name; } catch { }

                if (!string.IsNullOrWhiteSpace(referencedConfig) &&
                    !string.Equals(originalConfig, referencedConfig, StringComparison.OrdinalIgnoreCase))
                {
                    try { compModel.ShowConfiguration2(referencedConfig); } catch { }
                }

                var widthApplied = TrySetBusbarWidthOnPartModel(compModel, width, asmModel, component);
                var thicknessApplied = TrySetBusbarThicknessOnPartModel(compModel, thickness, asmModel, component);

                if (widthApplied || thicknessApplied)
                {
                    try { compModel.EditRebuild3(); } catch { }
                }

                // 规格及零件类型属于组件引用配置，供反读和铜排识别使用。
                WriteConfigurationCustomProperty(compModel, referencedConfig,
                    SpecificationPropertyName, FormatSpecificationValue(width, thickness));
                WriteConfigurationCustomProperty(compModel, referencedConfig,
                    PartTypePropertyName, BusbarPartTypeValue);
                try
                {
                    var materialGradeBelowValue = FormatMaterialGradeBelowValue(width, thickness);
                    compModel.AddPropertyToModel(MaterialGradeBelowPropertyName, materialGradeBelowValue, string.Empty);
                }
                catch
                {
                    // ignore
                }

                if (!string.IsNullOrWhiteSpace(originalConfig) &&
                    !string.IsNullOrWhiteSpace(referencedConfig) &&
                    !string.Equals(originalConfig, referencedConfig, StringComparison.OrdinalIgnoreCase))
                {
                    try { compModel.ShowConfiguration2(originalConfig); } catch { }
                }

                processed.Add(processKey);
            }

            // 驱动子零件排宽/排厚后可能停留在零件编辑模式，需回到装配体编辑上下文。
            EnsureAssemblyEditingContext(swApp, asmModel);

            if (!finalizeAssembly)
            {
                return;
            }

            asmModel.EditRebuild3();
        }

        /// <summary>
        /// 驱动装配体内未压缩铜排的排宽/排厚，并同步装配体全局变量（排宽/排厚/相间距）。
        /// 段排、L 排及柜体伸出排等均可复用此方法。
        /// </summary>
        /// <param name="asmModel">铜排装配体模型。</param>
        /// <param name="width">铜排宽度（mm）。</param>
        /// <param name="thickness">铜排厚度（mm）。</param>
        /// <param name="phaseSpacing">相间距（mm）。</param>
        /// <param name="finalizeAssembly">是否在方法末尾重建并保存装配体。</param>
        public void DriveBusbarSpecInAssembly(
            IModelDoc2 asmModel,
            double width,
            double thickness,
            double phaseSpacing,
            bool finalizeAssembly = true)
        {
            if (asmModel == null || width <= 0 || thickness <= 0)
            {
                return;
            }

            ApplyBusbarAssemblyGlobalVariables(asmModel, width, thickness, phaseSpacing);
            ModifyUnsuppressedBusbarsInAssembly(asmModel, width, thickness, resolveLightweight: true, finalizeAssembly: finalizeAssembly);
        }

        /// <summary>
        /// 驱动柜内伸出排装配体规格：
        /// 1) 校验三相源件，再写入排厚、相间距并驱动零件尺寸；
        /// 2) 最后通过阵列方程驱动根数，重建后核验实际数量。
        /// </summary>
        /// <param name="asmModel">伸出排容器装配体（如“A柜内伸出排-A柜前”）。</param>
        /// <param name="width">铜排宽度（mm）。</param>
        /// <param name="thickness">铜排厚度（mm）。</param>
        /// <param name="phaseSpacing">相间距（mm）。</param>
        /// <param name="patternQuantity">阵列数量（= 单相根数 - 1）。</param>
        /// <param name="finalizeAssembly">保留原入口参数；根数驱动始终重建并核验实际数量。</param>
        public void DriveExtensionBusbarSpecInAssembly(
            IModelDoc2 asmModel,
            double width,
            double thickness,
            double phaseSpacing,
            int patternQuantity,
            bool finalizeAssembly = true)
        {
            // 兼容原规格驱动入口；装配体全局变量合并写入后统一重建。
            if (asmModel == null || width <= 0 || thickness <= 0 || !(asmModel is AssemblyDoc)) return;
            ExtensionBusbarPhaseDrivePlan.ValidatePatternQuantity(patternQuantity);
            ValidateExtensionBusbarSources(asmModel);
            DriveExtensionBusbarDimensionsInAssembly(asmModel, width, thickness);
            ApplyExtensionBusbarAssemblyGlobalVariables(asmModel, thickness, phaseSpacing, patternQuantity, driveDimensions: true);
        }

        private void DriveExtensionBusbarDimensionsInAssembly(
            IModelDoc2 asmModel,
            double width,
            double thickness)
        {
            if (asmModel == null || width <= 0 || thickness <= 0)
            {
                return;
            }

            var asm = asmModel as AssemblyDoc;
            if (asm == null || asmModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                return;
            }

            TryResolveLightweightComponents(asm);
            EnsureAssemblyEditingContext(swApp, asmModel);

            // 驱动伸出排零件的排宽/排厚，装配体全局变量在末尾合并写入。
            // 与上游反读采用相同的零件范围。阵列实例可共享零件文件，下面按文件和引用配置去重。
            var sourcePartComponents = CollectExtensionBusbarSpecificationPartComponents(asm)
                .OrderByDescending(IsExtensionBusbarSourcePartComponent)
                .ToList();
            if (sourcePartComponents.Count == 0)
            {
                throw new InvalidOperationException("伸出排装配体中未找到柜内伸出排零件，无法确认铜排规格已驱动。");
            }

            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var solidWorksDriver = new SolidWorksDriver(swApp);
            var specification = FormatSpecificationValue(width, thickness);

            foreach (var component in sourcePartComponents)
            {
                var componentName = component.Name2;

                string compPath = null;
                try { compPath = component.GetPathName(); } catch { }

                IModelDoc2 compModel = null;
                try { compModel = component.GetModelDoc2() as IModelDoc2; } catch { }
                if (compModel == null)
                {
                    try { compModel = component.IGetModelDoc(); } catch { }
                }
                if (string.IsNullOrWhiteSpace(compPath))
                {
                    try { compPath = compModel?.GetPathName(); } catch { }
                }

                if (string.IsNullOrWhiteSpace(compPath))
                {
                    throw new InvalidOperationException("伸出排子件“" + componentName + "”缺少零件路径，铜排规格未驱动。");
                }

                if (compModel == null && File.Exists(compPath))
                {
                    try
                    {
                        compModel = solidWorksDriver.OpenOrActivateDocument(compPath, rebuildAfterResolve: false);
                    }
                    catch (Exception exception)
                    {
                        throw new InvalidOperationException(
                            "伸出排子件“" + componentName + "”无法打开零件模型，铜排规格未驱动。",
                            exception);
                    }
                }

                if (compModel == null || compModel.GetType() != (int)swDocumentTypes_e.swDocPART)
                {
                    throw new InvalidOperationException("伸出排子件“" + componentName + "”无法加载零件模型，铜排规格未驱动。");
                }

                string compFullPath;
                try { compFullPath = Path.GetFullPath(compPath); }
                catch { compFullPath = compPath; }

                string loadedPath = null;
                try { loadedPath = compModel.GetPathName(); } catch { }
                if (!string.IsNullOrWhiteSpace(loadedPath) &&
                    !string.Equals(Path.GetFullPath(loadedPath), compFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    compModel = solidWorksDriver.OpenOrActivateDocument(compFullPath, rebuildAfterResolve: false);
                    if (compModel.GetType() != (int)swDocumentTypes_e.swDocPART)
                    {
                        throw new InvalidOperationException("伸出排子件“" + componentName + "”加载的模型与组件引用路径不一致。");
                    }
                }

                string referencedConfig = null;
                try { referencedConfig = component.ReferencedConfiguration; } catch { }
                if (string.IsNullOrWhiteSpace(referencedConfig))
                {
                    throw new InvalidOperationException("伸出排子件“" + componentName + "”缺少引用配置，铜排规格未驱动。");
                }

                var processKey = compFullPath + "|" + (referencedConfig ?? string.Empty);
                if (processed.Contains(processKey)) continue;

                string originalConfig = null;
                try { originalConfig = compModel.ConfigurationManager.ActiveConfiguration.Name; } catch { }

                if (!string.Equals(originalConfig, referencedConfig, StringComparison.OrdinalIgnoreCase))
                {
                    if (!compModel.ShowConfiguration2(referencedConfig))
                    {
                        throw new InvalidOperationException("伸出排子件“" + componentName + "”无法激活引用配置“" + referencedConfig + "”。");
                    }
                }

                try
                {
                    EnsureAssemblyEditingContext(swApp, asmModel);

                    var widthApplied = TrySetBusbarWidthOnPartModel(compModel, width, asmModel, component);
                    var thicknessApplied = TrySetBusbarThicknessOnPartModel(compModel, thickness, asmModel, component);
                    InvalidOperationException verificationFailure = null;
                    if (widthApplied && thicknessApplied)
                    {
                        compModel.EditRebuild3();
                        try { VerifyExtensionBusbarPartDimensions(compModel, componentName, width, thickness); }
                        catch (InvalidOperationException exception) { verificationFailure = exception; }
                    }

                    if (!widthApplied || !thicknessApplied || verificationFailure != null)
                    {
                        // 个别源排的特征不接受装配体上下文修改；改在零件文档中重试。
                        compModel = solidWorksDriver.OpenOrActivateDocument(compFullPath, rebuildAfterResolve: false);
                        var activeConfig = compModel.ConfigurationManager?.ActiveConfiguration?.Name;
                        if (!string.Equals(activeConfig, referencedConfig, StringComparison.OrdinalIgnoreCase) &&
                            !compModel.ShowConfiguration2(referencedConfig))
                        {
                            throw new InvalidOperationException("伸出排子件“" + componentName + "”无法激活引用配置“" + referencedConfig + "”。");
                        }

                        widthApplied = TrySetBusbarWidthOnPartModel(compModel, width);
                        thicknessApplied = TrySetBusbarThicknessOnPartModel(compModel, thickness);
                        if (!widthApplied || !thicknessApplied)
                        {
                            throw new InvalidOperationException(
                                "伸出排子件“" + componentName + "”的" +
                                (!widthApplied && !thicknessApplied ? "排宽和排厚" : !widthApplied ? "排宽" : "排厚") +
                                "在装配体和零件上下文中均未驱动成功。",
                                verificationFailure);
                        }

                        compModel.EditRebuild3();
                        VerifyExtensionBusbarPartDimensions(compModel, componentName, width, thickness);
                    }

                    // 只在物理尺寸均写入成功后标记配置规格。
                    WriteConfigurationCustomProperty(compModel, referencedConfig,
                        SpecificationPropertyName, specification);
                    WriteConfigurationCustomProperty(compModel, referencedConfig,
                        PartTypePropertyName, BusbarPartTypeValue);
                    try
                    {
                        var materialGradeBelowValue = FormatMaterialGradeBelowValue(width, thickness);
                        (compModel as ModelDoc2)?.AddPropertyToModel(
                            MaterialGradeBelowPropertyName,
                            materialGradeBelowValue,
                            string.Empty);
                    }
                    catch
                    {
                        // 材料牌号不影响规格核验。
                    }

                    // 伸出排零件可能由多个阵列实例共用。装配体的 SaveReferenced
                    // 不保证每个已修改的子零件都落盘，必须逐件确认保存结果。
                    if (!TrySaveModelSilentChecked(compModel))
                    {
                        throw new InvalidOperationException(
                            "伸出排子件“" + componentName + "”尺寸已修改，但零件保存失败：" + compFullPath);
                    }
                }
                finally
                {
                    if (!string.IsNullOrWhiteSpace(originalConfig) &&
                        !string.Equals(originalConfig, referencedConfig, StringComparison.OrdinalIgnoreCase))
                    {
                        try { compModel.ShowConfiguration2(originalConfig); } catch { }
                    }
                }

                processed.Add(processKey);
            }

            EnsureAssemblyEditingContext(swApp, asmModel);
        }

        private void ValidateExtensionBusbarSources(IModelDoc2 model)
        {
            var assembly = model as AssemblyDoc;
            if (assembly == null) throw new InvalidOperationException("伸出排源件校验需要有效装配体。");
            EnsureAssemblyEditingContext(swApp, model);
            RequireExtensionBusbarPattern(model);
            var equations = model.GetEquationMgr() as IEquationMgr;
            if (equations == null || FindExtensionBusbarEquation(equations, ExtensionBusbarPatternQuantityGlobalVariableName) < 0)
                throw new InvalidOperationException("伸出排装配体“" + model.GetTitle() + "”缺少全局变量“阵列数量”。");
            var sources = CollectExtensionBusbarSourcePartComponents(assembly)
                .Where(component => !component.IsPatternInstance()).ToList();
            var phaseCounts = CountExtensionBusbarPhases(sources);
            ExtensionBusbarPhaseDrivePlan.ValidateSourcePhaseCounts(
                model.GetTitle(), phaseCounts["A"], phaseCounts["B"], phaseCounts["C"]);
            var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var component in sources)
            {
                var path = component.GetPathName();
                var configuration = component.ReferencedConfiguration;
                var part = component.GetModelDoc2() as IModelDoc2;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || string.IsNullOrWhiteSpace(configuration)
                    || part == null || part.GetType() != (int)swDocumentTypes_e.swDocPART
                    || !string.Equals(Path.GetFullPath(part.GetPathName()), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
                    || part.GetConfigurationByName(configuration) == null)
                    throw new InvalidOperationException("伸出排源件“" + component.Name2 + "”的零件路径或引用配置无效。");
                if (!references.Add(Path.GetFullPath(path) + "|" + configuration))
                    throw new InvalidOperationException("伸出排装配体“" + model.GetTitle() + "”存在不同相源件引用同一零件及配置，无法安全驱动。");
            }
        }

        private static Feature RequireExtensionBusbarPattern(IModelDoc2 model)
        {
            var pattern = (model as AssemblyDoc)?.FeatureByName(ExtensionBusbarPatternFeatureName) as Feature;
            if (pattern == null || !string.Equals(pattern.GetTypeName2(), "LocalLPattern", StringComparison.Ordinal)
                || !(model.Parameter(ExtensionBusbarPatternDimensionName) is Dimension))
                throw new InvalidOperationException("伸出排装配体“" + model.GetTitle() + "”缺少有效的局部线性阵列及根数尺寸。");
            return pattern;
        }

        private static Dictionary<string, int> CountExtensionBusbarPhases(IEnumerable<Component2> components)
        {
            var counts = new Dictionary<string, int> { { "A", 0 }, { "B", 0 }, { "C", 0 } };
            foreach (var component in components)
                counts[ExtensionBusbarPhaseDrivePlan.ResolveSourcePhase(NormalizeBusbarComponentName(component.Name2))]++;
            return counts;
        }

        private static int FindExtensionBusbarEquation(IEquationMgr equations, string leftName)
        {
            var found = -1;
            for (var index = 0; index < equations.GetCount(); index++)
            {
                var equation = equations.Equation[index];
                if (string.IsNullOrWhiteSpace(equation) || !EquationMatchesGlobalVariable(equation, leftName)) continue;
                if (found >= 0) throw new InvalidOperationException("伸出排方程重复定义：“" + leftName + "”。");
                found = index;
            }
            return found;
        }

        private static void VerifyExtensionBusbarRootCount(IModelDoc2 model, int patternQuantity)
        {
            var equations = (IEquationMgr)model.GetEquationMgr();
            var index = FindExtensionBusbarEquation(equations, ExtensionBusbarPatternQuantityGlobalVariableName);
            var bindingIndex = FindExtensionBusbarEquation(equations, ExtensionBusbarPatternDimensionName);
            var dimension = model.Parameter(ExtensionBusbarPatternDimensionName) as Dimension;
            var values = index >= 0 && bindingIndex >= 0 && dimension != null
                ? new[] { equations.Value[index], equations.Value[bindingIndex], dimension.SystemValue } : Array.Empty<double>();
            if (values.Length != 3 || values.Any(value => double.IsNaN(value) || double.IsInfinity(value)
                || Math.Abs(value - patternQuantity) > 1e-6))
                throw new InvalidOperationException("伸出排装配体“" + model.GetTitle() + "”的阵列变量或实际阵列尺寸未达到目标根数。");
            // 重建后重新枚举，不能沿用换相前或缩减阵列前的派生组件对象。
            var counts = CountExtensionBusbarPhases(CollectExtensionBusbarSourcePartComponents((AssemblyDoc)model));
            ExtensionBusbarPhaseDrivePlan.ValidateDrivenPhaseRootCounts(
                model.GetTitle(), patternQuantity, counts["A"], counts["B"], counts["C"]);
        }

        /// <summary>
        /// 驱动单个伸出排装配体的 ABC/CBA 相序；模板默认 A 柜 CBA、B 柜 ABC。
        /// </summary>
        private void DriveExtensionBusbarPhaseInAssembly(
            IModelDoc2 asmModel,
            string assemblyName,
            string targetSequence,
            bool finalizeAssembly)
        {
            var asm = asmModel as AssemblyDoc;
            if (asm == null || asmModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new InvalidOperationException("伸出排相序驱动目标不是有效的装配体。");
            }

            string configurationName = null;
            try
            {
                configurationName = asmModel.ConfigurationManager?.ActiveConfiguration?.Name;
            }
            catch
            {
                configurationName = null;
            }

            if (string.IsNullOrWhiteSpace(configurationName))
            {
                throw new InvalidOperationException("无法确定伸出排装配体的当前配置。");
            }

            var storedSequence = ReadConfigurationCustomProperty(
                asmModel,
                configurationName,
                ExtensionBusbarPhaseSequencePropertyName);
            var phasePlan = ExtensionBusbarPhaseDrivePlan.Build(
                assemblyName,
                storedSequence,
                targetSequence);

            Action writeTargetSequence = () => WriteConfigurationCustomProperty(
                asmModel,
                configurationName,
                ExtensionBusbarPhaseSequencePropertyName,
                phasePlan.TargetSequence);

            if (phasePlan.ShouldSwap)
            {
                ApplyExtensionBusbarPhaseReferenceSwap(
                    asmModel,
                    asm,
                    assemblyName,
                    phasePlan.PartUpdates,
                    writeTargetSequence);
            }
            else
            {
                writeTargetSequence();
            }

            EnsureAssemblyEditingContext(swApp, asmModel);
            if (finalizeAssembly)
            {
                asmModel.EditRebuild3();
            }
        }

        private void ApplyExtensionBusbarPhaseReferenceSwap(
            IModelDoc2 asmModel,
            AssemblyDoc asm,
            string assemblyName,
            IReadOnlyList<ExtensionBusbarPhasePartUpdate> updates,
            Action writeTargetSequence)
        {
            var targets = BuildExtensionBusbarPhasePartTargets(asm, assemblyName);
            var aTargets = targets
                .Where(target => string.Equals(
                    target.SourcePhase,
                    "A",
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            var cTargets = targets
                .Where(target => string.Equals(
                    target.SourcePhase,
                    "C",
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            var aReference = ResolveSingleExtensionBusbarPhaseReference(
                aTargets,
                assemblyName,
                "A");
            var cReference = ResolveSingleExtensionBusbarPhaseReference(
                cTargets,
                assemblyName,
                "C");

            if (AreSameExtensionBusbarPartReference(aReference, cReference))
            {
                throw new InvalidOperationException(
                    string.Format(
                        "伸出排装配体“{0}”的 A、C 相源排引用相同，无法安全交换相序。",
                        assemblyName));
            }

            ValidateExtensionBusbarPhaseReferenceUpdates(updates, assemblyName);

            ExtensionBusbarPhaseDrivePlan.ExecuteReferenceSwap(
                () => ReplaceExtensionBusbarPhaseReferences(
                    asmModel, asm, aTargets, cReference, assemblyName, "A", "C"),
                () => ReplaceExtensionBusbarPhaseReferences(
                    asmModel, asm, cTargets, aReference, assemblyName, "C", "A"),
                () => ReplaceExtensionBusbarPhaseReferences(
                    asmModel, asm, aTargets, aReference, assemblyName, "A", "A"),
                () => ReplaceExtensionBusbarPhaseReferences(
                    asmModel, asm, cTargets, cReference, assemblyName, "C", "C"),
                writeTargetSequence);
        }

        private static List<ExtensionBusbarPhasePartTarget> BuildExtensionBusbarPhasePartTargets(
            AssemblyDoc asm,
            string assemblyName)
        {
            var targets = new List<ExtensionBusbarPhasePartTarget>();
            foreach (var component in CollectExtensionBusbarSourcePartComponents(asm))
            {
                if (component == null)
                {
                    continue;
                }

                // 派生实例跟随阵列源件更新；不能缓存它们并在重建后继续校验旧对象。
                if (component.IsPatternInstance()) continue;

                var normalizedName = NormalizeBusbarComponentName(component.Name2);
                if (string.IsNullOrWhiteSpace(normalizedName))
                {
                    try
                    {
                        normalizedName = NormalizeBusbarComponentName(
                            Path.GetFileNameWithoutExtension(component.GetPathName()));
                    }
                    catch
                    {
                        normalizedName = string.Empty;
                    }
                }

                string sourcePhase;
                try
                {
                    sourcePhase = ExtensionBusbarPhaseDrivePlan.ResolveSourcePhase(normalizedName);
                }
                catch
                {
                    continue;
                }

                if (!string.Equals(sourcePhase, "A", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(sourcePhase, "C", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string referencedConfiguration = null;
                try { referencedConfiguration = component.ReferencedConfiguration; } catch { }
                if (string.IsNullOrWhiteSpace(referencedConfiguration))
                {
                    try
                    {
                        referencedConfiguration = component
                            .IGetModelDoc()
                            .ConfigurationManager
                            ?.ActiveConfiguration
                            ?.Name;
                    }
                    catch
                    {
                        referencedConfiguration = null;
                    }
                }

                if (string.IsNullOrWhiteSpace(referencedConfiguration))
                {
                    throw new InvalidOperationException(
                        string.Format(
                            "伸出排装配体“{0}”的 {1} 相源排无法确定引用配置。",
                            assemblyName,
                            sourcePhase));
                }

                string partPath = null;
                try { partPath = component.GetPathName(); } catch { }
                if (string.IsNullOrWhiteSpace(partPath) || !File.Exists(partPath))
                {
                    throw new InvalidOperationException(
                        string.Format(
                            "伸出排装配体“{0}”的 {1} 相源排没有有效的磁盘零件引用。",
                            assemblyName,
                            sourcePhase));
                }

                targets.Add(new ExtensionBusbarPhasePartTarget
                {
                    Component = component,
                    SourcePhase = sourcePhase,
                    ReferencedConfiguration = referencedConfiguration,
                    PartPath = Path.GetFullPath(partPath)
                });
            }

            return targets;
        }

        private static ExtensionBusbarPhasePartTarget ResolveSingleExtensionBusbarPhaseReference(
            IReadOnlyList<ExtensionBusbarPhasePartTarget> targets,
            string assemblyName,
            string sourcePhase)
        {
            if (targets == null || targets.Count == 0)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "伸出排装配体“{0}”未找到 {1} 相源排子件，无法交换相序。",
                        assemblyName,
                        sourcePhase));
            }

            var reference = targets[0];
            if (targets.Any(target => !AreSameExtensionBusbarPartReference(
                target,
                reference)))
            {
                throw new InvalidOperationException(
                    string.Format(
                        "伸出排装配体“{0}”的 {1} 相源排引用了多个零件或配置，无法安全交换相序。",
                        assemblyName,
                        sourcePhase));
            }

            return reference;
        }

        private static bool AreSameExtensionBusbarPartReference(
            ExtensionBusbarPhasePartTarget left,
            ExtensionBusbarPhasePartTarget right)
        {
            return left != null &&
                   right != null &&
                   string.Equals(
                       left.PartPath,
                       right.PartPath,
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       left.ReferencedConfiguration,
                       right.ReferencedConfiguration,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateExtensionBusbarPhaseReferenceUpdates(
            IReadOnlyList<ExtensionBusbarPhasePartUpdate> updates,
            string assemblyName)
        {
            var aUpdate = updates?.SingleOrDefault(update => string.Equals(
                update.SourcePhase,
                "A",
                StringComparison.OrdinalIgnoreCase));
            var cUpdate = updates?.SingleOrDefault(update => string.Equals(
                update.SourcePhase,
                "C",
                StringComparison.OrdinalIgnoreCase));

            if (aUpdate == null ||
                cUpdate == null ||
                !string.Equals(
                    NormalizeExtensionBusbarPhaseCode(aUpdate.TargetBusbarPhase),
                    "C",
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    NormalizeExtensionBusbarPhaseCode(cUpdate.TargetBusbarPhase),
                    "A",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    string.Format(
                        "伸出排装配体“{0}”的相序交换计划不是 A/C 引用互换。",
                        assemblyName));
            }
        }

        private static string NormalizeExtensionBusbarPhaseCode(string busbarPhase)
        {
            var normalized = (busbarPhase ?? string.Empty).Trim();
            return normalized.EndsWith("相", StringComparison.Ordinal)
                ? normalized.Substring(0, normalized.Length - 1)
                : normalized;
        }

        private void ReplaceExtensionBusbarPhaseReferences(
            IModelDoc2 asmModel,
            AssemblyDoc asm,
            IReadOnlyList<ExtensionBusbarPhasePartTarget> sourceTargets,
            ExtensionBusbarPhasePartTarget replacementReference,
            string assemblyName,
            string sourcePhase,
            string replacementPhase)
        {
            if (sourceTargets == null || sourceTargets.Count == 0 || replacementReference == null)
            {
                throw new InvalidOperationException("伸出排相序替换缺少源子件或目标引用。");
            }

            try
            {
                asmModel.ClearSelection2(true);
                for (var index = 0; index < sourceTargets.Count; index++)
                {
                    string selectName = null;
                    try { selectName = sourceTargets[index].Component.GetSelectByIDString(); } catch { }
                    var selected = !string.IsNullOrWhiteSpace(selectName) &&
                                   asmModel.Extension.SelectByID2(
                                       selectName,
                                       "COMPONENT",
                                       0,
                                       0,
                                       0,
                                       index > 0,
                                       0,
                                       null,
                                       0);
                    if (!selected)
                    {
                        throw new InvalidOperationException(
                            string.Format(
                                "伸出排装配体“{0}”的 {1} 相源排子件选择失败。",
                                assemblyName,
                                sourcePhase));
                    }
                }

                var replaced = asm.ReplaceComponents2(
                    replacementReference.PartPath,
                    replacementReference.ReferencedConfiguration,
                    false,
                    (int)swReplaceComponentsConfiguration_e
                        .swReplaceComponentsConfiguration_ManuallySelect,
                    true);
                if (!replaced)
                {
                    throw new InvalidOperationException(
                        string.Format(
                            "伸出排装配体“{0}”将 {1} 相子件替换为 {2} 相零件引用失败。",
                            assemblyName,
                            sourcePhase,
                            replacementPhase));
                }

                asmModel.EditRebuild3();
                VerifyExtensionBusbarPhaseReferences(
                    sourceTargets,
                    replacementReference,
                    assemblyName,
                    sourcePhase,
                    replacementPhase);
            }
            finally
            {
                try { asmModel.ClearSelection2(true); } catch { }
                EnsureAssemblyEditingContext(swApp, asmModel);
            }
        }

        private static void VerifyExtensionBusbarPhaseReferences(
            IEnumerable<ExtensionBusbarPhasePartTarget> sourceTargets,
            ExtensionBusbarPhasePartTarget replacementReference,
            string assemblyName,
            string sourcePhase,
            string replacementPhase)
        {
            foreach (var target in sourceTargets)
            {
                string actualPath = null;
                string actualConfiguration = null;
                try { actualPath = target.Component.GetPathName(); } catch { }
                try { actualConfiguration = target.Component.ReferencedConfiguration; } catch { }

                if (string.IsNullOrWhiteSpace(actualPath) ||
                    !string.Equals(
                        Path.GetFullPath(actualPath),
                        replacementReference.PartPath,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        actualConfiguration,
                        replacementReference.ReferencedConfiguration,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        string.Format(
                            "伸出排装配体“{0}”的 {1} 相子件未正确引用 {2} 相零件或配置。",
                            assemblyName,
                            sourcePhase,
                            replacementPhase));
                }
            }
        }

        private static void WriteConfigurationCustomProperty(
            IModelDoc2 model,
            string configurationName,
            string propertyName,
            string propertyValue)
        {
            if (model?.Extension == null ||
                string.IsNullOrWhiteSpace(configurationName) ||
                string.IsNullOrWhiteSpace(propertyName))
            {
                throw new InvalidOperationException("目标模型或引用配置不支持属性“" + propertyName + "”写入。");
            }

            var customPropertyManager = model.Extension.CustomPropertyManager[configurationName];
            if (customPropertyManager == null)
            {
                throw new InvalidOperationException("目标配置“" + configurationName + "”不支持属性“" + propertyName + "”写入。");
            }

            customPropertyManager.Add3(
                propertyName,
                (int)swCustomInfoType_e.swCustomInfoText,
                propertyValue ?? string.Empty,
                (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);

            var writtenValue = ReadConfigurationCustomProperty(
                model,
                configurationName,
                propertyName);
            if (!string.Equals(
                writtenValue,
                propertyValue ?? string.Empty,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    string.Format(
                        "配置“{0}”的属性“{1}”写入失败。",
                        configurationName,
                        propertyName));
            }
        }

        internal static string ReadConfigurationCustomProperty(
            IModelDoc2 model,
            string configurationName,
            string propertyName)
        {
            // 实现迁入界面运行时；正式生成与反读共用相同规则。
            return BridgeModelReadOperations.ReadConfigurationCustomProperty(model, configurationName, propertyName);
        }

        /// <summary>
        /// 在柜体总装配体中驱动所有柜内伸出排装配体的铜排规格。
        /// 支持面对面（A/B 柜内伸出排-A/B 柜前/后）与面对墙（柜内伸出排-柜前/后）两种命名。
        /// 装配体方程式写入排厚/相间距/阵列数量，源排子零件驱动排宽/排厚。
        /// </summary>
        /// <param name="rootModel">柜体总装配体模型。</param>
        /// <param name="busbarSpec">铜排规格文本（如“80×8”）。</param>
        /// <param name="phaseSpacing">相间距（mm）。</param>
        /// <param name="singlePhaseRootCount">单相根数。</param>
        public void DriveExtensionBusbarAssembliesInRootModel(
            IModelDoc2 rootModel,
            string busbarSpec,
            double phaseSpacing,
            int singlePhaseRootCount)
        {
            DriveExtensionBusbarAssembliesInRootModelCore(
                rootModel,
                busbarSpec,
                phaseSpacing,
                singlePhaseRootCount,
                aCabinetTargetSequence: null,
                bCabinetTargetSequence: null,
                drivePhaseSequence: false,
                busbarMaterial: null);
        }

        /// <summary>
        /// 在面对面或侧对侧总装中校验源件后换相、驱动尺寸，最后通过方程驱动根数并核验。
        /// </summary>
        public void DriveExtensionBusbarAssembliesInRootModel(
            IModelDoc2 rootModel,
            string busbarSpec,
            double phaseSpacing,
            int singlePhaseRootCount,
            string aCabinetTargetSequence,
            string bCabinetTargetSequence)
        {
            DriveExtensionBusbarAssembliesInRootModelCore(
                rootModel,
                busbarSpec,
                phaseSpacing,
                singlePhaseRootCount,
                aCabinetTargetSequence,
                bCabinetTargetSequence,
                drivePhaseSequence: true,
                busbarMaterial: null);
        }

        /// <summary>
        /// 在面对面或侧对侧柜体总装中驱动伸出排规格、相序及材质。
        /// 材质只在各伸出排容器装配体内应用，不扩散到总装中的其他铜排。
        /// </summary>
        public void DriveExtensionBusbarAssembliesInRootModel(
            IModelDoc2 rootModel,
            string busbarSpec,
            double phaseSpacing,
            int singlePhaseRootCount,
            string aCabinetTargetSequence,
            string bCabinetTargetSequence,
            string busbarMaterial)
        {
            DriveExtensionBusbarAssembliesInRootModelCore(
                rootModel,
                busbarSpec,
                phaseSpacing,
                singlePhaseRootCount,
                aCabinetTargetSequence,
                bCabinetTargetSequence,
                drivePhaseSequence: true,
                busbarMaterial: busbarMaterial);
        }

        /// <summary>面对墙只驱动规格和实际零件材质，不写入或交换默认ABC相序。</summary>
        public void DriveExtensionBusbarAssembliesInRootModel(
            IModelDoc2 rootModel, string busbarSpec, double phaseSpacing,
            int singlePhaseRootCount, string busbarMaterial)
        {
            DriveExtensionBusbarAssembliesInRootModelCore(rootModel, busbarSpec, phaseSpacing,
                singlePhaseRootCount, null, null, drivePhaseSequence: false, busbarMaterial: busbarMaterial);
        }

        internal void DriveExtensionBusbarAssembliesInRootModel(
            IModelDoc2 rootModel, string busbarSpec, double phaseSpacing,
            int singlePhaseRootCount, string aCabinetTargetSequence,
            string bCabinetTargetSequence, string busbarMaterial,
            ExtensionBusbarDriveSelection selection)
        {
            DriveExtensionBusbarAssembliesInRootModelCore(
                rootModel, busbarSpec, phaseSpacing, singlePhaseRootCount,
                aCabinetTargetSequence, bCabinetTargetSequence,
                drivePhaseSequence: true, busbarMaterial: busbarMaterial, selection: selection);
        }

        private void DriveExtensionBusbarAssembliesInRootModelCore(
            IModelDoc2 rootModel,
            string busbarSpec,
            double phaseSpacing,
            int singlePhaseRootCount,
            string aCabinetTargetSequence,
            string bCabinetTargetSequence,
            bool drivePhaseSequence,
            string busbarMaterial,
            ExtensionBusbarDriveSelection selection = null)
        {
            if (rootModel == null ||
                !TryParseSpecification(busbarSpec, out var width, out var thickness))
            {
                return;
            }

            var patternQuantity = singlePhaseRootCount - 1;
            ExtensionBusbarPhaseDrivePlan.ValidatePatternQuantity(patternQuantity);
            var solidWorksDriver = new SolidWorksDriver(swApp);
            var extensionAssemblies = FindExtensionBusbarAssemblyComponents(rootModel).ToList();
            if (extensionAssemblies.Count == 0)
            {
                return;
            }

            var skipRemainingMaterialSteps = false;
            foreach (var extensionAssemblyComponent in extensionAssemblies)
            {
                IModelDoc2 extensionAssemblyModel = null;
                var openedExtensionAssembly = false;
                string assemblyPath = null;
                string assemblyName = null;
                string referencedAssemblyConfiguration = null;
                try { assemblyPath = extensionAssemblyComponent.GetPathName(); } catch { }
                try { assemblyName = NormalizeBusbarComponentName(extensionAssemblyComponent.Name2); } catch { }
                try { referencedAssemblyConfiguration = extensionAssemblyComponent.ReferencedConfiguration; } catch { }
                if (string.IsNullOrWhiteSpace(assemblyName))
                {
                    try
                    {
                        assemblyName = NormalizeBusbarComponentName(
                            Path.GetFileNameWithoutExtension(assemblyPath));
                    }
                    catch
                    {
                        assemblyName = string.Empty;
                    }
                }

                try
                {
                    if (!string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath))
                    {
                        extensionAssemblyModel = solidWorksDriver.OpenOrActivateDocument(assemblyPath);
                        openedExtensionAssembly = extensionAssemblyModel != null;
                    }

                    if (extensionAssemblyModel == null)
                    {
                        extensionAssemblyModel = extensionAssemblyComponent.GetModelDoc2() as IModelDoc2;
                    }

                    if (extensionAssemblyModel == null ||
                        extensionAssemblyModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                    {
                        throw new InvalidOperationException(
                            "伸出排装配体“" + assemblyName + "”无法加载，铜排规格未驱动。");
                    }

                    if (extensionAssemblyModel is AssemblyDoc extensionAssemblyDoc)
                    {
                        try { extensionAssemblyDoc.ResolveAllLightWeightComponents(false); } catch { }
                    }

                    if (!string.IsNullOrWhiteSpace(referencedAssemblyConfiguration))
                    {
                        string activeAssemblyConfiguration = null;
                        try
                        {
                            activeAssemblyConfiguration = extensionAssemblyModel
                                .ConfigurationManager
                                ?.ActiveConfiguration
                                ?.Name;
                        }
                        catch
                        {
                            activeAssemblyConfiguration = null;
                        }

                        if (!string.Equals(
                            activeAssemblyConfiguration,
                            referencedAssemblyConfiguration,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            var configurationActivated = false;
                            try
                            {
                                configurationActivated = extensionAssemblyModel
                                    .ShowConfiguration2(referencedAssemblyConfiguration);
                            }
                            catch
                            {
                                configurationActivated = false;
                            }

                            if (!configurationActivated)
                            {
                                throw new InvalidOperationException(
                                    string.Format(
                                        "无法激活伸出排装配体“{0}”的引用配置“{1}”。",
                                        assemblyName,
                                        referencedAssemblyConfiguration));
                            }
                        }
                    }

                    EnsureAssemblyEditingContext(swApp, extensionAssemblyModel);

                    var targetSequence = drivePhaseSequence
                        ? ExtensionBusbarPhaseDrivePlan.ResolveTargetSequence(
                            assemblyName, aCabinetTargetSequence, bCabinetTargetSequence)
                        : null;
                    foreach (var driveStage in ExtensionBusbarPhaseDrivePlan.ResolveDriveStages(assemblyName, selection))
                    {
                        switch (driveStage)
                        {
                            case ExtensionBusbarDriveStage.ValidateSources:
                                ValidateExtensionBusbarSources(extensionAssemblyModel);
                                break;
                            case ExtensionBusbarDriveStage.PhaseSequence:
                                if (drivePhaseSequence)
                                    DriveExtensionBusbarPhaseInAssembly(
                                        extensionAssemblyModel, assemblyName,
                                        targetSequence, finalizeAssembly: false);
                                break;
                            case ExtensionBusbarDriveStage.Specification:
                                DriveExtensionBusbarDimensionsInAssembly(
                                    extensionAssemblyModel, width, thickness);
                                break;
                            case ExtensionBusbarDriveStage.Material:
                                if (!skipRemainingMaterialSteps && !string.IsNullOrWhiteSpace(busbarMaterial))
                                {
                                    var materialResult = BusbarMaterialFailurePolicy.Execute(
                                        () => ApplyBusbarMaterials(extensionAssemblyModel, busbarMaterial),
                                        busbarMaterial, showWarning: null);
                                    skipRemainingMaterialSteps = materialResult.IsDefaultMaterialFallback
                                        || BusbarMaterialFailurePolicy.HasFailure(materialResult);
                                }
                                break;
                            case ExtensionBusbarDriveStage.RootCount:
                                ApplyExtensionBusbarAssemblyGlobalVariables(
                                    extensionAssemblyModel, thickness, phaseSpacing, patternQuantity,
                                    driveDimensions: selection == null || selection.DriveDimensions);
                                break;
                            default:
                                throw new InvalidOperationException("存在未支持的伸出排驱动阶段。");
                        }
                    }
                    SaveExtensionBusbarAssembly(extensionAssemblyModel, openedExtensionAssembly);
                }
                finally
                {
                    EnsureAssemblyEditingContext(swApp, rootModel);
                }
            }

            EnsureAssemblyEditingContext(swApp, rootModel);
            if (!rootModel.EditRebuild3()) throw new InvalidOperationException("伸出排驱动完成后总装重建失败。");
            foreach (var component in FindExtensionBusbarAssemblyComponents(rootModel))
            {
                var parts = new List<Component2>();
                CollectExtensionBusbarPartComponentsRecursive(component, parts, IsExtensionBusbarSourcePartComponent);
                var counts = CountExtensionBusbarPhases(parts);
                ExtensionBusbarPhaseDrivePlan.ValidateDrivenPhaseRootCounts(
                    component.Name2, patternQuantity, counts["A"], counts["B"], counts["C"]);
            }
        }

        private void SaveExtensionBusbarAssembly(IModelDoc2 model, bool closeDocument)
        {
            var errors = 0;
            var warnings = 0;
            if (!model.Save3((int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_SaveReferenced),
                ref errors, ref warnings) || errors != 0)
                throw new InvalidOperationException("伸出排装配体保存失败：" + model.GetPathName()
                    + "；errors=" + errors + "；warnings=" + warnings);
            if (closeDocument) swApp.CloseDoc(model.GetTitle());
        }

        /// <summary>
        /// 在柜体总装配体中查找柜内伸出排容器装配体（不含 A/B/C 相子零件）。
        /// </summary>
        private static IEnumerable<IComponent2> FindExtensionBusbarAssemblyComponents(IModelDoc2 rootModel)
        {
            var result = new List<IComponent2>();
            if (rootModel == null)
            {
                return result;
            }

            var rootComponent = rootModel.ConfigurationManager?.ActiveConfiguration?.GetRootComponent3(true) as IComponent2;
            if (rootComponent == null)
            {
                return result;
            }

            CollectExtensionBusbarAssemblyComponents(rootComponent, result);
            return result;
        }

        /// <summary>
        /// 递归收集名称匹配柜内伸出排容器装配体的组件。
        /// 面对面：{A|B}柜内伸出排-{A|B}柜前/后；面对墙：柜内伸出排-柜前/后（位于柜子装配体下）。
        /// </summary>
        private static void CollectExtensionBusbarAssemblyComponents(IComponent2 parentComponent, ICollection<IComponent2> result)
        {
            if (parentComponent == null || result == null)
            {
                return;
            }

            var children = parentComponent.GetChildren() as object[];
            if (children == null || children.Length == 0)
            {
                return;
            }

            foreach (var childObject in children)
            {
                var childComponent = childObject as IComponent2;
                if (childComponent == null)
                {
                    continue;
                }

                try
                {
                    if (childComponent.IsSuppressed())
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                if (IsExtensionBusbarContainerAssembly(childComponent))
                {
                    result.Add(childComponent);
                }

                CollectExtensionBusbarAssemblyComponents(childComponent, result);
            }
        }

        /// <summary>
        /// 判断组件是否为柜内伸出排容器装配体（如“A柜内伸出排-A柜前”或“柜内伸出排-柜前”）。
        /// </summary>
        private static bool IsExtensionBusbarContainerAssembly(IComponent2 component)
        {
            if (component == null)
            {
                return false;
            }

            var normalizedName = NormalizeBusbarComponentName(component.Name2);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                try { normalizedName = NormalizeBusbarComponentName(Path.GetFileNameWithoutExtension(component.GetPathName())); } catch { }
            }

            return !string.IsNullOrWhiteSpace(normalizedName) &&
                   ExtensionBusbarAssemblyNamePattern.IsMatch(normalizedName);
        }

        /// <summary>
        /// 收集伸出排装配体中的阵列源铜排零件（A/B/C 相，名称形如“A柜内伸出排-A-A柜前”或“柜内伸出排-A-柜前”）。
        /// </summary>
        private static List<Component2> CollectExtensionBusbarSourcePartComponents(AssemblyDoc asm)
        {
            return CollectExtensionBusbarPartComponents(asm, IsExtensionBusbarSourcePartComponent);
        }

        private static List<Component2> CollectExtensionBusbarSpecificationPartComponents(AssemblyDoc asm)
        {
            return CollectExtensionBusbarPartComponents(asm, IsExtensionBusbarSpecificationPartComponent);
        }

        private static List<Component2> CollectExtensionBusbarPartComponents(
            AssemblyDoc asm,
            Func<Component2, bool> isTarget)
        {
            var result = new List<Component2>();
            if (asm == null)
            {
                return result;
            }

            var asmModel = (IModelDoc2)asm;
            var rootComponent = asmModel.ConfigurationManager?.ActiveConfiguration?.GetRootComponent3(true) as Component2;
            if (rootComponent == null)
            {
                return result;
            }

            CollectExtensionBusbarPartComponentsRecursive(rootComponent, result, isTarget);
            return result;
        }

        private static void CollectExtensionBusbarPartComponentsRecursive(
            IComponent2 parentComponent,
            ICollection<Component2> result,
            Func<Component2, bool> isTarget)
        {
            if (parentComponent == null || result == null)
            {
                return;
            }

            var children = parentComponent.GetChildren() as object[];
            if (children == null || children.Length == 0)
            {
                return;
            }

            foreach (var childObject in children)
            {
                var childComponent = childObject as Component2;
                if (childComponent == null)
                {
                    continue;
                }

                try
                {
                    if (childComponent.IsSuppressed())
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                if (isTarget(childComponent))
                {
                    result.Add(childComponent);
                    continue;
                }

                CollectExtensionBusbarPartComponentsRecursive(childComponent, result, isTarget);
            }
        }

        private static bool IsExtensionBusbarSpecificationPartComponent(Component2 component)
        {
            if (component == null)
            {
                return false;
            }

            var name = NormalizeBusbarComponentName(component.Name2);
            if (name.IndexOf("柜内伸出排", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            IModelDoc2 model = null;
            try { model = component.GetModelDoc2() as IModelDoc2; } catch { }
            if (model != null)
            {
                return model.GetType() == (int)swDocumentTypes_e.swDocPART;
            }

            string path = null;
            try { path = component.GetPathName(); } catch { }
            return string.IsNullOrWhiteSpace(path) ||
                   string.Equals(Path.GetExtension(path), ".SLDPRT", StringComparison.OrdinalIgnoreCase);
        }

        private static void VerifyExtensionBusbarPartDimensions(
            IModelDoc2 partModel,
            string componentName,
            double width,
            double thickness)
        {
            var featureWidthRead = TryReadSheetMetalWidthFromFeature(partModel, out var featureWidth);
            if (featureWidthRead && Math.Abs(featureWidth - width) > 0.01)
            {
                throw new InvalidOperationException(
                    "伸出排子件“" + componentName + "”的铜排基体排宽重建后为 " +
                    featureWidth.ToString("0.###", CultureInfo.InvariantCulture) +
                    " mm，目标为 " + width.ToString("0.###", CultureInfo.InvariantCulture) +
                    " mm；铜排规格未写入。");
            }

            if (!featureWidthRead)
            {
                VerifyExtensionBusbarPartDimension(
                    partModel,
                    componentName,
                    "排宽",
                    width,
                    new[] { BusbarWidthDimensionName, "D1@铜排基体", "D1@形状草图" });
            }
            var featureThicknessRead = TryReadSheetMetalThicknessFromFeature(partModel, out var featureThickness);
            if (featureThicknessRead && Math.Abs(featureThickness - thickness) > 0.01)
            {
                throw new InvalidOperationException(
                    "伸出排子件“" + componentName + "”的铜排基体排厚重建后为 " +
                    featureThickness.ToString("0.###", CultureInfo.InvariantCulture) +
                    " mm，目标为 " + thickness.ToString("0.###", CultureInfo.InvariantCulture) +
                    " mm；铜排规格未写入。");
            }

            if (!featureThicknessRead)
            {
                VerifyExtensionBusbarPartDimension(
                    partModel,
                    componentName,
                    "排厚",
                    thickness,
                    new[] { "Thickness@钣金", "Thickness@铜排基体", "厚度@钣金", "厚度@铜排基体" });
            }
        }

        private static void VerifyExtensionBusbarPartDimension(
            IModelDoc2 partModel,
            string componentName,
            string dimensionLabel,
            double expectedMm,
            IEnumerable<string> dimensionNames)
        {
            foreach (var dimensionName in dimensionNames)
            {
                Dimension dimension = null;
                try { dimension = partModel.Parameter(dimensionName) as Dimension; } catch { }
                if (dimension == null)
                {
                    continue;
                }

                var actualMm = FromSystemLengthValue(dimension.SystemValue);
                if (Math.Abs(actualMm - expectedMm) > 0.01)
                {
                    throw new InvalidOperationException(
                        "伸出排子件“" + componentName + "”的" + dimensionLabel +
                        "重建后为 " + actualMm.ToString("0.###", CultureInfo.InvariantCulture) +
                        " mm，目标为 " + expectedMm.ToString("0.###", CultureInfo.InvariantCulture) +
                        " mm；铜排规格未写入。");
                }

                return;
            }
        }

        private static bool TryReadSheetMetalWidthFromFeature(IModelDoc2 partModel, out double widthMm)
        {
            widthMm = 0;
            var feature = FindFeature(partModel, BusbarBaseFeatureName, "SMBaseFlange");
            if (feature == null)
            {
                return false;
            }

            IBaseFlangeFeatureData data = null;
            try { data = feature.GetDefinition() as IBaseFlangeFeatureData; } catch { }
            if (data == null)
            {
                return false;
            }

            try
            {
                var value = data.GetType().InvokeMember(
                    "D1EndConditionDistance",
                    System.Reflection.BindingFlags.GetProperty,
                    null,
                    data,
                    null);
                widthMm = FromSystemLengthValue(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            }
            catch
            {
                try { widthMm = FromSystemLengthValue(data.D1OffsetDistance); }
                catch { return false; }
            }

            return widthMm > 0 && !double.IsNaN(widthMm) && !double.IsInfinity(widthMm);
        }

        private static bool TryReadSheetMetalThicknessFromFeature(IModelDoc2 partModel, out double thicknessMm)
        {
            thicknessMm = 0;
            var feature = FindFeature(partModel, BusbarBaseFeatureName, "SMBaseFlange");
            if (feature == null)
            {
                return false;
            }

            IBaseFlangeFeatureData data = null;
            try { data = feature.GetDefinition() as IBaseFlangeFeatureData; } catch { }
            if (data == null)
            {
                return false;
            }

            try { thicknessMm = FromSystemLengthValue(data.Thickness); }
            catch { return false; }
            return thicknessMm > 0 && !double.IsNaN(thicknessMm) && !double.IsInfinity(thicknessMm);
        }

        private static bool IsExtensionBusbarSourcePartComponent(Component2 component)
        {
            if (component == null)
            {
                return false;
            }

            var normalizedName = NormalizeBusbarComponentName(component.Name2);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                try { normalizedName = NormalizeBusbarComponentName(Path.GetFileNameWithoutExtension(component.GetPathName())); } catch { }
            }

            return !string.IsNullOrWhiteSpace(normalizedName) &&
                   ExtensionBusbarSourcePartNamePattern.IsMatch(normalizedName);
        }

        private static string NormalizeBusbarComponentName(string componentName)
        {
            if (string.IsNullOrWhiteSpace(componentName))
            {
                return string.Empty;
            }

            var normalizedName = componentName.Trim();

            var atIndex = normalizedName.IndexOf('@');
            if (atIndex >= 0)
            {
                normalizedName = normalizedName.Substring(0, atIndex);
            }

            var slashIndex = normalizedName.LastIndexOf('/');
            if (slashIndex >= 0)
            {
                normalizedName = normalizedName.Substring(slashIndex + 1);
            }

            normalizedName = Regex.Replace(normalizedName, @"<\d+>$", string.Empty);
            normalizedName = Regex.Replace(normalizedName, @"-\d+$", string.Empty);

            return normalizedName.Trim();
        }

        private static bool TrySetAssemblyGlobalVariable(IEquationMgr equationManager, string globalVariableName, object value)
        {
            if (equationManager == null || string.IsNullOrWhiteSpace(globalVariableName))
            {
                return false;
            }

            var equationCount = equationManager.GetCount();
            for (var i = 0; i < equationCount; i++)
            {
                var equation = equationManager.Equation[i] as string;
                if (string.IsNullOrWhiteSpace(equation) || !EquationMatchesGlobalVariable(equation, globalVariableName))
                {
                    continue;
                }

                equationManager.Equation[i] = BuildGlobalVariableEquation(globalVariableName, value);
                return true;
            }

            return false;
        }

        private static bool EquationMatchesGlobalVariable(string equation, string globalVariableName)
        {
            var left = equation;
            var equalSignIndex = equation.IndexOf('=');
            if (equalSignIndex >= 0)
            {
                left = equation.Substring(0, equalSignIndex);
            }

            left = left.Trim();
            if (left.Length >= 2 && left[0] == '"' && left[left.Length - 1] == '"')
            {
                left = left.Substring(1, left.Length - 2);
            }

            return string.Equals(left, globalVariableName, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildGlobalVariableEquation(string globalVariableName, object value)
        {
            if (value == null)
            {
                return $"\"{globalVariableName}\"=\"\"";
            }

            if (value is bool booleanValue)
            {
                return $"\"{globalVariableName}\"={(booleanValue ? 1 : 0)}";
            }

            if (value is string stringValue)
            {
                return $"\"{globalVariableName}\"=\"{stringValue.Replace("\"", "\"\"")}\"";
            }

            if (value is IFormattable formattableValue)
            {
                return $"\"{globalVariableName}\"={formattableValue.ToString(null, CultureInfo.InvariantCulture)}";
            }

            return $"\"{globalVariableName}\"={value}";
        }

        /// <summary>
        /// 为装配体中所有目标铜排零件批量应用材质。
        /// </summary>
        /// <param name="assemblyModel">目标装配体模型。</param>
        /// <param name="busbarMaterial">铜排材质名称。</param>
        /// <returns>材质应用结果统计。</returns>
        public BusbarMaterialApplyResult ApplyBusbarMaterials(IModelDoc2 assemblyModel, string busbarMaterial)
            => ApplyBusbarMaterials(assemblyModel, busbarMaterial, null);

        internal BusbarMaterialApplyResult ApplyBusbarMaterials(IModelDoc2 assemblyModel, string busbarMaterial,
            ISet<string> preservedPaths)
        {
            var result = new BusbarMaterialApplyResult
            {
                MaterialName = SolidWorksMaterialHelper.NormalizeBusbarMaterial(busbarMaterial)
            };

            if (swApp == null)
            {
                result.ErrorMessage = "未获取到 SolidWorks 应用对象，无法设置铜排材质。";
                return result;
            }

            if (assemblyModel == null)
            {
                result.ErrorMessage = "未提供目标装配体，无法设置铜排材质。";
                return result;
            }

            if (!(assemblyModel is AssemblyDoc asm) || assemblyModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                result.ErrorMessage = "目标文档不是装配体，无法设置铜排材质。";
                return result;
            }

            if (!SolidWorksMaterialHelper.TryResolveBusbarMaterialDatabasePath(swApp, out var databasePath, out _) ||
                !SolidWorksMaterialHelper.TryValidateBusbarMaterialDatabase(databasePath, result.MaterialName))
            {
                // 整个材质步骤直接跳过，不能继续写入未实际应用的材质名称、中文材质或净重。
                result.IsDefaultMaterialFallback = true;
                return result;
            }
            result.IsDatabaseResolved = true;
            result.DatabasePath = databasePath;

            TryResolveLightweightComponents(asm);

            var allBusbarComponents = asm.GetAllBusbarComponents(false);
            if (allBusbarComponents == null || allBusbarComponents.Count == 0)
            {
                result.ErrorMessage = "目标装配体中未找到铜排零件，无法设置铜排材质。";
                return result;
            }

            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var component in allBusbarComponents)
            {
                if (component == null)
                {
                    result.SkippedCount++;
                    continue;
                }

                try
                {
                    if (component.IsSuppressed())
                    {
                        result.SkippedCount++;
                        continue;
                    }
                }
                catch
                {
                    result.SkippedCount++;
                    continue;
                }

                string componentName = null;
                try { componentName = component.Name2; } catch { }

                string compPath = null;
                try { compPath = component.GetPathName(); } catch { }

                var compModel = component.IGetModelDoc() as ModelDoc2;
                if (string.IsNullOrWhiteSpace(compPath))
                {
                    try { compPath = compModel?.GetPathName(); } catch { }
                }

                if (string.IsNullOrWhiteSpace(compPath) || compModel == null)
                {
                    result.SkippedCount++;
                    continue;
                }

                if (compModel.GetType() != (int)swDocumentTypes_e.swDocPART)
                {
                    result.SkippedCount++;
                    continue;
                }

                var partDoc = compModel as IPartDoc;
                if (partDoc == null)
                {
                    result.SkippedCount++;
                    continue;
                }

                string compFullPath;
                try { compFullPath = Path.GetFullPath(compPath); }
                catch { compFullPath = compPath; }

                string referencedConfig = null;
                try { referencedConfig = component.ReferencedConfiguration; } catch { }
                if (referencedConfig == null)
                {
                    referencedConfig = string.Empty;
                }

                var processKey = compFullPath + "|" + referencedConfig;
                if (preservedPaths != null && preservedPaths.Contains(compFullPath))
                {
                    result.SuccessCount++;
                    processed.Add(processKey);
                    continue;
                }
                if (processed.Contains(processKey))
                {
                    continue;
                }

                string originalConfig = null;
                try { originalConfig = compModel.ConfigurationManager.ActiveConfiguration.Name; } catch { }

                if (!string.IsNullOrWhiteSpace(referencedConfig) &&
                    !string.Equals(originalConfig, referencedConfig, StringComparison.OrdinalIgnoreCase))
                {
                    try { compModel.ShowConfiguration2(referencedConfig); } catch { }
                }

                var materialAlreadyApplied = IsPartMaterialAlreadyApplied(
                        partDoc,
                        referencedConfig,
                        result.MaterialName);
                if (!materialAlreadyApplied
                    && !TrySetPartMaterial(partDoc, referencedConfig, databasePath, result.MaterialName))
                {
                    result.FailedCount++;
                    result.Failures.Add($"{componentName ?? compFullPath}：SetMaterialPropertyName2 失败");
                    processed.Add(processKey);
                    continue;
                }

                // 设置铜排材质后，同步更新铜排零件“材质”自定义属性（写入应用的材质名）。
                // 写入文档级“自定义”属性（配置传空字符串），而非“配置特定”。
                try
                {
                    compModel.AddPropertyToModel(MaterialPropertyName, result.MaterialName ?? string.Empty, string.Empty);
                }
                catch
                {
                    // ignore
                }

                // 设置铜排材质后，同步更新铜排零件“中文材质”自定义属性（按材质类型 TMY / TMQY）。
                // 写入文档级“自定义”属性（配置传空字符串），而非“配置特定”。
                try
                {
                    var chineseMaterialValue = GetChineseMaterialValue(result.MaterialName);
                    compModel.AddPropertyToModel(ChineseMaterialPropertyName, chineseMaterialValue, string.Empty);
                }
                catch
                {
                    // ignore
                }

                // 更新铜排展开长度：在保存/写属性前，按钣金 FlatPattern 边界框刷新“铜排展开长度”属性。
                // 该展开长度后续用于净重的孔减重计算（TMQY 圆角排的展开长公式）。
                try
                {
                    UpdateFlatPatternForPart(compModel, referencedConfig);
                }
                catch
                {
                    // ignore
                }

                WriteConfigurationCustomProperty(compModel, referencedConfig,
                    PartTypePropertyName, BusbarPartTypeValue);

                // 设置铜排材质后，同步更新铜排零件“净重”自定义属性。
                // 生成铜排时尚未开孔，零件体积为满排（无孔）体积，不能直接用体积×密度。
                // 净重 = 展开长度 × 净截面积（圆角排减 0.8584×r²）× 密度 − 两端工艺库开孔减重，并向下截断到 2 位小数。
                try
                {
                    var isFullRoundEdge = string.Equals(
                        result.MaterialName?.Trim(),
                        SolidWorksMaterialHelper.FullRoundEdgeBusbarMaterial,
                        StringComparison.Ordinal);
                    var fullWeightKg = TryCalculateBusbarFullWeightKg(
                        compModel,
                        referencedConfig,
                        isFullRoundEdge);
                    if (fullWeightKg.HasValue)
                    {
                        // 两端工艺库开孔减重：按排宽查询开孔工艺库，扣除两端孔的重量。
                        var holeDeductionKg = TryCalculateBusbarHoleDeductionKg(
                            compModel,
                            referencedConfig);
                        var finalWeightKg = fullWeightKg.Value - holeDeductionKg;
                        if (finalWeightKg < 0)
                        {
                            finalWeightKg = 0;
                        }

                        finalWeightKg = Math.Floor(finalWeightKg * 100) / 100;
                        var netWeightValue = finalWeightKg.ToString("0.#####", CultureInfo.InvariantCulture);
                        compModel.AddPropertyToModel(NetWeightPropertyName, netWeightValue, string.Empty);
                    }
                }
                catch
                {
                    // ignore
                }

                if (!string.IsNullOrWhiteSpace(originalConfig) &&
                    !string.IsNullOrWhiteSpace(referencedConfig) &&
                    !string.Equals(originalConfig, referencedConfig, StringComparison.OrdinalIgnoreCase))
                {
                    try { compModel.ShowConfiguration2(originalConfig); } catch { }
                }

                result.SuccessCount++;
                processed.Add(processKey);
            }

            return result;
        }

        /// <summary>
        /// 在铜排开孔完成后，按绝缘子开孔草图数量重新计算质量属性，并回写“净重(Kg)”。
        ///
        /// 生成阶段的净重是基于无孔铜排和工艺库端孔的预估值；绝缘子开孔在后续
        /// “刷新铜排开孔”流程中才写入零件，因此该流程必须重新读取“绝缘子开孔草图”数量，
        /// 才能把实际绝缘子孔的减重纳入最终净重。
        /// </summary>
        /// <param name="assemblyModel">已完成开孔刷新的总装配体模型。</param>
        /// <returns>质量属性刷新结果。</returns>
        public BusbarMassPropertyRefreshResult RefreshBusbarMassProperties(IModelDoc2 assemblyModel)
        {
            var result = new BusbarMassPropertyRefreshResult();

            if (swApp == null)
            {
                result.ErrorMessage = "未获取到 SolidWorks 应用对象，无法刷新铜排质量属性。";
                return result;
            }

            if (assemblyModel == null ||
                !(assemblyModel is AssemblyDoc assemblyDocument) ||
                assemblyModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                result.ErrorMessage = "目标文档不是装配体，无法刷新铜排质量属性。";
                return result;
            }

            try
            {
                TryResolveLightweightComponents(assemblyDocument);
                assemblyModel.EditRebuild3();
            }
            catch
            {
                // 单个零件的质量属性仍可继续尝试，避免一个装配体重建异常阻断全部回写。
            }

            List<Component2> busbarComponents = null;
            try
            {
                busbarComponents = assemblyDocument.GetAllBusbarComponents(false);
                busbarComponents = (busbarComponents ?? new List<Component2>())
                    .Where(GeneratedBusbarSubassemblyIdentity
                        .IsUnderGeneratedSubassembly)
                    .ToList();
            }
            catch (Exception componentReadException)
            {
                result.ErrorMessage = "读取装配体铜排组件失败：" + componentReadException.Message;
                return result;
            }

            if (busbarComponents == null || busbarComponents.Count == 0)
            {
                result.ErrorMessage =
                    "目标装配体中未找到 RD类型=铜排子装配 下的铜排零件，无法刷新质量属性。";
                return result;
            }

            var processedComponentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var busbarComponent in busbarComponents)
            {
                if (busbarComponent == null)
                {
                    result.SkippedCount++;
                    continue;
                }

                try
                {
                    if (busbarComponent.IsSuppressed())
                    {
                        result.SkippedCount++;
                        continue;
                    }
                }
                catch
                {
                    result.SkippedCount++;
                    continue;
                }

                string componentLabel = null;
                try { componentLabel = busbarComponent.Name2; } catch { }

                string componentPath = null;
                try { componentPath = busbarComponent.GetPathName(); } catch { }

                var componentModel = busbarComponent.IGetModelDoc() as ModelDoc2;
                if (string.IsNullOrWhiteSpace(componentPath))
                {
                    try { componentPath = componentModel?.GetPathName(); } catch { }
                }

                if (componentModel == null ||
                    componentModel.GetType() != (int)swDocumentTypes_e.swDocPART ||
                    string.IsNullOrWhiteSpace(componentPath))
                {
                    result.SkippedCount++;
                    continue;
                }

                string referencedConfiguration = null;
                try { referencedConfiguration = busbarComponent.ReferencedConfiguration; } catch { }
                referencedConfiguration = referencedConfiguration ?? string.Empty;

                string fullComponentPath;
                try { fullComponentPath = Path.GetFullPath(componentPath); }
                catch { fullComponentPath = componentPath; }

                var processedComponentKey = fullComponentPath + "|" + referencedConfiguration;
                if (!processedComponentKeys.Add(processedComponentKey))
                {
                    continue;
                }

                string originalConfiguration = null;
                try { originalConfiguration = componentModel.ConfigurationManager?.ActiveConfiguration?.Name; }
                catch { }

                try
                {
                    if (!string.IsNullOrWhiteSpace(referencedConfiguration) &&
                        !string.Equals(
                            originalConfiguration,
                            referencedConfiguration,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (!componentModel.ShowConfiguration2(referencedConfiguration))
                        {
                            throw new InvalidOperationException(
                                "无法激活引用配置“" + referencedConfiguration + "”。");
                        }
                    }

                    componentModel.EditRebuild3();

                    // 开孔流程会为每个绝缘子孔生成一条独立的“绝缘子开孔草图”。
                    // 这里直接按草图数量计算绝缘子孔减重，不能用固定孔数或仅依赖
                    // SolidWorks 当前质量缓存，否则刷新开孔后的净重仍会漏掉绝缘子孔。
                    if (!TryCountInsulatorOpeningSketches(
                            componentModel,
                            out var insulatorOpeningSketchCount))
                    {
                        throw new InvalidOperationException("无法读取铜排特征树中的绝缘子开孔草图数量。");
                    }
                    result.InsulatorHoleCount += insulatorOpeningSketchCount;
                    var fullWeightKg = TryCalculateBusbarFullWeightKg(
                        componentModel,
                        referencedConfiguration,
                        IsFullRoundEdgeBusbar(componentModel, referencedConfiguration));
                    if (!fullWeightKg.HasValue)
                    {
                        throw new InvalidOperationException("无法计算铜排无孔基准重量。");
                    }

                    var processHoleDeductionKg = TryCalculateBusbarHoleDeductionKg(
                        componentModel,
                        referencedConfiguration);
                    var insulatorHoleDeductionKg = TryCalculateInsulatorHoleDeductionKg(
                        componentModel,
                        referencedConfiguration,
                        insulatorOpeningSketchCount);
                    if (!insulatorHoleDeductionKg.HasValue)
                    {
                        throw new InvalidOperationException("无法根据绝缘子开孔草图计算孔减重。");
                    }
                    var finalWeightKg = fullWeightKg.Value -
                        processHoleDeductionKg -
                        insulatorHoleDeductionKg.Value;
                    if (finalWeightKg < 0)
                    {
                        finalWeightKg = 0;
                    }

                    // 与生成阶段保持一致：净重按公斤向下截断到 2 位小数，写入文档级属性。
                    var truncatedMassKg = Math.Floor(finalWeightKg * 100d) / 100d;
                    var netWeightText = truncatedMassKg.ToString("0.#####", CultureInfo.InvariantCulture);
                    componentModel.AddPropertyToModel(
                        NetWeightPropertyName,
                        netWeightText,
                        string.Empty);

                    // 开孔和质量属性都属于零件文档状态，显式保存零件，避免只保存总装后
                    // “净重(Kg)”仍停留在内存中的情况。
                    TrySaveModelSilent(componentModel);
                    result.SuccessCount++;
                }
                catch (Exception refreshException)
                {
                    result.FailedCount++;
                    result.Failures.Add(
                        (componentLabel ?? fullComponentPath) + "：" + refreshException.Message);
                }
                finally
                {
                    if (!string.IsNullOrWhiteSpace(originalConfiguration) &&
                        !string.IsNullOrWhiteSpace(referencedConfiguration) &&
                        !string.Equals(
                            originalConfiguration,
                            referencedConfiguration,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        try { componentModel.ShowConfiguration2(originalConfiguration); } catch { }
                    }
                }
            }

            try
            {
                assemblyModel.EditRebuild3();
                TrySaveModelSilent(assemblyModel);
            }
            catch
            {
                // 零件属性已经逐件保存；总装保存失败不覆盖已完成的统计结果。
            }

            return result;
        }

        /// <summary>
        /// 统计铜排零件当前特征树中的绝缘子开孔草图数量。
        /// 刷新开孔前旧的绝缘子开孔特征会被删除，刷新后每个实际孔对应一条
        /// “绝缘子开孔草图” ProfileFeature，因此该数量就是本次应扣除的绝缘子孔数。
        /// </summary>
        private static bool TryCountInsulatorOpeningSketches(
            ModelDoc2 partModel,
            out int insulatorOpeningSketchCount)
        {
            insulatorOpeningSketchCount = 0;
            if (partModel == null)
            {
                return false;
            }

            try
            {
                var profileFeatureNames = partModel.GetFeatureNamesByType("ProfileFeature");
                if (profileFeatureNames == null)
                {
                    return true;
                }

                insulatorOpeningSketchCount = profileFeatureNames.Count(featureName =>
                    !string.IsNullOrWhiteSpace(featureName) &&
                    featureName.IndexOf("绝缘子开孔草图", StringComparison.OrdinalIgnoreCase) >= 0);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 按绝缘子开孔草图数量计算绝缘子孔减重（kg）。
        /// 孔径和孔形仍从当前排宽对应的开孔工艺库读取，数量只来自零件特征树。
        /// </summary>
        private double? TryCalculateInsulatorHoleDeductionKg(
            ModelDoc2 partModel,
            string referencedConfiguration,
            int insulatorOpeningSketchCount)
        {
            if (partModel == null || insulatorOpeningSketchCount <= 0)
            {
                return 0d;
            }

            var busbarWidth = TryGetBusbarWidthForHoleDeduction(
                partModel,
                referencedConfiguration);
            if (busbarWidth <= 0)
            {
                return null;
            }

            MatingFaceOpenHoleInfo openHoleInfo;
            try
            {
                openHoleInfo = DataHelper.GetMatingFaceOpenHole(
                    busbarWidth,
                    busbarWidth,
                    "水平",
                    BusbarPartTypeValue,
                    BusbarPartTypeValue,
                    busbarWidth,
                    busbarWidth);
            }
            catch
            {
                return null;
            }

            if (openHoleInfo == null)
            {
                return null;
            }

            var singleHoleAreaMm2 = CalculateSingleHoleArea(openHoleInfo);
            var thicknessMm = TryGetPartThickness(partModel);
            if (singleHoleAreaMm2 <= 0 || thicknessMm <= 0)
            {
                return null;
            }

            if (!SolidWorksMaterialHelper.TryGetPartMaterialDensityKgPerM3(
                    partModel as IPartDoc,
                    partModel,
                    referencedConfiguration,
                    swApp,
                    out var densityKgPerM3,
                    out _))
            {
                return null;
            }

            // 绝缘子孔总减重 = 单孔面积 × 特征树中的实际孔数 × 排厚 × 密度。
            return singleHoleAreaMm2 * insulatorOpeningSketchCount * thicknessMm *
                   1e-9 * densityKgPerM3;
        }

        /// <summary>
        /// 读取当前配置的铜排材质，用于判断圆角排的无孔截面积。
        /// </summary>
        private static bool IsFullRoundEdgeBusbar(
            ModelDoc2 partModel,
            string referencedConfiguration)
        {
            var partDocument = partModel as IPartDoc;
            if (partDocument == null)
            {
                return false;
            }

            var configurationCandidates = new[]
            {
                referencedConfiguration,
                "默认",
                "Default",
                string.Empty
            };

            foreach (var configurationName in configurationCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var databasePath = string.Empty;
                    var materialName = partDocument.GetMaterialPropertyName2(
                        configurationName ?? string.Empty,
                        out databasePath);
                    if (string.Equals(
                            materialName?.Trim(),
                            SolidWorksMaterialHelper.FullRoundEdgeBusbarMaterial,
                            StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                catch
                {
                    // 尝试下一个配置候选名。
                }
            }

            return false;
        }

        private static bool IsPartMaterialAlreadyApplied(
            IPartDoc partDoc,
            string referencedConfiguration,
            string expectedMaterialName)
        {
            if (partDoc == null || string.IsNullOrWhiteSpace(expectedMaterialName))
            {
                return false;
            }

            var configCandidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(referencedConfiguration))
            {
                configCandidates.Add(referencedConfiguration.Trim());
            }
            else
            {
                configCandidates.Add("默认");
                configCandidates.Add("Default");
            }

            foreach (var configurationName in configCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string currentDatabasePath;
                    var currentMaterialName = partDoc.GetMaterialPropertyName2(
                        configurationName,
                        out currentDatabasePath);
                    if (string.Equals(
                            currentMaterialName?.Trim(),
                            expectedMaterialName.Trim(),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    // 继续检查兼容配置名称。
                }
            }

            return false;
        }

        /// <summary>
        /// 为零件指定配置设置材料；依次尝试引用配置、默认中文/英文配置名。
        /// </summary>
        private static bool TrySetPartMaterial(IPartDoc partDoc, string referencedConfiguration, string databasePath, string materialName)
        {
            if (partDoc == null || string.IsNullOrWhiteSpace(databasePath) || string.IsNullOrWhiteSpace(materialName))
            {
                return false;
            }

            var configCandidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(referencedConfiguration))
            {
                configCandidates.Add(referencedConfiguration.Trim());
            }

            configCandidates.Add("默认");
            configCandidates.Add("Default");

            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var configName in configCandidates)
            {
                if (!tried.Add(configName))
                {
                    continue;
                }

                try
                {
                    partDoc.SetMaterialPropertyName2(configName, databasePath, materialName);
                    return true;
                }
                catch
                {
                    // 尝试下一个配置名
                }
            }

            return false;
        }

        /// <summary>
        /// 解析铜排规格文本并提取宽度与厚度。
        /// </summary>
        /// <param name="specification">规格文本。</param>
        /// <param name="width">输出宽度值。</param>
        /// <param name="thickness">输出厚度值。</param>
        /// <returns>解析成功返回 true，否则返回 false。</returns>
        private static bool TryParseSpecification(string specification, out double width, out double thickness)
        {
            width = 0;
            thickness = 0;

            if (string.IsNullOrWhiteSpace(specification)) return false;

            // 兼容："宽x厚" / "宽×厚" / "宽*厚"
            var spec = specification.Trim();
            var parts = spec
                .Replace('×', 'x')
                .Replace('*', 'x')
                .Split(new[] { 'x', 'X' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 2) return false;

            if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out width) &&
                !double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out width))
            {
                return false;
            }

            if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out thickness) &&
                !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out thickness))
            {
                return false;
            }

            return width > 0 && thickness > 0;
        }

        /// <summary>
        /// 将排宽与排厚格式化为“铜排规格”属性值（排宽*排厚）。
        /// </summary>
        /// <param name="width">铜排宽度（mm）。</param>
        /// <param name="thickness">铜排厚度（mm）。</param>
        /// <returns>形如 “100*10” 的铜排规格文本。</returns>
        private static string FormatSpecificationValue(double width, double thickness)
        {
            return FormatDimension(width) + "*" + FormatDimension(thickness);
        }

        /// <summary>
        /// 将排宽与排厚格式化为“材料牌号下”属性值（排宽X排厚 GB/T 5585.1）。
        /// </summary>
        /// <param name="width">铜排宽度（mm）。</param>
        /// <param name="thickness">铜排厚度（mm）。</param>
        /// <returns>形如 “100X10 GB/T 5585.1” 的材料牌号文本。</returns>
        private static string FormatMaterialGradeBelowValue(double width, double thickness)
        {
            return FormatDimension(width) + "X" + FormatDimension(thickness) + " " + MaterialGradeStandardSuffix;
        }

        /// <summary>
        /// 根据铜排材质类型返回“中文材质”属性值：全圆边铜母线为 TMQY，其余（直角铜母线）为 TMY。
        /// </summary>
        /// <param name="materialName">已规范化的铜排材质名称。</param>
        /// <returns>TMY 或 TMQY。</returns>
        private static string GetChineseMaterialValue(string materialName)
        {
            return string.Equals(materialName?.Trim(), SolidWorksMaterialHelper.FullRoundEdgeBusbarMaterial, StringComparison.Ordinal)
                ? FullRoundEdgeBusbarChineseMaterial
                : RightAngleBusbarChineseMaterial;
        }

        /// <summary>
        /// 更新铜排零件的展开长度：读取钣金 FlatPattern 特征的边界框草图，
        /// 计算展开长度并写入「铜排展开长度」自定义属性（写入引用配置）。
        /// 逻辑对齐客户项目 FreeVerticalBendBusbarCreatorHelper.UpdateFlatPattern。
        /// </summary>
        /// <param name="partModel">铜排零件模型。</param>
        /// <param name="referencedConfig">零件引用配置名称。</param>
        private void UpdateFlatPatternForPart(ModelDoc2 partModel, string referencedConfig)
        {
            if (partModel == null) return;

            double? flatLength = TryGetFlatPatternBoundingLength(partModel);
            if (!flatLength.HasValue || flatLength.Value <= 0)
            {
                return;
            }

            var flatLengthText = Math.Round(flatLength.Value, 2)
                .ToString(CultureInfo.InvariantCulture);

            // 展开长度写入引用配置（若为空则写文档级）。
            partModel.AddPropertyToModel(
                UnfoldedLengthPropertyName,
                flatLengthText,
                referencedConfig ?? string.Empty);
        }

        /// <summary>
        /// 从钣金 FlatPattern 特征的边界框草图读取展开长度（mm）。
        /// 遍历反向特征找到 FlatPattern，再从其子特征中定位边界框草图（Bounding-Box / 边界框），
        /// 取非折弯线段的长度作为展开长度；当厚度接近 boundingLength 时交换长宽，保证取长边。
        /// </summary>
        /// <param name="partModel">铜排零件模型。</param>
        /// <returns>展开长度（mm）；无法读取时返回 null。</returns>
        private static double? TryGetFlatPatternBoundingLength(ModelDoc2 partModel)
        {
            if (partModel == null) return null;

            try
            {
                var featureCount = partModel.GetFeatureCount();
                for (var i = 0; i < featureCount; i++)
                {
                    var curFeat = partModel.FeatureByPositionReverse(i) as Feature;
                    if (curFeat == null) break;
                    if (curFeat.GetTypeName() != "FlatPattern") continue;

                    double boundingLength = 0;
                    double boundingWidth = 0;

                    var swSubFeat = curFeat.GetFirstSubFeature() as Feature;
                    while (swSubFeat != null)
                    {
                        if (swSubFeat.GetTypeName() == "ProfileFeature")
                        {
                            var sketchSpc = swSubFeat.GetSpecificFeature2() as Sketch;
                            if (sketchSpc == null)
                            {
                                swSubFeat = swSubFeat.GetNextSubFeature() as Feature;
                                continue;
                            }

                            var vSketchSeg = sketchSpc.GetSketchSegments() as object[];
                            if (vSketchSeg == null)
                            {
                                swSubFeat = swSubFeat.GetNextSubFeature() as Feature;
                                continue;
                            }

                            var isBoundingBox = swSubFeat.Name.Contains("Bounding-Box")
                                || swSubFeat.Name.Contains("边界框");

                            foreach (var seg in vSketchSeg)
                            {
                                var swSketchSeg = seg as SketchSegment;
                                if (swSketchSeg == null) continue;

                                // 非折弯线且为边界框草图时，按线段 ID 区分长/宽。
                                if (!swSketchSeg.IsBendLine() && isBoundingBox)
                                {
                                    var vID = swSketchSeg.GetID() as int[];
                                    if (vID != null && vID.Length > 1)
                                    {
                                        var len = swSketchSeg.GetLength() * 1000d;
                                        if (vID[1] == 1)
                                        {
                                            boundingLength = len;
                                        }
                                        else if (vID[1] == 3 || vID[1] == 4)
                                        {
                                            boundingWidth = len;
                                        }
                                    }
                                }
                            }
                        }

                        swSubFeat = swSubFeat.GetNextSubFeature() as Feature;
                    }

                    if (boundingLength > 0)
                    {
                        // 厚度接近 boundingLength 时交换长宽，保证展开长度为长边（对齐客户逻辑）。
                        double thickness = 0;
                        try { thickness = TryGetPartThickness(partModel); } catch { }

                        if (thickness > 0 && Math.Abs(thickness - boundingLength) < 0.01 && boundingWidth > 0)
                        {
                            return boundingWidth;
                        }

                        return boundingLength;
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 读取铜排零件厚度（mm）：优先读「Thickness@钣金」/「Thickness@铜排基体」参数，失败时返回 0。
        /// </summary>
        private static double TryGetPartThickness(ModelDoc2 partModel)
        {
            if (partModel == null) return 0;

            foreach (var dimName in new[] { "Thickness@钣金", "Thickness@铜排基体", "厚度@钣金", "厚度@铜排基体" })
            {
                try
                {
                    var dim = partModel.Parameter(dimName) as Dimension;
                    if (dim != null)
                    {
                        return FromSystemLengthValue(dim.SystemValue);
                    }
                }
                catch
                {
                    // 尝试下一个尺寸名。
                }
            }

            return 0;
        }

        /// <summary>
        /// 计算铜排满排（无孔）重量（kg）。
        /// 生成铜排时尚未开孔，零件体积为满排体积，不能直接用体积×密度。
        /// 满排重量 = 展开长度(mm) × 净截面积(mm²) × 密度(kg/m³) × 1e-9。
        /// 净截面积：圆角排（TMQY）为 宽×厚 − 0.8584×r²（4 个圆角减量，r=厚/2）；直角排（TMY）/铝排为 宽×厚。
        /// </summary>
        /// <param name="partModel">铜排零件模型。</param>
        /// <param name="referencedConfig">零件引用配置名称。</param>
        /// <param name="isFullRoundEdge">是否为全圆边铜母线（TMQY 圆角排），需减圆角部分。</param>
        /// <returns>满排重量（kg）；无法计算时返回 null。</returns>
        private double? TryCalculateBusbarFullWeightKg(
            ModelDoc2 partModel,
            string referencedConfig,
            bool isFullRoundEdge)
        {
            if (partModel == null) return null;

            var flatLength = TryGetFlatPatternBoundingLength(partModel);
            if (!flatLength.HasValue || flatLength.Value <= 0)
            {
                // 回退：读「铜排展开长度」属性。
                try
                {
                    var flatLengthText = partModel.GetCustomInfoValue(
                        referencedConfig ?? string.Empty,
                        UnfoldedLengthPropertyName);
                    if (!string.IsNullOrWhiteSpace(flatLengthText) &&
                        double.TryParse(flatLengthText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    {
                        flatLength = parsed;
                    }
                    else if (!string.IsNullOrWhiteSpace(flatLengthText) &&
                        double.TryParse(flatLengthText, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed))
                    {
                        flatLength = parsed;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            if (!flatLength.HasValue || flatLength.Value <= 0) return null;

            var width = TryGetBusbarWidthForHoleDeduction(partModel, referencedConfig);
            if (width <= 0) return null;

            var thickness = TryGetPartThickness(partModel);
            if (thickness <= 0) return null;

            double density;
            if (!SolidWorksMaterialHelper.TryGetPartMaterialDensityKgPerM3(
                    partModel as IPartDoc,
                    partModel,
                    referencedConfig,
                    swApp,
                    out density,
                    out _))
            {
                return null;
            }

            // 净截面积(mm²)：圆角排（TMQY）减 4 个圆角面积（0.8584×r²，r=厚/2）；直角排/铝排为宽×厚。
            double sectionalArea = width * thickness;
            if (isFullRoundEdge)
            {
                var cornerRadius = thickness / 2.0; // 圆角半径 = 排厚的一半。
                sectionalArea = width * thickness - (0.8584 * cornerRadius * cornerRadius);
            }

            if (sectionalArea <= 0) return null;

            // 满排重量(kg) = 展开长(mm) × 净截面积(mm²) × 密度(kg/m³) × 1e-9（mm³ → m³）。
            return flatLength.Value * sectionalArea * density * 1e-9;
        }

        /// <summary>
        /// 计算铜排两端工艺库开孔的减重（kg）。
        /// 按排宽查询开孔工艺库（DataHelper.GetMatingFaceOpenHole），得到单端孔参数（孔形/孔径/孔数），
        /// 两端各开一套孔（总孔数 = 2 × 单端孔数），按孔面积 × 厚度 × 密度扣除。
        /// </summary>
        /// <param name="partModel">铜排零件模型。</param>
        /// <param name="referencedConfig">零件引用配置名称。</param>
        /// <returns>孔减重（kg）；无法计算时返回 0。</returns>
        private double TryCalculateBusbarHoleDeductionKg(
            ModelDoc2 partModel,
            string referencedConfig)
        {
            if (partModel == null) return 0;

            var busbarWidth = TryGetBusbarWidthForHoleDeduction(partModel, referencedConfig);
            if (busbarWidth <= 0) return 0;

            MatingFaceOpenHoleInfo openHoleInfo = null;
            try
            {
                openHoleInfo = DataHelper.GetMatingFaceOpenHole(
                    busbarWidth,
                    busbarWidth,
                    "水平",
                    BusbarPartTypeValue,
                    BusbarPartTypeValue,
                    busbarWidth,
                    busbarWidth);
            }
            catch
            {
                return 0;
            }

            if (openHoleInfo == null) return 0;

            var singleEndHoleCount = ResolveSingleEndHoleCount(openHoleInfo);
            if (singleEndHoleCount <= 0) return 0;

            var singleHoleArea = CalculateSingleHoleArea(openHoleInfo);
            if (singleHoleArea <= 0) return 0;

            var thickness = TryGetPartThickness(partModel);
            if (thickness <= 0) return 0;

            double density;
            if (!SolidWorksMaterialHelper.TryGetPartMaterialDensityKgPerM3(
                    partModel as IPartDoc,
                    partModel,
                    referencedConfig,
                    swApp,
                    out density,
                    out _))
            {
                return 0;
            }

            // 两端各一套孔：总孔数 = 2 × 单端孔数。
            var totalHoleCount = singleEndHoleCount * 2;
            var totalHoleAreaMm2 = singleHoleArea * totalHoleCount;

            // 孔减重(kg) = 孔总面积(mm²) × 厚度(mm) × 1e-9(mm³→m³) × 密度(kg/m³)。
            var holeDeductionKg = totalHoleAreaMm2 * thickness * 1e-9 * density;
            return holeDeductionKg;
        }

        /// <summary>
        /// 读取铜排排宽（mm），供孔减重查询工艺库使用。
        /// 优先读「W@铜排基体」参数；失败时从引用配置的「铜排规格」属性解析（宽*厚）。
        /// </summary>
        private static double TryGetBusbarWidthForHoleDeduction(ModelDoc2 partModel, string referencedConfig)
        {
            if (partModel == null) return 0;

            foreach (var dimName in new[] { "W@铜排基体", "D1@铜排基体" })
            {
                try
                {
                    var dim = partModel.Parameter(dimName) as Dimension;
                    if (dim != null)
                    {
                        var width = FromSystemLengthValue(dim.SystemValue);
                        if (width > 0) return width;
                    }
                }
                catch
                {
                    // 尝试下一个尺寸名。
                }
            }

            // 回退：从引用配置的「铜排规格」属性解析排宽。
            try
            {
                var specification = partModel.GetCustomInfoValue(referencedConfig ?? string.Empty, SpecificationPropertyName);
                if (!string.IsNullOrWhiteSpace(specification) &&
                    TryParseSpecification(specification, out var width, out _))
                {
                    return width;
                }
            }
            catch
            {
                // ignore
            }

            return 0;
        }

        /// <summary>
        /// 解析单端孔数：优先使用工艺库的 HoleQty，缺失时按 HoleLocation 坐标串项数统计。
        /// </summary>
        private static int ResolveSingleEndHoleCount(MatingFaceOpenHoleInfo openHoleInfo)
        {
            if (openHoleInfo.HoleQty > 0)
            {
                return openHoleInfo.HoleQty;
            }

            if (!string.IsNullOrWhiteSpace(openHoleInfo.HoleLocation))
            {
                var items = openHoleInfo.HoleLocation.Split(';');
                var count = items.Count(item => !string.IsNullOrWhiteSpace(item));
                if (count > 0)
                {
                    return count;
                }
            }

            return 0;
        }

        /// <summary>
        /// 计算单个孔的净面积（mm²）：圆孔 π×(D/2)²；腰孔 π×(D/2)² + (X−D)×D。
        /// </summary>
        private static double CalculateSingleHoleArea(MatingFaceOpenHoleInfo openHoleInfo)
        {
            if (openHoleInfo.HoleD <= 0) return 0;

            var radius = openHoleInfo.HoleD / 2d;
            var circleArea = Math.PI * radius * radius;

            if (string.Equals(openHoleInfo.HoleStyle, "圆孔", StringComparison.Ordinal))
            {
                return circleArea;
            }

            // 腰孔：两端半圆 + 中间矩形。
            var slotLongAxis = openHoleInfo.HoleX > 0 ? openHoleInfo.HoleX : openHoleInfo.HoleY;
            if (slotLongAxis > openHoleInfo.HoleD)
            {
                return circleArea + (slotLongAxis - openHoleInfo.HoleD) * openHoleInfo.HoleD;
            }

            return circleArea;
        }

        /// <summary>
        /// 以最简形式格式化尺寸数值（整数不带小数，去除多余的尾零）。
        /// </summary>
        private static string FormatDimension(double value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 尝试解析装配体中的轻量化组件。
        /// </summary>
        /// <param name="asm">目标装配体文档。</param>
        private static void TryResolveLightweightComponents(AssemblyDoc asm)
        {
            if (asm == null) return;

            try
            {
                asm.ResolveAllLightWeightComponents(false);
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// 以静默方式保存模型文档。
        /// </summary>
        /// <param name="model">待保存的模型文档。</param>
        private static void TrySaveModelSilent(IModelDoc2 model)
        {
            if (model == null) return;

            int saveErr = 0;
            int saveWarn = 0;
            try
            {
                model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref saveErr, ref saveWarn);
            }
            catch
            {
                // ignore
            }
        }


        private static bool TrySaveModelSilentChecked(IModelDoc2 model)
        {
            if (model == null)
            {
                return false;
            }

            var saveErr = 0;
            var saveWarn = 0;
            try
            {
                var saved = model.Save3(
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                    ref saveErr,
                    ref saveWarn);
                return saved && saveErr == 0;
            }
            catch
            {
                return false;
            }
        }


        /// <summary>
        /// 将文档显示单位（mm）转换为 SolidWorks 系统单位（m）。
        /// </summary>
        /// <param name="value">以 mm 为单位的长度值。</param>
        /// <returns>以 m 为单位的长度值。</returns>
        private static double ToSystemLengthValue(double value)
        {
            // SolidWorks 的 Dimension.SystemValue / SheetMetalFeatureData.Thickness 使用系统单位（通常是米）。
            // 当前项目输入的尺寸（规格/长度）按 mm 传入，因此统一按 mm -> m 转换。
            return value / 1000d;
        }

        /// <summary>
        /// 将 SolidWorks 系统单位（m）转换为文档显示单位（mm）。
        /// </summary>
        /// <param name="systemValue">以 m 为单位的长度值。</param>
        /// <returns>以 mm 为单位的长度值。</returns>
        private static double FromSystemLengthValue(double systemValue)
        {
            // 与 ToSystemLengthValue 反向：m -> mm。
            return systemValue * 1000d;
        }

        /// <summary>
        /// 尝试按尺寸名称向模型写入尺寸值。
        /// </summary>
        /// <param name="model">目标模型文档。</param>
        /// <param name="dimName">尺寸完整名称。</param>
        /// <param name="value">要写入的值（mm）。</param>
        /// <returns>写入成功返回 true，否则返回 false。</returns>
        private static bool TrySetDimensionSystemValue(IModelDoc2 model, string dimName, double value)
        {
            if (model == null) return false;
            if (string.IsNullOrWhiteSpace(dimName)) return false;

            try
            {
                // Parameter("W@铜排基体") 这种命名方式来自 SolidWorks："尺寸名@特征(或草图)名"
                var dim = model.Parameter(dimName) as Dimension;
                if (dim == null) return false;

                // SystemValue：以系统单位写值（通常为米/弧度），与文档显示单位可能不同。
                dim.SystemValue = ToSystemLengthValue(value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 尝试在铜排零件上写入排宽（优先装配体上下文下修改「铜排基体」钣金 D1 偏移，失败时回退尺寸名）。
        /// </summary>
        public static bool TrySetBusbarWidthOnPartModel(
            IModelDoc2 partModel,
            double width,
            IModelDoc2 topDoc = null,
            Component2 component = null)
        {
            if (partModel == null || width <= 0) return false;

            if (TrySetSheetMetalWidth(partModel, BusbarBaseFeatureName, width, topDoc, component))
            {
                return true;
            }

            foreach (var dimName in BuildBusbarWidthDimensionCandidates(partModel, component))
            {
                if (TrySetDimensionSystemValue(partModel, dimName, width))
                {
                    return true;
                }
            }

            if (topDoc != null && component != null)
            {
                foreach (var dimName in BuildBusbarWidthAssemblyDimensionCandidates(partModel, component))
                {
                    if (TrySetDimensionSystemValue(topDoc, dimName, width))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static IEnumerable<string> BuildBusbarWidthDimensionCandidates(IModelDoc2 partModel, Component2 component)
        {
            yield return BusbarWidthDimensionName;
            yield return $"D1@{BusbarBaseFeatureName}";
            yield return "D1@形状草图";
        }

        private static IEnumerable<string> BuildBusbarWidthAssemblyDimensionCandidates(IModelDoc2 partModel, Component2 component)
        {
            string componentName = null;
            string partFileName = null;

            try { componentName = component?.Name2; } catch { }
            try { partFileName = Path.GetFileNameWithoutExtension(partModel?.GetPathName()); } catch { }

            if (!string.IsNullOrWhiteSpace(componentName))
            {
                yield return $"{BusbarWidthDimensionName}@{componentName}";
                yield return $"D1@{BusbarBaseFeatureName}@{componentName}";
            }

            if (!string.IsNullOrWhiteSpace(partFileName) && !string.IsNullOrWhiteSpace(componentName))
            {
                yield return $"{BusbarWidthDimensionName}@{partFileName}@{componentName}";
                yield return $"D1@{BusbarBaseFeatureName}@{partFileName}@{componentName}";
            }
        }

        /// <summary>
        /// 尝试在铜排零件上写入排厚（优先钣金/薄壁特征 API，失败时回退 Thickness@ 尺寸）。
        /// </summary>
        /// <param name="partModel">铜排零件模型。</param>
        /// <param name="thickness">排厚（mm）。</param>
        /// <param name="topDoc">装配体上下文下的顶层文档；独立零件编辑时传 null。</param>
        /// <param name="component">装配体上下文下的零件组件；独立零件编辑时传 null。</param>
        /// <returns>设置成功返回 true，否则返回 false。</returns>
        public static bool TrySetBusbarThicknessOnPartModel(
            IModelDoc2 partModel,
            double thickness,
            IModelDoc2 topDoc = null,
            Component2 component = null)
        {
            if (partModel == null || thickness <= 0) return false;

            if (TrySetSheetMetalThickness(partModel, "铜排基体", thickness, topDoc, component))
            {
                return true;
            }

            return TrySetDimensionSystemValue(partModel, "Thickness@铜排基体", thickness);
        }

        /// <summary>
        /// 尝试设置「铜排基体」特征的排宽（D1 偏移距离，与 AutoOpeningHoleBase.GetBusbarWidthFromSMBaseFlange 一致）。
        /// 须在装配体上下文中通过 AccessSelections + ModifyDefinition 写入，以兼容在位零件外部参考。
        /// </summary>
        /// <param name="model">目标零件模型。</param>
        /// <param name="sheetMetalFeatureName">钣金/基体特征名称。</param>
        /// <param name="width">排宽值（mm）。</param>
        /// <param name="topDoc">装配体上下文下的顶层文档；独立零件编辑时传 null。</param>
        /// <param name="component">装配体上下文下的零件组件；独立零件编辑时传 null。</param>
        /// <returns>设置成功返回 true，否则返回 false。</returns>
        private static bool TrySetSheetMetalWidth(
            IModelDoc2 model,
            string sheetMetalFeatureName,
            double width,
            IModelDoc2 topDoc = null,
            Component2 component = null)
        {
            if (model == null) return false;
            if (string.IsNullOrWhiteSpace(sheetMetalFeatureName)) return false;
            if (width <= 0) return false;

            try
            {
                var feat = FindFeature(model, sheetMetalFeatureName, "SMBaseFlange")
                    ?? FindFeature(model, sheetMetalFeatureName, "SheetMetal");
                if (feat == null) return false;

                var editTopDoc = topDoc ?? model;
                var sysWidth = ToSystemLengthValue(width);

                IBaseFlangeFeatureData baseFlangeData = null;
                try { baseFlangeData = feat.GetDefinition() as IBaseFlangeFeatureData; } catch { }

                if (baseFlangeData != null)
                {
                    var accessGranted = false;
                    var definitionApplied = false;
                    try
                    {
                        accessGranted = baseFlangeData.AccessSelections(editTopDoc, component);
                        if (!accessGranted) return false;

                        TryEnableBaseFlangeWidthOverrideDefaults(baseFlangeData);
                        if (!TrySetComProperty(baseFlangeData, "D1EndConditionDistance", sysWidth))
                        {
                            baseFlangeData.D1OffsetDistance = sysWidth;
                        }

                        definitionApplied = feat.ModifyDefinition(baseFlangeData, editTopDoc, component);
                        return definitionApplied;
                    }
                    finally
                    {
                        if (accessGranted && !definitionApplied)
                        {
                            try { baseFlangeData.ReleaseSelectionAccess(); } catch { }
                        }

                        try { model.ClearSelection2(true); } catch { }
                    }
                }

                object defObj = null;
                try { defObj = feat.GetDefinition(); } catch { }

                if (defObj != null &&
                    TrySetSheetMetalWidthViaReflection(feat, defObj, editTopDoc, component, sysWidth, model))
                {
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 勾选「覆盖默认参数」，使 D1 偏移写入不被 gauge 表/文档默认值覆盖。
        /// </summary>
        private static void TryEnableBaseFlangeWidthOverrideDefaults(IBaseFlangeFeatureData featureData)
        {
            if (featureData == null) return;

            TryEnableBusbarWidthOverrideDefaultsViaReflection(featureData);

            try { featureData.UseGaugeTable = false; } catch { }
        }

        /// <summary>
        /// 通过反射修改特征定义中的 D1 偏移（非 IBaseFlangeFeatureData 时的兜底）。
        /// </summary>
        private static bool TrySetSheetMetalWidthViaReflection(
            IFeature feat,
            object defObj,
            IModelDoc2 editTopDoc,
            Component2 component,
            double sysWidth,
            IModelDoc2 partModel)
        {
            var accessGranted = false;
            var definitionApplied = false;
            try
            {
                if (!TryInvokeComBooleanMethod(defObj, "AccessSelections", new object[] { editTopDoc, component }, out accessGranted) ||
                    !accessGranted)
                {
                    return false;
                }

                TryEnableBusbarWidthOverrideDefaultsViaReflection(defObj);

                if (!TrySetComProperty(defObj, "D1EndConditionDistance", sysWidth) &&
                    !TrySetComProperty(defObj, "D1OffsetDistance", sysWidth) &&
                    !TrySetComProperty(defObj, "d1OffsetDistance", sysWidth))
                {
                    return false;
                }

                try
                {
                    definitionApplied = feat.ModifyDefinition(defObj, editTopDoc, component);
                    return definitionApplied;
                }
                catch
                {
                    return false;
                }
            }
            finally
            {
                if (accessGranted && !definitionApplied)
                {
                    TryInvokeComMethod(defObj, "ReleaseSelectionAccess", null, out _);
                }

                try { partModel.ClearSelection2(true); } catch { }
            }
        }

        /// <summary>
        /// 反射方式勾选排宽相关的「覆盖默认参数」属性。
        /// </summary>
        private static void TryEnableBusbarWidthOverrideDefaultsViaReflection(object featureData)
        {
            if (featureData == null) return;

            TrySetComProperty(featureData, "OverrideDefaultSheetMetalParameters", true);
            TrySetComProperty(featureData, "UseGaugeTable", false);
        }

        /// <summary>
        /// 尝试设置「铜排基体」特征的板厚参数。
        /// 模板中修改排厚前须勾选「覆盖默认参数」，对应 OverrideThickness / OverrideDefaultSheetMetalParameters。
        /// </summary>
        /// <param name="model">目标零件模型。</param>
        /// <param name="sheetMetalFeatureName">钣金/基体特征名称。</param>
        /// <param name="thickness">板厚值（mm）。</param>
        /// <param name="topDoc">装配体上下文下的顶层文档；独立零件编辑时传 null。</param>
        /// <param name="component">装配体上下文下的零件组件；独立零件编辑时传 null。</param>
        /// <returns>设置成功返回 true，否则返回 false。</returns>
        private static bool TrySetSheetMetalThickness(
            IModelDoc2 model,
            string sheetMetalFeatureName,
            double thickness,
            IModelDoc2 topDoc = null,
            Component2 component = null)
        {
            if (model == null) return false;
            if (string.IsNullOrWhiteSpace(sheetMetalFeatureName)) return false;
            if (thickness <= 0) return false;

            try
            {
                // 优先按名称匹配（铜排基体），找不到时用 GetTypeName2() 兜底。
                var feat = FindFeature(model, sheetMetalFeatureName, "SMBaseFlange")
                    ?? FindFeature(model, sheetMetalFeatureName, "SheetMetal");
                if (feat == null) return false;

                var editTopDoc = topDoc ?? model;
                var sysThickness = ToSystemLengthValue(thickness);

                IBaseFlangeFeatureData baseFlangeData = null;
                try { baseFlangeData = feat.GetDefinition() as IBaseFlangeFeatureData; } catch { }

                if (baseFlangeData != null)
                {
                    var accessGranted = false;
                    var definitionApplied = false;
                    try
                    {
                        accessGranted = baseFlangeData.AccessSelections(editTopDoc, component);
                        if (!accessGranted) return false;

                        TryEnableBaseFlangeThicknessOverrideDefaults(baseFlangeData);
                        baseFlangeData.Thickness = sysThickness;

                        definitionApplied = feat.ModifyDefinition(baseFlangeData, editTopDoc, component);
                        return definitionApplied;
                    }
                    finally
                    {
                        if (accessGranted && !definitionApplied)
                        {
                            try { baseFlangeData.ReleaseSelectionAccess(); } catch { }
                        }

                        try { model.ClearSelection2(true); } catch { }
                    }
                }

                IExtrudeFeatureData2 extrudeData = null;
                try { extrudeData = feat.GetDefinition() as IExtrudeFeatureData2; } catch { }

                if (extrudeData != null && extrudeData.IsThinFeature())
                {
                    var accessGranted = false;
                    var definitionApplied = false;
                    try
                    {
                        accessGranted = extrudeData.AccessSelections(editTopDoc, component);
                        if (!accessGranted) return false;

                        extrudeData.SetWallThickness(true, sysThickness);

                        definitionApplied = feat.ModifyDefinition(extrudeData, editTopDoc, component);
                        return definitionApplied;
                    }
                    finally
                    {
                        if (accessGranted && !definitionApplied)
                        {
                            try { extrudeData.ReleaseSelectionAccess(); } catch { }
                        }

                        try { model.ClearSelection2(true); } catch { }
                    }
                }

                object defObj = null;
                try { defObj = feat.GetDefinition(); } catch { }

                if (defObj != null &&
                    TrySetSheetMetalThicknessViaReflection(feat, defObj, editTopDoc, component, sysThickness, model))
                {
                    return true;
                }

                return TrySetDimensionSystemValue(model, $"Thickness@{sheetMetalFeatureName}", thickness);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 勾选「覆盖默认参数」，使 Thickness 写入不被 gauge 表/文档默认值覆盖。
        /// </summary>
        private static void TryEnableBaseFlangeThicknessOverrideDefaults(IBaseFlangeFeatureData featureData)
        {
            if (featureData == null) return;

            try { featureData.OverrideThickness = true; } catch { }
            try { featureData.UseGaugeTable = false; } catch { }
        }

        /// <summary>
        /// 通过反射修改特征定义中的板厚（非 IBaseFlangeFeatureData / IExtrudeFeatureData2 时的兜底）。
        /// </summary>
        private static bool TrySetSheetMetalThicknessViaReflection(
            IFeature feat,
            object defObj,
            IModelDoc2 editTopDoc,
            Component2 component,
            double sysThickness,
            IModelDoc2 partModel)
        {
            var accessGranted = false;
            var definitionApplied = false;
            try
            {
                if (!TryInvokeComBooleanMethod(defObj, "AccessSelections", new object[] { editTopDoc, component }, out accessGranted) ||
                    !accessGranted)
                {
                    return false;
                }

                TryEnableBusbarThicknessOverrideDefaultsViaReflection(defObj);

                if (!TrySetComProperty(defObj, "Thickness", sysThickness) &&
                    !TrySetComProperty(defObj, "thickness", sysThickness))
                {
                    return false;
                }

                try
                {
                    definitionApplied = feat.ModifyDefinition(defObj, editTopDoc, component);
                    return definitionApplied;
                }
                catch
                {
                    return false;
                }
            }
            finally
            {
                if (accessGranted && !definitionApplied)
                {
                    TryInvokeComMethod(defObj, "ReleaseSelectionAccess", null, out _);
                }

                try { partModel.ClearSelection2(true); } catch { }
            }
        }

        /// <summary>
        /// 反射方式勾选「覆盖默认参数」相关属性。
        /// </summary>
        private static void TryEnableBusbarThicknessOverrideDefaultsViaReflection(object featureData)
        {
            if (featureData == null) return;

            TrySetComProperty(featureData, "OverrideThickness", true);
            TrySetComProperty(featureData, "OverrideDefaultSheetMetalParameters", true);
            TrySetComProperty(featureData, "UseGaugeTable", false);
        }

        /// <summary>
        /// 通过反射尝试调用 COM 对象方法。
        /// </summary>
        /// <param name="target">目标 COM 对象。</param>
        /// <param name="methodName">方法名称。</param>
        /// <param name="args">调用参数。</param>
        /// <param name="result">输出调用结果。</param>
        /// <returns>调用成功返回 true，否则返回 false。</returns>
        private static bool TryInvokeComMethod(object target, string methodName, object[] args, out object result)
        {
            result = null;
            if (target == null) return false;
            if (string.IsNullOrWhiteSpace(methodName)) return false;

            try
            {
                result = target.GetType().InvokeMember(methodName,
                    System.Reflection.BindingFlags.InvokeMethod,
                    null,
                    target,
                    args);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 通过反射尝试调用返回布尔值的 COM 方法。
        /// </summary>
        /// <param name="target">目标 COM 对象。</param>
        /// <param name="methodName">方法名称。</param>
        /// <param name="args">调用参数。</param>
        /// <param name="result">输出布尔返回值。</param>
        /// <returns>调用成功且返回布尔值时返回 true，否则返回 false。</returns>
        private static bool TryInvokeComBooleanMethod(object target, string methodName, object[] args, out bool result)
        {
            result = false;

            if (!TryInvokeComMethod(target, methodName, args, out var invocationResult))
            {
                return false;
            }

            return TryConvertComBooleanResult(invocationResult, out result);
        }

        /// <summary>
        /// 将 COM 布尔返回值转换为 bool（兼容 bool / int / short 等互操作类型）。
        /// </summary>
        private static bool TryConvertComBooleanResult(object value, out bool result)
        {
            result = false;
            if (value == null) return false;

            switch (value)
            {
                case bool booleanValue:
                    result = booleanValue;
                    return true;
                case int intValue:
                    result = intValue != 0;
                    return true;
                case short shortValue:
                    result = shortValue != 0;
                    return true;
                case long longValue:
                    result = longValue != 0;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 通过反射尝试设置 COM 对象属性值。
        /// </summary>
        /// <param name="target">目标 COM 对象。</param>
        /// <param name="propertyName">属性名称。</param>
        /// <param name="value">属性值。</param>
        /// <returns>设置成功返回 true，否则返回 false。</returns>
        private static bool TrySetComProperty(object target, string propertyName, object value)
        {
            if (target == null) return false;
            if (string.IsNullOrWhiteSpace(propertyName)) return false;

            try
            {
                target.GetType().InvokeMember(propertyName,
                    System.Reflection.BindingFlags.SetProperty,
                    null,
                    target,
                    new object[] { value });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
