using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using CKBusbarBridgePreview;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 绝缘子已经完成参数驱动与重建后，由宿主装配阶段提供的终结操作。
    /// 保存和关闭被拆成显式结果，避免把 CloseDoc 未抛异常误判为成功。
    /// </summary>
    internal interface IFaceToFaceInsulatorPostAssemblyOperations
    {
        void RebuildHost();

        bool SaveInsulator();

        FaceToFaceFrameCleanupConfirmation CloseInsulator();
    }

    /// <summary>
    /// 独立基座与面对面基架共用的绝缘子生命周期尾段：
    /// 装配完成后重建宿主，再定向保存并确认关闭绝缘子。
    /// </summary>
    internal static class FaceToFaceInsulatorAssemblyLifecycle
    {
        internal static void Run(
            Action insertAndMate,
            IFaceToFaceInsulatorPostAssemblyOperations operations)
        {
            if (insertAndMate == null) throw new ArgumentNullException(nameof(insertAndMate));
            if (operations == null) throw new ArgumentNullException(nameof(operations));

            insertAndMate();
            operations.RebuildHost();
            if (!operations.SaveInsulator())
            {
                throw new InvalidOperationException(
                    "保存已装配的绝缘子模型失败，已禁止关闭该模型。");
            }

            var closeResult = operations.CloseInsulator();
            if (closeResult == null || !closeResult.IsConfirmed)
            {
                var exception = new InvalidOperationException(
                    "关闭已装配的绝缘子模型失败或未得到确认。");
                FaceToFaceFrameCleanupDiagnostics.AttachCleanupConfirmation(
                    exception,
                    closeResult,
                    null,
                    "绝缘子文档关闭");
                throw exception;
            }
        }
    }

    /// <summary>
    /// 绝缘子装配驱动：
    /// - 负责驱动项目内绝缘子装配体全局变量（排厚/相间距/绝缘子高度/支架长度）；
    /// - 按单相根数压缩/解压绝缘子装配体中的垫块特征（垫块1-1、垫块2、垫块3 等）；
    /// - 负责在当前激活的总装配体中插入/定位绝缘子子装配体；
    /// - 并根据界面输入的“各基架绝缘子定位”与“母排分段”进行奇偶段判断与平移定位。
    /// </summary>
    public partial class InsulatorDriver
    {
        private readonly ISldWorks swApp;

        private const string CabinetBaseFolderName = "柜体基座";
        private const string InsulatorTemplateFolderName = "绝缘子模板";
        private const string ConventionalInsulatorTemplateFolderName = "常规绝缘子模版";
        private const string LBracketInsulatorTemplateFolderName = "L型支架绝缘子模板";
        private const string InsulatorProjectFolderName = "绝缘子";
        private const string InsulatorOutputFolderNamePrefix = "绝缘子";
        private const string ThreePadFolderLabel = "3垫块";
        private const string InsulatorAssemblyFileNamePrefix = "绝缘子+垫片+支架";
        private const string BusbarThicknessGlobalVariableName = "排厚";
        private const string PhaseSpacingGlobalVariableName = "相间距";
        private const string InsulatorHeightGlobalVariableName = "绝缘子高度";
        private const string BracketLengthGlobalVariableName = "支架长度";
        private const double BracketLengthBaseWidthOffset = 20;
        private const string PadFeatureNamePrefix = "垫块";
        private const string TwelveMillimeterPadPatternName = "排厚12阵列";
        private const int MaxSinglePhaseRootCount = 4;
        private const string OddSegmentInsulatorAsmFileName = "绝缘子+垫片+支架（3垫块）.SLDASM";
        private const string EvenSegmentInsulatorAsmFileName = "绝缘子+垫片+支架（4垫块）.SLDASM";
        private const string LBracketOddSegmentInsulatorAsmFileName =
            "绝缘子+垫片+支架（3垫块）-L.SLDASM";
        private const string LBracketPadSubAssemblyNamePrefix = "绝缘子垫块";

        /// <summary>
        /// 创建绝缘子驱动器。
        /// </summary>
        /// <param name="_swApp">当前 SolidWorks 应用程序对象（由外部创建/注入）。</param>
        public InsulatorDriver(ISldWorks _swApp)
            : this(_swApp, null)
        {
        }

        private readonly InsulatorDriveRecoverySession _driveRecovery;

        internal InsulatorDriver(ISldWorks application, InsulatorDriveRecoverySession driveRecovery)
        {
            swApp = application;
            _driveRecovery = driveRecovery;
        }

        /// <summary>
        /// 将 3 垫块绝缘子装配到当前激活的总装配体中，并以首个基架左侧起点坐标作为定位基准。
        /// 遍历所有基架的绝缘子定位点，按整条基架的绝对位置统一沿装配体 X 方向平移定位；
        /// Y 方向按绝缘子本体高度变换量的一半补偿（变换量 = 实际高度 − 205），
        /// 并叠加由调用方传入的排厚/墙孔高度等额外变换量（调用方对不需折半的项会预先乘以 2）。
        /// 大基架绝缘子可传入独立的 Z 起点（不做墙柜偏心补偿）；未传时与常规基架共用 firstFrameStartZ。
        /// </summary>
        /// <param name="frameInfo">当前基架及绝缘子定位信息（含各基架宽度）。</param>
        /// <param name="insulatorAssemblyPathByFrameWidth">按基架宽（mm，四舍五入到 0.1）映射到已完成 PackAndGo 与驱动的 3 垫块绝缘子装配体完整路径（见 <see cref="PrepareThreePadInsulatorFromTemplate"/>）；每个基架按自身宽度选取对应副本。</param>
        /// <param name="firstFrameStartX">首个基架左侧起点 X 坐标（mm）。</param>
        /// <param name="firstFrameStartY">首个基架左侧起点 Y 坐标（mm）。</param>
        /// <param name="firstFrameStartZ">常规基架绝缘子起点 Z 坐标（mm）。</param>
        /// <param name="insulatorBodyHeightDelta">绝缘子本体高度相对默认值 205 的变换量（mm）。</param>
        /// <param name="largeFrameStartZ">大基架绝缘子起点 Z 坐标（mm）；为 null 时与常规基架共用 firstFrameStartZ。</param>
        /// <returns>成功插入并定位的绝缘子数量。</returns>
        public int AssembleThreePadInsulatorsInActiveAssembly(
            FrameInfoParameters frameInfo,
            IReadOnlyDictionary<double, string> insulatorAssemblyPathByFrameWidth,
            double firstFrameStartX,
            double firstFrameStartY,
            double firstFrameStartZ,
            double insulatorBodyHeightDelta = 0,
            double? largeFrameStartZ = null)
        {
            if (swApp == null)
            {
                return 0;
            }

            if (frameInfo == null || frameInfo.Frames == null || frameInfo.Frames.Count == 0)
            {
                return 0;
            }

            if (insulatorAssemblyPathByFrameWidth == null || insulatorAssemblyPathByFrameWidth.Count == 0)
            {
                throw new ArgumentException("缺少按基架宽映射的绝缘子装配体路径。", nameof(insulatorAssemblyPathByFrameWidth));
            }

            var expectedCount = CountInsulatorAssemblyPositions(frameInfo);
            if (expectedCount == 0)
            {
                return 0;
            }

            var asmModel = swApp.IActiveDoc2 as ModelDoc2;
            var asm = asmModel as AssemblyDoc;
            if (asmModel == null || asm == null || asmModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new InvalidOperationException("请先打开并激活总装配体(.SLDASM)，再装配绝缘子。");
            }

            SWHelper.EnsureAssemblyEditingContext(swApp, asmModel);

            try
            {
                asm.ResolveAllLightWeightComponents(false);
            }
            catch
            {
                // ignore
            }

            RemoveExistingInsulatorComponentsInActiveAssembly(asm, asmModel);

            var asmTitle = string.Empty;
            try { asmTitle = asmModel.GetTitle(); } catch { asmTitle = string.Empty; }

            // 归一化「基架宽 -> 绝缘子副本路径」映射，并校验各副本文件存在。
            var normalizedPathByWidth = new Dictionary<double, string>();
            foreach (var pair in insulatorAssemblyPathByFrameWidth)
            {
                if (string.IsNullOrWhiteSpace(pair.Value) || !File.Exists(pair.Value))
                {
                    throw new FileNotFoundException(
                        $"未找到已驱动的 3 垫块绝缘子装配体（基架宽 {pair.Key:0.##}）：{pair.Value}",
                        pair.Value ?? OddSegmentInsulatorAsmFileName);
                }

                normalizedPathByWidth[Math.Round(pair.Key, 1)] = pair.Value;
            }

            var insertedCount = 0;
            var openedInsulatorModels = new List<ModelDoc2>();

            try
            {
                // 预先打开所有用到的绝缘子副本，确保 AddComponent5 可按文件名解析。
                var solidWorksDriver = new SolidWorksDriver(swApp);
                foreach (var insulatorPath in normalizedPathByWidth.Values.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var openedInsulatorModel = solidWorksDriver.OpenOrActivateDocument(insulatorPath) as ModelDoc2;
                    if (openedInsulatorModel == null)
                    {
                        throw new InvalidOperationException($"无法打开绝缘子装配体：{insulatorPath}");
                    }

                    openedInsulatorModels.Add(openedInsulatorModel);
                }

                int activateErrors = 0;
                swApp.ActivateDoc3(asmTitle, true, (int)swRebuildOnActivation_e.swRebuildActiveDoc, ref activateErrors);

                asmModel = swApp.IActiveDoc2 as ModelDoc2;
                asm = asmModel as AssemblyDoc;
                if (asmModel == null || asm == null)
                {
                    throw new InvalidOperationException("装配绝缘子时未能激活总装配体。");
                }

                SWHelper.EnsureAssemblyEditingContext(swApp, asmModel);

                var insulatorYOffset = insulatorBodyHeightDelta / 2d;
                var resolvedLargeFrameStartZ = largeFrameStartZ ?? firstFrameStartZ;
                var totalOffset = 0d;
                for (var frameIndex = 0; frameIndex < frameInfo.Frames.Count; frameIndex++)
                {
                    var frame = frameInfo.Frames[frameIndex];
                    if (frame == null)
                    {
                        continue;
                    }

                    var frameLength = frame.Length > 0 ? frame.Length : 0;
                    if (frame.InsulatorPositions != null && frame.InsulatorPositions.Count > 0)
                    {
                        // 每个基架按自身宽度选取对应的绝缘子副本（支架长 = 基架宽 − 20）。
                        var insulatorAssemblyPath = ResolveInsulatorAssemblyPathForFrameWidth(normalizedPathByWidth, frame.Width);
                        if (string.IsNullOrWhiteSpace(insulatorAssemblyPath))
                        {
                            throw new InvalidOperationException(
                                $"未找到基架宽 {frame.Width:0.##} mm 对应的绝缘子副本，无法装配。");
                        }

                        var componentFileName = Path.GetFileName(insulatorAssemblyPath);
                        // 大基架绝缘子定位不做墙柜偏心补偿，使用独立 Z 起点。
                        var frameStartZ = frame.UseLargeFrameTemplate ? resolvedLargeFrameStartZ : firstFrameStartZ;
                        foreach (var posInFrame in frame.InsulatorPositions)
                        {
                            if (posInFrame < 0)
                            {
                                continue;
                            }

                            var absPos = totalOffset + posInFrame;
                            Component2 newComp = null;
                            try
                            {
                                newComp = asm.AddComponent5(
                                    componentFileName,
                                    (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                                    string.Empty,
                                    false,
                                    string.Empty,
                                    firstFrameStartX / 1000d,
                                    firstFrameStartY / 1000d,
                                    frameStartZ / 1000d);
                            }
                            catch (Exception ex)
                            {
                                throw new InvalidOperationException(
                                    $"插入绝缘子装配体失败：{insulatorAssemblyPath}（{ex.Message}）",
                                    ex);
                            }

                            if (newComp == null)
                            {
                                throw new InvalidOperationException($"插入绝缘子装配体失败：{insulatorAssemblyPath}");
                            }

                            insertedCount++;

                            MathTransform baseTransform = null;
                            try { baseTransform = newComp.Transform2; } catch { baseTransform = null; }
                            if (baseTransform == null)
                            {
                                throw new InvalidOperationException("读取绝缘子组件变换矩阵失败，无法完成定位。");
                            }

                            var moved = TransformHelper.TranslateTransformXY(swApp, baseTransform, absPos, insulatorYOffset);
                            if (moved == null)
                            {
                                throw new InvalidOperationException($"绝缘子 X={absPos:0.##} mm、Y 偏移={insulatorYOffset:0.##} mm 的定位变换计算失败。");
                            }

                            newComp.Transform2 = moved;
                            asmModel.ClearSelection2(true);
                            newComp.Select2(false, 0);
                            asm.FixComponent();
                        }
                    }

                    totalOffset += frameLength;
                }

                if (insertedCount < expectedCount)
                {
                    throw new InvalidOperationException(
                        $"绝缘子装配不完整：应插入 {expectedCount} 个，实际插入 {insertedCount} 个。");
                }

                asmModel.EditRebuild3();
            }
            finally
            {
                foreach (var openedInsulatorModel in openedInsulatorModels)
                {
                    if (openedInsulatorModel == null)
                    {
                        continue;
                    }

                    try
                    {
                        SaveAndCloseModel(openedInsulatorModel);
                    }
                    catch
                    {
                        // ignore
                    }
                }

                TryActivateDocByTitle(asmTitle);
            }

            return insertedCount;
        }

        /// <summary>
        /// 按基架宽（四舍五入到 0.1）在映射中查找对应的绝缘子副本路径；允许 0.05mm 容差回退。
        /// </summary>
        private static string ResolveInsulatorAssemblyPathForFrameWidth(IDictionary<double, string> pathByWidth, double frameWidth)
        {
            if (pathByWidth == null || pathByWidth.Count == 0)
            {
                return null;
            }

            var key = Math.Round(frameWidth, 1);
            if (pathByWidth.TryGetValue(key, out var path))
            {
                return path;
            }

            foreach (var pair in pathByWidth)
            {
                if (Math.Abs(pair.Key - frameWidth) < 0.05)
                {
                    return pair.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// 统计各基架绝缘子定位点总数。
        /// </summary>
        private static int CountInsulatorAssemblyPositions(FrameInfoParameters frameInfo)
        {
            if (frameInfo?.Frames == null)
            {
                return 0;
            }

            return frameInfo.Frames.Sum(frame => frame?.InsulatorPositions?.Count(position => position >= 0) ?? 0);
        }

        /// <summary>
        /// 解析常规 3 垫块绝缘子装配体路径：优先精确文件名，再回退到指定目录（含子目录）
        /// 搜索同名文件。L 型和偏移模板绝不能作为常规模板的回退候选。
        /// </summary>
        internal static string ResolveOddSegmentInsulatorAssemblyPath(string searchDirectory)
        {
            if (string.IsNullOrWhiteSpace(searchDirectory))
            {
                return null;
            }

            var exactPath = Path.GetFullPath(
                Path.Combine(searchDirectory, OddSegmentInsulatorAsmFileName));
            if (File.Exists(exactPath))
            {
                return exactPath;
            }

            var conventionalPath = Path.GetFullPath(
                Path.Combine(
                    searchDirectory,
                    ConventionalInsulatorTemplateFolderName,
                    OddSegmentInsulatorAsmFileName));
            if (File.Exists(conventionalPath))
            {
                return conventionalPath;
            }

            var candidates = FindInsulatorAssemblyPaths(searchDirectory, SearchOption.AllDirectories)
                .Where(path => string.Equals(
                    Path.GetFileName(path),
                    OddSegmentInsulatorAsmFileName,
                    StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            if (candidates.Count > 1)
            {
                throw new InvalidOperationException(
                    "常规绝缘子模板不唯一，请指定精确模板目录："
                    + string.Join("；", candidates));
            }

            return null;
        }

        /// <summary>
        /// 从共享“模板.zip”根目录精确定位常规绝缘子模板。
        /// </summary>
        internal static string ResolveConventionalInsulatorAssemblyPath(string templateRoot)
        {
            // 实现迁入界面运行时；正式生成与反读共用相同规则。
            return BridgeModelReadOperations.ResolveConventionalInsulatorAssemblyPath(templateRoot);
        }

        /// <summary>
        /// 从共享“模板.zip”根目录精确定位 L 型支架绝缘子模板。
        /// </summary>
        internal static string ResolveLBracketInsulatorAssemblyPath(string templateRoot)
        {
            // 实现迁入界面运行时；正式生成与反读共用相同规则。
            return BridgeModelReadOperations.ResolveLBracketInsulatorAssemblyPath(templateRoot);
        }

        /// <summary>
        /// 删除当前总装配体顶层已插入的绝缘子子装配体，避免重复装配。
        /// </summary>
        /// <param name="asm">当前总装配体文档。</param>
        /// <param name="asmModel">当前总装配体模型。</param>
        private static void RemoveExistingInsulatorComponentsInActiveAssembly(AssemblyDoc asm, ModelDoc2 asmModel)
        {
            if (asm == null || asmModel == null)
            {
                return;
            }

            object[] topLevelComponents = null;
            try
            {
                topLevelComponents = asm.GetComponents(true) as object[];
            }
            catch
            {
                topLevelComponents = null;
            }

            if (topLevelComponents == null || topLevelComponents.Length == 0)
            {
                return;
            }

            var needDeleteComponents = new List<Component2>();
            foreach (var obj in topLevelComponents)
            {
                var component = obj as Component2;
                if (component == null)
                {
                    continue;
                }

                if (!IsInsulatorAssemblyComponent(component))
                {
                    continue;
                }

                needDeleteComponents.Add(component);
            }

            if (needDeleteComponents.Count == 0)
            {
                return;
            }

            try
            {
                asmModel.ClearSelection2(true);
            }
            catch
            {
                // ignore
            }

            var append = false;
            foreach (var component in needDeleteComponents)
            {
                string selectName = null;
                try { selectName = component.GetSelectByIDString(); } catch { selectName = null; }
                if (string.IsNullOrWhiteSpace(selectName))
                {
                    continue;
                }

                var selected = false;
                try
                {
                    selected = asmModel.Extension.SelectByID2(selectName, "COMPONENT", 0, 0, 0, append, 0, null, 0);
                }
                catch
                {
                    selected = false;
                }

                if (selected)
                {
                    append = true;
                }
            }

            if (append)
            {
                try
                {
                    asm.DeleteSelections((int)swDeleteSelectionOptions_e.swDelete_Children);
                    asmModel.EditRebuild3();
                }
                catch
                {
                    // ignore
                }
            }

            try
            {
                asmModel.ClearSelection2(true);
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// 判断是否为绝缘子装配体：路径为 .SLDASM，且文件名包含「绝缘子」或为已知绝缘子模板名。
        /// </summary>
        private static bool IsInsulatorAssemblyComponent(Component2 component)
        {
            if (component == null)
            {
                return false;
            }

            string path = null;
            try
            {
                path = component.GetPathName();
            }
            catch
            {
                path = null;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (!path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var file = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(file))
            {
                return false;
            }

            if (file.IndexOf("绝缘子", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (string.Equals(file, OddSegmentInsulatorAsmFileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(file, EvenSegmentInsulatorAsmFileName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 尝试按标题重新激活文档。
        /// 用于在打开/保存子装配体后，将焦点切回总装配体，避免后续装配逻辑拿到错误的 ActiveDoc。
        /// </summary>
        /// <param name="title">文档标题（通常为文件名）。</param>
        private void TryActivateDocByTitle(string title)
        {
            if (swApp == null) return;
            if (string.IsNullOrWhiteSpace(title)) return;

            try
            {
                int errors = 0;
                swApp.ActivateDoc3(title, false, (int)swRebuildOnActivation_e.swRebuildActiveDoc, ref errors);
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// 驱动项目内全部绝缘子装配体（3/4 垫块）：写入排厚/相间距/绝缘子高度/支架长度，并按单相根数解压/压缩垫块阵列特征。
        /// </summary>
        /// <param name="projectPath">项目根目录。</param>
        /// <param name="busbarSpec">铜排规格文本（如“80×8”）。</param>
        /// <param name="phaseSpacing">相间距（mm）。</param>
        /// <param name="insulatorHeight">绝缘子高度（mm）。</param>
        /// <param name="baseWidth">基座宽（mm）；支架长度 = 基座宽 − 20。</param>
        /// <param name="singlePhaseRootCount">单相根数（1~4）。</param>
        /// <param name="restoreContextModel">驱动完成后需要恢复编辑上下文的模型（通常为柜体总装配体）。</param>
        public void DriveInsulatorAssembliesInProject(
            string projectPath,
            string busbarSpec,
            double phaseSpacing,
            double insulatorHeight,
            double baseWidth,
            int singlePhaseRootCount,
            IModelDoc2 restoreContextModel)
        {
            if (swApp == null || restoreContextModel == null || string.IsNullOrWhiteSpace(projectPath))
            {
                return;
            }

            if (!TryParseBusbarSpec(busbarSpec, out _, out var thickness))
            {
                return;
            }

            var insulatorAssemblyPaths = CollectInsulatorAssemblyPathsForProject(projectPath, restoreContextModel).ToList();
            if (insulatorAssemblyPaths.Count == 0)
            {
                throw new InvalidOperationException(
                    $"项目目录下未找到绝缘子装配体（文件名以“{InsulatorAssemblyFileNamePrefix}”开头）：{projectPath}");
            }

            var variableMap = BuildInsulatorGlobalVariableMap(thickness, phaseSpacing, insulatorHeight, baseWidth);

            CloseDrivenInsulatorModels(DriveInsulatorAssemblyFiles(
                insulatorAssemblyPaths,
                variableMap,
                singlePhaseRootCount,
                restoreContextModel));

            SWHelper.EnsureAssemblyEditingContext(swApp, restoreContextModel);
            restoreContextModel.EditRebuild3();
        }

        /// <summary>
        /// 生成基架场景：将「绝缘子模板」文件夹中的 3 垫块绝缘子模板 PackAndGo 到项目根目录下的
        /// 「绝缘子/绝缘子-3垫块-绝缘子高-相间距-支架长」文件夹（已存在则复用、跳过 PackAndGo），随后按单相根数驱动该副本
        /// （写入排厚/相间距/绝缘子高度/支架长度并压缩垫块阵列特征）。
        /// </summary>
        /// <param name="projectPath">项目根目录。</param>
        /// <param name="scenarioId">当前设计场景；保留该参数以兼容现有调用契约。</param>
        /// <param name="busbarSpec">铜排规格文本（如“80×8”）。</param>
        /// <param name="phaseSpacing">相间距（mm）。</param>
        /// <param name="insulatorHeight">绝缘子高度（mm）。</param>
        /// <param name="frameWidth">当前所在基架宽（mm）；支架长度 = 基架宽 − 20。</param>
        /// <param name="singlePhaseRootCount">单相根数（1~4）。</param>
        /// <param name="restoreContextModel">驱动完成后需要恢复编辑上下文的模型（通常为柜体总装配体）。</param>
        /// <returns>驱动完成后的 3 垫块绝缘子装配体完整路径，供后续插入总装配体使用。</returns>
        public string PrepareThreePadInsulatorFromTemplate(
            string projectPath,
            DesignScenarioId scenarioId,
            string busbarSpec,
            double phaseSpacing,
            double insulatorHeight,
            double frameWidth,
            int singlePhaseRootCount,
            IModelDoc2 restoreContextModel)
        {
            // 除“侧对侧 + 基座1”外，所有场景统一使用共享模板中的常规绝缘子。
            var templateAssemblyPath = InsulatorDriver.ResolveConventionalInsulatorAssemblyPath(
                TemplateArchiveCacheManager.GetTemplateRootPath());
            var templateDirectory = Path.GetDirectoryName(templateAssemblyPath);
            return PrepareThreePadInsulatorFromTemplateDirectory(
                projectPath,
                templateDirectory,
                busbarSpec,
                phaseSpacing,
                insulatorHeight,
                frameWidth,
                singlePhaseRootCount,
                restoreContextModel,
                templateAssemblyFileName: Path.GetFileName(templateAssemblyPath));
        }

        /// <summary>
        /// 从指定「绝缘子模板」目录 PackAndGo 并驱动 3 垫块绝缘子装配体
        /// （默认文件名：绝缘子+垫片+支架（3垫块）.SLDASM）。
        /// 面对面独立基座流程应传入共享模板缓存下的「模板/绝缘子模板」。
        /// </summary>
        /// <param name="projectPath">项目根目录；输出统一放在其下的「绝缘子」目录。</param>
        /// <param name="templateDirectory">含 3 垫块绝缘子装配体的模板目录。</param>
        /// <param name="busbarSpec">铜排规格文本（如“80×8”）。</param>
        /// <param name="phaseSpacing">相间距（mm）。</param>
        /// <param name="insulatorHeight">绝缘子高度（mm）。</param>
        /// <param name="frameWidth">当前所在基座/基架宽（mm）；支架长度 = 宽 − 20。</param>
        /// <param name="singlePhaseRootCount">单相根数（1~4）。</param>
        /// <param name="restoreContextModel">驱动完成后需要恢复编辑上下文的模型；首次批量生成关闭总装时可为空。</param>
        /// <param name="isolationDirectoryName">「绝缘子」目录下的可选参数隔离目录名；独立基座使用。</param>
        /// <param name="closeAfterDrive">不继续装配绝缘子时，是否立即保存并关闭已驱动副本。</param>
        /// <param name="templateAssemblyFileName">可选的精确模板装配体文件名；侧对侧基座 1 用于指定 L 型支架模板。</param>
        /// <param name="modelFileNameSuffix">可选的模型身份后缀；用于同一宿主内同时加载多套独立绝缘子模型。</param>
        /// <param name="suppressedLBracketPadComponentIndexes">L 型模板中需要压缩的“绝缘子垫块*”子装配体序号；不传或传空集合表示全部解压。</param>
        /// <returns>驱动完成后的 3 垫块绝缘子装配体完整路径。</returns>
        /// <remarks>兼容转发壳（可偏移模板扩展）：保留公共签名，统一委托 PrepareThreePadInsulatorCore。</remarks>
        public string PrepareThreePadInsulatorFromTemplateDirectory(
            string projectPath,
            string templateDirectory,
            string busbarSpec,
            double phaseSpacing,
            double insulatorHeight,
            double frameWidth,
            int singlePhaseRootCount,
            IModelDoc2 restoreContextModel,
            string isolationDirectoryName = null,
            bool closeAfterDrive = false,
            string templateAssemblyFileName = null,
            string modelFileNameSuffix = null,
            IReadOnlyCollection<int> suppressedLBracketPadComponentIndexes = null)
        {
            return PrepareThreePadInsulatorCore(projectPath, templateDirectory, busbarSpec, phaseSpacing,
                insulatorHeight, frameWidth, singlePhaseRootCount, restoreContextModel, isolationDirectoryName,
                closeAfterDrive, templateAssemblyFileName, modelFileNameSuffix, suppressedLBracketPadComponentIndexes, null);
        }

        internal string PrepareUniversalThreePadInsulatorFromTemplateDirectory(
            bool useLBracket, string projectPath, string templateDirectory, string busbarSpec,
            double phaseSpacing, double insulatorHeight, double frameWidth, int singlePhaseRootCount,
            IModelDoc2 restoreContextModel, string isolationDirectoryName = null, bool closeAfterDrive = false,
            string templateAssemblyFileName = null, string modelFileNameSuffix = null,
            IReadOnlyCollection<int> suppressedLBracketPadComponentIndexes = null,
            UniversalInsulatorLayout layout = null)
        {
            return PrepareThreePadInsulatorCore(projectPath, templateDirectory, busbarSpec, phaseSpacing,
                insulatorHeight, frameWidth, singlePhaseRootCount, restoreContextModel, isolationDirectoryName,
                closeAfterDrive, templateAssemblyFileName, modelFileNameSuffix, suppressedLBracketPadComponentIndexes, null, useLBracket, layout);
        }

        internal string PrepareOffsetThreePadInsulator(string projectPath, string templateDirectory,
            string busbarSpec, double phaseSpacing, double insulatorHeight, double frameWidth,
            int singlePhaseRootCount, IModelDoc2 restoreContextModel, string isolationDirectoryName,
            string modelFileNameSuffix, double transverseOffset)
        {
            return PrepareThreePadInsulatorCore(projectPath, templateDirectory, busbarSpec, phaseSpacing,
                insulatorHeight, frameWidth, singlePhaseRootCount, restoreContextModel, isolationDirectoryName,
                false, OddSegmentInsulatorAsmFileName, modelFileNameSuffix, null, transverseOffset);
        }

        private string PrepareThreePadInsulatorCore(string projectPath, string templateDirectory,
            string busbarSpec, double phaseSpacing, double insulatorHeight, double frameWidth,
            int singlePhaseRootCount, IModelDoc2 restoreContextModel, string isolationDirectoryName,
            bool closeAfterDrive, string templateAssemblyFileName, string modelFileNameSuffix,
            IReadOnlyCollection<int> suppressedLBracketPadComponentIndexes, double? transverseOffset, bool useLBracket = false,
            UniversalInsulatorLayout layout = null)
        {
            if (swApp == null || string.IsNullOrWhiteSpace(projectPath))
            {
                return null;
            }

            if (!TryParseBusbarSpec(busbarSpec, out _, out var thickness))
            {
                throw new InvalidOperationException($"铜排规格无效，无法驱动绝缘子：{busbarSpec}");
            }

            // 支架长度 = 当前所在基座/基架宽 − 20（与 BuildInsulatorGlobalVariableMap 的口径一致）。
            var variableMap = BuildInsulatorGlobalVariableMap(thickness, phaseSpacing, insulatorHeight, frameWidth);
            layout?.ValidateDriveDimensions(frameWidth - BracketLengthBaseWidthOffset, phaseSpacing);
            if (transverseOffset.HasValue)
            {
                if (double.IsNaN(transverseOffset.Value) || double.IsInfinity(transverseOffset.Value))
                    throw new ArgumentOutOfRangeException(nameof(transverseOffset));
                variableMap["偏移方向"] = transverseOffset.Value < 0 ? 0d : 1d;
                variableMap["偏移尺寸"] = Math.Abs(transverseOffset.Value);
            }
            var bracketLength = frameWidth - BracketLengthBaseWidthOffset;

            if (string.IsNullOrWhiteSpace(templateDirectory) || !Directory.Exists(templateDirectory))
            {
                throw new DirectoryNotFoundException($"未找到绝缘子模板目录：{templateDirectory}");
            }

            // 指定文件名时必须精确使用该模板；旧调用方仍按原规则解析三垫块模板。
            var templateAssemblyPath = string.IsNullOrWhiteSpace(templateAssemblyFileName)
                ? ResolveOddSegmentInsulatorAssemblyPath(templateDirectory)
                : Path.GetFullPath(Path.Combine(
                    templateDirectory,
                    Path.GetFileName(templateAssemblyFileName)));
            if (string.IsNullOrWhiteSpace(templateAssemblyPath) || !File.Exists(templateAssemblyPath))
            {
                throw new FileNotFoundException(
                    $"未找到 3 垫块绝缘子模板：{templateAssemblyFileName ?? OddSegmentInsulatorAsmFileName}（目录：{templateDirectory}）",
                    templateAssemblyPath ?? templateAssemblyFileName ?? OddSegmentInsulatorAsmFileName);
            }

            var outputDirectory = BuildThreePadInsulatorOutputDirectory(
                projectPath,
                isolationDirectoryName,
                insulatorHeight,
                phaseSpacing,
                bracketLength);

            // PackAndGo 时对所有文档追加「-绝缘子高-相间距-支架长」后缀（原名-绝缘子高-相间距-支架长）。
            var universalTemplate = string.Equals(new DirectoryInfo(templateDirectory).Name, "绝缘子通用模板", StringComparison.Ordinal);
            if (layout != null && !universalTemplate)
                throw new InvalidOperationException("侧对侧基座距边布局需要通用绝缘子模板：" + templateAssemblyPath);
            var renameSuffix = BuildInsulatorRenameSuffix(
                insulatorHeight,
                phaseSpacing,
                bracketLength)
                + NormalizeModelFileNameSuffix(modelFileNameSuffix)
                + (universalTemplate ? (useLBracket ? "-U1-L" : "-U1-N") : string.Empty);
            var templateExt = Path.GetExtension(templateAssemblyPath);
            var preparedAssemblyBaseName = Path.GetFileNameWithoutExtension(templateAssemblyPath) + renameSuffix;
            var preparedAssemblyPath = Path.Combine(outputDirectory, preparedAssemblyBaseName + templateExt);

            ProjectLocalTestLog.Default.Write("绝缘子准备-" + Guid.NewGuid().ToString("N"), "模板及输出副本", () =>
                "模板=" + templateAssemblyPath + "；输出=" + preparedAssemblyPath
                + "；副本已存在=" + File.Exists(preparedAssemblyPath) + "；通用模板=" + universalTemplate
                + "；宿主宽=" + frameWidth.ToString("R", CultureInfo.InvariantCulture)
                + "；期望支架长度=" + bracketLength.ToString("R", CultureInfo.InvariantCulture)
                + "；单相根数=" + singlePhaseRootCount);

            // 已存在同名副本时复用、跳过 PackAndGo（与基架/铜排生成的缓存策略一致）。
            if (!File.Exists(preparedAssemblyPath))
            {
                if (!Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                var fileHelper = new SolidWorksFileHelper((SldWorks)swApp);
                if (transverseOffset.HasValue)
                {
                    var templateModel = swApp.GetOpenDocumentByName(templateAssemblyPath) as IModelDoc2;
                    // GetPackAndGo在SW2022中依赖活动文档；多横移副本连续准备时必须恢复明确模板上下文。
                    if (templateModel != null) SWHelper.EnsureAssemblyEditingContext(swApp, templateModel);
                }
                if (universalTemplate)
                    CopyUniversalTemplateWithPackAndGo(templateAssemblyPath, outputDirectory, renameSuffix);
                else
                    fileHelper.CopyFileUsePackAndGoWithSuffix(templateAssemblyPath, outputDirectory, renameSuffix, new List<string>(), ensureLeadingDot: false);

                if (!File.Exists(preparedAssemblyPath))
                {
                    throw new FileNotFoundException(
                        $"PackAndGo 复制后的绝缘子装配体不存在：{preparedAssemblyPath}",
                        preparedAssemblyPath);
                }
            }

            if (universalTemplate)
            {
                // 旧项目搬迁后已有副本也要迁移磁盘引用，不能只在首次复制时重绑定。
                RepairUniversalInsulatorOutputReferences(templateAssemblyPath, outputDirectory, renameSuffix);
                EnsureInsulatorReferencesInsideOutputTree(preparedAssemblyPath);
            }
            else if (IsLBracketInsulatorAssemblyPath(preparedAssemblyPath) || transverseOffset.HasValue)
                EnsureCopiedInsulatorReferencesAreIsolated(templateAssemblyPath, preparedAssemblyPath, renameSuffix);

            // 无论是否复用，都按当前参数重新驱动副本（相间距/绝缘子高度/支架长度已进入文件夹名，单相根数在此生效）。
            var drivenModels = DriveInsulatorAssemblyFiles(
                new[] { NormalizeExistingFilePath(preparedAssemblyPath) ?? preparedAssemblyPath },
                variableMap,
                singlePhaseRootCount,
                restoreContextModel,
                suppressedLBracketPadComponentIndexes,
                universalTemplate ? (bool?)useLBracket : null,
                layout);
            if (closeAfterDrive)
            {
                CloseDrivenInsulatorModels(drivenModels);
            }

            if (restoreContextModel != null)
            {
                SWHelper.EnsureAssemblyEditingContext(swApp, restoreContextModel);
                restoreContextModel.EditRebuild3();
            }

            return preparedAssemblyPath;
        }

        private static string NormalizeModelFileNameSuffix(string suffix)
        {
            if (string.IsNullOrWhiteSpace(suffix))
            {
                return string.Empty;
            }

            var trimmed = suffix.Trim();
            return trimmed.StartsWith("-", StringComparison.Ordinal)
                ? trimmed
                : "-" + trimmed;
        }

        /// <summary>
        /// 二次驱动：打开已有 3 垫块绝缘子装配体并按模板结构驱动；
        /// L 型模板使用顶层变量、子装配体压缩、子装配体变量的专用顺序。
        /// </summary>
        public void DriveExistingThreePadInsulatorAssembly(
            string insulatorAssemblyPath,
            string busbarSpec,
            double phaseSpacing,
            double insulatorHeight,
            double frameWidth,
            int singlePhaseRootCount,
            IModelDoc2 restoreContextModel,
            IReadOnlyCollection<int> suppressedLBracketPadComponentIndexes = null)
        {
            // 兼容入口保留旧签名；侧对侧调用内部重载显式传入当前孔位对应布局。
            DriveExistingThreePadInsulatorAssembly(insulatorAssemblyPath, busbarSpec, phaseSpacing,
                insulatorHeight, frameWidth, singlePhaseRootCount, restoreContextModel, null,
                suppressedLBracketPadComponentIndexes);
        }

        internal void DriveExistingThreePadInsulatorAssembly(
            string insulatorAssemblyPath, string busbarSpec, double phaseSpacing,
            double insulatorHeight, double frameWidth, int singlePhaseRootCount,
            IModelDoc2 restoreContextModel, UniversalInsulatorLayout layout,
            IReadOnlyCollection<int> suppressedLBracketPadComponentIndexes)
        {
            if (string.IsNullOrWhiteSpace(insulatorAssemblyPath) || !File.Exists(insulatorAssemblyPath))
            {
                throw new FileNotFoundException(
                    "待二次驱动的绝缘子装配体不存在：" + insulatorAssemblyPath,
                    insulatorAssemblyPath);
            }

            if (!TryParseBusbarSpec(busbarSpec, out _, out var thickness))
            {
                throw new InvalidOperationException($"铜排规格无效，无法驱动绝缘子：{busbarSpec}");
            }

            var variableMap = BuildInsulatorGlobalVariableMap(
                thickness,
                phaseSpacing,
                insulatorHeight,
                frameWidth);
            var driven = DriveInsulatorAssemblyFiles(
                new[] { NormalizeExistingFilePath(insulatorAssemblyPath) ?? insulatorAssemblyPath },
                variableMap,
                singlePhaseRootCount,
                restoreContextModel,
                suppressedLBracketPadComponentIndexes,
                universalLayout: layout);
            CloseDrivenInsulatorModels(driven);
            if (restoreContextModel != null)
            {
                SWHelper.EnsureAssemblyEditingContext(swApp, restoreContextModel);
                restoreContextModel.EditRebuild3();
            }
        }

        /// <summary>
        /// 统一把绝缘子副本放到项目级「绝缘子」目录；独立基座可追加参数隔离目录。
        /// </summary>
        internal static string BuildThreePadInsulatorOutputDirectory(
            string projectPath,
            string isolationDirectoryName,
            double insulatorHeight,
            double phaseSpacing,
            double bracketLength)
        {
            var outputRoot = Path.Combine(projectPath, InsulatorProjectFolderName);
            if (!string.IsNullOrWhiteSpace(isolationDirectoryName))
            {
                outputRoot = Path.Combine(outputRoot, isolationDirectoryName);
            }

            return Path.Combine(
                outputRoot,
                BuildInsulatorOutputFolderName(
                    ThreePadFolderLabel,
                    insulatorHeight,
                    phaseSpacing,
                    bracketLength));
        }

        /// <summary>
        /// 构建绝缘子物理驱动身份目录。W 使用实际支架长度（支架所在方向的宿主尺寸减 20），
        /// 厚度和单相根数虽然不在旧文件名中，也必须参与隔离，避免重新驱动已被其他宿主引用的文件。
        /// </summary>
        internal static string BuildThreePadInsulatorIsolationDirectoryName(
            double busbarThickness,
            double phaseSpacing,
            double insulatorHeight,
            double bracketHostSpan,
            int singlePhaseRootCount)
        {
            return CKBusbarBridge.Prototype.ThreePadInsulatorIdentity.DirectoryName(
                busbarThickness, phaseSpacing, insulatorHeight, bracketHostSpan, singlePhaseRootCount);
        }

        /// <summary>
        /// 构建绝缘子 PackAndGo 输出文件夹名：绝缘子-{垫块标签}-{绝缘子高}-{相间距}-{支架长}。
        /// </summary>
        private static string BuildInsulatorOutputFolderName(string padLabel, double insulatorHeight, double phaseSpacing, double bracketLength)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}-{1}-{2}-{3}-{4}",
                InsulatorOutputFolderNamePrefix,
                padLabel,
                FormatDimensionForFolderName(insulatorHeight),
                FormatDimensionForFolderName(phaseSpacing),
                FormatDimensionForFolderName(bracketLength));
        }

        /// <summary>
        /// 构建绝缘子 PackAndGo 文件名后缀：-{绝缘子高}-{相间距}-{支架长}（原名 + 此后缀）。
        /// </summary>
        private static string BuildInsulatorRenameSuffix(double insulatorHeight, double phaseSpacing, double bracketLength)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "-{0}-{1}-{2}",
                FormatDimensionForFolderName(insulatorHeight),
                FormatDimensionForFolderName(phaseSpacing),
                FormatDimensionForFolderName(bracketLength));
        }

        /// <summary>
        /// 将尺寸格式化为文件夹名片段（去除多余小数位）。
        /// </summary>
        private static string FormatDimensionForFolderName(double value)
        {
            return Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 依次驱动柜体基座目录下所有绝缘子装配体（文件名以“绝缘子+垫片+支架”开头，含 3/4 垫块），
        /// 将排厚、相间距、绝缘子高度、支架长度写入各装配体方程式全局变量。
        /// </summary>
        /// <param name="cabinetBaseDirectory">柜体基座目录完整路径。</param>
        /// <param name="busbarThickness">铜排厚度（mm）。</param>
        /// <param name="phaseSpacing">相间距（mm）。</param>
        /// <param name="insulatorHeight">绝缘子高度（mm）。</param>
        /// <param name="baseWidth">基座宽（mm）；支架长度 = 基座宽 − 20。</param>
        /// <param name="restoreContextModel">驱动完成后需要恢复编辑上下文的模型（通常为柜体总装配体）。</param>
        public void DriveInsulatorAssembliesInCabinetBase(
            string cabinetBaseDirectory,
            double busbarThickness,
            double phaseSpacing,
            double insulatorHeight,
            double baseWidth,
            IModelDoc2 restoreContextModel)
        {
            DriveInsulatorAssembliesInCabinetBase(
                cabinetBaseDirectory,
                busbarThickness,
                phaseSpacing,
                insulatorHeight,
                baseWidth,
                0,
                restoreContextModel);
        }

        /// <summary>
        /// 依次驱动柜体基座目录下所有绝缘子装配体（含子目录中的基座/柜体变体副本）。
        /// </summary>
        /// <param name="cabinetBaseDirectory">柜体基座目录完整路径。</param>
        /// <param name="busbarThickness">铜排厚度（mm）。</param>
        /// <param name="phaseSpacing">相间距（mm）。</param>
        /// <param name="insulatorHeight">绝缘子高度（mm）。</param>
        /// <param name="baseWidth">基座宽（mm）；支架长度 = 基座宽 − 20。</param>
        /// <param name="singlePhaseRootCount">单相根数（1~4）；为 0 时跳过垫块阵列压缩。</param>
        /// <param name="restoreContextModel">驱动完成后需要恢复编辑上下文的模型（通常为柜体总装配体）。</param>
        public void DriveInsulatorAssembliesInCabinetBase(
            string cabinetBaseDirectory,
            double busbarThickness,
            double phaseSpacing,
            double insulatorHeight,
            double baseWidth,
            int singlePhaseRootCount,
            IModelDoc2 restoreContextModel)
        {
            if (swApp == null)
            {
                return;
            }

            var insulatorAssemblyPaths = FindInsulatorAssemblyPaths(cabinetBaseDirectory, SearchOption.AllDirectories).ToList();
            if (insulatorAssemblyPaths.Count == 0)
            {
                throw new InvalidOperationException(
                    $"柜体基座目录下未找到绝缘子装配体（文件名以“{InsulatorAssemblyFileNamePrefix}”开头）：{cabinetBaseDirectory}");
            }

            var variableMap = BuildInsulatorGlobalVariableMap(busbarThickness, phaseSpacing, insulatorHeight, baseWidth);

            CloseDrivenInsulatorModels(DriveInsulatorAssemblyFiles(
                insulatorAssemblyPaths,
                variableMap,
                singlePhaseRootCount,
                restoreContextModel));
        }

        /// <summary>
        /// 依次驱动柜体基座目录下所有绝缘子装配体（含 3/4 垫块），
        /// 将排厚、相间距、绝缘子高度、支架长度写入各装配体方程式全局变量。
        /// </summary>
        /// <param name="cabinetBaseDirectory">柜体基座目录完整路径。</param>
        /// <param name="busbarSpec">铜排规格文本（如“80×8”）。</param>
        /// <param name="phaseSpacing">相间距（mm）。</param>
        /// <param name="insulatorHeight">绝缘子高度（mm）。</param>
        /// <param name="baseWidth">基座宽（mm）；支架长度 = 基座宽 − 20。</param>
        /// <param name="singlePhaseRootCount">单相根数（1~4）。</param>
        /// <param name="restoreContextModel">驱动完成后需要恢复编辑上下文的模型（通常为柜体总装配体）。</param>
        public void DriveInsulatorAssembliesInCabinetBase(
            string cabinetBaseDirectory,
            string busbarSpec,
            double phaseSpacing,
            double insulatorHeight,
            double baseWidth,
            int singlePhaseRootCount,
            IModelDoc2 restoreContextModel)
        {
            if (swApp == null || restoreContextModel == null || string.IsNullOrWhiteSpace(cabinetBaseDirectory))
            {
                return;
            }

            if (!TryParseBusbarSpec(busbarSpec, out _, out var thickness))
            {
                return;
            }

            DriveInsulatorAssembliesInCabinetBase(
                cabinetBaseDirectory,
                thickness,
                phaseSpacing,
                insulatorHeight,
                baseWidth,
                singlePhaseRootCount,
                restoreContextModel);

            SWHelper.EnsureAssemblyEditingContext(swApp, restoreContextModel);
            restoreContextModel.EditRebuild3();
        }

        /// <summary>
        /// 驱动柜体子装配体下未压缩的绝缘子装配体（3/4 垫块）：
        /// 写入排厚/相间距/绝缘子高度/支架长度，并按单相根数解压/压缩垫块阵列特征。
        /// 面对面场景限定在 A/B 柜-子装配体；面对墙场景限定在柜子装配体；压缩件不参与驱动。
        /// </summary>
        /// <param name="rootModel">柜体总装配体模型。</param>
        /// <param name="cabinetSubAssemblyPaths">柜体子装配体在总装配体中的层级路径，例如「A柜-子装配体」。</param>
        /// <param name="busbarSpec">铜排规格文本（如“80×8”）。</param>
        /// <param name="phaseSpacing">相间距（mm）。</param>
        /// <param name="insulatorHeight">绝缘子高度（mm）。</param>
        /// <param name="baseWidth">基座宽（mm）；支架长度 = 基座宽 − 20。</param>
        /// <param name="singlePhaseRootCount">单相根数（1~4）。</param>
        /// <param name="restoreContextModel">驱动完成后需要恢复编辑上下文的模型（通常为柜体总装配体）。</param>
        public void DriveInsulatorAssembliesInCabinetSubAssemblies(
            IModelDoc2 rootModel,
            IEnumerable<IReadOnlyList<string>> cabinetSubAssemblyPaths,
            string busbarSpec,
            double phaseSpacing,
            double insulatorHeight,
            double baseWidth,
            int singlePhaseRootCount,
            IModelDoc2 restoreContextModel)
        {
            if (swApp == null || rootModel == null || restoreContextModel == null || cabinetSubAssemblyPaths == null)
            {
                return;
            }

            if (!TryParseBusbarSpec(busbarSpec, out _, out var thickness))
            {
                return;
            }

            var insulatorAssemblyPaths = CollectInsulatorAssemblyPathsFromCabinetSubAssemblies(
                    rootModel,
                    cabinetSubAssemblyPaths)
                .ToList();
            if (insulatorAssemblyPaths.Count == 0)
            {
                return;
            }

            var variableMap = BuildInsulatorGlobalVariableMap(thickness, phaseSpacing, insulatorHeight, baseWidth);

            CloseDrivenInsulatorModels(DriveInsulatorAssemblyFiles(
                insulatorAssemblyPaths,
                variableMap,
                singlePhaseRootCount,
                restoreContextModel));

            SWHelper.EnsureAssemblyEditingContext(swApp, restoreContextModel);
            restoreContextModel.EditRebuild3();
        }

        /// <summary>
        /// 构建绝缘子装配体全局变量映射（排厚/相间距/绝缘子高度/支架长度）。
        /// </summary>
        private static Dictionary<string, object> BuildInsulatorGlobalVariableMap(
            double busbarThickness,
            double phaseSpacing,
            double insulatorHeight,
            double baseWidth)
        {
            if (baseWidth <= BracketLengthBaseWidthOffset)
            {
                throw new InvalidOperationException(
                    $"基座宽无效（{baseWidth}），无法计算支架长度（基座宽 − {BracketLengthBaseWidthOffset}）。");
            }

            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                { BusbarThicknessGlobalVariableName, busbarThickness },
                { PhaseSpacingGlobalVariableName, phaseSpacing },
                { InsulatorHeightGlobalVariableName, insulatorHeight },
                { BracketLengthGlobalVariableName, baseWidth - BracketLengthBaseWidthOffset }
            };
        }

        /// <summary>
        /// 依次打开绝缘子装配体文件；普通模板按通用逻辑驱动，L 型模板按其专用层级顺序驱动。
        /// 返回仍保持打开的模型，由实际宿主在装配完成后统一保存关闭。
        /// </summary>
        private IReadOnlyList<IModelDoc2> DriveInsulatorAssemblyFiles(
            IReadOnlyList<string> insulatorAssemblyPaths,
            IDictionary<string, object> variableMap,
            int singlePhaseRootCount,
            IModelDoc2 restoreContextModel,
            IReadOnlyCollection<int> suppressedLBracketPadComponentIndexes = null,
            bool? universalUseLBracket = null,
            UniversalInsulatorLayout universalLayout = null)
        {
            if (swApp == null || insulatorAssemblyPaths == null || insulatorAssemblyPaths.Count == 0 || variableMap == null)
            {
                return Array.Empty<IModelDoc2>();
            }

            universalLayout?.ValidateDriveDimensions(
                Convert.ToDouble(variableMap[BracketLengthGlobalVariableName], CultureInfo.InvariantCulture),
                Convert.ToDouble(variableMap[PhaseSpacingGlobalVariableName], CultureInfo.InvariantCulture));

            var swDriver = new SolidWorksDriver(swApp);
            var drivenModels = new List<IModelDoc2>();
            var lBracketModelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var insulatorAssemblyPath in insulatorAssemblyPaths)
                {
                    if (string.IsNullOrWhiteSpace(insulatorAssemblyPath) ||
                        IsSolidWorksTemporaryFileName(Path.GetFileName(insulatorAssemblyPath)))
                    {
                        continue;
                    }

                    var isLBracket = IsLBracketInsulatorAssemblyPath(insulatorAssemblyPath);
                    // 打开/重建本身就可能求值外部方程；L型必须先在关闭状态核验磁盘引用。
                    if (universalUseLBracket.HasValue || isLBracket || variableMap.ContainsKey("偏移尺寸")) EnsureInsulatorReferencesInsideOutputTree(insulatorAssemblyPath);
                    var isOffset = variableMap.ContainsKey("偏移尺寸");
                    var insulatorAssemblyModel = swDriver.OpenOrActivateDocument(insulatorAssemblyPath, rebuildAfterResolve: !universalUseLBracket.HasValue && !isLBracket && !isOffset);
                    if (insulatorAssemblyModel == null ||
                        insulatorAssemblyModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                    {
                        throw new InvalidOperationException(
                            $"无法打开绝缘子装配体：{insulatorAssemblyPath}");
                    }

                    // 先登记再驱动；若中途失败，catch 仍能定向保存关闭已修改的模型。
                    drivenModels.Add(insulatorAssemblyModel);
                    if (isLBracket) lBracketModelPaths.Add(Path.GetFullPath(insulatorAssemblyModel.GetPathName()));
                    if (insulatorAssemblyModel is AssemblyDoc insulatorAssemblyDoc)
                    {
                        try { insulatorAssemblyDoc.ResolveAllLightWeightComponents(false); } catch { }
                    }

                    // 各轮驱动由恢复会话核验实际文档和编辑目标，不能仅凭文档类型认定切换成功。
                    var isUniversal = IsUniversalInsulator(insulatorAssemblyModel);
                    if (universalLayout != null && !isUniversal)
                        throw new InvalidOperationException("当前绝缘子不支持三组距边布局，请使用通用绝缘子模板：" + insulatorAssemblyPath);
                    if (isUniversal)
                    {
                        lBracketModelPaths.Add(Path.GetFullPath(insulatorAssemblyModel.GetPathName()));
                        var reader = new ReferencedModelValueReader(insulatorAssemblyModel);
                        var offsetLayout = universalLayout != null || isOffset || reader.TryProperty(UniversalOffsetProperty) == "1";
                        var offset = universalLayout != null ? universalLayout.Offset : isOffset
                            ? (Convert.ToDouble(variableMap["偏移方向"], CultureInfo.InvariantCulture) > .5 ? 1d : -1d)
                                * Convert.ToDouble(variableMap["偏移尺寸"], CultureInfo.InvariantCulture)
                            : offsetLayout ? ReadUniversalLayout(reader).Offset : 0d;
                        var layout = universalLayout ?? UniversalInsulatorLayout.FromExistingCopperLayout(
                            Convert.ToDouble(variableMap[BracketLengthGlobalVariableName], CultureInfo.InvariantCulture),
                            Convert.ToDouble(variableMap[PhaseSpacingGlobalVariableName], CultureInfo.InvariantCulture), offset);
                        var plan = new UniversalInsulatorDrivePlan(layout,
                            universalUseLBracket ?? ReadUniversalLBracket(insulatorAssemblyModel),
                            Convert.ToDouble(variableMap[InsulatorHeightGlobalVariableName], CultureInfo.InvariantCulture),
                            Convert.ToDouble(variableMap[BusbarThicknessGlobalVariableName], CultureInfo.InvariantCulture),
                            singlePhaseRootCount, suppressedLBracketPadComponentIndexes);
                        DriveUniversalInsulator(insulatorAssemblyModel, plan, childModel =>
                        {
                            if (!drivenModels.Any(m => string.Equals(m.GetPathName(), childModel.GetPathName(), StringComparison.OrdinalIgnoreCase)))
                                drivenModels.Add(childModel);
                            lBracketModelPaths.Add(Path.GetFullPath(childModel.GetPathName()));
                        }, offsetLayout);
                    }
                    else if (isLBracket)
                    {
                        var recovery = _driveRecovery ?? new InsulatorDriveRecoverySession(swApp);
                        recovery.Drive(insulatorAssemblyModel, (currentModel, registerModel) => DriveSingleLBracketInsulatorAssemblyModel(
                            swApp,
                            swDriver,
                            currentModel,
                            variableMap,
                            singlePhaseRootCount,
                            suppressedLBracketPadComponentIndexes,
                            insulatorAssemblyPath,
                            registerModel), childModel =>
                            {
                                // 叶子也要登记，确保被压缩的垫块仍会在父总成之前保存。
                                if (!drivenModels.Any(model => string.Equals(model.GetPathName(), childModel.GetPathName(), StringComparison.OrdinalIgnoreCase)))
                                    drivenModels.Add(childModel);
                                lBracketModelPaths.Add(Path.GetFullPath(childModel.GetPathName()));
                            }, allowDeferred: _driveRecovery != null);
                    }
                    else
                    {
                        var recovery = _driveRecovery ?? new InsulatorDriveRecoverySession(swApp);
                        recovery.Drive(insulatorAssemblyModel, (currentModel, registerModel) =>
                        {
                            if (isOffset) RebindOffsetInsulatorEquations(currentModel);
                            DriveSingleInsulatorAssemblyModel(swDriver, currentModel, variableMap,
                                singlePhaseRootCount, insulatorAssemblyPath);
                            if (isOffset) ValidateOffsetInsulatorGeometry(currentModel, variableMap);
                        }, childModel =>
                        {
                            if (!drivenModels.Any(existing => string.Equals(existing.GetPathName(), childModel.GetPathName(), StringComparison.OrdinalIgnoreCase)))
                                drivenModels.Add(childModel);
                        }, allowDeferred: _driveRecovery != null);
                    }
                }

                return drivenModels;
            }
            catch (Exception originalException)
            {
                try
                {
                    CloseDrivenInsulatorModels(drivenModels, lBracketModelPaths);
                }
                catch (Exception cleanupException)
                {
                    FaceToFaceFrameCleanupDiagnostics.AppendData(
                        originalException,
                        FaceToFaceFrameCleanupDiagnostics.DocumentCleanupWarningDataKey,
                        "绝缘子驱动失败后的保存关闭异常：" + cleanupException.Message);
                }

                throw;
            }
            finally
            {
                if (restoreContextModel != null)
                {
                    SWHelper.EnsureAssemblyEditingContext(swApp, restoreContextModel);
                }
            }
        }

        private void CloseDrivenInsulatorModels(IEnumerable<IModelDoc2> models, ISet<string> closeWithoutSavePaths = null)
        {
            if (models == null)
            {
                return;
            }

            foreach (var model in models.Reverse())
            {
                // L型驱动失败时禁止SaveReferenced，未通过隔离检查的引用可能仍指向源模板。
                if (closeWithoutSavePaths != null && closeWithoutSavePaths.Contains(Path.GetFullPath(model.GetPathName())))
                    swApp.CloseDoc(model.GetTitle());
                else
                    SaveAndCloseModel(model);
            }
        }

        /// <summary>
        /// 驱动单个绝缘子装配体，严格按以下顺序执行：
        /// 1. 按单相根数确定垫块特征（垫块1-1、垫块2、垫块3…）压缩状态；
        /// 2. 写入排厚、相间距、绝缘子高度、支架长度（此步不重建）；
        /// 3. 新常规模板的12厚先刷新6厚基础实例，再启用双片阵列并刷新最终状态；
        /// 保存由宿主装配完成后的生命周期统一执行。
        /// </summary>
        /// <param name="swDriver">SolidWorks 驱动器。</param>
        /// <param name="insulatorAssemblyModel">绝缘子装配体模型。</param>
        /// <param name="variableMap">全局变量映射。</param>
        /// <param name="singlePhaseRootCount">单相根数（1~4）；为 0 时跳过垫块阵列压缩。</param>
        /// <param name="insulatorAssemblyPath">绝缘子装配体路径，用于异常提示。</param>
        private void DriveSingleInsulatorAssemblyModel(
            SolidWorksDriver swDriver,
            IModelDoc2 insulatorAssemblyModel,
            IDictionary<string, object> variableMap,
            int singlePhaseRootCount,
            string insulatorAssemblyPath)
        {
            if (swDriver == null || insulatorAssemblyModel == null || variableMap == null)
            {
                return;
            }

            // 12厚阵列引用基础垫块实例，先压缩依赖方，避免改根数时丢失种子引用。
            var stackedPadPattern = (insulatorAssemblyModel as AssemblyDoc)?.FeatureByName(TwelveMillimeterPadPatternName) as Feature;
            if (stackedPadPattern != null) SetFeatureSuppression(stackedPadPattern, true);

            // 1. 先定垫块阵列结构，避免后续在旧阵列状态下用新相间距等参数求值。
            if (singlePhaseRootCount > 0)
            {
                ApplyInsulatorPadArraySuppression(insulatorAssemblyModel, singlePhaseRootCount);
            }
            new InsulatorDriveRecoverySession(swApp).Activate(insulatorAssemblyModel.GetPathName(),
                insulatorAssemblyModel.ConfigurationManager.ActiveConfiguration.Name);

            // 2. 再写全局变量；暂不重建，所有尺寸写完后统一处理。
            swDriver.ApplyGlobalVariablesRequired(insulatorAssemblyModel, variableMap, rebuildAfterChange: false,
                equationWriteObserver: (manager, index, previous, requested) =>
                {
                    var writeStatus = manager.Status;
                    if (writeStatus < 0)
                        throw new InsulatorDriveRetryException("绝缘子方程写入失败：" + insulatorAssemblyPath
                            + "；请求=" + requested + "；实际=" + manager.Equation[index]);
                });
            EnsureRequiredInsulatorGlobalVariablesApplied(
                insulatorAssemblyModel,
                variableMap,
                insulatorAssemblyPath);

            var padThicknessEquation = ApplyConventionalInsulatorPadThickness(insulatorAssemblyModel, variableMap);

            // 3. 刷新最终阵列状态；装配完成前不做额外保存或关闭。
            FinalizeInsulatorAssemblyModel(insulatorAssemblyModel);
            var valueReader = new ReferencedModelValueReader(insulatorAssemblyModel);
            foreach (var variable in variableMap)
            {
                var expected = Convert.ToDouble(variable.Value, CultureInfo.InvariantCulture);
                var actual = valueReader.Global(variable.Key);
                if (double.IsNaN(actual) || double.IsInfinity(actual) || Math.Abs(actual - expected) > 0.01d)
                    throw new InsulatorDriveRetryException("绝缘子驱动反读不符：" + insulatorAssemblyPath + "；"
                        + variable.Key + "；实际=" + actual.ToString("R", CultureInfo.InvariantCulture)
                        + "；期望=" + expected.ToString("R", CultureInfo.InvariantCulture));
            }
            if (singlePhaseRootCount > 0)
            {
                var actualRootCount = ReadSinglePhaseRootCount(insulatorAssemblyModel);
                if (actualRootCount != singlePhaseRootCount)
                    throw new InsulatorDriveRetryException("绝缘子垫块根数反读不符：" + insulatorAssemblyPath
                        + "；实际=" + actualRootCount + "；期望=" + singlePhaseRootCount);
            }
            if (padThicknessEquation >= 0)
            {
                VerifyConventionalInsulatorPadThickness(insulatorAssemblyModel, variableMap, padThicknessEquation);
            }
        }

        private static int ApplyConventionalInsulatorPadThickness(
            IModelDoc2 model,
            IDictionary<string, object> variableMap)
        {
            var pattern = (model as AssemblyDoc)?.FeatureByName(TwelveMillimeterPadPatternName) as Feature;
            // 常规与可偏移模板共用此结构契约；L型在垫块子总成内处理双片阵列。
            if (pattern == null) return -1;

            var thickness = Convert.ToDouble(variableMap[BusbarThicknessGlobalVariableName], CultureInfo.InvariantCulture);
            if (double.IsNaN(thickness) || double.IsInfinity(thickness) || thickness <= 0)
                throw new InvalidOperationException("绝缘子垫块排厚无效：" + thickness);

            var manager = model.GetEquationMgr() as IEquationMgr
                ?? throw new InvalidOperationException("绝缘子垫块厚度方程缺失：" + model.GetPathName());
            var indexes = Enumerable.Range(0, manager.GetCount()).Where(index =>
            {
                var equation = manager.Equation[index];
                var separator = equation.IndexOf('=');
                return separator > 0 && Regex.IsMatch(equation.Substring(0, separator).Trim(),
                    "^\"厚度@[^\"@]+@8UQ\\.584\\.013[^\"@]*\\.Part\"$", RegexOptions.IgnoreCase);
            }).ToArray();
            if (indexes.Length != 1)
                throw new InvalidOperationException("绝缘子必须唯一确定垫块原厚度方程：" + model.GetPathName());

            var stacked = Math.Abs(thickness - 12d) < 0.01d;
            var original = manager.Equation[indexes[0]];
            // 保留模板原写入对象；12厚使用两片6厚，其他排厚恢复原变量关联。
            manager.Equation[indexes[0]] = original.Substring(0, original.IndexOf('=') + 1)
                + (stacked ? " 6" : " \"" + BusbarThicknessGlobalVariableName + "\"");
            var thicknessWriteStatus = manager.Status;
            if (thicknessWriteStatus < 0)
                throw new InsulatorDriveRetryException("垫块厚度方程写入失败：" + model.GetPathName()
                    + "；实际=" + manager.Equation[indexes[0]] + "；期望厚度=" + (stacked ? 6d : thickness));
            // SW2022在旧尺寸未求值时解压会丢失后级阵列的种子；先让基础实例稳定。
            if (stacked && (!model.ForceRebuild3(false) || !model.EditRebuild3()))
                throw new InsulatorDriveRetryException("绝缘子6厚垫块基础阵列重建失败：" + model.GetPathName());

            var action = stacked ? swFeatureSuppressionAction_e.swUnSuppressFeature : swFeatureSuppressionAction_e.swSuppressFeature;
            if (pattern.IsSuppressed() == stacked
                && !pattern.SetSuppression2((int)action, (int)swInConfigurationOpts_e.swThisConfiguration, null))
                throw new InsulatorDriveRetryException("绝缘子排厚12阵列压缩状态写入失败：" + model.GetPathName());
            if (pattern.IsSuppressed() == stacked)
                throw new InsulatorDriveRetryException("绝缘子排厚12阵列压缩状态写入失败：" + model.GetPathName());

            return indexes[0];
        }

        private static void VerifyConventionalInsulatorPadThickness(
            IModelDoc2 model,
            IDictionary<string, object> variableMap,
            int equationIndex)
        {
            var thickness = Convert.ToDouble(variableMap[BusbarThicknessGlobalVariableName], CultureInfo.InvariantCulture);
            var stacked = Math.Abs(thickness - 12d) < 0.01d;
            var expected = stacked ? 6d : thickness;
            var manager = (IEquationMgr)model.GetEquationMgr();
            var padComponents = (((AssemblyDoc)model).GetComponents(true) as object[] ?? Array.Empty<object>())
                .OfType<Component2>()
                .Where(component => Path.GetFileNameWithoutExtension(component.GetPathName())
                    .StartsWith("8UQ.584.013", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var padModels = padComponents.Select(component => component.GetModelDoc2() as IModelDoc2)
                .Where(pad => pad != null).GroupBy(pad => pad.GetPathName(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToArray();
            if (padModels.Length != 1 || padComponents.Any(component => !string.Equals(
                component.ReferencedConfiguration, padModels[0].ConfigurationManager.ActiveConfiguration.Name,
                StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("绝缘子垫块模型或引用配置不唯一：" + model.GetPathName());

            var sheet = (((PartDoc)padModels[0]).FeatureByName("钣金8") as Feature)?.GetDefinition() as ISheetMetalFeatureData;
            var actualThickness = sheet == null ? double.NaN : sheet.Thickness * 1000d;
            var equationValue = manager.Value[equationIndex];
            var pattern = ((AssemblyDoc)model).FeatureByName(TwelveMillimeterPadPatternName) as Feature;
            var patternData = pattern?.GetDefinition() as ILocalLinearPatternFeatureData;
            var stackedQuantityMatches = !stacked || (patternData != null
                && patternData.D1TotalInstances == 2
                && padComponents.Count(component => !component.IsSuppressed()) == patternData.GetSeedComponentCount() * 2);
            if (double.IsNaN(actualThickness) || double.IsInfinity(actualThickness)
                || Math.Abs(actualThickness - expected) > 0.01d
                || double.IsNaN(equationValue) || double.IsInfinity(equationValue)
                || Math.Abs(equationValue - expected) > 0.01d
                || pattern == null || pattern.IsSuppressed() == stacked || !stackedQuantityMatches)
                throw new InsulatorDriveRetryException("绝缘子垫块厚度、叠加数量或排厚12阵列未生效：" + model.GetPathName()
                    + "；实体厚度=" + actualThickness + "；方程厚度=" + equationValue + "；期望厚度=" + expected);
        }

        private static void RebindOffsetInsulatorEquations(IModelDoc2 model)
        {
            var components = (((AssemblyDoc)model).GetComponents(true) as object[] ?? Array.Empty<object>()).OfType<Component2>().ToArray();
            var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var stem in new[] { "8GX.043.003-P", "8UQ.584.013-P", "绝缘子-P" })
            {
                var paths = components.Select(c => c.GetPathName()).Where(p => Path.GetFileNameWithoutExtension(p)
                    .StartsWith(stem, StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (paths.Length != 1) throw new InvalidOperationException("可偏移绝缘子模板零件缺失或重复：" + stem);
                mapping.Add(stem, Path.GetFileNameWithoutExtension(paths[0]));
            }
            var manager = model.GetEquationMgr() as IEquationMgr;
            if (manager == null) throw new InvalidOperationException("可偏移绝缘子缺少方程式管理器。");
            // SW2022 PackAndGo会改组件文件名，但方程中的<实例号>.Part引用可能仍保留源名。
            for (var i = 0; i < manager.GetCount(); i++)
            {
                var original = manager.Equation[i]; var expression = original;
                foreach (var pair in mapping)
                    expression = System.Text.RegularExpressions.Regex.Replace(expression,
                        "@" + System.Text.RegularExpressions.Regex.Escape(pair.Key) + @"(?=(?:<\d+>|-\d+)\.Part)",
                        match => "@" + pair.Value);
                if (expression != original)
                {
                    manager.Equation[i] = expression;
                    var rebindStatus = manager.Status;
                    if (rebindStatus < 0) throw new InsulatorDriveRetryException("可偏移绝缘子原尺寸方程重绑定失败："
                        + model.GetPathName() + "；请求=" + expression + "；实际=" + manager.Equation[i]);
                }
            }
        }

        private static void ValidateOffsetInsulatorGeometry(IModelDoc2 model, IDictionary<string, object> variables)
        {
            var direction = Convert.ToDouble(variables["偏移方向"], CultureInfo.InvariantCulture);
            var size = Convert.ToDouble(variables["偏移尺寸"], CultureInfo.InvariantCulture);
            var offset = direction > .5 ? size : -size;
            var spacing = Convert.ToDouble(variables[PhaseSpacingGlobalVariableName], CultureInfo.InvariantCulture);
            var children = (((AssemblyDoc)model).GetComponents(true) as object[] ?? Array.Empty<object>()).OfType<Component2>()
                .Where(c => !c.IsSuppressed()).ToArray();
            var bodies = children.Where(c => Path.GetFileNameWithoutExtension(c.GetPathName())
                .StartsWith("绝缘子-P", StringComparison.Ordinal)).Select(c => (double[])c.Transform2.ArrayData)
                .OrderBy(t => t[11]).ToArray();
            if (bodies.Length != 3 || bodies.Where((t, i) => Math.Abs(t[11] * 1000 - offset - (i - 1) * spacing) > .01).Any())
                throw new InsulatorDriveRetryException("可偏移模板三相绝缘子实际位置与横移计划不符：" + model.GetPathName());
            var bracket = children.Single(c => Path.GetFileNameWithoutExtension(c.GetPathName()).StartsWith("8GX.043.003-P", StringComparison.Ordinal));
            var bracketModel = bracket.GetModelDoc2() as IModelDoc2;
            var bodyModel = children.First(c => Path.GetFileNameWithoutExtension(c.GetPathName()).StartsWith("绝缘子-P", StringComparison.Ordinal))
                .GetModelDoc2() as IModelDoc2;
            var bracketSpan = (bracketModel?.Parameter("D2@草图2") as Dimension)?.SystemValue * 1000;
            var bodyHeight = (bodyModel?.Parameter("D2@草图1") as Dimension)?.SystemValue * 1000;
            if (!bracketSpan.HasValue || Math.Abs(bracketSpan.Value - Convert.ToDouble(variables[BracketLengthGlobalVariableName], CultureInfo.InvariantCulture)) > .01 ||
                !bodyHeight.HasValue || Math.Abs(bodyHeight.Value - Convert.ToDouble(variables[InsulatorHeightGlobalVariableName], CultureInfo.InvariantCulture)) > .01)
                throw new InsulatorDriveRetryException("可偏移模板支架或绝缘子本体实际尺寸与计划不符：" + model.GetPathName()
                    + "；支架实际=" + bracketSpan + "；期望=" + variables[BracketLengthGlobalVariableName]
                    + "；本体实际=" + bodyHeight + "；期望=" + variables[InsulatorHeightGlobalVariableName]);
            var transform = (double[])bracket.Transform2.ArrayData;
            if (Enumerable.Range(9, 3).Any(i => Math.Abs(transform[i] * 1000) > .01))
                throw new InvalidOperationException("可偏移模板的支架安装基准发生移动：" + model.GetPathName());
        }

        /// <summary>
        /// 驱动 L 型支架绝缘子：
        /// 同步支架自身相间距及垫块实体厚度，按N−1启用垫块，最后反读实际几何和既有安装布局。
        /// </summary>
        private static void DriveSingleLBracketInsulatorAssemblyModel(
            ISldWorks swApp,
            SolidWorksDriver swDriver,
            IModelDoc2 insulatorAssemblyModel,
            IDictionary<string, object> variableMap,
            int singlePhaseRootCount,
            IReadOnlyCollection<int> suppressedPadComponentIndexes,
            string insulatorAssemblyPath,
            Action<IModelDoc2> registerModel)
        {
            if (swApp == null || swDriver == null || insulatorAssemblyModel == null || variableMap == null)
            {
                return;
            }

            if (!(insulatorAssemblyModel is AssemblyDoc))
            {
                throw new InvalidOperationException(
                    "L型支架绝缘子模型不是有效装配体：" + insulatorAssemblyPath);
            }

            var variables = SelectRequiredInsulatorVariables(
                variableMap,
                PhaseSpacingGlobalVariableName,
                BracketLengthGlobalVariableName,
                InsulatorHeightGlobalVariableName,
                BusbarThicknessGlobalVariableName);
            var plan = new LBracketInsulatorDrivePlan(
                Convert.ToDouble(variables[PhaseSpacingGlobalVariableName], CultureInfo.InvariantCulture),
                Convert.ToDouble(variables[BusbarThicknessGlobalVariableName], CultureInfo.InvariantCulture),
                Convert.ToDouble(variables[InsulatorHeightGlobalVariableName], CultureInfo.InvariantCulture),
                Convert.ToDouble(variables[BracketLengthGlobalVariableName], CultureInfo.InvariantCulture),
                singlePhaseRootCount,
                NormalizeLBracketSuppressedPadComponentIndexes(suppressedPadComponentIndexes));
            try
            {
                var operations = new InsulatorSolidWorksDriveOperations(swApp, swDriver, insulatorAssemblyModel, registerModel);
                operations.Prepare();
                operations.DriveParts(plan);
                operations.DrivePadAssemblies(plan);
                operations.DriveAssembly(plan);
                var actual = operations.Readback();
                try { plan.Validate(actual); }
                catch (InvalidOperationException mismatch)
                { throw new InsulatorDriveRetryException(mismatch.Message, mismatch); }
            }
            catch (System.Runtime.InteropServices.COMException exception)
            {
                throw new InsulatorDriveRetryException("L型支架绝缘子驱动或反读失败：" + insulatorAssemblyPath, exception);
            }
        }

        private static IReadOnlyList<Component2> ApplyLBracketPadSubAssemblySuppression(
            AssemblyDoc assembly,
            IReadOnlyCollection<int> suppressedPadComponentIndexes,
            string insulatorAssemblyPath)
        {
            object[] componentObjects;
            try
            {
                componentObjects = assembly.GetComponents(true) as object[];
            }
            catch (System.Runtime.InteropServices.COMException exception)
            {
                throw new InsulatorDriveRetryException(
                    "读取L型支架绝缘子垫块子装配体失败：" + insulatorAssemblyPath,
                    exception);
            }

            var topLevelComponents = (componentObjects ?? Array.Empty<object>())
                .OfType<Component2>()
                .ToArray();
            var componentsByIndex = new Dictionary<int, Component2>();
            foreach (var component in topLevelComponents)
            {
                if (!TryResolveLBracketPadComponentIndex(component, out var index))
                {
                    continue;
                }

                if (componentsByIndex.ContainsKey(index))
                {
                    throw new InvalidOperationException(
                        "L型支架绝缘子存在重复的绝缘子垫块组件序号："
                        + index
                        + "；宿主="
                        + insulatorAssemblyPath);
                }

                componentsByIndex.Add(index, component);
            }

            var missingIndexes = Enumerable.Range(1, MaxSinglePhaseRootCount - 1)
                .Where(index => !componentsByIndex.ContainsKey(index))
                .ToArray();
            if (missingIndexes.Length > 0)
            {
                throw new InvalidOperationException(
                    "L型支架绝缘子缺少子装配体："
                    + string.Join("、", missingIndexes.Select(
                        index => LBracketPadSubAssemblyNamePrefix + "<" + index + ">"))
                    + "；宿主="
                    + insulatorAssemblyPath
                    + "；实际一级组件="
                    + BuildComponentDiagnostics(topLevelComponents));
            }

            var suppressedIndexes = NormalizeLBracketSuppressedPadComponentIndexes(
                suppressedPadComponentIndexes);
            var enabledComponents = new List<Component2>();
            foreach (var pair in componentsByIndex.OrderBy(item => item.Key))
            {
                var suppress = suppressedIndexes.Contains(pair.Key);
                pair.Value.SetSuppression2(
                    suppress
                        ? (int)swComponentSuppressionState_e.swComponentSuppressed
                        : (int)swComponentSuppressionState_e.swComponentResolved);
                if (pair.Value.IsSuppressed() != suppress)
                {
                    throw new InsulatorDriveRetryException(
                        "L型支架绝缘子垫块子装配体压缩状态未生效："
                        + pair.Value.Name2
                        + "；实际压缩=" + pair.Value.IsSuppressed()
                        + "；期望压缩="
                        + suppress);
                }

                if (!suppress)
                {
                    enabledComponents.Add(pair.Value);
                }
            }

            return enabledComponents;
        }

        private static Dictionary<string, object> SelectRequiredInsulatorVariables(
            IDictionary<string, object> variableMap,
            params string[] requiredNames)
        {
            var selected = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var requiredName in requiredNames)
            {
                if (!variableMap.TryGetValue(requiredName, out var value))
                {
                    throw new InvalidOperationException(
                        "绝缘子驱动参数缺少全局变量：" + requiredName);
                }

                selected.Add(requiredName, value);
            }

            return selected;
        }

        internal static bool IsLBracketInsulatorAssemblyPath(string assemblyPath)
        {
            var fileName = Path.GetFileNameWithoutExtension(assemblyPath);
            var templateName = Path.GetFileNameWithoutExtension(
                LBracketOddSegmentInsulatorAsmFileName);
            return string.Equals(fileName, templateName, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(fileName)
                    && fileName.StartsWith(
                        templateName + "-",
                        StringComparison.OrdinalIgnoreCase));
        }

        internal static bool TryParseLBracketPadComponentIndex(
            string componentName,
            out int componentIndex)
        {
            componentIndex = 0;
            if (string.IsNullOrWhiteSpace(componentName))
            {
                return false;
            }

            var leafName = componentName.Trim();
            var atIndex = leafName.IndexOf('@');
            if (atIndex >= 0)
            {
                leafName = leafName.Substring(0, atIndex);
            }

            var slashIndex = leafName.LastIndexOf('/');
            if (slashIndex >= 0)
            {
                leafName = leafName.Substring(slashIndex + 1);
            }

            if (!leafName.StartsWith(
                LBracketPadSubAssemblyNamePrefix,
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var match = Regex.Match(leafName, @"<(\d+)>$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                // SolidWorks 的 Component2.Name2/GetSelectByIDString 常以“-1”表示实例序号，
                // 即使特征树界面显示为“<1>”。
                match = Regex.Match(leafName, @"-(\d+)$", RegexOptions.IgnoreCase);
            }

            return match.Success
                && int.TryParse(
                    match.Groups[1].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out componentIndex)
                && componentIndex >= 1
                && componentIndex < MaxSinglePhaseRootCount;
        }

        internal static bool TryResolveLBracketPadComponentIndex(
            Component2 component,
            out int componentIndex)
        {
            componentIndex = 0;
            if (component == null)
            {
                return false;
            }

            string name = null;
            string selectId = null;
            try { name = component.Name2; } catch { }
            if (TryParseLBracketPadComponentIndex(name, out componentIndex))
            {
                return true;
            }

            try { selectId = component.GetSelectByIDString(); } catch { }
            return TryParseLBracketPadComponentIndex(selectId, out componentIndex);
        }

        private static string BuildComponentDiagnostics(
            IEnumerable<Component2> components)
        {
            var descriptions = new List<string>();
            foreach (var component in components ?? Enumerable.Empty<Component2>())
            {
                string name = null;
                string selectId = null;
                string path = null;
                try { name = component.Name2; } catch { }
                try { selectId = component.GetSelectByIDString(); } catch { }
                try { path = component.GetPathName(); } catch { }
                descriptions.Add(
                    "Name2=" + (name ?? "<空>")
                    + ", SelectId=" + (selectId ?? "<空>")
                    + ", Path=" + (path ?? "<空>"));
            }

            return descriptions.Count == 0
                ? "<无>"
                : string.Join(" | ", descriptions);
        }

        internal static ISet<int> NormalizeLBracketSuppressedPadComponentIndexes(
            IReadOnlyCollection<int> suppressedPadComponentIndexes)
        {
            var indexes = new HashSet<int>();
            if (suppressedPadComponentIndexes == null)
            {
                return indexes;
            }

            foreach (var index in suppressedPadComponentIndexes)
            {
                if (index < 1 || index >= MaxSinglePhaseRootCount)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(suppressedPadComponentIndexes),
                        "L型支架绝缘子垫块子装配体序号必须为1、2或3：" + index);
                }

                indexes.Add(index);
            }

            return indexes;
        }

        /// <summary>
        /// 校验当前变量映射中要求的绝缘子全局变量已成功写入。
        /// </summary>
        private static void EnsureRequiredInsulatorGlobalVariablesApplied(
            IModelDoc2 insulatorAssemblyModel,
            IDictionary<string, object> variableMap,
            string insulatorAssemblyPath)
        {
            var eqMgr = insulatorAssemblyModel?.GetEquationMgr() as IEquationMgr;
            if (eqMgr == null)
            {
                throw new InvalidOperationException(
                    $"绝缘子装配体“{Path.GetFileName(insulatorAssemblyPath)}”不支持方程式写入。");
            }

            var missingVariables = new List<string>();
            foreach (var requiredName in new[]
                     {
                         BusbarThicknessGlobalVariableName,
                         PhaseSpacingGlobalVariableName,
                         InsulatorHeightGlobalVariableName,
                         BracketLengthGlobalVariableName,
                         "偏移方向", "偏移尺寸"
                     })
            {
                if (!variableMap.ContainsKey(requiredName))
                {
                    continue;
                }

                if (!EquationManagerContainsGlobalVariable(eqMgr, requiredName))
                {
                    missingVariables.Add(requiredName);
                }
            }

            if (missingVariables.Count > 0)
            {
                throw new InvalidOperationException(
                    $"绝缘子装配体“{Path.GetFileName(insulatorAssemblyPath)}”驱动失败，未找到以下全局变量：{string.Join("、", missingVariables)}");
            }
        }

        /// <summary>
        /// 按单相根数压缩/解压绝缘子装配体及其子零件中的垫块特征。
        /// 规则：保留序号 1..(单相根数−1) 的垫块（如垫块1-1、垫块2、垫块3），其余压缩。
        /// </summary>
        /// <param name="insulatorAssemblyModel">绝缘子装配体模型。</param>
        /// <param name="singlePhaseRootCount">单相根数（1~4）。</param>
        private void ApplyInsulatorPadArraySuppression(IModelDoc2 insulatorAssemblyModel, int singlePhaseRootCount)
        {
            if (insulatorAssemblyModel == null)
            {
                return;
            }

            var normalizedRootCount = singlePhaseRootCount;
            if (normalizedRootCount < 1)
            {
                normalizedRootCount = 1;
            }

            if (normalizedRootCount > MaxSinglePhaseRootCount)
            {
                normalizedRootCount = MaxSinglePhaseRootCount;
            }

            var enabledPadArrayCount = normalizedRootCount - 1;
            var processedModelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            ApplyInsulatorPadArraySuppressionToModel(insulatorAssemblyModel, enabledPadArrayCount, processedModelPaths);

            if (insulatorAssemblyModel is AssemblyDoc assemblyDoc)
            {
                object[] components = null;
                try
                {
                    components = assemblyDoc.GetComponents(true) as object[];
                }
                catch
                {
                    components = null;
                }

                if (components != null)
                {
                    foreach (var componentObject in components)
                    {
                        var component = componentObject as Component2;
                        if (component == null)
                        {
                            continue;
                        }

                        IModelDoc2 componentModel = null;
                        try
                        {
                            componentModel = component.GetModelDoc2() as IModelDoc2;
                        }
                        catch
                        {
                            componentModel = null;
                        }

                        ApplyInsulatorPadArraySuppressionToModel(componentModel, enabledPadArrayCount, processedModelPaths);
                    }
                }
            }
        }

        /// <summary>
        /// 从绝缘子装配体及其一级子模型的垫块特征压缩状态反读单相根数。
        /// </summary>
        internal static int ReadSinglePhaseRootCount(IModelDoc2 insulatorAssemblyModel, string configuration = null)
        {
            if (insulatorAssemblyModel == null)
            {
                throw new ArgumentNullException(nameof(insulatorAssemblyModel));
            }

            if (IsUniversalInsulator(insulatorAssemblyModel))
                return new ReferencedModelValueReader(insulatorAssemblyModel, configuration).SharedInsulatorRootCount();

            var states = new List<Tuple<int, bool>>();
            CollectPadSuppressionStates(insulatorAssemblyModel, states, configuration);
            if (insulatorAssemblyModel is AssemblyDoc assemblyDoc)
            {
                var components = assemblyDoc.GetComponents(true) as object[];
                if (components != null)
                {
                    foreach (var component in components.OfType<Component2>())
                    {
                        var model = component.GetModelDoc2() as IModelDoc2;
                        if (model != null)
                        {
                            CollectPadSuppressionStates(model, states, configuration == null ? null : component.ReferencedConfiguration);
                        }
                    }
                }
            }

            return ResolveSinglePhaseRootCount(states);
        }

        private static void CollectPadSuppressionStates(
            IModelDoc2 model,
            ICollection<Tuple<int, bool>> states, string configuration = null)
        {
            foreach (var feature in CollectPadFeatures(model))
            {
                int index;
                if (TryParsePadFeatureIndex(feature.Name, out index))
                {
                    if (configuration == null) states.Add(Tuple.Create(index, feature.IsSuppressed()));
                    else
                    {
                        var suppressed = feature.IsSuppressed2((int)swInConfigurationOpts_e.swSpecifyConfiguration,
                            new[] { configuration }) as bool[];
                        if (suppressed == null || suppressed.Length != 1)
                            throw new InvalidOperationException("无法读取绝缘子引用配置的垫块状态：" + configuration + "/" + feature.Name);
                        states.Add(Tuple.Create(index, suppressed[0]));
                    }
                }
            }
        }

        internal static int ResolveSinglePhaseRootCount(
            IEnumerable<Tuple<int, bool>> suppressionStates)
        {
            if (suppressionStates == null)
            {
                throw new ArgumentNullException(nameof(suppressionStates));
            }

            var statesByIndex = suppressionStates
                .Where(state => state != null && state.Item1 >= 1 && state.Item1 < MaxSinglePhaseRootCount)
                .GroupBy(state => state.Item1)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(state => state.Item2).Distinct().ToArray());
            if (statesByIndex.Count == 0)
            {
                throw new InvalidOperationException("绝缘子模型缺少可反读的垫块特征。");
            }

            var enabledPadCount = 0;
            var encounteredSuppressed = false;
            for (var index = 1; index < MaxSinglePhaseRootCount; index++)
            {
                bool[] states;
                if (!statesByIndex.TryGetValue(index, out states) || states.Length != 1)
                {
                    throw new InvalidOperationException(
                        "绝缘子垫块特征状态缺失或不一致：垫块" + index);
                }

                var isSuppressed = states[0];
                if (isSuppressed)
                {
                    encounteredSuppressed = true;
                }
                else
                {
                    if (encounteredSuppressed)
                    {
                        throw new InvalidOperationException("绝缘子垫块特征不是连续解压状态。");
                    }

                    enabledPadCount++;
                }
            }

            return enabledPadCount + 1;
        }

        /// <summary>
        /// 全局变量与垫块阵列均写入后，强制完整重建装配体，使方程式与特征状态同步生效。
        /// </summary>
        private static void FinalizeInsulatorAssemblyModel(IModelDoc2 insulatorAssemblyModel)
        {
            if (insulatorAssemblyModel == null)
            {
                return;
            }

            if (!insulatorAssemblyModel.ForceRebuild3(false) || !insulatorAssemblyModel.EditRebuild3())
                throw new InsulatorDriveRetryException("绝缘子重建未通过：" + insulatorAssemblyModel.GetPathName());
        }

        /// <summary>
        /// 在单个零件/装配体文档中调整垫块阵列特征压缩状态。
        /// </summary>
        private void ApplyInsulatorPadArraySuppressionToModel(
            IModelDoc2 model,
            int enabledPadArrayCount,
            ISet<string> processedModelPaths)
        {
            if (model == null || processedModelPaths == null)
            {
                return;
            }

            string modelPath = null;
            try
            {
                modelPath = model.GetPathName();
            }
            catch
            {
                modelPath = null;
            }

            if (!string.IsNullOrWhiteSpace(modelPath))
            {
                if (!processedModelPaths.Add(modelPath))
                {
                    return;
                }
            }

            var padFeatures = CollectPadFeatures(model).ToArray();
            if (padFeatures.Length == 0) return;
            new InsulatorDriveRecoverySession(swApp).Activate(modelPath, model.ConfigurationManager.ActiveConfiguration.Name);
            foreach (var padFeature in padFeatures)
            {
                if (!TryParsePadFeatureIndex(padFeature.Name, out var padIndex))
                {
                    continue;
                }

                SetFeatureSuppression(padFeature, padIndex > enabledPadArrayCount);
            }
        }

        /// <summary>
        /// 收集模型特征树中需按单相根数压缩的垫块特征（垫块1-1、垫块2、垫块3 等）。
        /// </summary>
        private static IEnumerable<Feature> CollectPadFeatures(IModelDoc2 model)
        {
            var features = new List<Feature>();
            if (model == null)
            {
                return features;
            }

            Feature feature = null;
            try
            {
                feature = model.FirstFeature() as Feature;
            }
            catch
            {
                feature = null;
            }

            while (feature != null)
            {
                if (IsPadFeature(feature))
                {
                    features.Add(feature);
                }

                CollectPadFeaturesFromSubFeatures(feature, features);

                try
                {
                    feature = feature.GetNextFeature() as Feature;
                }
                catch
                {
                    break;
                }
            }

            return features;
        }

        private static void CollectPadFeaturesFromSubFeatures(Feature parentFeature, ICollection<Feature> features)
        {
            if (parentFeature == null || features == null)
            {
                return;
            }

            Feature subFeature = null;
            try
            {
                subFeature = parentFeature.GetFirstSubFeature() as Feature;
            }
            catch
            {
                subFeature = null;
            }

            while (subFeature != null)
            {
                if (IsPadFeature(subFeature))
                {
                    features.Add(subFeature);
                }

                CollectPadFeaturesFromSubFeatures(subFeature, features);

                try
                {
                    subFeature = subFeature.GetNextSubFeature() as Feature;
                }
                catch
                {
                    break;
                }
            }
        }

        private static bool IsPadFeature(Feature feature)
        {
            if (feature == null)
            {
                return false;
            }

            string featureName = null;
            try
            {
                featureName = feature.Name;
            }
            catch
            {
                featureName = null;
            }

            return !string.IsNullOrWhiteSpace(featureName) &&
                   TryParsePadFeatureIndex(featureName, out _);
        }

        /// <summary>
        /// 从垫块特征名解析序号，例如「垫块1-1」→ 1、「垫块2」→ 2。
        /// </summary>
        private static bool TryParsePadFeatureIndex(string featureName, out int padIndex)
        {
            padIndex = 0;
            if (string.IsNullOrWhiteSpace(featureName) ||
                !featureName.StartsWith(PadFeatureNamePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var suffix = featureName.Substring(PadFeatureNamePrefix.Length);
            var match = Regex.Match(suffix, @"^(\d+)");
            if (!match.Success ||
                !int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out padIndex) ||
                padIndex <= 0)
            {
                padIndex = 0;
                return false;
            }

            return true;
        }

        /// <summary>
        /// 设置特征压缩状态。
        /// </summary>
        private static void SetFeatureSuppression(Feature feature, bool isSuppressed)
        {
            if (feature == null)
            {
                return;
            }

            var action = isSuppressed
                ? (int)swFeatureSuppressionAction_e.swSuppressFeature
                : (int)swFeatureSuppressionAction_e.swUnSuppressFeature;

            if (feature.IsSuppressed() == isSuppressed) return;
            if (!feature.SetSuppression2(action, (int)swInConfigurationOpts_e.swThisConfiguration, null)
                || feature.IsSuppressed() != isSuppressed)
                throw new InsulatorDriveRetryException("绝缘子特征压缩状态未生效：" + feature.Name
                    + "；实际=" + feature.IsSuppressed() + "；期望=" + isSuppressed);
        }

        /// <summary>
        /// 收集项目内需要驱动的绝缘子装配体路径：柜体基座目录（递归）+ 总装配体组件树引用。
        /// </summary>
        private static IEnumerable<string> CollectInsulatorAssemblyPathsForProject(string projectPath, IModelDoc2 rootModel)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                return paths;
            }

            var cabinetBasePath = Path.Combine(projectPath, CabinetBaseFolderName);
            foreach (var path in FindInsulatorAssemblyPaths(cabinetBasePath, SearchOption.AllDirectories))
            {
                paths.Add(path);
            }

            foreach (var path in CollectInsulatorAssemblyPathsFromAssemblyTree(rootModel))
            {
                paths.Add(path);
            }

            return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 从总装配体组件树中收集绝缘子装配体磁盘路径（含压缩件）。
        /// </summary>
        private static IEnumerable<string> CollectInsulatorAssemblyPathsFromAssemblyTree(IModelDoc2 rootModel)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (rootModel == null || rootModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                return paths;
            }

            var rootComponent = rootModel.ConfigurationManager?.ActiveConfiguration?.GetRootComponent3(true) as Component2;
            if (rootComponent == null)
            {
                return paths;
            }

            CollectInsulatorAssemblyPathsFromComponent(rootComponent, paths, skipSuppressed: false);
            return paths;
        }

        /// <summary>
        /// 从指定柜体子装配体下收集未压缩绝缘子装配体的磁盘路径。
        /// </summary>
        private static IEnumerable<string> CollectInsulatorAssemblyPathsFromCabinetSubAssemblies(
            IModelDoc2 rootModel,
            IEnumerable<IReadOnlyList<string>> cabinetSubAssemblyPaths)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (rootModel == null || cabinetSubAssemblyPaths == null)
            {
                return paths;
            }

            foreach (var subAssemblyPath in cabinetSubAssemblyPaths)
            {
                if (subAssemblyPath == null || subAssemblyPath.Count == 0)
                {
                    continue;
                }

                var subAssemblyComponent = FindComponentByPath(rootModel, subAssemblyPath);
                if (subAssemblyComponent == null)
                {
                    throw new InvalidOperationException(
                        $"未找到柜体子装配体组件路径：{string.Join(" -> ", subAssemblyPath)}");
                }

                CollectInsulatorAssemblyPathsFromComponent(subAssemblyComponent, paths, skipSuppressed: true);
            }

            return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }

        private static void CollectInsulatorAssemblyPathsFromComponent(Component2 component, ISet<string> paths)
        {
            CollectInsulatorAssemblyPathsFromComponent(component, paths, skipSuppressed: false);
        }

        private static void CollectInsulatorAssemblyPathsFromComponent(Component2 component, ISet<string> paths, bool skipSuppressed)
        {
            if (component == null || paths == null)
            {
                return;
            }

            if (skipSuppressed && IsComponentSuppressedForDrive(component))
            {
                return;
            }

            if (IsInsulatorAssemblyComponent(component))
            {
                string componentPath = null;
                try { componentPath = component.GetPathName(); } catch { componentPath = null; }

                if (!string.IsNullOrWhiteSpace(componentPath) &&
                    IsInsulatorAssemblyFilePath(componentPath) &&
                    File.Exists(componentPath))
                {
                    try
                    {
                        paths.Add(Path.GetFullPath(componentPath));
                    }
                    catch
                    {
                        paths.Add(componentPath);
                    }
                }
            }

            object[] children = null;
            try { children = component.GetChildren() as object[]; } catch { children = null; }
            if (children == null || children.Length == 0)
            {
                return;
            }

            foreach (var childObject in children)
            {
                CollectInsulatorAssemblyPathsFromComponent(childObject as Component2, paths, skipSuppressed);
            }
        }

        /// <summary>
        /// 判断组件是否处于压缩/轻化等不应参与绝缘子驱动的状态。
        /// </summary>
        private static bool IsComponentSuppressedForDrive(Component2 component)
        {
            if (component == null)
            {
                return true;
            }

            try
            {
                if (component.IsSuppressed())
                {
                    return true;
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                var suppressionState = (int)component.GetSuppression();
                if (suppressionState == (int)swComponentSuppressionState_e.swComponentSuppressed ||
                    suppressionState == (int)swComponentSuppressionState_e.swComponentLightweight ||
                    suppressionState == (int)swComponentSuppressionState_e.swComponentFullyLightweight)
                {
                    return true;
                }
            }
            catch
            {
                // ignore
            }

            return false;
        }

        private static Component2 FindComponentByPath(IModelDoc2 rootModel, IReadOnlyList<string> componentPath)
        {
            if (rootModel == null || componentPath == null || componentPath.Count == 0)
            {
                return null;
            }

            var rootComponent = rootModel.ConfigurationManager?.ActiveConfiguration?.GetRootComponent3(true) as Component2;
            if (rootComponent == null)
            {
                return null;
            }

            return FindChildComponentByPath(rootComponent, componentPath, 0);
        }

        private static Component2 FindChildComponentByPath(Component2 parentComponent, IReadOnlyList<string> componentPath, int pathIndex)
        {
            if (parentComponent == null || componentPath == null || pathIndex < 0 || pathIndex >= componentPath.Count)
            {
                return null;
            }

            object[] children = null;
            try { children = parentComponent.GetChildren() as object[]; } catch { children = null; }
            if (children == null || children.Length == 0)
            {
                return null;
            }

            foreach (var childObject in children)
            {
                var childComponent = childObject as Component2;
                if (childComponent == null || !ComponentNameMatches(childComponent, componentPath[pathIndex]))
                {
                    continue;
                }

                if (pathIndex == componentPath.Count - 1)
                {
                    return childComponent;
                }

                var matchedDescendant = FindChildComponentByPath(childComponent, componentPath, pathIndex + 1);
                if (matchedDescendant != null)
                {
                    return matchedDescendant;
                }
            }

            return null;
        }

        private static bool ComponentNameMatches(Component2 component, string expectedName)
        {
            if (component == null || string.IsNullOrWhiteSpace(expectedName))
            {
                return false;
            }

            var normalizedExpectedName = NormalizeComponentName(expectedName);
            string componentName = null;
            string componentPath = null;
            try { componentName = component.Name2; } catch { componentName = null; }
            try { componentPath = component.GetPathName(); } catch { componentPath = null; }

            var candidateNames = new[]
            {
                NormalizeComponentName(componentName),
                NormalizeComponentName(Path.GetFileNameWithoutExtension(componentPath))
            };

            return candidateNames.Any(name => string.Equals(name, normalizedExpectedName, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeComponentName(string componentName)
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

        private static bool EquationManagerContainsGlobalVariable(IEquationMgr eqMgr, string globalVariableName)
        {
            if (eqMgr == null || string.IsNullOrWhiteSpace(globalVariableName))
            {
                return false;
            }

            var equationCount = eqMgr.GetCount();
            for (var i = 0; i < equationCount; i++)
            {
                var equation = eqMgr.Equation[i] as string;
                if (string.IsNullOrWhiteSpace(equation))
                {
                    continue;
                }

                var equalsIndex = equation.IndexOf('=');
                if (equalsIndex <= 0)
                {
                    continue;
                }

                var leftSide = equation.Substring(0, equalsIndex).Trim().Trim('"');
                if (string.Equals(leftSide, globalVariableName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 解析铜排规格文本（兼容 “宽×厚” / “宽x厚” / “宽*厚”）并提取宽度与厚度。
        /// </summary>
        private static bool TryParseBusbarSpec(string specification, out double width, out double thickness)
        {
            width = 0;
            thickness = 0;

            if (string.IsNullOrWhiteSpace(specification))
            {
                return false;
            }

            var parts = specification.Trim()
                .Replace('×', 'x')
                .Replace('*', 'x')
                .Replace('X', 'x')
                .Split(new[] { 'x' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 2)
            {
                return false;
            }

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
        /// 查找柜体基座目录下所有绝缘子装配体文件路径（含 3 垫块与 4 垫块变体）。
        /// </summary>
        /// <param name="cabinetBaseDirectory">柜体基座目录完整路径。</param>
        /// <returns>按文件名排序后的绝缘子装配体完整路径序列。</returns>
        private static IEnumerable<string> FindInsulatorAssemblyPaths(string cabinetBaseDirectory, SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            if (string.IsNullOrWhiteSpace(cabinetBaseDirectory) || !Directory.Exists(cabinetBaseDirectory))
            {
                return Enumerable.Empty<string>();
            }

            return Directory.EnumerateFiles(cabinetBaseDirectory, "*.SLDASM", searchOption)
                .Where(path => IsInsulatorAssemblyFilePath(path))
                .Select(NormalizeExistingFilePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 判断磁盘路径是否为绝缘子装配体文件（含 3/4 垫块及柜体/基座变体命名）。
        /// </summary>
        private static bool IsInsulatorAssemblyFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var fileName = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(fileName) || IsSolidWorksTemporaryFileName(fileName))
            {
                return false;
            }

            if (fileName.StartsWith(InsulatorAssemblyFileNamePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return fileName.IndexOf("绝缘子", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   fileName.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 判断是否为 SolidWorks 临时锁文件（如 ~$xxx.SLDASM），此类文件不可作为 OpenDoc 目标。
        /// </summary>
        private static bool IsSolidWorksTemporaryFileName(string fileName)
        {
            return !string.IsNullOrWhiteSpace(fileName) &&
                   fileName.StartsWith("~$", StringComparison.Ordinal);
        }

        private static string NormalizeExistingFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        private void SaveAndCloseModel(IModelDoc2 model)
        {
            if (swApp == null || model == null) return;

            // 只保存当前绝缘子引用树，不能用全局 SaveAll 把尚未完成的宿主或总装提前落盘。
            var errors = 0;
            var warnings = 0;
            var saved = model.Save3(
                (int)(swSaveAsOptions_e.swSaveAsOptions_Silent
                    | swSaveAsOptions_e.swSaveAsOptions_SaveReferenced),
                ref errors,
                ref warnings);
            if (!saved || errors != 0)
            {
                throw new InvalidOperationException(
                    $"保存绝缘子模型失败：{model.GetPathName()}；errors={errors}；warnings={warnings}");
            }

            swApp.CloseDoc(model.GetTitle());
        }
    }

    /// <summary>
    /// 将通用绝缘子驱动器适配到面对面独立基座生成流程。
    /// 模板来源为共享「模板.zip」下的「绝缘子模板/绝缘子+垫片+支架（3垫块）.SLDASM」；
    /// 在基座驱动阶段把绝缘子直接装入对应普通/增高基座装配体。
    /// </summary>
    internal sealed class FaceToFaceBaseInsulatorDriver
        : IFaceToFaceBaseExistingInsulatorDriver,
          IFaceToFaceBaseInsulatorOutputResolver
    {
        private const string HostInsulatorCoordinateSystemName = "绝缘子安装坐标系";
        private const string InsulatorMountCoordinateSystemName = "安装坐标系";

        private readonly ISldWorks _swApp;
        private readonly InsulatorDriver _driver;
        private readonly IModelDoc2 _restoreContextModel;

        internal FaceToFaceBaseInsulatorDriver(
            ISldWorks swApp,
            IModelDoc2 restoreContextModel,
            InsulatorDriveRecoverySession driveRecovery = null)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
            _restoreContextModel = restoreContextModel
                ?? throw new ArgumentNullException(nameof(restoreContextModel));
            _driver = new InsulatorDriver(swApp, driveRecovery);
        }

        /// <summary>
        /// 宿主基座内绝缘子配合：父级为基座根级「绝缘子安装坐标系」，
        /// 子级为绝缘子组件上的「安装坐标系」。
        /// </summary>
        internal static FaceToFaceBaseMateRoute ResolveInsulatorMateRoute()
        {
            return new FaceToFaceBaseMateRoute(
                HostInsulatorCoordinateSystemName,
                InsulatorMountCoordinateSystemName);
        }

        /// <summary>
        /// 构建独立基座绝缘子参数隔离目录名，防止不同排厚或根数复用同一磁盘模型。
        /// </summary>
        internal static string BuildInsulatorIsolationDirectoryName(
            FaceToFaceBaseInsulatorPlan plan,
            double baseWidth)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            return InsulatorDriver.BuildThreePadInsulatorIsolationDirectoryName(
                Convert.ToDouble(plan.GlobalVariables["排厚"], CultureInfo.InvariantCulture),
                Convert.ToDouble(plan.GlobalVariables["相间距"], CultureInfo.InvariantCulture),
                Convert.ToDouble(plan.GlobalVariables["绝缘子高度"], CultureInfo.InvariantCulture),
                baseWidth,
                plan.SinglePhaseRootCount);
        }

        public string ResolveOutputDirectory(
            string projectPath,
            FaceToFaceBaseInsulatorPlan plan,
            double baseWidth)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var isolationDirectoryName = BuildInsulatorIsolationDirectoryName(plan, baseWidth);
            return InsulatorDriver.BuildThreePadInsulatorOutputDirectory(
                projectPath,
                isolationDirectoryName,
                Convert.ToDouble(plan.GlobalVariables["绝缘子高度"], CultureInfo.InvariantCulture),
                Convert.ToDouble(plan.GlobalVariables["相间距"], CultureInfo.InvariantCulture),
                Convert.ToDouble(plan.GlobalVariables["支架长度"], CultureInfo.InvariantCulture));
        }

        public string PrepareInsulator(
            string projectPath,
            FaceToFaceBaseInsulatorPlan plan,
            double baseWidth)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            // 通用驱动器统一输出到「项目目录/绝缘子」；独立基座只追加参数隔离目录，
            // 避免后续位置重新驱动同一磁盘文件而改变已装配实例。
            var isolationDirectoryName = BuildInsulatorIsolationDirectoryName(plan, baseWidth);

            // 面对面独立基座固定使用共享模板中的常规绝缘子。
            var templateAssemblyPath = InsulatorDriver.ResolveConventionalInsulatorAssemblyPath(
                TemplateArchiveCacheManager.GetTemplateRootPath());
            var templateDirectory = Path.GetDirectoryName(templateAssemblyPath);
            var preparedPath = _driver.PrepareThreePadInsulatorFromTemplateDirectory(
                projectPath,
                templateDirectory,
                plan.BusbarSpec,
                Convert.ToDouble(plan.GlobalVariables["相间距"], CultureInfo.InvariantCulture),
                Convert.ToDouble(plan.GlobalVariables["绝缘子高度"], CultureInfo.InvariantCulture),
                baseWidth,
                plan.SinglePhaseRootCount,
                _restoreContextModel,
                isolationDirectoryName,
                templateAssemblyFileName: Path.GetFileName(templateAssemblyPath));
            if (string.IsNullOrWhiteSpace(preparedPath) || !File.Exists(preparedPath))
            {
                throw new FileNotFoundException(
                    "独立基座绝缘子驱动完成后未找到可装配模型：" + preparedPath,
                    preparedPath);
            }

            return preparedPath;
        }

        /// <summary>
        /// 二次驱动：对已装入宿主的绝缘子文件写全局变量和垫块阵列。
        /// </summary>
        public void DriveExistingInsulator(
            string insulatorAssemblyPath,
            FaceToFaceBaseInsulatorPlan plan,
            double baseWidth)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            if (string.IsNullOrWhiteSpace(insulatorAssemblyPath) || !File.Exists(insulatorAssemblyPath))
            {
                throw new FileNotFoundException(
                    "待二次驱动的绝缘子装配体不存在：" + insulatorAssemblyPath,
                    insulatorAssemblyPath);
            }

            _driver.DriveExistingThreePadInsulatorAssembly(
                insulatorAssemblyPath,
                plan.BusbarSpec,
                Convert.ToDouble(plan.GlobalVariables["相间距"], CultureInfo.InvariantCulture),
                Convert.ToDouble(plan.GlobalVariables["绝缘子高度"], CultureInfo.InvariantCulture),
                baseWidth,
                plan.SinglePhaseRootCount,
                _restoreContextModel);
        }

        /// <summary>
        /// 二次驱动：只改宿主基座草图上的绝缘子定位，不改绝缘子文件。
        /// </summary>
        public void WritePlacement(
            string hostBaseAssemblyPath,
            FaceToFaceBaseInsulatorPlan plan)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            if (string.IsNullOrWhiteSpace(hostBaseAssemblyPath) || !File.Exists(hostBaseAssemblyPath))
            {
                throw new FileNotFoundException(
                    "宿主基座装配体不存在，无法写入绝缘子定位：" + hostBaseAssemblyPath,
                    hostBaseAssemblyPath);
            }

            var hostFullPath = Path.GetFullPath(hostBaseAssemblyPath);
            var swDriver = new SolidWorksDriver(_swApp);
            IModelDoc2 hostModel = null;
            try
            {
                hostModel = swDriver.OpenOrActivateDocument(hostFullPath);
                if (hostModel == null)
                {
                    throw new InvalidOperationException(
                        "无法打开宿主基座以写入绝缘子定位：" + hostFullPath);
                }

                SWHelper.EnsureAssemblyEditingContext(_swApp, hostModel);
                SetInsulatorPlacementDimension(hostModel, plan);
            }
            finally
            {
                FinalizeHostSubAssembly(swDriver, hostModel, hostFullPath);
            }
        }

        /// <summary>
        /// 读取宿主基座当前装入的绝缘子组件和物理路径；只关闭读取文档，不保存模型。
        /// </summary>
        public bool TryGetHostedInsulator(
            string hostBaseAssemblyPath,
            out string componentFullName,
            out string modelPath)
        {
            componentFullName = null;
            modelPath = null;
            if (string.IsNullOrWhiteSpace(hostBaseAssemblyPath)
                || !File.Exists(hostBaseAssemblyPath))
            {
                return false;
            }

            var hostFullPath = Path.GetFullPath(hostBaseAssemblyPath);
            var swDriver = new SolidWorksDriver(_swApp);
            IModelDoc2 hostModel = null;
            try
            {
                hostModel = swDriver.OpenOrActivateDocument(hostFullPath);
                if (hostModel == null)
                {
                    return false;
                }

                var component = CabinetAssemblyDimensionReader.FindFirstDescendantComponentContaining(
                    hostModel,
                    "绝缘子+垫片+支架",
                    (int)swDocumentTypes_e.swDocASSEMBLY) as Component2;
                if (component == null)
                {
                    return false;
                }

                componentFullName = component.Name2;
                modelPath = component.GetPathName();
                return !string.IsNullOrWhiteSpace(componentFullName)
                    && !string.IsNullOrWhiteSpace(modelPath)
                    && File.Exists(modelPath);
            }
            finally
            {
                CloseHostAfterRead(swDriver, hostModel, hostFullPath);
            }
        }

        /// <summary>
        /// 删除宿主绝缘子时必须先删配合，再删组件并确认组件已从宿主装配树消失。
        /// </summary>
        public void RemoveHostedInsulator(
            string hostBaseAssemblyPath,
            string componentFullName)
        {
            if (string.IsNullOrWhiteSpace(hostBaseAssemblyPath)
                || !File.Exists(hostBaseAssemblyPath))
            {
                throw new FileNotFoundException(
                    "宿主基座不存在，无法移除绝缘子：" + hostBaseAssemblyPath,
                    hostBaseAssemblyPath);
            }

            if (string.IsNullOrWhiteSpace(componentFullName))
            {
                throw new ArgumentException("宿主绝缘子组件全名不能为空。", nameof(componentFullName));
            }

            var hostFullPath = Path.GetFullPath(hostBaseAssemblyPath);
            var swDriver = new SolidWorksDriver(_swApp);
            IModelDoc2 hostModel = null;
            try
            {
                hostModel = swDriver.OpenOrActivateDocument(hostFullPath);
                var hostAssembly = hostModel as AssemblyDoc;
                if (hostModel == null || hostAssembly == null)
                {
                    throw new InvalidOperationException("宿主基座不是有效装配体：" + hostFullPath);
                }

                SWHelper.EnsureAssemblyEditingContext(_swApp, hostModel);
                var component = FindHostComponent(hostAssembly, componentFullName);
                if (component == null)
                {
                    throw new InvalidOperationException(
                        "宿主基座内未找到待移除绝缘子：" + componentFullName);
                }

                DeleteComponentMates(hostModel, component);
                hostModel.ClearSelection2(true);
                try
                {
                    if (!component.Select(false))
                    {
                        throw new InvalidOperationException(
                            "无法选中待移除宿主绝缘子：" + componentFullName);
                    }

                    var deleted = hostAssembly.DeleteSelections(
                        (int)swDeleteSelectionOptions_e.swDelete_Children);
                    if (!deleted)
                    {
                        throw new InvalidOperationException(
                            "SolidWorks 未确认宿主绝缘子组件删除：" + componentFullName);
                    }
                }
                finally
                {
                    hostModel.ClearSelection2(true);
                }

                FaceToFaceBaseOperationGuard.EnsureRebuildSucceeded(
                    hostModel.EditRebuild3(),
                    "移除宿主绝缘子后重建",
                    hostFullPath);
                if (FindHostComponent(hostAssembly, componentFullName) != null)
                {
                    throw new InvalidOperationException(
                        "宿主绝缘子删除后仍存在于装配树：" + componentFullName);
                }
            }
            finally
            {
                FinalizeHostSubAssembly(swDriver, hostModel, hostFullPath);
            }
        }

        /// 随后基座再插入总装时，绝缘子作为基座子件一起进入总装。
        /// </summary>
        public void AssembleIntoHostBase(
            string hostBaseAssemblyPath,
            string insulatorAssemblyPath,
            FaceToFaceBaseInsulatorPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (string.IsNullOrWhiteSpace(hostBaseAssemblyPath) || !File.Exists(hostBaseAssemblyPath))
            {
                throw new FileNotFoundException(
                    "宿主基座装配体不存在，无法装配绝缘子：" + hostBaseAssemblyPath,
                    hostBaseAssemblyPath);
            }

            if (string.IsNullOrWhiteSpace(insulatorAssemblyPath) || !File.Exists(insulatorAssemblyPath))
            {
                throw new FileNotFoundException(
                    "绝缘子装配体不存在，无法装入基座：" + insulatorAssemblyPath,
                    insulatorAssemblyPath);
            }

            var hostFullPath = Path.GetFullPath(hostBaseAssemblyPath);
            var insulatorFullPath = Path.GetFullPath(insulatorAssemblyPath);
            var swDriver = new SolidWorksDriver(_swApp);
            IModelDoc2 hostModel = null;
            Component2 insertedInsulator = null;
            try
            {
                hostModel = swDriver.OpenOrActivateDocument(hostFullPath);
                if (hostModel == null
                    || hostModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY
                    || !(hostModel is AssemblyDoc hostAssembly))
                {
                    throw new InvalidOperationException(
                        "宿主基座不是有效的装配体文档：" + hostFullPath);
                }

                SWHelper.EnsureAssemblyEditingContext(_swApp, hostModel);
                SetInsulatorPlacementDimension(hostModel, plan);

                // 公共驱动正常会保持绝缘子打开；若文档被用户关闭，则按准确路径重新打开。
                var insulatorModel = _swApp.GetOpenDocumentByName(insulatorFullPath) as IModelDoc2;
                if (insulatorModel == null)
                {
                    insulatorModel = swDriver.OpenOrActivateDocument(insulatorFullPath);
                }

                if (insulatorModel == null)
                {
                    throw new InvalidOperationException(
                        "无法打开已驱动的独立基座绝缘子模型：" + insulatorFullPath);
                }

                FaceToFaceInsulatorAssemblyLifecycle.Run(
                    () =>
                    {
                    SWHelper.EnsureAssemblyEditingContext(_swApp, hostModel);
                    insertedInsulator = hostAssembly.AddComponent5(
                        insulatorFullPath,
                        (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                        string.Empty,
                        false,
                        string.Empty,
                        0,
                        0,
                        0);
                    if (insertedInsulator == null)
                    {
                        throw new InvalidOperationException(
                            "向宿主基座插入绝缘子失败：" + insulatorFullPath);
                    }

                    try
                    {
                        hostAssembly.ResolveAllLightWeightComponents(false);
                    }
                    catch
                    {
                        // 后续配合失败会给出完整诊断信息。
                    }

                    var mateRoute = ResolveInsulatorMateRoute();
                    // 父坐标系在基座装配体根级，因此 parent 传 null。
                    if (!PublicMethod.AddMateUseCoord(
                        _swApp,
                        hostAssembly,
                        null,
                        insertedInsulator,
                        mateRoute.ParentCoordinateName,
                        mateRoute.ChildCoordinateName))
                    {
                        throw new InvalidOperationException(
                            "宿主基座内绝缘子坐标系配合失败："
                            + "模型=" + insulatorFullPath
                            + "，父级坐标系=" + mateRoute.ParentCoordinateName
                            + "，子模型坐标系=" + mateRoute.ChildCoordinateName
                            + "。");
                    }
                    },
                    new SolidWorksBaseInsulatorPostAssemblyOperations(
                        _swApp,
                        hostModel,
                        insulatorModel,
                        hostFullPath,
                        insulatorFullPath));
            }
            catch (Exception originalException)
            {
                if (insertedInsulator != null && hostModel is AssemblyDoc hostAssemblyForCleanup)
                {
                    try
                    {
                        TryDeleteComponent(hostModel, hostAssemblyForCleanup, insertedInsulator);
                    }
                    catch (Exception cleanupException)
                    {
                        FaceToFaceFrameCleanupDiagnostics.AppendData(
                            originalException,
                            FaceToFaceFrameCleanupDiagnostics.RollbackWarningDataKey,
                            "独立基座内绝缘子组件清理失败：" + cleanupException.Message);
                    }
                }

                var closeConfirmation = TryRequestCloseInsulator(insulatorFullPath);
                FaceToFaceFrameCleanupDiagnostics.AttachCleanupConfirmation(
                    originalException,
                    closeConfirmation,
                    Path.GetDirectoryName(insulatorFullPath),
                    "独立基座绝缘子失败清理");
                throw;
            }
            finally
            {
                FinalizeHostSubAssembly(swDriver, hostModel, hostFullPath);
            }
        }

        private FaceToFaceFrameCleanupConfirmation TryRequestCloseInsulator(
            string insulatorFullPath)
        {
            try
            {
                return new FaceToFaceFrameScopedDocumentCleaner(
                    new SolidWorksFaceToFaceFrameDocumentSession(_swApp))
                    .RequestCloseDocumentsUnderDirectory(
                        Path.GetDirectoryName(insulatorFullPath));
            }
            catch (Exception exception)
            {
                return FaceToFaceFrameCleanupConfirmation.Unconfirmed(
                    "请求关闭绝缘子文档异常：" + exception.Message);
            }
        }

        private void CloseHostAfterRead(
            SolidWorksDriver driver,
            IModelDoc2 hostModel,
            string hostFullPath)
        {
            try
            {
                if (driver != null && !string.IsNullOrWhiteSpace(hostFullPath))
                {
                    driver.TryCloseDocumentByPath(hostFullPath);
                }
            }
            finally
            {
                SWHelper.EnsureAssemblyEditingContext(_swApp, _restoreContextModel);
                SolidWorksFileHelper.ReleaseComReference(hostModel);
            }
        }

        private static Component2 FindHostComponent(
            AssemblyDoc hostAssembly,
            string componentFullName)
        {
            var components = hostAssembly?.GetComponents(true) as object[];
            if (components == null)
            {
                return null;
            }

            return components
                .OfType<Component2>()
                .FirstOrDefault(component => string.Equals(
                    component.Name2,
                    componentFullName,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static void DeleteComponentMates(
            IModelDoc2 hostModel,
            Component2 component)
        {
            var mates = component?.GetMates() as object[];
            if (mates == null || mates.Length == 0)
            {
                return;
            }

            var mateNames = new List<string>();
            foreach (var mateObject in mates)
            {
                var feature = mateObject as Feature;
                if (feature != null && !string.IsNullOrWhiteSpace(feature.Name))
                {
                    mateNames.Add(feature.Name);
                }
            }

            SWHelper.TryDeleteAssemblyMatesByName(hostModel, mateNames);
        }

        private static void SaveHostBeforeClose(IModelDoc2 hostModel, string hostFullPath)
        {
            var errors = 0;
            var warnings = 0;
            var saved = hostModel.Save3(
                (int)(swSaveAsOptions_e.swSaveAsOptions_Silent
                    | swSaveAsOptions_e.swSaveAsOptions_SaveReferenced),
                ref errors,
                ref warnings);
            if (!saved || errors != 0)
            {
                throw new InvalidOperationException(
                    $"关闭前保存已装配绝缘子的宿主基座失败：{hostFullPath}；"
                    + $"errors={errors}；warnings={warnings}");
            }
        }

        /// <summary>
        /// 二次驱动在宿主基座内操作完成后保存并关闭子件，总装配体保持打开。
        /// </summary>
        private void FinalizeHostSubAssembly(
            SolidWorksDriver driver,
            IModelDoc2 hostModel,
            string hostFullPath)
        {
            if (driver == null)
            {
                SWHelper.EnsureAssemblyEditingContext(_swApp, _restoreContextModel);
                return;
            }

            if (hostModel == null || string.IsNullOrWhiteSpace(hostFullPath))
            {
                SWHelper.EnsureAssemblyEditingContext(_swApp, _restoreContextModel);
                return;
            }

            var normalizedHostPath = Path.GetFullPath(hostFullPath);
            try
            {
                SaveHostBeforeClose(hostModel, normalizedHostPath);
            }
            finally
            {
                try
                {
                    driver.TryCloseDocumentByPath(normalizedHostPath);
                }
                catch
                {
                    // 宿主可能已被 SolidWorks 自动卸载。
                }

                var hostDirectory = Path.GetDirectoryName(normalizedHostPath);
                if (!string.IsNullOrWhiteSpace(hostDirectory))
                {
                    driver.CloseOpenDocumentsUnderDirectory(
                        hostDirectory,
                        GetRestoreContextExcludedPaths());
                }

                SWHelper.EnsureAssemblyEditingContext(_swApp, _restoreContextModel);
                SolidWorksFileHelper.ReleaseComReference(hostModel);
            }
        }

        private string[] GetRestoreContextExcludedPaths()
        {
            if (_restoreContextModel == null)
            {
                return Array.Empty<string>();
            }

            try
            {
                var restorePath = _restoreContextModel.GetPathName();
                if (string.IsNullOrWhiteSpace(restorePath))
                {
                    return Array.Empty<string>();
                }

                return new[] { Path.GetFullPath(restorePath) };
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private sealed class SolidWorksBaseInsulatorPostAssemblyOperations
            : IFaceToFaceInsulatorPostAssemblyOperations
        {
            private readonly ISldWorks _swApp;
            private readonly IModelDoc2 _hostModel;
            private readonly IModelDoc2 _insulatorModel;
            private readonly string _hostFullPath;
            private readonly string _insulatorFullPath;

            internal SolidWorksBaseInsulatorPostAssemblyOperations(
                ISldWorks swApp,
                IModelDoc2 hostModel,
                IModelDoc2 insulatorModel,
                string hostFullPath,
                string insulatorFullPath)
            {
                _swApp = swApp;
                _hostModel = hostModel;
                _insulatorModel = insulatorModel;
                _hostFullPath = hostFullPath;
                _insulatorFullPath = insulatorFullPath;
            }

            public void RebuildHost()
            {
                FaceToFaceBaseOperationGuard.EnsureRebuildSucceeded(
                    _hostModel.EditRebuild3(),
                    "宿主基座绝缘子装配",
                    _hostFullPath);
            }

            public bool SaveInsulator()
            {
                var errors = 0;
                var warnings = 0;
                var saved = _insulatorModel.Save3(
                    (int)(swSaveAsOptions_e.swSaveAsOptions_Silent
                        | swSaveAsOptions_e.swSaveAsOptions_SaveReferenced),
                    ref errors,
                    ref warnings);
                return saved && errors == 0;
            }

            public FaceToFaceFrameCleanupConfirmation CloseInsulator()
            {
                return new FaceToFaceFrameScopedDocumentCleaner(
                    new SolidWorksFaceToFaceFrameDocumentSession(_swApp))
                    .RequestCloseDocumentsUnderDirectory(
                        Path.GetDirectoryName(_insulatorFullPath));
            }
        }

        private static void SetInsulatorPlacementDimension(
            IModelDoc2 hostModel,
            FaceToFaceBaseInsulatorPlan plan)
        {
            if (plan.PlacementHeight < 0
                || double.IsNaN(plan.PlacementHeight)
                || double.IsInfinity(plan.PlacementHeight))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(plan.PlacementHeight),
                    plan.PlacementHeight,
                    "绝缘子定位尺寸必须为有限非负数。");
            }

            var dimension = hostModel.Parameter(plan.PlacementDimensionName) as Dimension;
            if (dimension == null)
            {
                throw new InvalidOperationException(
                    $"宿主基座缺少绝缘子定位尺寸“{plan.PlacementDimensionName}”：{hostModel.GetPathName()}");
            }

            // SolidWorks SystemValue 的长度单位为米，业务计划统一使用毫米。
            dimension.SystemValue = plan.PlacementHeight / 1000d;
            FaceToFaceBaseOperationGuard.EnsureRebuildSucceeded(
                hostModel.EditRebuild3(),
                "绝缘子定位尺寸驱动",
                hostModel.GetPathName());
        }

        private static void TryDeleteComponent(
            IModelDoc2 hostModel,
            AssemblyDoc hostAssembly,
            Component2 component)
        {
            hostModel.ClearSelection2(true);
            try
            {
                var selectName = component.GetSelectByIDString();
                var selected = !string.IsNullOrWhiteSpace(selectName)
                    && hostModel.Extension.SelectByID2(
                        selectName,
                        "COMPONENT",
                        0,
                        0,
                        0,
                        false,
                        0,
                        null,
                        0);
                if (!selected)
                {
                    selected = component.Select(false);
                }

                if (selected)
                {
                    hostAssembly.DeleteSelections((int)swDeleteSelectionOptions_e.swDelete_Children);
                }
            }
            finally
            {
                hostModel.ClearSelection2(true);
            }
        }
    }
}
