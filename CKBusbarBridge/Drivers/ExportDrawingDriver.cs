using CKBusbarBridge.Models;
using CKBusbarBridgePreview;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// SolidWorks 出工程图驱动：打开与柜体总装配体同名的工程图（.SLDDRW）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 入口工程图优先为与当前激活装配体同名的 <c>.SLDDRW</c>（总装配体重命名后工程图会同步改名）；
    /// 未找到时回退为 <c>&lt;项目路径&gt;/柜体基座/柜体装配.SLDDRW</c>。
    /// 项目路径解析优先使用调用方传入值；为空时从当前激活装配体所在目录沿父目录向上
    /// 回溯，找到首个包含 <see cref="ProjectRootMarkerFolders"/> 子目录的目录作为项目根。
    /// 工程图已打开则直接激活，未打开则静默打开。
    /// </para>
    /// <para>
    /// 打开工程图后在视图3标注基架；面对面、侧对侧的大偏移组改在视图4标注：
    /// 水平驱动长度尺寸（草图 <see cref="FrameLengthReferenceSketchName"/> 内按名称匹配的参考直线）、名称注释（默认取装配体文件所在文件夹名），
    /// 以及各基架/基座自身子树内绝缘子相对对应「边线参考草图」的定位尺寸。
    /// </para>
    /// <para>
    /// 绝缘子侧标注点：工程图常无法选中子装配<strong>原点</strong>，若遇此情况请在绝缘子模型中增加草图
    /// <c>绝缘子工程图标注点</c>，在原点处放一个构造点并命名为 <c>点1</c>（或 <c>Point1</c>），程序将自动选该点参与标注。
    /// 若草图内<strong>仅有一个点</strong>，也可不依赖名称；多点时优先匹配名称，否则选用距草图原点最近的点（约 0.1mm 内视为重合）。
    /// </para>
    /// <para>详见 <see cref="AnnotateFrameComponentsInCabinetDrawing"/>；该步骤失败不影响工程图打开。</para>
    /// </remarks>
    public class ExportDrawingDriver
    {
        /// <summary>
        /// 柜体装配工程图默认文件名（模板未改名时的回退名）。
        /// </summary>
        private const string CabinetAssemblyDrawingFileName = "柜体装配.SLDDRW";

        /// <summary>
        /// 模板解压后的默认总装配体基名（面对面 / 面对墙）。
        /// </summary>
        private static readonly string[] DefaultCabinetAssemblyBaseNames =
        {
            "柜体装配",
            "面对墙柜体装配",
        };

        /// <summary>
        /// 项目根目录下存放柜体装配相关模型的子目录名。
        /// </summary>
        private const string CabinetBaseFolderName = "柜体基座";

        /// <summary>
        /// 项目根目录的“结构标志”子目录名集合：当某一级目录中存在以下任意子目录，
        /// 则认为该目录是项目根目录。用于从激活装配体所在目录向上回溯定位项目根。
        /// </summary>
        private static readonly string[] ProjectRootMarkerFolders =
        {
            "柜体",
            "桥身",
            "桥座",
            "桥座 - 右",
            "柜体基座",
        };

        /// <summary>
        /// 自动向上回溯查找项目根目录的最大层数（含起始目录本身），避免极端情况下沿盘符无限上溯。
        /// </summary>
        private const int ProjectRootSearchMaxDepth = 6;

        /// <summary>
        /// 柜体装配工程图中用于标注基架的桥身视图名称（固定）。
        /// </summary>
        private const string TargetDrawingViewName = "工程图视图3";

        private const string LargeOffsetDrawingViewName = "工程图视图4";

        /// <summary>
        /// 面对面场景桥身视图 <see cref="TargetDrawingViewName"/> 对应的总装配体工程图实例后缀。
        /// </summary>
        private const int FaceToFaceTargetDrawingViewRootInstanceSuffix = 3;

        /// <summary>
        /// 面对墙场景桥身视图 <see cref="TargetDrawingViewName"/> 对应的总装配体工程图实例后缀。
        /// </summary>
        private const int FaceToWallTargetDrawingViewRootInstanceSuffix = 4;

        /// <summary>
        /// 总装配体自定义属性「设计场景」名称。
        /// </summary>
        private const string DesignScenarioCustomPropertyName = DesignScenarioDefaults.ScenarioCustomPropertyKey;

        /// <summary>
        /// 基架装配体内用于标注长度的参考草图名称。
        /// </summary>
        private const string FrameLengthReferenceSketchName = "基架长度参考草图";

        /// <summary>
        /// 基架装配体内用于绝缘子相对基架边线标注的参考草图名称。
        /// </summary>
        private const string FrameEdgeReferenceSketchName = "基架边线参考草图";

        /// <summary>
        /// 基座装配体内用于绝缘子相对基座边线标注的参考草图名称。
        /// </summary>
        private const string BaseEdgeReferenceSketchName = "基座边线参考草图";

        /// <summary>
        /// 基座装配体内用于标注基座长度的参考草图名称。
        /// </summary>
        private const string BaseLengthReferenceSketchName = "基座长度参考草图";

        /// <summary>
        /// 基座装配体内用于标注基座开孔高度的参考草图名称。
        /// </summary>
        private const string BaseOpeningReferenceSketchName = "基座开孔参考草图";

        /// <summary>
        /// 上述草图内用作长度标注的直线实体名（与特征树显示一致）；英文模板通常为 Line1。
        /// </summary>
        /// <remarks>边线参考草图内同名直线与长度参考草图共用此列表。</remarks>
        private static readonly string[] FrameLengthReferenceSketchLineNames =
        {
            "直线1",
            "Line1",
            "直线2",
            "Line2"
        };

        /// <summary>
        /// 工程图中选中组件<strong>原点</strong>时 <c>SelectByID2</c> 的类型字符串（依版本/语言环境依次尝试）。
        /// </summary>
        private static readonly string[] ComponentOriginSelectTypes =
        {
            "EXTREFERENCE",
            "REFPRTDRFITEM",
            "COORDSYS",
            "DATUMPOINT",
            "SKETCHPOINT",
            "ORIGIN",
            "FEATURE",
        };

        /// <summary>
        /// 与特征树/英文化环境对应的零件坐标系名称，用于 <c>SelectByID2</c> 首段标识。
        /// </summary>
        private static readonly string[] ComponentOriginEntityNames =
        {
            "原点",
            "Origin",
        };

        /// <summary>
        /// 绝缘子模型内用于工程图标注的参考草图名：在装配原点上放置草图点「点1」/「Point1」（可为构造几何），
        /// 以便在工程图中选中（工程图往往无法直接选中零部件原点）。
        /// </summary>
        private const string InsulatorDrawingAnchorSketchName = "绝缘子工程图标注点";

        /// <summary>
        /// <see cref="InsulatorDrawingAnchorSketchName"/> 内需选中的草图点名称（与特征树一致）。
        /// </summary>
        private static readonly string[] InsulatorDrawingAnchorPointNames =
        {
            "点1",
            "Point1",
        };

        /// <summary>
        /// 工程图中选中投影<strong>草图点</strong>时 <c>SelectByID2</c> 的类型字符串。
        /// </summary>
        private static readonly string[] SketchPointSelectTypes =
        {
            "EXTSKETCHPOINT",
            "EXTSKETCHPOINTS",
            "SKETCHPOINT",
        };
        private static readonly string[] FrameLengthSketchSegmentSelectTypes =
        {
            "EXTSKETCHSEGMENT",
            "EXTSKETCHSEGS",
            "SKETCHSEGMENT",
        };

        /// <summary>
        /// 项目根下的基架输出目录名（与基架生成逻辑一致）。
        /// </summary>
        private const string FrameOutputFolderName = "基架";

        /// <summary>
        /// 项目根下的基座输出目录名。
        /// </summary>
        private const string BaseOutputFolderName = "基座";

        /// <summary>
        /// 基架装配体写入的“长度偏移”自定义属性名（与 FrameDriverWindowVM 一致）。
        /// </summary>
        private const string FrameLengthOffsetPropertyName = "c";

        /// <summary>
        /// 标准基架默认长度（mm），用于由属性 c 推算总长。
        /// </summary>
        private const double DefaultStandardFrameLengthMm = 1000d;

        /// <summary>
        /// 自动生成的水平尺寸命名前缀（幂等清理用）。
        /// </summary>
        private const string AnnotationPrefixLen = "FRAME_LEN_";

        /// <summary>
        /// 自动生成的名称注释命名前缀（幂等清理用）。
        /// </summary>
        private const string AnnotationPrefixName = "FRAME_NAME_";

        /// <summary>
        /// 基架—绝缘子边线自动化尺寸命名前缀（幂等清理用）。
        /// </summary>
        private const string AnnotationPrefixInsulatorEdge = "FRAME_INS_EDGE_";

        /// <summary>
        /// 基座—绝缘子边线自动化尺寸命名前缀（幂等清理用）。
        /// </summary>
        private const string AnnotationPrefixBaseInsulatorEdge = "BASE_INS_EDGE_";

        /// <summary>
        /// 基座长度自动化尺寸命名前缀（幂等清理用）。
        /// </summary>
        private const string AnnotationPrefixBaseLength = "BASE_LEN_";

        /// <summary>
        /// 基座开孔高度自动化尺寸命名前缀（幂等清理用）。
        /// </summary>
        private const string AnnotationPrefixBaseOpeningHeight = "BASE_OPENING_HEIGHT_";

        /// <summary>
        /// 绝缘子装配文件名：奇数排段模板（与 <see cref="InsulatorDriver"/> 一致）。
        /// </summary>
        private const string InsulatorOddSegmentAsmFileName = "绝缘子+垫片+支架（3垫块）.SLDASM";

        /// <summary>
        /// 绝缘子装配文件名：偶数排段模板（与 <see cref="InsulatorDriver"/> 一致）。
        /// </summary>
        private const string InsulatorEvenSegmentAsmFileName = "绝缘子+垫片+支架（4垫块）.SLDASM";

        /// <summary>
        /// 文件名中识别基架装配体的片段（图号系列），路径过滤失败时的兜底。
        /// </summary>
        private const string FrameAssemblyFileNameToken = "355.002";

        /// <summary>
        /// 长度驱动尺寸文字锚点：置于名称注释下方、组件投影顶边上方（图纸坐标）。
        /// </summary>
        private const double FrameLengthDimensionBelowNameOffsetM = 0.010;

        /// <summary>
        /// 长度尺寸锚点距投影顶边的最小间距（米），避免与轮廓线重合。
        /// </summary>
        private const double FrameLengthDimensionMinAboveTopEdgeM = 0.004;

        /// <summary>
        /// 基架名称注释放在投影包围盒顶边之上的间距（米）。
        /// </summary>
        private const double FrameNameNoteAboveBBoxClearanceM = 0.018;

        /// <summary>
        /// 无法取得投影包围盒时，名称注释相对组件原点投影位置的上方偏移（米），避免全部落到视图中心回退点。
        /// </summary>
        private const double FrameNameNoteOriginFallbackAboveOffsetM = 0.028;

        /// <summary>
        /// 长度尺寸文字若落在基架投影区域内，则移到包围盒底边下方的间距（米）。
        /// </summary>
        private const double FrameDimensionOutsideBBoxMarginM = 0.012;

        /// <summary>
        /// 同一基座多条参考尺寸线之间的图纸间距（米）。
        /// </summary>
        private const double BaseReferenceDimensionLaneSpacingM = 0.010;

        private readonly ISldWorks _swApp;

        /// <summary>
        /// 当前标注流程中桥身视图总装配体实例后缀（面对面 3 / 面对墙 4）。
        /// </summary>
        [ThreadStatic]
        private static int? _drawingViewRootInstanceSuffixOverride;

        /// <summary>
        /// 初始化出工程图驱动。
        /// </summary>
        /// <param name="swApp">SolidWorks 应用对象。</param>
        /// <exception cref="ArgumentNullException">swApp 为空。</exception>
        public ExportDrawingDriver(ISldWorks swApp)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
        }

        /// <summary>
        /// 打开柜体装配工程图（柜体装配.SLDDRW）。已打开则直接激活，未打开则静默打开。
        /// </summary>
        /// <remarks>
        /// 成功打开后会调用 <see cref="HideDanglingDimensions"/> 隐藏悬空尺寸，并尝试 <see cref="AnnotateFrameComponentsInCabinetDrawing"/>；
        /// 后两步异常均不向调用方抛出，以免阻断出图。
        /// </remarks>
        /// <param name="projectPath">项目根目录；为空时从当前激活装配体目录回溯定位。</param>
        /// <returns>实际打开的工程图完整路径。</returns>
        /// <exception cref="InvalidOperationException">无法定位项目目录或工程图打开失败。</exception>
        /// <exception cref="FileNotFoundException">目标工程图文件不存在。</exception>
        public string OpenCabinetAssemblyDrawing(string projectPath = null)
        {
            var drawingPath = ResolveDrawingPath(projectPath);
            if (string.IsNullOrWhiteSpace(drawingPath))
            {
                throw new InvalidOperationException(
                    "无法定位柜体装配工程图，请先打开柜体装配体或在“" + AboutInformation.ProductName + "”窗口中设置项目路径。");
            }

            if (!File.Exists(drawingPath))
            {
                throw new FileNotFoundException(
                    $"未找到柜体装配工程图：{drawingPath}", drawingPath);
            }

            // 工程图常仍引用模板目录中的旧装配体路径；打开前先重定向到当前总装配体或柜体基座下实际文件。
            try
            {
                RepairDrawingReferencesBeforeOpen(drawingPath, projectPath);
            }
            catch
            {
                // 引用修复失败不阻断出图，后续打开时 SolidWorks 仍可能提示用户手动查找。
            }

            ModelDoc2 drawingModel;

            // 已打开：直接激活。
            var existing = FindOpenDocumentByPath(drawingPath);
            if (existing != null)
            {
                var activateErrors = 0;
                _swApp.ActivateDoc3(
                    existing.GetTitle(),
                    true,
                    (int)swRebuildOnActivation_e.swRebuildActiveDoc,
                    ref activateErrors);
                drawingModel = existing;
            }
            else
            {
                // 未打开：静默打开。
                var errors = 0;
                var warnings = 0;
                drawingModel = _swApp.OpenDoc6(
                    drawingPath,
                    (int)swDocumentTypes_e.swDocDRAWING,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    string.Empty,
                    ref errors,
                    ref warnings) as ModelDoc2;

                if (drawingModel == null)
                {
                    throw new InvalidOperationException(
                        $"打开柜体装配工程图失败：{drawingPath}（errors={errors}, warnings={warnings}）");
                }
            }

            // 模型组件被压缩后，工程图中引用这些组件的尺寸会变为悬空（报错）尺寸。
            // 此处统一隐藏，避免出图时显示红色报错。失败不影响主流程。
            try
            {
                HideDanglingDimensions(drawingModel);
            }
            catch
            {
                // 隐藏过程中的任何异常都不应阻断"出工程图"流程。
            }

            // 在目标视图上为基架自动标注长度（驱动尺寸）与名称注释；失败不影响主流程。
            try
            {
                AnnotateFrameComponentsInCabinetDrawing(drawingModel, drawingPath);
            }
            catch (Exception ex)
            {
                FaceToWallPreviewTrace.Error("总装图基架标注失败", ex, drawingPath);
            }

            return drawingPath;
        }

        /// <summary>
        /// 隐藏当前工程图中所有"悬空"（IsDangling=true）的显示尺寸。
        /// 这些尺寸通常由模型组件被压缩后失去引用所致，会在工程图中以红色"报错"形式显示。
        /// </summary>
        /// <param name="drawingModel">工程图模型文档。</param>
        /// <returns>实际被隐藏的尺寸数量。</returns>
        private static int HideDanglingDimensions(ModelDoc2 drawingModel)
        {
            if (drawingModel == null)
            {
                return 0;
            }

            if (drawingModel.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
            {
                return 0;
            }

            var drawingDoc = drawingModel as DrawingDoc;
            if (drawingDoc == null)
            {
                return 0;
            }

            // 强制重建：确保悬空状态依据当前模型压缩状态实时评估。
            try { drawingModel.ForceRebuild3(false); } catch { /* 容错 */ }

            var hiddenState = (int)swAnnotationVisibilityState_e.swAnnotationHidden;
            var hiddenCount = 0;

            // GetFirstView 返回的是首张图纸（Sheet 视图），后续 GetNextView 会依次返回
            // 该图纸上的各个视图，再切换到下一张图纸的 Sheet 视图，覆盖整本工程图。
            var view = drawingDoc.GetFirstView() as View;
            while (view != null)
            {
                // 先取下一个视图，避免后续操作影响遍历指针。
                var nextView = view.GetNextView() as View;

                try
                {
                    // 注：IsDangling 在 IDisplayDimension 上不存在，需通过其 IAnnotation 对象访问。
                    var dispDim = view.GetFirstDisplayDimension5() as DisplayDimension;
                    while (dispDim != null)
                    {
                        var nextDim = dispDim.GetNext5() as DisplayDimension;

                        try
                        {
                            var annotation = dispDim.GetAnnotation() as Annotation;
                            if (annotation != null
                                && annotation.IsDangling()
                                && annotation.Visible != hiddenState)
                            {
                                annotation.Visible = hiddenState;
                                hiddenCount++;
                            }
                        }
                        catch
                        {
                            // 单个尺寸处理失败时跳过，继续处理其它尺寸。
                        }

                        dispDim = nextDim;
                    }
                }
                catch
                {
                    // 单个视图处理失败时跳过，继续处理其它视图。
                }

                view = nextView;
            }

            if (hiddenCount > 0)
            {
                try { drawingModel.GraphicsRedraw2(); } catch { /* 容错 */ }
            }

            return hiddenCount;
        }

        /// <summary>
        /// 按基架所属组分配视图：普通基架在视图3，大偏移组在视图4。
        /// </summary>
        /// <param name="drawingModel">当前已打开的柜体装配工程图文档（必须为工程图类型）。</param>
        /// <param name="drawingPath">
        /// 该工程图磁盘路径，用于反推项目根目录（见 <see cref="TryGetProjectRootFromDrawingPath"/>），进而拼接 <see cref="FrameOutputFolderName"/> 以筛选组件。
        /// </param>
        /// <remarks>
        /// <para><b>执行顺序概要</b></para>
        /// <list type="number">
        /// <item>遍历图纸，定位名为 <see cref="TargetDrawingViewName"/> 且引用装配体的视图。</item>
        /// <item>将引用装配体重解析为非轻量（<c>ResolveAllLightWeightComponents(false)</c>），降低几何/API 取不到数据的风险。</item>
        /// <item>调用 <see cref="RemoveExistingFrameAnnotations"/> 删除此前自动生成的 FRAME_LEN_* / FRAME_NAME_* / FRAME_INS_EDGE_*（幂等重跑）。</item>
        /// <item><see cref="CollectFrameComponents"/> 收集基架实例，按 <see cref="GetComponentSortKey"/>（装配变换平移 X）排序，决定退回名称时的序号 <c>n</c>。</item>
        /// <item>对每个实例调用 <see cref="AnnotateSingleFrameComponent"/>：长度尺寸 + 名称注释。</item>
        /// <item>调用 <see cref="AnnotateInsulatorsToOwnerEdgeInDrawing"/>：按组件父子树分别处理基架和基座内绝缘子，并标注至各自边线参考草图。</item>
        /// </list>
        /// <para>
        /// <b>长度尺寸</b>：优先用 <see cref="BuildFrameSketchSegmentSelectById"/> 在视图中选中「基架长度参考草图」下的「直线1」投影线段，失败时再选中草图并按名称匹配直线；
        /// 再在图纸坐标下对 <see cref="ModelDoc2"/> 调用 <c>AddDimension2(x, y, 0)</c>（见 SolidWorks <c>IModelDoc2::AddDimension2</c>）。
        /// <b>名称注释</b>：显示文本见 <see cref="GetFrameDisplayNameFromFolder"/>；注释对象命名仍为 <c>FRAME_NAME_</c> + 实例键，保证多个副本可区分删除。
        /// </para>
        /// </remarks>
        private void AnnotateFrameComponentsInCabinetDrawing(ModelDoc2 drawingModel, string drawingPath)
        {
            if (drawingModel == null || string.IsNullOrWhiteSpace(drawingPath))
            {
                return;
            }

            if (drawingModel.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
            {
                return;
            }

            var drawingDoc = drawingModel as DrawingDoc;
            if (drawingDoc == null)
            {
                return;
            }

            // 项目根 + 「基架」目录：用于路径前缀识别（文件名命中 355.002 时仍可收录，但路径匹配优先）。
            var projectRoot = TryGetProjectRootFromDrawingPath(drawingPath);
            var frameRootPath = !string.IsNullOrWhiteSpace(projectRoot)
                ? Path.Combine(projectRoot, FrameOutputFolderName)
                : null;
            var baseRootPath = !string.IsNullOrWhiteSpace(projectRoot)
                ? Path.Combine(projectRoot, BaseOutputFolderName)
                : null;

            var sheetNames = EnumerateSheetNames(drawingDoc);
            if (sheetNames.Count == 0)
            {
                return;
            }

            View targetView = null;
            ModelDoc2 referencedAsm = null;
            var targetSheetName = string.Empty;

            foreach (var sheetName in sheetNames)
            {
                try
                {
                    drawingDoc.ActivateSheet(sheetName);
                }
                catch
                {
                    continue;
                }

                var v = drawingDoc.GetFirstView() as View;
                while (v != null)
                {
                    try
                    {
                        if (string.Equals(v.Name, TargetDrawingViewName, StringComparison.Ordinal))
                        {
                            var refDoc = TryGetViewReferencedDocument(v);
                            if (refDoc != null
                                && refDoc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                            {
                                targetView = v;
                                referencedAsm = refDoc;
                                targetSheetName = sheetName;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // ignore
                    }

                    v = v.GetNextView() as View;
                }

                if (targetView != null)
                {
                    break;
                }
            }

            if (targetView == null || referencedAsm == null)
            {
                return;
            }

            var asmDoc = referencedAsm as AssemblyDoc;
            if (asmDoc == null)
            {
                return;
            }

            try
            {
                // false：解析为完全还原的零部件；轻量模式下 GetBox/几何选取可能不可靠。
                asmDoc.ResolveAllLightWeightComponents(false);
            }
            catch
            {
                // ignore
            }

            var cabinetTitleWithoutExt = Path.GetFileNameWithoutExtension(
                TryGetPathName(referencedAsm) ?? referencedAsm.GetTitle() ?? string.Empty);
            if (string.IsNullOrWhiteSpace(cabinetTitleWithoutExt))
            {
                cabinetTitleWithoutExt = Path.GetFileNameWithoutExtension(CabinetAssemblyDrawingFileName);
            }

            var previousRootInstanceSuffixOverride = _drawingViewRootInstanceSuffixOverride;
            _drawingViewRootInstanceSuffixOverride = ResolveTargetDrawingViewRootInstanceSuffix(
                referencedAsm,
                cabinetTitleWithoutExt);

            try
            {
                var frameComponents = CollectFrameComponents(asmDoc, frameRootPath);
                var largeOffsetIds = CollectLargeOffsetFrameInstanceIds(
                    referencedAsm, cabinetTitleWithoutExt, frameComponents);
                var largeOffsetView = largeOffsetIds.Count == 0
                    ? null
                    : FindLargeOffsetDrawingView(drawingDoc, targetSheetName, referencedAsm);
                // 分组与目标视图确认后再清理；同时清理视图3、4，保证重跑不会残留旧标注。
                drawingDoc.ActivateSheet(targetSheetName);
                RemoveExistingFrameAnnotations(drawingModel);
                var requireFaceToFaceBaseOwnership = IsFaceToFaceCabinetAssembly(
                    referencedAsm,
                    cabinetTitleWithoutExt);
                var baseComponents = CollectBaseComponentsForEdgeAnnotations(
                    referencedAsm,
                    baseRootPath,
                    requireFaceToFaceOwnership: requireFaceToFaceBaseOwnership);
                var baseReferenceComponents = CollectBaseComponentsForReferenceDimensions(
                    referencedAsm,
                    baseRootPath,
                    requireFaceToFaceOwnership: requireFaceToFaceBaseOwnership);
                if (frameComponents.Count == 0
                    && baseComponents.Count == 0
                    && baseReferenceComponents.Count == 0)
                {
                    return;
                }

                // 沿装配 X（变换矩阵平移分量）排序，仅影响「基架{n}」退回序号与标注遍历顺序，不改变文件夹命名规则。
                frameComponents = frameComponents.OrderBy(GetComponentSortKey)
                    .ThenBy(component => component.Name2, StringComparer.OrdinalIgnoreCase).ToList();
                var regularFrames = frameComponents
                    .Where(component => !largeOffsetIds.Contains(component.Name2)).ToList();
                var largeOffsetFrames = frameComponents
                    .Where(component => largeOffsetIds.Contains(component.Name2)).ToList();
                baseComponents.Sort((a, b) =>
                    GetComponentSortKey(a).CompareTo(GetComponentSortKey(b)));
                baseReferenceComponents.Sort((a, b) =>
                    GetComponentSortKey(a).CompareTo(GetComponentSortKey(b)));

                if (!string.IsNullOrWhiteSpace(targetSheetName))
                {
                    try
                    {
                        drawingDoc.ActivateSheet(targetSheetName);
                    }
                    catch
                    {
                        // ignore
                    }
                }

                try
                {
                    drawingDoc.ActivateView(TargetDrawingViewName);
                }
                catch
                {
                    // ignore
                }

                var mathUtility = _swApp.IGetMathUtility();

                for (var frameIndex = 0; frameIndex < frameComponents.Count; frameIndex++)
                {
                    var component = frameComponents[frameIndex];
                    if (component == null)
                    {
                        continue;
                    }

                    // 注释/尺寸名称中的唯一键：同一零件多实例时 SolidWorks 会给 Name2 加 「-2」「-3」等后缀，Sanitize 后作为 Annotation.SetName 的稳定后缀。
                    var annotationKey = SanitizeAnnotationKey(GetFrameAnnotationBaseKey(component));
                    if (string.IsNullOrWhiteSpace(annotationKey))
                    {
                        continue;
                    }

                    try
                    {
                        AnnotateSingleFrameComponent(
                            drawingModel,
                            largeOffsetIds.Contains(component.Name2) ? largeOffsetView : targetView,
                            component,
                            cabinetTitleWithoutExt,
                            annotationKey,
                            frameOrdinalFallback: frameIndex + 1,
                            mathUtility);
                    }
                    catch
                    {
                        // 单个基架失败不影响其余基架。
                    }
                }

                try
                {
                    AnnotateBaseReferenceDimensions(
                        drawingModel,
                        targetView,
                        baseReferenceComponents,
                        cabinetTitleWithoutExt,
                        mathUtility);
                }
                catch
                {
                    // 基座长度/开孔高度标注失败不影响基架及绝缘子定位标注。
                }

                try
                {
                    AnnotateInsulatorsToOwnerEdgeInDrawing(
                        drawingModel,
                        targetView,
                        asmDoc,
                        regularFrames,
                        FrameEdgeReferenceSketchName,
                        AnnotationPrefixInsulatorEdge,
                        cabinetTitleWithoutExt,
                        mathUtility);
                }
                catch
                {
                    // 绝缘子边线标注失败不影响已完成的基架长度/名称标注。
                }

                if (largeOffsetFrames.Count > 0)
                {
                    try
                    {
                        AnnotateInsulatorsToOwnerEdgeInDrawing(
                            drawingModel,
                            largeOffsetView,
                            asmDoc,
                            largeOffsetFrames,
                            FrameEdgeReferenceSketchName,
                            AnnotationPrefixInsulatorEdge,
                            cabinetTitleWithoutExt,
                            mathUtility);
                    }
                    catch (Exception ex)
                    {
                        FaceToWallPreviewTrace.Error("大偏移基架绝缘子标注失败", ex, drawingPath);
                    }
                }

                try
                {
                    AnnotateInsulatorsToOwnerEdgeInDrawing(
                        drawingModel,
                        targetView,
                        asmDoc,
                        baseComponents,
                        BaseEdgeReferenceSketchName,
                        AnnotationPrefixBaseInsulatorEdge,
                        cabinetTitleWithoutExt,
                        mathUtility);
                }
                catch
                {
                    // 基座内绝缘子定位标注失败不影响基架标注。
                }

                try
                {
                    drawingModel.GraphicsRedraw2();
                }
                catch
                {
                    // ignore
                }
            }
            finally
            {
                _drawingViewRootInstanceSuffixOverride = previousRootInstanceSuffixOverride;
            }
        }

        private static View FindLargeOffsetDrawingView(
            DrawingDoc drawingDoc, string sheetName, ModelDoc2 referencedAssembly)
        {
            drawingDoc.ActivateSheet(sheetName);
            var view = drawingDoc.GetFirstView() as View;
            while (view != null)
            {
                if (string.Equals(view.Name, LargeOffsetDrawingViewName, StringComparison.Ordinal))
                {
                    var model = TryGetViewReferencedDocument(view);
                    if (model != null && model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY
                        && string.Equals(TryGetPathName(model), TryGetPathName(referencedAssembly),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return view;
                    }
                }

                view = view.GetNextView() as View;
            }

            throw new InvalidOperationException("大偏移基架标注需要同一图纸中引用当前总装配体的工程图视图4。");
        }

        private static HashSet<string> CollectLargeOffsetFrameInstanceIds(
            ModelDoc2 assemblyModel, string assemblyTitle, List<Component2> frameComponents)
        {
            if (IsFaceToWallCabinetAssembly(assemblyModel, assemblyTitle))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var scenario = DesignScenarioDefaults.ParseScenarioId(
                ReadAssemblyCustomProperty(assemblyModel, DesignScenarioCustomPropertyName));
            var operations = new SolidWorksFaceToFaceFrameReadOperations(assemblyModel);
            var topLevel = operations.GetTopLevelComponents()
                .Where(component => !component.IsSuppressed).ToList();
            var bases = topLevel.Where(component =>
                component.ConfiguredRdSystemPartType == SystemPartType_e.Base).ToList();
            if (scenario == DesignScenarioId.FaceToFace && bases.Count != 2)
            {
                throw new InvalidOperationException("面对面总装图必须从顶层唯一识别两个独立基座。");
            }

            var bBase = FaceToFaceTopLevelBaseOwnershipResolver.ResolveBCabinetBase(topLevel, bases);
            var knownFrameIds = new HashSet<string>(
                frameComponents.Select(component => component.Name2), StringComparer.OrdinalIgnoreCase);
            // 基架从总装全局查找，不能限定在 A/B 柜体子树，也不能用基座补齐。
            var frames = operations.GetAllComponents()
                .Where(component => !component.IsSuppressed
                    && (component.ConfiguredRdSystemPartType == SystemPartType_e.Frame
                        || knownFrameIds.Contains(component.InstanceId)))
                .Select(component =>
                {
                    FaceToFaceFramePathClassifier.TryGetFrameTemplateKindFromComponentName(
                        component.ComponentName, out var kind);
                    return new FaceToFaceFrameComponentSnapshot(
                        component.InstanceId, component.ComponentName, component.ModelPath,
                        component.ComponentName, true, false, kind, component.BoxCenter);
                }).ToList();
            if (!frames.Any(frame => frame.FrameTemplateKind == FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            frames.Add(new FaceToFaceFrameComponentSnapshot(
                bBase.InstanceId, bBase.ComponentName, bBase.ModelPath, null,
                false, true, FaceToWallFrameTemplateKind.Regular, bBase.BoxCenter));
            var mates = new List<FaceToFaceFrameMateSnapshot>();
            var mateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (scenario == DesignScenarioId.FaceToFace)
            {
                foreach (var component in frames)
                {
                    foreach (var mate in operations.GetMates(component.InstanceId))
                    {
                        if (string.IsNullOrWhiteSpace(mate.First.ComponentInstanceId)
                            || string.IsNullOrWhiteSpace(mate.Second.ComponentInstanceId))
                        {
                            continue;
                        }

                        var endpoints = new[]
                        {
                            mate.First.ComponentInstanceId + ":" + mate.First.EntityName,
                            mate.Second.ComponentInstanceId + ":" + mate.Second.EntityName
                        };
                        Array.Sort(endpoints, StringComparer.OrdinalIgnoreCase);
                        if (mateKeys.Add(string.Join("|", endpoints)))
                        {
                            mates.Add(new FaceToFaceFrameMateSnapshot(
                                mate.First.ComponentInstanceId, mate.First.EntityName,
                                mate.Second.ComponentInstanceId, mate.Second.EntityName));
                        }
                    }
                }
            }

            var ids = SelectLargeOffsetFrameInstanceIds(new FaceToFaceFrameTopologySnapshot(frames, mates), scenario);
            var allComponents = ((AssemblyDoc)assemblyModel).GetComponents(false) as object[] ?? Array.Empty<object>();
            frameComponents.AddRange(allComponents.OfType<Component2>()
                .Where(component => ids.Contains(component.Name2) && knownFrameIds.Add(component.Name2)));
            return ids;
        }

        internal static HashSet<string> SelectLargeOffsetFrameInstanceIds(
            FaceToFaceFrameTopologySnapshot topology, DesignScenarioId scenario)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (scenario != DesignScenarioId.FaceToFace && scenario != DesignScenarioId.SideToSide)
            {
                return ids;
            }

            var corners = topology.Components.Where(component => component.IsFrame
                && component.FrameTemplateKind == FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner).ToList();
            if (corners.Count == 0)
            {
                return ids;
            }

            if (corners.Count != 2)
            {
                throw new InvalidOperationException("大偏移总装图必须且只能包含两个 028 拐角基架。");
            }

            // 与模型反读共用顺序：面对面走实际配合链，侧对侧按 B 柜基座的空间距离。
            var ordered = FaceToFaceFrameChainAnalyzer.Analyze(
                topology, scenario == DesignScenarioId.SideToSide).Select(item => item.Component).ToList();
            var first = ordered.FindIndex(component => component.FrameTemplateKind == FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner);
            var last = ordered.FindLastIndex(component => component.FrameTemplateKind == FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner);
            if (last <= first + 1 || ordered.Skip(first + 1).Take(last - first - 1)
                .Any(component => component.FrameTemplateKind != FaceToWallFrameTemplateKind.Regular))
            {
                throw new InvalidOperationException("大偏移两个拐角之间必须是一个或多个常规连接基架。");
            }

            ids.UnionWith(ordered.Skip(first).Take(last - first + 1).Select(component => component.InstanceId));
            return ids;
        }

        /// <summary>
        /// 为单个基架子装配实例创建长度驱动尺寸（若成功）与名称注释。
        /// </summary>
        /// <param name="drawingModel">工程图 <see cref="ModelDoc2"/>（插入尺寸与注释时使用）。</param>
        /// <param name="targetView">柜体视图引用，用于图纸坐标变换与包围盒投影。</param>
        /// <param name="component">基架装配组件实例。</param>
        /// <param name="cabinetTitleWithoutExt">顶层柜体装配文件名（无扩展名），用于拼接 SelectByID2 字符串末尾段。</param>
        /// <param name="annotationKey">已消毒的实例键，用于 FRAME_LEN_* / FRAME_NAME_* 命名。</param>
        /// <param name="frameOrdinalFallback">
        /// 当装配体路径无法给出有意义的父文件夹名（例如直接位于「基架」根目录）时，用于生成退回显示名「基架{n}」的序号；与排序后的 <c>frameIndex+1</c> 一致。
        /// </param>
        /// <param name="mathUtility">SolidWorks 数学工具，用于点变换与包围盒计算。</param>
        /// <remarks>
        /// <para>
        /// 长度：在工程图中选中「基架长度参考草图」下的「直线1」（见 <see cref="FrameLengthReferenceSketchLineNames"/>）后调用 <c>AddDimension2</c> 插入尺寸；
        /// 若插入失败且可从自定义属性推算理论长度，则插入纯文本备注（<see cref="TryInsertLengthFallbackNote"/>）。
        /// </para>
        /// <para>
        /// 名称：可见字符串由 <see cref="GetFrameDisplayNameFromFolder"/> 决定；可与同类文件夹内其它实例重复，但注释对象的内部名称仍带唯一 <paramref name="annotationKey"/>。
        /// </para>
        /// </remarks>
        private static void AnnotateSingleFrameComponent(
            ModelDoc2 drawingModel,
            View targetView,
            Component2 component,
            string cabinetTitleWithoutExt,
            string annotationKey,
            int frameOrdinalFallback,
            MathUtility mathUtility)
        {
            (drawingModel as DrawingDoc)?.ActivateView(targetView.Name);
            // 来自零部件摘要属性「c」与默认长度；尺寸插入失败时用于备选备注（毫米）。
            var expectedLengthMm = TryGetExpectedFrameLengthMm(component);

            drawingModel.ClearSelection2(true);

            var lengthLineSelected = TrySelectFrameLengthReferenceLineInDrawing(
                drawingModel,
                component,
                targetView,
                cabinetTitleWithoutExt,
                out var lineSegment);

            var lengthContext = "视图=" + targetView.Name + "；组件=" + component.Name2;
            if (!lengthLineSelected)
            {
                FaceToWallPreviewTrace.Warning("工程图-基架长度参考线未选中", lengthContext);
            }

            var dimPlaced = false;
            double dimX = 0;
            double dimY = 0;
            if (lineSegment != null)
            {
                if (!TryGetFrameLengthDimensionSheetPosition(
                        targetView,
                        component,
                        lineSegment,
                        mathUtility,
                        out dimX,
                        out dimY))
                {
                    if (!TryGetHorizontalDimensionSheetPosition(
                            targetView,
                            component,
                            lineSegment,
                            mathUtility,
                            out dimX,
                            out dimY))
                    {
                        TryGetFallbackDimensionSheetPosition(targetView, out dimX, out dimY);
                    }
                }

                try
                {
                    // dimX/dimY：图纸平面上的尺寸文字位置（米）。第三参数与 API 一致，此处为 0。
                    // 依赖当前选择集中的「基架长度参考草图」线段（见 TrySelectFrameLengthReferenceLineInDrawing）。
                    var dispDim = drawingModel.AddDimension2(dimX, dimY, 0) as DisplayDimension;
                    if (dispDim != null)
                    {
                        var ann = dispDim.GetAnnotation() as Annotation;
                        if (ann != null)
                        {
                            try
                            {
                                ann.SetName(AnnotationPrefixLen + annotationKey);
                            }
                            catch
                            {
                                // ignore
                            }
                        }

                        dimPlaced = true;
                        FaceToWallPreviewTrace.Info("工程图-基架长度尺寸已插入", lengthContext);
                    }
                    else
                    {
                        FaceToWallPreviewTrace.Warning("工程图-基架长度尺寸插入失败", lengthContext);
                    }
                }
                catch (Exception ex)
                {
                    FaceToWallPreviewTrace.Error("工程图-基架长度尺寸插入异常", ex, lengthContext);
                    dimPlaced = false;
                }

                drawingModel.ClearSelection2(true);
            }

            // 尺寸未放置时，至少给出可读数值（不含强制前缀），便于车间核对。
            if (!dimPlaced && expectedLengthMm > 0)
            {
                if (lineSegment == null)
                {
                    // 参考线不可选时也必须把备选长度放在当前基架旁，不能落到图纸原点。
                    if (!TryGetFrameNameNoteSheetPosition(targetView, component, mathUtility, out dimX, out dimY))
                    {
                        TryGetFallbackDimensionSheetPosition(targetView, out dimX, out dimY);
                    }
                    dimY -= FrameLengthDimensionBelowNameOffsetM;
                }
                TryInsertLengthFallbackNote(
                    drawingModel,
                    expectedLengthMm,
                    dimX,
                    dimY,
                    AnnotationPrefixLen + annotationKey + "_NOTE");
            }

            // 显示名：磁盘上父文件夹名；退回「基架{n}」见 GetFrameDisplayNameFromFolder 注释。
            var displayName = GetFrameDisplayNameFromFolder(component, frameOrdinalFallback);
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                TryInsertFrameNameNote(
                    drawingModel,
                    targetView,
                    displayName,
                    AnnotationPrefixName + annotationKey,
                    mathUtility,
                component);
            }
        }

        /// <summary>
        /// 为总装配体顶层基座分别标注基座长度与基座开孔高度。
        /// </summary>
        private static void AnnotateBaseReferenceDimensions(
            ModelDoc2 drawingModel,
            View targetView,
            IReadOnlyList<Component2> baseComponents,
            string cabinetTitleWithoutExt,
            MathUtility mathUtility)
        {
            if (drawingModel == null || targetView == null || baseComponents == null
                || baseComponents.Count == 0 || mathUtility == null)
            {
                return;
            }

            try
            {
                (drawingModel as DrawingDoc)?.ActivateView(targetView.Name);
            }
            catch
            {
                // ignore
            }

            foreach (var baseComponent in baseComponents)
            {
                if (baseComponent == null)
                {
                    continue;
                }

                var annotationKey = SanitizeAnnotationKey(baseComponent.Name2 ?? string.Empty);
                if (string.IsNullOrWhiteSpace(annotationKey))
                {
                    continue;
                }

                TryAnnotateBaseReferenceDimension(
                    drawingModel,
                    targetView,
                    baseComponent,
                    cabinetTitleWithoutExt,
                    BaseLengthReferenceSketchName,
                    AnnotationPrefixBaseLength + annotationKey,
                    dimensionLane: 1,
                    mathUtility: mathUtility);

                TryAnnotateBaseReferenceDimension(
                    drawingModel,
                    targetView,
                    baseComponent,
                    cabinetTitleWithoutExt,
                    BaseOpeningReferenceSketchName,
                    AnnotationPrefixBaseOpeningHeight + annotationKey,
                    dimensionLane: 2,
                    mathUtility: mathUtility);
            }
        }

        /// <summary>
        /// 选中基座指定参考草图中的直线，并为该直线插入一个驱动尺寸。
        /// </summary>
        private static void TryAnnotateBaseReferenceDimension(
            ModelDoc2 drawingModel,
            View targetView,
            Component2 baseComponent,
            string cabinetTitleWithoutExt,
            string referenceSketchName,
            string annotationName,
            int dimensionLane,
            MathUtility mathUtility)
        {
            if (drawingModel == null || targetView == null || baseComponent == null
                || string.IsNullOrWhiteSpace(referenceSketchName)
                || string.IsNullOrWhiteSpace(annotationName))
            {
                return;
            }

            drawingModel.ClearSelection2(true);
            try
            {
                if (!TrySelectFrameSketchLineInDrawing(
                        drawingModel,
                        baseComponent,
                        targetView,
                        cabinetTitleWithoutExt,
                        referenceSketchName,
                        appendToSelection: false,
                        clearSelectionFirst: true,
                        out var lineSegment)
                    || lineSegment == null)
                {
                    return;
                }

                if (!TryGetBaseReferenceDimensionSheetPosition(
                        targetView,
                        baseComponent,
                        lineSegment,
                        mathUtility,
                        dimensionLane,
                        out var dimX,
                        out var dimY))
                {
                    TryGetFallbackDimensionSheetPosition(targetView, out dimX, out dimY);
                }

                var displayDimension = drawingModel.AddDimension2(dimX, dimY, 0) as DisplayDimension;
                var annotation = displayDimension?.GetAnnotation() as Annotation;
                if (annotation != null)
                {
                    try
                    {
                        annotation.SetName(annotationName);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
            catch
            {
                // 单个参考草图尺寸失败时继续处理其它基座/尺寸。
            }
            finally
            {
                try
                {
                    drawingModel.ClearSelection2(true);
                }
                catch
                {
                    // ignore
                }
            }
        }

        /// <summary>
        /// 在目标视图上，为每个顶层基架或基座自身组件子树内的绝缘子添加相对边线参考草图的定位尺寸。
        /// </summary>
        /// <remarks>
        /// 绝缘子归属只根据 <see cref="Component2.Name2"/> 的完整父子实例路径判定，不再按全局 X 范围回配，
        /// 因而基座子树内的绝缘子不会被误标到基架边线，基架子树内的绝缘子也不会被基座使用。
        /// </remarks>
        private static void AnnotateInsulatorsToOwnerEdgeInDrawing(
            ModelDoc2 drawingModel,
            View targetView,
            AssemblyDoc asmDoc,
            IReadOnlyList<Component2> ownerComponents,
            string edgeReferenceSketchName,
            string annotationPrefix,
            string cabinetTitleWithoutExt,
            MathUtility mathUtility)
        {
            if (drawingModel == null || targetView == null || asmDoc == null || ownerComponents == null
                || ownerComponents.Count == 0 || string.IsNullOrWhiteSpace(edgeReferenceSketchName)
                || string.IsNullOrWhiteSpace(annotationPrefix) || mathUtility == null)
            {
                return;
            }

            var insulators = CollectInsulatorComponents(asmDoc);
            if (insulators.Count == 0)
            {
                return;
            }

            try
            {
                (drawingModel as DrawingDoc)?.ActivateView(targetView.Name);
            }
            catch
            {
                // ignore
            }

            foreach (var ownerComponent in ownerComponents)
            {
                if (ownerComponent == null)
                {
                    continue;
                }

                var ownerKey = SanitizeAnnotationKey(ownerComponent.Name2 ?? string.Empty);
                if (string.IsNullOrWhiteSpace(ownerKey))
                {
                    continue;
                }

                foreach (var insulatorComponent in insulators)
                {
                    if (!IsDescendantComponent(insulatorComponent, ownerComponent))
                    {
                        continue;
                    }

                    var insulatorKey = SanitizeAnnotationKey(insulatorComponent.Name2 ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(insulatorKey))
                    {
                        continue;
                    }

                    try
                    {
                        AnnotateOneInsulatorToOwnerEdgeDimension(
                            drawingModel,
                            targetView,
                            ownerComponent,
                            insulatorComponent,
                            edgeReferenceSketchName,
                            cabinetTitleWithoutExt,
                            annotationPrefix + ownerKey + "__" + insulatorKey,
                            mathUtility);
                    }
                    catch (Exception ex)
                    {
                        FaceToWallPreviewTrace.Error("工程图-绝缘子定位标注", ex,
                            "所属组件=" + ownerKey + "；绝缘子=" + insulatorKey);
                    }
                }
            }
        }

        /// <summary>
        /// 先解析所属组件的边线，再选标注点并追加已解析的边线，允许两端独立执行草图回退。
        /// </summary>
        private static void AnnotateOneInsulatorToOwnerEdgeDimension(
            ModelDoc2 drawingModel,
            View targetView,
            Component2 ownerComponent,
            Component2 insulatorComponent,
            string edgeReferenceSketchName,
            string cabinetTitleWithoutExt,
            string annotationName,
            MathUtility mathUtility)
        {
            try
            {
                if (!TrySelectFrameSketchLineInDrawing(
                    drawingModel, ownerComponent, targetView, cabinetTitleWithoutExt,
                    edgeReferenceSketchName, false, true, out var edgeLine))
                {
                    FaceToWallPreviewTrace.Warning("工程图-绝缘子边线未选中",
                        annotationName + "；草图=" + edgeReferenceSketchName);
                    return;
                }

                if (!TryGetInsulatorOwnerEdgeDimensionSheetPosition(
                    targetView, ownerComponent, insulatorComponent, edgeLine, mathUtility,
                    out var dimX, out var dimY))
                {
                    TryGetFallbackDimensionSheetPosition(targetView, out dimX, out dimY);
                }

                // 专用草图优先；只有选取或插入失败才尝试旧模型的原点。
                foreach (var useOrigin in new[] { false, true })
                {
                    var source = useOrigin ? "原点" : InsulatorDrawingAnchorSketchName;
                    try
                    {
                        drawingModel.ClearSelection2(true);
                        if (!TrySelectInsulatorDimensionAnchorInDrawing(
                            drawingModel, insulatorComponent, targetView, cabinetTitleWithoutExt, useOrigin))
                        {
                            FaceToWallPreviewTrace.Warning("工程图-绝缘子标注点未选中", annotationName + "；来源=" + source);
                            continue;
                        }

                        var selection = drawingModel.SelectionManager as SelectionMgr;
                        var selectData = selection.CreateSelectData();
                        selectData.View = targetView;
                        if (!edgeLine.Select4(true, selectData)
                            || selection.GetSelectedObjectCount2(-1) != 2
                            || !IsSelectedDimensionPoint(drawingModel, 1)
                            || TryGetSelectedSketchSegmentByIndex(drawingModel, 2) == null)
                        {
                            FaceToWallPreviewTrace.Warning("工程图-绝缘子定位选择集无效", annotationName + "；来源=" + source);
                            continue;
                        }

                        var dispDim = drawingModel.AddDimension2(dimX, dimY, 0) as DisplayDimension;
                        if (dispDim == null)
                        {
                            FaceToWallPreviewTrace.Warning("工程图-绝缘子定位尺寸插入失败", annotationName + "；来源=" + source);
                            continue;
                        }

                        // 尺寸已插入后即结束重试，命名失败也不能重复创建尺寸。
                        try
                        {
                            (dispDim.GetAnnotation() as Annotation)?.SetName(annotationName);
                        }
                        catch (Exception ex)
                        {
                            FaceToWallPreviewTrace.Error("工程图-绝缘子定位尺寸命名失败", ex, annotationName);
                        }
                        FaceToWallPreviewTrace.Info("工程图-绝缘子定位标注完成", annotationName + "；来源=" + source);
                        return;
                    }
                    catch (System.Runtime.InteropServices.COMException ex)
                    {
                        FaceToWallPreviewTrace.Error("工程图-绝缘子定位尝试失败", ex, annotationName + "；来源=" + source);
                    }
                }
            }
            finally
            {
                drawingModel.ClearSelection2(true);
            }
        }

        /// <summary>
        /// 收集总装配体全部层级中的绝缘子子装配实例。
        /// </summary>
        private static List<Component2> CollectInsulatorComponents(AssemblyDoc asmDoc)
        {
            var list = new List<Component2>();
            if (asmDoc == null)
            {
                return list;
            }

            object[] all = null;
            try
            {
                // GetComponents 的参数是 TopLevelOnly：绝缘子位于基座/基架子树中，
                // 必须传 false 才会同时返回顶层组件和全部子组件。
                all = asmDoc.GetComponents(false) as object[];
            }
            catch
            {
                all = null;
            }

            if (all == null || all.Length == 0)
            {
                return list;
            }

            foreach (var obj in all)
            {
                var comp = obj as Component2;
                if (comp == null)
                {
                    continue;
                }

                try
                {
                    if (comp.IsSuppressed())
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                if (!IsInsulatorAssemblyComponent(comp))
                {
                    continue;
                }

                list.Add(comp);
            }

            return list;
        }

        /// <summary>
        /// 判断候选组件是否严格位于指定顶层基架/基座的组件子树内。
        /// </summary>
        private static bool IsDescendantComponent(Component2 candidate, Component2 ancestor)
        {
            if (candidate == null || ancestor == null)
            {
                return false;
            }

            var candidateName = candidate.Name2 ?? string.Empty;
            var ancestorName = ancestor.Name2 ?? string.Empty;
            if (string.IsNullOrWhiteSpace(candidateName) || string.IsNullOrWhiteSpace(ancestorName))
            {
                return false;
            }

            return candidateName.StartsWith(
                ancestorName + "/",
                StringComparison.OrdinalIgnoreCase);
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

            if (file.Equals(InsulatorOddSegmentAsmFileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (file.Equals(InsulatorEvenSegmentAsmFileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 计算绝缘子—所属基架/基座边线尺寸的图纸放置点：标注点与边线中点投影的中点，并尽量避开所属组件投影范围。
        /// </summary>
        private static bool TryGetInsulatorOwnerEdgeDimensionSheetPosition(
            View targetView,
            Component2 ownerComponent,
            Component2 insulatorComponent,
            SketchSegment edgeLine,
            MathUtility mathUtility,
            out double sheetX,
            out double sheetY)
        {
            sheetX = 0;
            sheetY = 0;

            if (targetView == null || ownerComponent == null || insulatorComponent == null
                || edgeLine == null || mathUtility == null)
            {
                return false;
            }

            var hasInsOrigin = TryGetProjectedComponentOriginSheetXY(
                targetView,
                insulatorComponent,
                mathUtility,
                out var ix,
                out var iy);

            if (!TryGetHorizontalDimensionSheetPosition(
                    targetView,
                    ownerComponent,
                    edgeLine,
                    mathUtility,
                    out var lx,
                    out var ly))
            {
                if (hasInsOrigin)
                {
                    sheetX = ix;
                    sheetY = iy - 0.006;
                    return true;
                }

                return false;
            }

            if (hasInsOrigin)
            {
                sheetX = (ix + lx) / 2d;
                sheetY = (iy + ly) / 2d;
            }
            else
            {
                sheetX = lx;
                sheetY = ly;
            }

            TryClampHorizontalDimensionYOutsideBBox(
                targetView,
                ownerComponent,
                mathUtility,
                ref sheetY);
            return true;
        }

        /// <summary>
        /// 优先读取长度参考线的真实长度；旧模型没有参考线时再按 c 属性推算（毫米）。
        /// </summary>
        /// <returns>无法读取文档或解析属性时返回负数，表示无推算长度可供备选备注。</returns>
        private static double TryGetExpectedFrameLengthMm(Component2 component)
        {
            if (component == null)
            {
                return -1;
            }

            var referenceLine = TryGetFrameLengthModelLine(component);
            if (referenceLine != null)
            {
                var lengthMm = referenceLine.GetLength() * 1000d;
                if (!double.IsNaN(lengthMm) && !double.IsInfinity(lengthMm) && lengthMm > 0)
                {
                    return lengthMm;
                }
            }

            ModelDoc2 doc = null;
            try
            {
                doc = component.IGetModelDoc() as ModelDoc2 ?? component.GetModelDoc2() as ModelDoc2;
            }
            catch
            {
                doc = null;
            }

            if (doc == null)
            {
                return -1;
            }

            string config = string.Empty;
            try
            {
                config = component.ReferencedConfiguration ?? string.Empty;
            }
            catch
            {
                config = string.Empty;
            }

            string cText = null;
            try
            {
                cText = doc.GetCustomInfoValue(config, FrameLengthOffsetPropertyName);
            }
            catch
            {
                cText = null;
            }

            if (string.IsNullOrWhiteSpace(cText))
            {
                try
                {
                    cText = doc.GetCustomInfoValue(string.Empty, FrameLengthOffsetPropertyName);
                }
                catch
                {
                    cText = null;
                }
            }

            if (!TryParseInvariantDouble(cText, out var cMm))
            {
                return -1;
            }

            return DefaultStandardFrameLengthMm + cMm;
        }

        private static bool TryParseInvariantDouble(string text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return double.TryParse(
                text.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value)
                || double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }

        /// <summary>
        /// 当无法放置驱动尺寸时，插入纯文本注释显示推算长度（毫米，保留一位小数）。
        /// </summary>
        /// <param name="annotationName">通常使用 FRAME_LEN_*_NOTE，便于与尺寸前缀一并清理。</param>
        private static void TryInsertLengthFallbackNote(
            ModelDoc2 drawingModel,
            double lengthMm,
            double dimX,
            double dimY,
            string annotationName)
        {
            try
            {
                var text = Math.Round(lengthMm, 1).ToString("0.##", CultureInfo.InvariantCulture);
                TryInsertNamedNote(
                    drawingModel,
                    text,
                    dimX,
                    dimY - 0.004,
                    annotationName);
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// 在图纸坐标系下插入基架名称注释，优先置于组件投影包围盒顶边中点上方。
        /// </summary>
        /// <param name="displayName">界面可见文字（文件夹名或退回的「基架{n}」）。</param>
        /// <param name="annotationName">写入 <see cref="Annotation.SetName"/> 的内部名（FRAME_NAME_*）。</param>
        private static void TryInsertFrameNameNote(
            ModelDoc2 drawingModel,
            View targetView,
            string displayName,
            string annotationName,
            MathUtility mathUtility,
            Component2 component)
        {
            double nx = 0;
            double ny = 0;
            if (!TryGetFrameNameNoteSheetPosition(targetView, component, mathUtility, out nx, out ny))
            {
                // 最后手段：视图外包矩形中心偏上；多实例同时失败时会重叠，故上游提供原点投影回退。
                TryGetFallbackDimensionSheetPosition(targetView, out nx, out ny);
                ny += 0.025;
            }

            TryInsertNamedNote(drawingModel, displayName, nx, ny, annotationName, centerTextHorizontally: true);
        }

        /// <summary>
        /// 插入通用图纸注释并可选设置水平居中，使 <paramref name="sheetX"/>/<paramref name="sheetY"/> 作为对齐锚点。
        /// </summary>
        /// <param name="sheetX">图纸坐标 X（米）。</param>
        /// <param name="sheetY">图纸坐标 Y（米）。</param>
        /// <param name="centerTextHorizontally">为 true 时调用 <see cref="Note.SetTextJustification"/> 居中，便于对准几何中心。</param>
        private static void TryInsertNamedNote(
            ModelDoc2 drawingModel,
            string text,
            double sheetX,
            double sheetY,
            string annotationName,
            bool centerTextHorizontally = false)
        {
            if (drawingModel == null || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            Note note = null;
            try
            {
                note = drawingModel.InsertNote(text) as Note;
            }
            catch
            {
                note = null;
            }

            if (note == null)
            {
                return;
            }

            try
            {
                // INote.SetBalloon 签名为 (样式, 大小)：swBS_None 表示无边力气泡框；第二个参数与文档气球 Size（常为 swBalloonFit）对应。
                note.SetBalloon((int)swBalloonStyle_e.swBS_None, (int)swBalloonFit_e.swBF_Tightest);
            }
            catch
            {
                // ignore
            }

            if (centerTextHorizontally)
            {
                try
                {
                    // 先设对齐再设锚点：居中时 (sheetX, sheetY) 表示文本块的中心参考点。
                    note.SetTextJustification((int)swTextJustification_e.swTextJustificationCenter);
                }
                catch
                {
                    // ignore
                }
            }

            try
            {
                // INote：图纸注释锚点位置用 SetTextPoint(x,y,z)，单位米（工程图纸坐标）。
                note.SetTextPoint(sheetX, sheetY, 0);
            }
            catch
            {
                // ignore
            }

            try
            {
                var ann = note.GetAnnotation() as Annotation;
                if (ann != null && !string.IsNullOrWhiteSpace(annotationName))
                {
                    ann.SetName(annotationName);
                }
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// 计算水平尺寸文字在图纸上的建议位置：参考线段中点 → 组件装配变换 → 视图 ModelToView → 图纸 XY。
        /// </summary>
        /// <remarks>
        /// 草图几何处于零部件坐标系，须左乘 <see cref="Component2.Transform2"/> 得到装配坐标，再经 <see cref="View.ModelToViewTransform"/> 投影到图纸。
        /// 随后用 <see cref="TryClampHorizontalDimensionYOutsideBBox"/> 避免尺寸文字落在投影外包矩形的竖向范围内而与几何重叠。
        /// </remarks>
        private static bool TryGetHorizontalDimensionSheetPosition(
            View view,
            Component2 component,
            SketchSegment lineSegment,
            MathUtility mathUtility,
            out double sheetX,
            out double sheetY)
        {
            sheetX = 0;
            sheetY = 0;

            if (view == null || component == null || lineSegment == null || mathUtility == null)
            {
                return false;
            }

            var sketchLine = lineSegment as SketchLine;
            if (sketchLine == null)
            {
                return false;
            }

            double mx;
            double my;
            double mz;
            try
            {
                object spObj = null;
                object epObj = null;
                try
                {
                    spObj = sketchLine.GetStartPoint2();
                    epObj = sketchLine.GetEndPoint2();
                }
                catch
                {
                    return false;
                }

                if (!TryGetXYZFromSketchEndpoint(spObj, out var sx, out var sy, out var sz)
                    || !TryGetXYZFromSketchEndpoint(epObj, out var ex, out var ey, out var ez))
                {
                    return false;
                }

                mx = (sx + ex) / 2d;
                my = (sy + ey) / 2d;
                mz = (sz + ez) / 2d;

                var mpLocal = mathUtility.CreatePoint(new double[] { mx, my, mz }) as MathPoint;
                if (mpLocal == null)
                {
                    return false;
                }

                var mpAsm = MultiplyMathPoint(mpLocal, component.Transform2 as MathTransform);
                if (mpAsm == null)
                {
                    return false;
                }

                var mtView = view.ModelToViewTransform as MathTransform;
                if (mtView == null)
                {
                    return false;
                }

                var mpSheet = MultiplyMathPoint(mpAsm, mtView);
                var sheetArr = mpSheet?.ArrayData as double[];
                if (sheetArr == null || sheetArr.Length < 2)
                {
                    return false;
                }

                sheetX = sheetArr[0];
                sheetY = sheetArr[1] - 0.004;

                TryClampHorizontalDimensionYOutsideBBox(
                    view,
                    component,
                    mathUtility,
                    ref sheetY);

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 根据参考直线在图纸上的投影方向，将基座参考尺寸放到组件投影包围盒之外。
        /// </summary>
        /// <remarks>
        /// 水平投影线的尺寸置于基座下方，竖直投影线的尺寸置于基座左侧；
        /// <paramref name="dimensionLane"/> 用于错开长度与开孔高度两类尺寸。
        /// </remarks>
        private static bool TryGetBaseReferenceDimensionSheetPosition(
            View view,
            Component2 component,
            SketchSegment lineSegment,
            MathUtility mathUtility,
            int dimensionLane,
            out double sheetX,
            out double sheetY)
        {
            sheetX = 0;
            sheetY = 0;

            var sketchLine = lineSegment as SketchLine;
            if (view == null || component == null || sketchLine == null || mathUtility == null)
            {
                return false;
            }

            object startPoint;
            object endPoint;
            try
            {
                startPoint = sketchLine.GetStartPoint2();
                endPoint = sketchLine.GetEndPoint2();
            }
            catch
            {
                return false;
            }

            if (!TryGetXYZFromSketchEndpoint(startPoint, out var sx, out var sy, out var sz)
                || !TryGetXYZFromSketchEndpoint(endPoint, out var ex, out var ey, out var ez)
                || !TryProjectComponentLocalPointToSheet(
                    view,
                    component,
                    mathUtility,
                    sx,
                    sy,
                    sz,
                    out var ssx,
                    out var ssy)
                || !TryProjectComponentLocalPointToSheet(
                    view,
                    component,
                    mathUtility,
                    ex,
                    ey,
                    ez,
                    out var esx,
                    out var esy)
                || !TryGetProjectedComponentBBoxSheetExtents(
                    view,
                    component,
                    mathUtility,
                    out var minX,
                    out _,
                    out var minY,
                    out _))
            {
                return false;
            }

            var lane = Math.Max(1, dimensionLane);
            var outsideOffset = FrameDimensionOutsideBBoxMarginM
                                + (lane - 1) * BaseReferenceDimensionLaneSpacingM;
            GetReferenceDimensionPosition(ssx, ssy, esx, esy, minX, minY, outsideOffset,
                out sheetX, out sheetY);
            return true;
        }

        internal static void GetReferenceDimensionPosition(
            double startX, double startY, double endX, double endY,
            double minX, double minY, double outsideOffset, out double sheetX, out double sheetY)
        {
            if (Math.Abs(endX - startX) >= Math.Abs(endY - startY))
            {
                sheetX = (startX + endX) / 2d;
                sheetY = minY - outsideOffset;
            }
            else
            {
                sheetX = minX - outsideOffset;
                sheetY = (startY + endY) / 2d;
            }
        }

        /// <summary>
        /// 将组件局部坐标点依次变换到装配坐标和目标工程图图纸坐标。
        /// </summary>
        private static bool TryProjectComponentLocalPointToSheet(
            View view,
            Component2 component,
            MathUtility mathUtility,
            double localX,
            double localY,
            double localZ,
            out double sheetX,
            out double sheetY)
        {
            sheetX = 0;
            sheetY = 0;
            if (view == null || component == null || mathUtility == null)
            {
                return false;
            }

            try
            {
                var localPoint = mathUtility.CreatePoint(new[] { localX, localY, localZ }) as MathPoint;
                var componentTransform = component.Transform2 as MathTransform;
                var viewTransform = view.ModelToViewTransform as MathTransform;
                if (localPoint == null || componentTransform == null || viewTransform == null)
                {
                    return false;
                }

                var assemblyPoint = MultiplyMathPoint(localPoint, componentTransform);
                var projectedPoint = MultiplyMathPoint(assemblyPoint, viewTransform);
                var data = projectedPoint?.ArrayData as double[];
                if (data == null || data.Length < 2)
                {
                    return false;
                }

                sheetX = data[0];
                sheetY = data[1];
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 基架长度尺寸锚点：水平取投影包围盒中心（与名称注释对齐）；垂直位于名称锚点下方、组件投影顶边上方。
        /// </summary>
        /// <remarks>
        /// 假定图纸坐标系 Y 向上递增（与 SW 常见图纸坐标一致）：名称锚点为 <c>maxY + <see cref="FrameNameNoteAboveBBoxClearanceM"/></c>，
        /// 尺寸锚点在其下方 <see cref="FrameLengthDimensionBelowNameOffsetM"/>，且不低于 <c>maxY + <see cref="FrameLengthDimensionMinAboveTopEdgeM"/></c>。
        /// </remarks>
        private static bool TryGetFrameLengthDimensionSheetPosition(
            View view,
            Component2 component,
            SketchSegment lineSegment,
            MathUtility mathUtility,
            out double sheetX,
            out double sheetY)
        {
            sheetX = 0;
            sheetY = 0;

            if (view != null && string.Equals(view.Name, LargeOffsetDrawingViewName, StringComparison.Ordinal))
            {
                return TryGetLargeOffsetLengthDimensionPosition(
                    view, component, mathUtility, out sheetX, out sheetY);
            }

            if (view == null || component == null || mathUtility == null)
            {
                return false;
            }

            if (!TryGetProjectedComponentBBoxSheetExtents(
                    view,
                    component,
                    mathUtility,
                    out var minX,
                    out var maxX,
                    out _,
                    out var maxY))
            {
                return false;
            }

            sheetX = (minX + maxX) / 2d;

            var nameAnchorY = maxY + FrameNameNoteAboveBBoxClearanceM;
            sheetY = nameAnchorY - FrameLengthDimensionBelowNameOffsetM;

            var minDimY = maxY + FrameLengthDimensionMinAboveTopEdgeM;
            if (sheetY < minDimY)
            {
                sheetY = minDimY;
            }

            return true;
        }

        private static bool TryGetLargeOffsetLengthDimensionPosition(
            View view, Component2 component, MathUtility mathUtility, out double sheetX, out double sheetY)
        {
            sheetX = 0;
            sheetY = 0;
            var modelLine = TryGetFrameLengthModelLine(component);
            var line = modelLine as SketchLine;
            if (line == null || mathUtility == null) return false;
            try
            {
                // 先从草图坐标回到组件模型坐标，再投影到图纸；不能把草图 XY 当成模型 XY。
                var sketchToModel = modelLine.GetSketch().ModelToSketchTransform.IInverse();
                var points = new[] { line.GetStartPoint2(), line.GetEndPoint2() };
                var projected = new double[2][];
                for (var index = 0; index < points.Length; index++)
                {
                    if (!TryGetXYZFromSketchEndpoint(points[index], out var x, out var y, out var z)) return false;
                    var sketchPoint = mathUtility.CreatePoint(new[] { x, y, z }) as MathPoint;
                    var modelPoint = MultiplyMathPoint(sketchPoint, sketchToModel);
                    var coordinates = modelPoint?.ArrayData as double[];
                    if (coordinates == null || coordinates.Length < 3
                        || !TryProjectComponentLocalPointToSheet(view, component, mathUtility,
                            coordinates[0], coordinates[1], coordinates[2], out var px, out var py)) return false;
                    projected[index] = new[] { px, py };
                }
                if (!TryGetProjectedComponentBBoxSheetExtents(view, component, mathUtility,
                    out var minX, out _, out var minY, out _)) return false;

                GetReferenceDimensionPosition(projected[0][0], projected[0][1], projected[1][0], projected[1][1],
                    minX, minY, FrameDimensionOutsideBBoxMarginM, out sheetX, out sheetY);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                FaceToWallPreviewTrace.Error("工程图-基架长度投影异常", ex, component.Name2);
                return false;
            }
        }

        /// <summary>
        /// 计算基架名称注释锚点的图纸坐标。
        /// </summary>
        /// <remarks>
        /// 首选 <see cref="TryGetProjectedComponentBBoxSheetExtents"/>：水平取投影包围盒中心，垂直取顶边 + <see cref="FrameNameNoteAboveBBoxClearanceM"/>。
        /// 若包围盒不可用（偶见于几何未加载），则用 <see cref="TryGetProjectedComponentOriginSheetXY"/> 在原点投影上方 <see cref="FrameNameNoteOriginFallbackAboveOffsetM"/> 处放置，
        /// 避免多个实例同时落入 <see cref="TryGetFallbackDimensionSheetPosition"/> 的同一点。
        /// </remarks>
        private static bool TryGetFrameNameNoteSheetPosition(
            View view,
            Component2 component,
            MathUtility mathUtility,
            out double sheetX,
            out double sheetY)
        {
            sheetX = 0;
            sheetY = 0;

            if (TryGetProjectedComponentBBoxSheetExtents(
                    view,
                    component,
                    mathUtility,
                    out var minX,
                    out var maxX,
                    out var minY,
                    out var maxY))
            {
                sheetX = (minX + maxX) / 2d;
                sheetY = maxY + FrameNameNoteAboveBBoxClearanceM;
                return true;
            }

            if (TryGetProjectedComponentOriginSheetXY(view, component, mathUtility, out var ox, out var oy))
            {
                sheetX = ox;
                sheetY = oy + FrameNameNoteOriginFallbackAboveOffsetM;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 将装配空间中组件轴对齐包围盒的八个角点投影到图纸，得到图纸平面上的 XY 轴对齐范围。
        /// </summary>
        /// <remarks>
        /// <see cref="Component2.GetBox"/> 返回装配坐标系下的 Axis-Aligned Bounding Box（六个值 xmin,ymin,zmin,xmax,ymax,zmax）。
        /// 各角点再左乘 <see cref="View.ModelToViewTransform"/> 得到视图平面内的外包矩形，用于标注避让与名称放置。
        /// </remarks>
        private static bool TryGetProjectedComponentBBoxSheetExtents(
            View view,
            Component2 component,
            MathUtility mathUtility,
            out double minX,
            out double maxX,
            out double minY,
            out double maxY)
        {
            minX = minY = double.MaxValue;
            maxX = maxY = double.MinValue;

            if (view == null || component == null || mathUtility == null)
            {
                return false;
            }

            double[] box = null;
            try
            {
                box = component.GetBox(false, false) as double[];
            }
            catch
            {
                box = null;
            }

            if (box == null || box.Length < 6)
            {
                return false;
            }

            var mtView = view.ModelToViewTransform as MathTransform;
            if (mtView == null)
            {
                return false;
            }

            var x0 = box[0];
            var y0 = box[1];
            var z0 = box[2];
            var x1 = box[3];
            var y1 = box[4];
            var z1 = box[5];

            try
            {
                for (var ix = 0; ix < 2; ix++)
                {
                    var px = ix == 0 ? x0 : x1;
                    for (var iy = 0; iy < 2; iy++)
                    {
                        var py = iy == 0 ? y0 : y1;
                        for (var iz = 0; iz < 2; iz++)
                        {
                            var pz = iz == 0 ? z0 : z1;
                            var mpAsm = mathUtility.CreatePoint(new double[] { px, py, pz }) as MathPoint;
                            if (mpAsm == null)
                            {
                                continue;
                            }

                            var mpSheet = MultiplyMathPoint(mpAsm, mtView);
                            var arr = mpSheet?.ArrayData as double[];
                            if (arr == null || arr.Length < 2)
                            {
                                continue;
                            }

                            var sx = arr[0];
                            var sy = arr[1];
                            if (sx < minX) minX = sx;
                            if (sx > maxX) maxX = sx;
                            if (sy < minY) minY = sy;
                            if (sy > maxY) maxY = sy;
                        }
                    }
                }
            }
            catch
            {
                return false;
            }

            return minX <= maxX && minY <= maxY
                   && minX < double.MaxValue
                   && minY < double.MaxValue;
        }

        /// <summary>
        /// 将零件坐标系原点经装配实例变换到装配空间，再投影到图纸，得到图纸平面 XY。
        /// </summary>
        /// <remarks>
        /// 用于名称注释在包围盒算法失败时的弱依赖回退；原点不一定在几何中心，但通常仍能区分不同实例的大致水平位置。
        /// </remarks>
        private static bool TryGetProjectedComponentOriginSheetXY(
            View view,
            Component2 component,
            MathUtility mathUtility,
            out double sheetX,
            out double sheetY)
        {
            sheetX = 0;
            sheetY = 0;

            if (view == null || component == null || mathUtility == null)
            {
                return false;
            }

            MathTransform mtComp = null;
            try
            {
                mtComp = component.Transform2 as MathTransform;
            }
            catch
            {
                mtComp = null;
            }

            if (mtComp == null)
            {
                return false;
            }

            MathPoint mpZero = null;
            try
            {
                mpZero = mathUtility.CreatePoint(new double[] { 0d, 0d, 0d }) as MathPoint;
            }
            catch
            {
                mpZero = null;
            }

            if (mpZero == null)
            {
                return false;
            }

            var mpAsm = MultiplyMathPoint(mpZero, mtComp);
            if (mpAsm == null)
            {
                return false;
            }

            MathTransform mtView = null;
            try
            {
                mtView = view.ModelToViewTransform as MathTransform;
            }
            catch
            {
                mtView = null;
            }

            if (mtView == null)
            {
                return false;
            }

            var mpSheet = MultiplyMathPoint(mpAsm, mtView);
            var arr = mpSheet?.ArrayData as double[];
            if (arr == null || arr.Length < 2)
            {
                return false;
            }

            sheetX = arr[0];
            sheetY = arr[1];
            return true;
        }

        /// <summary>
        /// 若尺寸锚点的图纸 Y 落在组件投影包围盒的垂直范围内，则将 Y 下移到盒底边以下，减少与剖视/外形重叠。
        /// </summary>
        private static void TryClampHorizontalDimensionYOutsideBBox(
            View view,
            Component2 component,
            MathUtility mathUtility,
            ref double sheetY)
        {
            if (!TryGetProjectedComponentBBoxSheetExtents(
                    view,
                    component,
                    mathUtility,
                    out _,
                    out _,
                    out var minY,
                    out var maxY))
            {
                return;
            }

            if (sheetY <= maxY && sheetY >= minY)
            {
                sheetY = minY - FrameDimensionOutsideBBoxMarginM;
            }
        }

        /// <summary>
        /// SolidWorks 互操作中 MathPoint 可能暴露 MultiplyTransform 或 IMultiplyTransform，二者择一调用。
        /// </summary>
        private static MathPoint MultiplyMathPoint(MathPoint point, MathTransform transform)
        {
            if (point == null || transform == null)
            {
                return null;
            }

            try
            {
                var r = point.MultiplyTransform(transform);
                return r as MathPoint;
            }
            catch
            {
                // ignore
            }

            try
            {
                var r = point.IMultiplyTransform(transform);
                return r as MathPoint;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 将 SketchLine 端点返回对象解析为 XYZ（装配草图坐标系）。
        /// </summary>
        private static bool TryGetXYZFromSketchEndpoint(object endpointObj, out double x, out double y, out double z)
        {
            x = y = z = 0;

            if (endpointObj == null)
            {
                return false;
            }

            var mp = endpointObj as MathPoint;
            if (mp != null)
            {
                try
                {
                    var arr = mp.ArrayData as double[];
                    if (arr != null && arr.Length >= 3)
                    {
                        x = arr[0];
                        y = arr[1];
                        z = arr[2];
                        return true;
                    }
                }
                catch
                {
                    return false;
                }
            }

            var sp = endpointObj as SketchPoint;
            if (sp != null)
            {
                try
                {
                    x = sp.X;
                    y = sp.Y;
                    z = sp.Z;
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// 弱依赖回退：将注释放在视图外包矩形水平居中、略上方的图纸坐标处。
        /// </summary>
        /// <remarks>多个实例同时失败时会重合；应尽量使用几何推导的定位函数。</remarks>
        private static void TryGetFallbackDimensionSheetPosition(View targetView, out double sheetX, out double sheetY)
        {
            sheetX = 0;
            sheetY = 0;
            if (targetView == null)
            {
                return;
            }

            try
            {
                var outline = targetView.GetOutline() as double[];
                if (outline != null && outline.Length >= 4)
                {
                    sheetX = (outline[0] + outline[2]) / 2d;
                    sheetY = outline[1] - 0.006;
                }
            }
            catch
            {
                // ignore
            }
        }

        private static Sketch TryGetSketchFromCurrentSelection(ModelDoc2 drawingModel)
        {
            if (drawingModel == null)
            {
                return null;
            }

            try
            {
                var selMgr = drawingModel.SelectionManager as SelectionMgr;
                if (selMgr == null || selMgr.GetSelectedObjectCount2(-1) < 1)
                {
                    return null;
                }

                var selObj = selMgr.GetSelectedObject6(1, -1);
                if (selObj == null)
                {
                    return null;
                }

                var sketch = selObj as Sketch;
                if (sketch != null)
                {
                    return sketch;
                }

                var feat = selObj as Feature;
                if (feat != null)
                {
                    var spec = feat.GetSpecificFeature2();
                    return spec as Sketch;
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        /// <summary>
        /// 在「基架长度参考草图」内查找用作长度标注的直线：<see cref="FrameLengthReferenceSketchLineNames"/> 之一（通过 <see cref="SketchSegment.GetName"/> 比对）。
        /// </summary>
        /// <returns>按名称匹配；专用边线草图允许回退到唯一的直线，多条未知直线不猜测。</returns>
        private static SketchSegment TryGetFrameLengthReferenceSketchLineSegment(Sketch sketch, bool allowUniqueLine = false)
        {
            if (sketch == null)
            {
                return null;
            }

            object segmentsObj = null;
            try
            {
                segmentsObj = sketch.GetSketchSegments();
            }
            catch
            {
                return null;
            }

            var segments = segmentsObj as object[];
            if (segments == null || segments.Length == 0)
            {
                return null;
            }

            var lines = new List<SketchSegment>();
            var namedLines = new Dictionary<string, SketchSegment>(StringComparer.OrdinalIgnoreCase);
            foreach (var segObj in segments)
            {
                // 避免与 System.Object.GetType 冲突；仅用 SketchLine 筛选直线。
                var line = segObj as SketchLine;
                if (line == null)
                {
                    continue;
                }

                var segment = line as SketchSegment;
                if (segment == null)
                {
                    continue;
                }

                lines.Add(segment);

                string entityName = null;
                try
                {
                    entityName = segment.GetName();
                }
                catch
                {
                    entityName = null;
                }

                if (string.IsNullOrWhiteSpace(entityName))
                {
                    continue;
                }

                entityName = entityName.Trim();

                namedLines[entityName] = segment;
            }

            // 固定名称优先级，不依赖 COM 枚举顺序。改名后的专用边线草图仅在直线唯一时回退。
            foreach (var candidate in FrameLengthReferenceSketchLineNames)
            {
                if (namedLines.TryGetValue(candidate, out var segment))
                {
                    return segment;
                }
            }

            return allowUniqueLine && lines.Count == 1 ? lines[0] : null;
        }

        /// <summary>
        /// 构造工程图中按名称选中<strong>零部件特征</strong>（草图、原点等）时 <c>SelectByID2</c> 所需的标识字符串。
        /// </summary>
        /// <remarks>
        /// 格式：<c>{特征显示名}@{总装视图根实例}@{视图名}/{组件完整选择路径}</c>。
        /// 组件完整选择路径优先使用 <see cref="Component2.GetSelectByIDString"/>，以保留
        /// 「基架实例@总装/绝缘子实例@基架装配」等全部嵌套层级；仅在 API 无法返回时
        /// 才回退到基于 <see cref="Component2.Name2"/> 的旧格式。
        /// </remarks>
        private static string BuildComponentFeatureSelectById(
            string featureVisibleName,
            Component2 component,
            View targetView,
            string cabinetTitleWithoutExt)
        {
            if (component == null || targetView == null)
            {
                return string.Empty;
            }

            var viewName = targetView.Name;
            var drawingViewRootInstanceName = ResolveDrawingViewRootInstanceName(
                targetView.RootDrawingComponent?.Name, viewName, cabinetTitleWithoutExt);
            string componentSelectId = null;
            try
            {
                componentSelectId = component.GetSelectByIDString();
            }
            catch
            {
                componentSelectId = null;
            }

            if (!string.IsNullOrWhiteSpace(featureVisibleName)
                && !string.IsNullOrWhiteSpace(drawingViewRootInstanceName)
                && !string.IsNullOrWhiteSpace(viewName)
                && !string.IsNullOrWhiteSpace(componentSelectId))
            {
                return $"{featureVisibleName}@{drawingViewRootInstanceName}@{viewName}/{componentSelectId}";
            }

            // 旧模板/未完全解析组件的保守回退；顶层组件仍可按原格式选中。
            var name2 = component?.Name2 ?? string.Empty;
            string parentInst;
            string leafPath;

            var slashIdx = name2.IndexOf('/');
            if (slashIdx >= 0)
            {
                parentInst = name2.Substring(0, slashIdx);
                leafPath = name2.Substring(slashIdx + 1);
            }
            else
            {
                parentInst = drawingViewRootInstanceName;
                leafPath = name2;
            }

            var mid = $"{viewName}/{leafPath}";
            return $"{featureVisibleName}@{parentInst}@{mid}@{cabinetTitleWithoutExt}";
        }

        /// <summary>
        /// 工程图视图中顶层装配实例名：<c>{装配体基名}-{实例后缀}</c>。
        /// </summary>
        /// <remarks>
        /// 桥身视图 <see cref="TargetDrawingViewName"/> 下按设计场景区分：
        /// 面对面 → <c>5C12345.0001-3</c>，面对墙 → <c>5C12345.0001-4</c>。
        /// 其它视图仍从视图名末尾数字解析实例后缀。
        /// </remarks>
        private static string GetDrawingViewRootInstanceName(string viewName, string cabinetTitleWithoutExt)
        {
            if (string.IsNullOrWhiteSpace(cabinetTitleWithoutExt))
            {
                return string.Empty;
            }

            var suffix = string.Equals(viewName, TargetDrawingViewName, StringComparison.Ordinal)
                ? _drawingViewRootInstanceSuffixOverride ?? TryParseDrawingViewInstanceSuffix(viewName)
                : TryParseDrawingViewInstanceSuffix(viewName);
            return $"{cabinetTitleWithoutExt}-{suffix}";
        }

        internal static string ResolveDrawingViewRootInstanceName(
            string actualRootName, string viewName, string cabinetTitleWithoutExt)
        {
            // 视图名称可重命名，且视图序号与其装配根实例序号并非一一对应。
            // 所有草图线、点和原点必须使用该视图实际的 DrawingComponent 名称。
            return !string.IsNullOrWhiteSpace(actualRootName)
                ? actualRootName.Trim()
                : GetDrawingViewRootInstanceName(viewName, cabinetTitleWithoutExt);
        }

        /// <summary>
        /// 解析桥身视图 <see cref="TargetDrawingViewName"/> 下总装配体的工程图实例后缀。
        /// </summary>
        private static int ResolveTargetDrawingViewRootInstanceSuffix(
            ModelDoc2 assemblyModel,
            string cabinetTitleWithoutExt)
        {
            return IsFaceToWallCabinetAssembly(assemblyModel, cabinetTitleWithoutExt)
                ? FaceToWallTargetDrawingViewRootInstanceSuffix
                : FaceToFaceTargetDrawingViewRootInstanceSuffix;
        }

        /// <summary>
        /// 判断当前柜体总装配体是否属于面对墙场景。
        /// </summary>
        private static bool IsFaceToWallCabinetAssembly(ModelDoc2 assemblyModel, string cabinetTitleWithoutExt)
        {
            if (!string.IsNullOrWhiteSpace(cabinetTitleWithoutExt)
                && cabinetTitleWithoutExt.IndexOf("面对墙", StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            var designScenarioText = ReadAssemblyCustomProperty(
                assemblyModel,
                DesignScenarioCustomPropertyName);
            if (string.Equals(
                    designScenarioText,
                    DesignScenarioDefaults.GetDisplayName(DesignScenarioId.FaceToWall),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(
                    designScenarioText,
                    DesignScenarioDefaults.GetDisplayName(DesignScenarioId.FaceToFace),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return FaceToWallCabinetOrientationDefaults.TryParse(
                ReadAssemblyCustomProperty(
                    assemblyModel,
                    FaceToWallCabinetOrientationDefaults.CabinetOrientationCustomPropertyName),
                out _);
        }

        /// <summary>
        /// 判断当前总装配体是否为面对面场景；面对墙和侧对侧均明确排除。
        /// </summary>
        private static bool IsFaceToFaceCabinetAssembly(
            ModelDoc2 assemblyModel,
            string cabinetTitleWithoutExt)
        {
            if (IsFaceToWallCabinetAssembly(assemblyModel, cabinetTitleWithoutExt))
            {
                return false;
            }

            return DesignScenarioDefaults.ParseScenarioId(ReadAssemblyCustomProperty(
                       assemblyModel,
                       DesignScenarioCustomPropertyName))
                   == DesignScenarioId.FaceToFace;
        }

        /// <summary>
        /// 读取装配体自定义属性文本。
        /// </summary>
        private static string ReadAssemblyCustomProperty(ModelDoc2 assemblyModel, string propertyName)
        {
            if (assemblyModel == null || string.IsNullOrWhiteSpace(propertyName))
            {
                return null;
            }

            try
            {
                var value = assemblyModel.GetCustomInfoValue(string.Empty, propertyName);
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 从工程图视图名称解析实例后缀（取名称末尾连续数字，缺省为 1）。
        /// </summary>
        private static int TryParseDrawingViewInstanceSuffix(string viewName)
        {
            if (string.IsNullOrWhiteSpace(viewName))
            {
                return 1;
            }

            var end = viewName.Length - 1;
            var start = end;
            while (start >= 0 && char.IsDigit(viewName, start))
            {
                start--;
            }

            if (start < end
                && int.TryParse(
                    viewName.Substring(start + 1, end - start),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var suffix)
                && suffix > 0)
            {
                return suffix;
            }

            return 1;
        }

        /// <summary>
        /// 为原点等特征生成多种可能的 <c>SelectByID2</c> 首参（不同语言包/视图层级下录制结果可能不同）。
        /// </summary>
        private static IEnumerable<string> EnumerateComponentOriginSelectCandidates(
            string originFeatureName,
            Component2 component,
            View targetView,
            string cabinetTitleWithoutExt)
        {
            if (string.IsNullOrWhiteSpace(originFeatureName) || component == null)
            {
                yield break;
            }

            yield return BuildComponentFeatureSelectById(
                originFeatureName,
                component,
                targetView,
                cabinetTitleWithoutExt);

            var name2 = component.Name2 ?? string.Empty;
            var slashIdx = name2.IndexOf('/');
            var leafPath = slashIdx >= 0 ? name2.Substring(slashIdx + 1) : name2;
            if (string.IsNullOrWhiteSpace(leafPath))
            {
                yield break;
            }

            // 部分环境：原点@零件实例名@视图名@工程图名（无父实例与「视图名/路径」段）
            yield return $"{originFeatureName}@{leafPath}@{targetView.Name}@{cabinetTitleWithoutExt}";
        }

        /// <summary>
        /// 在工程图中尝试选中指定零部件的<strong>坐标系原点</strong>（子装配在视图中常选不中，此时应改用 <see cref="TrySelectAnchoredSketchPointInDrawing"/>）。
        /// </summary>
        private static bool TrySelectComponentOriginInDrawing(
            ModelDoc2 drawingModel,
            Component2 component,
            View targetView,
            string cabinetTitleWithoutExt)
        {
            if (drawingModel == null || component == null || targetView == null)
            {
                return false;
            }

            foreach (var originName in ComponentOriginEntityNames)
            {
                foreach (var selId in EnumerateComponentOriginSelectCandidates(
                             originName,
                             component,
                             targetView,
                             cabinetTitleWithoutExt))
                {
                    foreach (var typeName in ComponentOriginSelectTypes)
                    {
                        try
                        {
                            if (drawingModel.Extension.SelectByID2(
                                    selId,
                                    typeName,
                                    0,
                                    0,
                                    0,
                                    false,
                                    0,
                                    null,
                                    0) && GetSelectionObjectCount(drawingModel) == 1
                                    && IsSelectedDimensionPoint(drawingModel, 1))
                            {
                                return true;
                            }
                        }
                        catch
                        {
                            // 尝试下一 typeName
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 只接受可标注的点对象，防止原点特征选择成功被误当成点选择成功。
        /// </summary>
        private static bool TrySelectInsulatorDimensionAnchorInDrawing(
            ModelDoc2 drawingModel,
            Component2 insulatorComponent,
            View targetView,
            string cabinetTitleWithoutExt,
            bool useOrigin)
        {
            var selected = useOrigin
                ? TrySelectComponentOriginInDrawing(drawingModel, insulatorComponent, targetView, cabinetTitleWithoutExt)
                : TrySelectAnchoredSketchPointInDrawing(
                    drawingModel, insulatorComponent, targetView, cabinetTitleWithoutExt,
                    InsulatorDrawingAnchorSketchName, InsulatorDrawingAnchorPointNames, false, true);
            return selected && GetSelectionObjectCount(drawingModel) == 1
                && IsSelectedDimensionPoint(drawingModel, 1);
        }

        private static bool IsSelectedDimensionPoint(ModelDoc2 drawingModel, int index)
        {
            var selection = drawingModel.SelectionManager as SelectionMgr;
            if (selection == null || index < 1 || index > selection.GetSelectedObjectCount2(-1)) return false;
            var selected = selection.GetSelectedObject6(index, -1);
            return selected is SketchPoint || selected is Vertex;
        }

        /// <summary>
        /// 在工程图中选中指定草图内、名称匹配的 <see cref="SketchPoint"/>（与线段选中方式相同：<c>点1@草图完整标识</c>）。
        /// </summary>
        private static bool TrySelectAnchoredSketchPointInDrawing(
            ModelDoc2 drawingModel,
            Component2 component,
            View targetView,
            string cabinetTitleWithoutExt,
            string sketchFeatureName,
            string[] pointEntityNames,
            bool appendToSelection,
            bool clearSelectionFirst)
        {
            if (drawingModel == null || component == null || targetView == null
                || string.IsNullOrWhiteSpace(sketchFeatureName)
                || pointEntityNames == null || pointEntityNames.Length == 0)
            {
                return false;
            }

            if (clearSelectionFirst)
            {
                drawingModel.ClearSelection2(true);
            }

            foreach (var pointName in pointEntityNames)
            {
                var selId = BuildFrameSketchSegmentSelectById(
                    pointName,
                    component,
                    targetView,
                    cabinetTitleWithoutExt,
                    sketchFeatureName);

                foreach (var typeName in SketchPointSelectTypes)
                {
                    try
                    {
                        if (drawingModel.Extension.SelectByID2(
                                selId,
                                typeName,
                                0,
                                0,
                                0,
                                appendToSelection,
                                0,
                                null,
                                0) && IsSelectedDimensionPoint(drawingModel, GetSelectionObjectCount(drawingModel)))
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        // 尝试下一 typeName
                    }

                    if (!appendToSelection)
                    {
                        drawingModel.ClearSelection2(true);
                    }
                }
            }

            if (appendToSelection)
            {
                return false;
            }

            var sketchSelectId = BuildFrameSketchSelectById(
                component,
                targetView,
                cabinetTitleWithoutExt,
                sketchFeatureName);

            var sketchSelected = drawingModel.Extension.SelectByID2(
                sketchSelectId,
                "SKETCH",
                0,
                0,
                0,
                false,
                0,
                null,
                0);

            var sketch = (sketchSelected ? TryGetSketchFromCurrentSelection(drawingModel) : null)
                ?? TryGetComponentSketchInDrawing(component, targetView, sketchFeatureName);
            var skPoint = TryFindNamedSketchPointInSketch(sketch, pointEntityNames);
            if (skPoint == null)
            {
                drawingModel.ClearSelection2(true);
                return false;
            }

            drawingModel.ClearSelection2(true);
            try
            {
                var selection = drawingModel.SelectionManager as SelectionMgr;
                var selectData = selection.CreateSelectData();
                selectData.View = targetView;
                return skPoint.Select4(false, selectData)
                    && GetSelectionObjectCount(drawingModel) == 1
                    && IsSelectedDimensionPoint(drawingModel, 1);
            }
            catch (Exception ex)
            {
                FaceToWallPreviewTrace.Error("工程图-草图点选择异常", ex,
                    component.Name2 + "；草图=" + sketchFeatureName);
                try
                {
                    drawingModel.ClearSelection2(true);
                }
                catch
                {
                    // ignore
                }

                return false;
            }
        }

        /// <summary>
        /// 在草图内按显示名查找第一个 <see cref="SketchPoint"/>。
        /// </summary>
        private static SketchPoint TryFindNamedSketchPointInSketch(Sketch sketch, string[] pointNames)
        {
            if (sketch == null || pointNames == null || pointNames.Length == 0)
            {
                return null;
            }

            object ptsObj = null;
            try
            {
                ptsObj = sketch.GetSketchPoints2();
            }
            catch
            {
                try
                {
                    ptsObj = sketch.GetSketchPoints();
                }
                catch
                {
                    ptsObj = null;
                }
            }

            var pts = ptsObj as object[];
            if (pts == null || pts.Length == 0)
            {
                return null;
            }

            var sketchPoints = new List<SketchPoint>();
            foreach (var o in pts)
            {
                if (o is SketchPoint sp)
                {
                    sketchPoints.Add(sp);
                }
            }

            if (sketchPoints.Count == 0)
            {
                return null;
            }

            foreach (var pt in sketchPoints)
            {
                var name = TryGetSketchEntityName(pt);

                if (!string.IsNullOrWhiteSpace(name))
                {
                    var trimmed = name.Trim();
                    foreach (var candidate in pointNames)
                    {
                        if (string.Equals(trimmed, candidate, StringComparison.OrdinalIgnoreCase))
                        {
                            return pt;
                        }
                    }
                }
            }

            // 专用「仅一点」草图：不依赖点名称
            if (sketchPoints.Count == 1)
            {
                return sketchPoints[0];
            }

            // 与零部件原点重合的点（距离²容差，米制；约 0.1mm 量级）
            const double originTol2 = 1e-8;
            SketchPoint nearestOrigin = null;
            var bestMag = double.MaxValue;
            foreach (var pt in sketchPoints)
            {
                double x = 0, y = 0, z = 0;
                try
                {
                    x = pt.X;
                    y = pt.Y;
                    z = pt.Z;
                }
                catch
                {
                    continue;
                }

                var m = x * x + y * y + z * z;
                if (m < bestMag)
                {
                    bestMag = m;
                    nearestOrigin = pt;
                }
            }

            if (nearestOrigin != null && bestMag <= originTol2)
            {
                return nearestOrigin;
            }

            return null;
        }

        /// <summary>
        /// 草图点等实体在互操作中可能实现为 <see cref="IEntity"/>，从而可取得与特征树一致的名称。
        /// </summary>
        private static string TryGetSketchEntityName(SketchPoint sketchPoint)
        {
            if (sketchPoint == null)
            {
                return null;
            }

            try
            {
                dynamic d = sketchPoint;
                var n = d.Name;
                if (n != null)
                {
                    return Convert.ToString(n, CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        /// <summary>
        /// 构造工程图中按名称选中草图<strong>线段</strong>所需的 <c>SelectByID2</c> 完整标识：<c>{线段名}@{草图标识}</c>。
        /// </summary>
        private static string BuildFrameSketchSegmentSelectById(
            string lineEntityName,
            Component2 component,
            View targetView,
            string cabinetTitleWithoutExt,
            string sketchFeatureName)
        {
            var sketchId = BuildFrameSketchSelectById(
                component,
                targetView,
                cabinetTitleWithoutExt,
                sketchFeatureName);
            return $"{lineEntityName}@{sketchId}";
        }

        /// <summary>
        /// 将当前选择集中指定序号（1 起）的选中对象解析为 <see cref="SketchSegment"/>。
        /// </summary>
        private static SketchSegment TryGetSelectedSketchSegmentByIndex(ModelDoc2 drawingModel, int oneBasedIndex)
        {
            if (drawingModel == null || oneBasedIndex < 1)
            {
                return null;
            }

            try
            {
                var selMgr = drawingModel.SelectionManager as SelectionMgr;
                if (selMgr == null || selMgr.GetSelectedObjectCount2(-1) < oneBasedIndex)
                {
                    return null;
                }

                var obj = selMgr.GetSelectedObject6(oneBasedIndex, -1);
                if (obj is SketchSegment segment)
                {
                    return segment;
                }

                if (obj is SketchLine line)
                {
                    return line as SketchSegment;
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static int GetSelectionObjectCount(ModelDoc2 drawingModel)
        {
            try
            {
                var selMgr = drawingModel?.SelectionManager as SelectionMgr;
                return selMgr?.GetSelectedObjectCount2(-1) ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 将当前选择集中首个选中对象解析为 <see cref="SketchSegment"/>（工程图投影草图线）。
        /// </summary>
        private static SketchSegment TryGetFirstSelectedSketchSegment(ModelDoc2 drawingModel)
        {
            return TryGetSelectedSketchSegmentByIndex(drawingModel, 1);
        }

        /// <summary>
        /// 在工程图中选中指定名称草图内需标注的直线（<see cref="FrameLengthReferenceSketchLineNames"/>）。
        /// </summary>
        /// <param name="appendToSelection">为 true 时在已有选择集上追加（用于已选中原点后再选边线）。</param>
        /// <param name="clearSelectionFirst">为 true 时先清空选择集。</param>
        /// <returns>选中成功且得到线段引用时返回 true。</returns>
        /// <remarks>追加模式下不启用「整草图选中再匹配直线」的回退，以免破坏多选。</remarks>
        private static bool TrySelectFrameSketchLineInDrawing(
            ModelDoc2 drawingModel,
            Component2 component,
            View targetView,
            string cabinetTitleWithoutExt,
            string sketchFeatureName,
            bool appendToSelection,
            bool clearSelectionFirst,
            out SketchSegment lineSegment)
        {
            lineSegment = null;
            if (drawingModel == null || component == null || targetView == null
                || string.IsNullOrWhiteSpace(sketchFeatureName))
            {
                return false;
            }

            if (clearSelectionFirst)
            {
                drawingModel.ClearSelection2(true);
            }

            foreach (var lineName in FrameLengthReferenceSketchLineNames)
            {
                var selId = BuildFrameSketchSegmentSelectById(
                    lineName,
                    component,
                    targetView,
                    cabinetTitleWithoutExt,
                    sketchFeatureName);

                foreach (var typeName in FrameLengthSketchSegmentSelectTypes)
                {
                    try
                    {
                        if (drawingModel.Extension.SelectByID2(
                                selId,
                                typeName,
                                0,
                                0,
                                0,
                                appendToSelection,
                                0,
                                null,
                                0))
                        {
                            var n = GetSelectionObjectCount(drawingModel);
                            if (n > 0)
                            {
                                lineSegment = TryGetSelectedSketchSegmentByIndex(drawingModel, n);
                            }

                            if (lineSegment != null)
                            {
                                return true;
                            }
                        }
                    }
                    catch
                    {
                        // 尝试下一 typeName
                    }

                    if (!appendToSelection)
                    {
                        drawingModel.ClearSelection2(true);
                    }
                }
            }

            if (appendToSelection)
            {
                return false;
            }

            var sketchSelectId = BuildFrameSketchSelectById(
                component,
                targetView,
                cabinetTitleWithoutExt,
                sketchFeatureName);

            var sketchSelected = drawingModel.Extension.SelectByID2(
                sketchSelectId,
                "SKETCH",
                0,
                0,
                0,
                false,
                0,
                null,
                0);

            Sketch sketch = null;
            if (sketchSelected)
            {
                sketch = TryGetSketchFromCurrentSelection(drawingModel);
            }

            sketch = sketch ?? TryGetComponentSketchInDrawing(component, targetView, sketchFeatureName);

            if (sketch != null)
            {
                lineSegment = TryGetFrameLengthReferenceSketchLineSegment(sketch,
                    sketchFeatureName == FrameEdgeReferenceSketchName || sketchFeatureName == BaseEdgeReferenceSketchName);
            }

            if (lineSegment == null)
            {
                drawingModel.ClearSelection2(true);
                return false;
            }

            drawingModel.ClearSelection2(true);
            try
            {
                var selection = drawingModel.SelectionManager as SelectionMgr;
                var selectData = selection.CreateSelectData();
                selectData.View = targetView;
                if (!lineSegment.Select4(false, selectData) || GetSelectionObjectCount(drawingModel) != 1)
                {
                    lineSegment = null;
                    return false;
                }
                lineSegment = TryGetSelectedSketchSegmentByIndex(drawingModel, 1);
                return lineSegment != null;
            }
            catch (Exception ex)
            {
                FaceToWallPreviewTrace.Error("工程图-草图边线选择异常", ex,
                    component.Name2 + "；草图=" + sketchFeatureName);
                lineSegment = null;
                try
                {
                    drawingModel.ClearSelection2(true);
                }
                catch
                {
                    // ignore
                }

                return false;
            }
        }

        private static Sketch TryGetComponentSketchInDrawing(
            Component2 component, View targetView, string sketchFeatureName)
        {
            try
            {
                // Component2.FeatureByName 保留具体装配实例上下文，避免两个拐角共用
                // 模型文件时，把一侧的参考几何误用于另一侧。
                var feature = component.FeatureByName(sketchFeatureName);
                var drawingFeature = feature == null ? null : targetView.GetCorresponding(feature) as Feature;
                var sketch = drawingFeature?.GetSpecificFeature2() as Sketch;
                if (sketch == null)
                {
                    FaceToWallPreviewTrace.Warning("工程图-参考草图映射失败",
                        "视图=" + targetView.Name + "；组件=" + component.Name2 + "；草图=" + sketchFeatureName);
                }
                return sketch;
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                FaceToWallPreviewTrace.Error("工程图-参考草图映射异常", ex,
                    "视图=" + targetView.Name + "；组件=" + component.Name2 + "；草图=" + sketchFeatureName);
                return null;
            }
        }

        private static SketchSegment TryGetFrameLengthModelLine(Component2 component)
        {
            if (component == null) return null;
            try
            {
                var model = component.GetModelDoc2() as ModelDoc2;
                var sketch = FindFeatureByName(model, FrameLengthReferenceSketchName)?.GetSpecificFeature2() as Sketch;
                return TryGetFrameLengthReferenceSketchLineSegment(sketch, allowUniqueLine: true);
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                FaceToWallPreviewTrace.Error("工程图-读取基架长度参考线异常", ex, component.Name2);
                return null;
            }
        }

        private static bool TrySelectFrameLengthModelLineInDrawing(
            ModelDoc2 drawingModel, Component2 component, View targetView, out SketchSegment lineSegment)
        {
            lineSegment = null;
            var modelLine = TryGetFrameLengthModelLine(component);
            if (drawingModel == null || modelLine == null || targetView == null) return false;
            try
            {
                drawingModel.ClearSelection2(true);
                var assemblyLine = component.GetCorresponding(modelLine) as SketchSegment;
                var drawingLine = assemblyLine == null ? null : targetView.GetCorresponding(assemblyLine) as SketchSegment;
                var selection = drawingModel.SelectionManager as SelectionMgr;
                var data = selection.CreateSelectData();
                data.View = targetView;
                // 直接选实体，不依赖草图线的语言、序号或视图中的特征路径。
                foreach (var candidate in new[] { drawingLine, assemblyLine })
                {
                    drawingModel.ClearSelection2(true);
                    if (candidate != null && candidate.Select4(false, data)
                        && GetSelectionObjectCount(drawingModel) == 1
                        && TryGetSelectedSketchSegmentByIndex(drawingModel, 1) != null)
                    {
                        // 选择集保留工程图实体；定位计算使用原模型线，避免把已投影坐标再次变换。
                        lineSegment = modelLine;
                        return true;
                    }
                }
                return false;
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                FaceToWallPreviewTrace.Error("工程图-基架长度实体选取异常", ex,
                    "视图=" + targetView.Name + "；组件=" + component.Name2);
                return false;
            }
        }

        /// <summary>
        /// 在工程图中选中「基架长度参考草图」内需标注长度的线段（优先 <c>SelectByID2</c> 按线段名选中，失败则选中草图并按 <see cref="FrameLengthReferenceSketchLineNames"/> 匹配后 <see cref="SketchSegment.Select4"/>）。
        /// </summary>
        /// <returns>选中成功且得到线段引用时返回 true（线段留在选择集中，供 <c>AddDimension2</c> 使用）。</returns>
        private static bool TrySelectFrameLengthReferenceLineInDrawing(
            ModelDoc2 drawingModel,
            Component2 component,
            View targetView,
            string cabinetTitleWithoutExt,
            out SketchSegment lineSegment)
        {
            if (targetView != null && string.Equals(targetView.Name, LargeOffsetDrawingViewName, StringComparison.Ordinal)
                && TrySelectFrameLengthModelLineInDrawing(drawingModel, component, targetView, out lineSegment))
            {
                return true;
            }

            return TrySelectFrameSketchLineInDrawing(
                drawingModel,
                component,
                targetView,
                cabinetTitleWithoutExt,
                FrameLengthReferenceSketchName,
                appendToSelection: false,
                clearSelectionFirst: true,
                out lineSegment);
        }

        /// <summary>
        /// 构造工程图中 <c>SelectByID2(..., "SKETCH", …)</c> 所需的完整草图标识字符串（与宏录制格式一致）。
        /// </summary>
        private static string BuildFrameSketchSelectById(
            Component2 component,
            View targetView,
            string cabinetTitleWithoutExt,
            string sketchFeatureName)
        {
            return BuildComponentFeatureSelectById(sketchFeatureName, component, targetView, cabinetTitleWithoutExt);
        }

        /// <summary>
        /// 返回视图所引用的模型文档（零件或装配体）。
        /// </summary>
        private static ModelDoc2 TryGetViewReferencedDocument(View view)
        {
            if (view == null)
            {
                return null;
            }

            try
            {
                return view.ReferencedDocument as ModelDoc2;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 列出当前工程图文档中的图纸名称；用于在多图纸工程中遍历查找目标视图。
        /// </summary>
        private static List<string> EnumerateSheetNames(DrawingDoc drawingDoc)
        {
            var result = new List<string>();
            if (drawingDoc == null)
            {
                return result;
            }

            try
            {
                var raw = drawingDoc.GetSheetNames();
                if (raw is string[] strArr)
                {
                    result.AddRange(strArr.Where(s => !string.IsNullOrWhiteSpace(s)));
                    return result;
                }

                if (raw is object[] objArr)
                {
                    foreach (var item in objArr)
                    {
                        var s = item as string;
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            result.Add(s);
                        }
                    }

                    return result;
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                var current = drawingDoc.GetCurrentSheet() as Sheet;
                var name = current?.GetName();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.Add(name);
                }
            }
            catch
            {
                // ignore
            }

            return result;
        }

        /// <summary>
        /// 根据柜体工程图所在目录推断「项目根」。
        /// </summary>
        /// <remarks>
        /// 典型布局：<c>…\项目根\柜体基座\柜体装配.SLDDRW</c>；此时返回「项目根」（上级目录）。
        /// 若工程图不在名为 <see cref="CabinetBaseFolderName"/> 的文件夹下，则保守返回工程图所在目录，避免误解析。
        /// </remarks>
        private static string TryGetProjectRootFromDrawingPath(string drawingPath)
        {
            if (string.IsNullOrWhiteSpace(drawingPath))
            {
                return null;
            }

            var drawingDir = Path.GetDirectoryName(drawingPath);
            if (string.IsNullOrWhiteSpace(drawingDir))
            {
                return null;
            }

            var leaf = Path.GetFileName(drawingDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(leaf, CabinetBaseFolderName, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(drawingDir);
            }

            return drawingDir;
        }

        /// <summary>
        /// 基架在工程图中的可见名称：使用装配体磁盘路径的<strong>直接父文件夹名</strong>。
        /// </summary>
        /// <param name="component">基架子装配实例。</param>
        /// <param name="ordinalFallback">父文件夹名为「基架」根目录本身（无法区分多实例）时的退回序号。</param>
        /// <remarks>
        /// 示例：<c>D:\项目\基架\基架1\xxx.SLDASM</c> → 显示 <c>基架1</c>；同一文件夹内多次插入的实例显示相同名称。
        /// 若文件位于 <c>…\基架\xxx.SLDASM</c>（父目录名为 <see cref="FrameOutputFolderName"/>），则退回 <c>基架{n}</c>，其中 <c>n</c> 为视图内排序序号。
        /// </remarks>
        private static string GetFrameDisplayNameFromFolder(Component2 component, int ordinalFallback)
        {
            if (component == null || ordinalFallback <= 0)
            {
                return string.Empty;
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
                return FormatFallbackFrameOrdinalName(ordinalFallback);
            }

            var parentDir = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(parentDir))
            {
                return FormatFallbackFrameOrdinalName(ordinalFallback);
            }

            var leafFolder = Path.GetFileName(parentDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(leafFolder))
            {
                return FormatFallbackFrameOrdinalName(ordinalFallback);
            }

            if (string.Equals(leafFolder, FrameOutputFolderName, StringComparison.OrdinalIgnoreCase))
            {
                return FormatFallbackFrameOrdinalName(ordinalFallback);
            }

            return leafFolder;
        }

        /// <summary>
        /// 当无法使用文件夹名时的可见名称：<c>基架</c> + 序号。
        /// </summary>
        private static string FormatFallbackFrameOrdinalName(int ordinalFallback)
        {
            return ordinalFallback > 0 ? $"基架{ordinalFallback}" : string.Empty;
        }

        /// <summary>
        /// 从项目「基座」目录中收集总装配体顶层、且自身子树包含绝缘子的基座装配体。
        /// </summary>
        /// <remarks>
        /// 面对面场景先通过 <see cref="FaceToFaceTopLevelBaseOwnershipResolver"/> 唯一定位 A柜、B柜和两个独立基座，
        /// 再按组件实际 Box 中心完成 A/B 归属校验。基架和柜体不会作为基座回退候选。
        /// </remarks>
        private static List<Component2> CollectBaseComponentsForEdgeAnnotations(
            ModelDoc2 assemblyModel,
            string baseRootPath,
            bool requireFaceToFaceOwnership)
        {
            var result = new List<Component2>();
            var asmDoc = assemblyModel as AssemblyDoc;
            if (asmDoc == null)
            {
                return result;
            }

            object[] topLevel = null;
            try
            {
                topLevel = asmDoc.GetComponents(true) as object[];
            }
            catch
            {
                topLevel = null;
            }

            if (topLevel == null || topLevel.Length == 0)
            {
                return result;
            }

            var insulators = CollectInsulatorComponents(asmDoc);

            foreach (var item in topLevel)
            {
                var component = item as Component2;
                if (component == null || IsComponentSuppressed(component))
                {
                    continue;
                }

                if (!IsBaseAssemblyComponent(component, baseRootPath)
                    || !insulators.Any(insulator => IsDescendantComponent(insulator, component)))
                {
                    continue;
                }

                result.Add(component);
            }

            if (!requireFaceToFaceOwnership)
            {
                return result;
            }

            return OrderFaceToFaceBaseComponentsByOwnership(assemblyModel, result);
        }

        /// <summary>
        /// 收集包含“基座长度参考草图”或“基座开孔参考草图”的顶层基座装配实例。
        /// </summary>
        private static List<Component2> CollectBaseComponentsForReferenceDimensions(
            ModelDoc2 assemblyModel,
            string baseRootPath,
            bool requireFaceToFaceOwnership)
        {
            var result = new List<Component2>();
            var asmDoc = assemblyModel as AssemblyDoc;
            if (asmDoc == null)
            {
                return result;
            }

            object[] topLevel;
            try
            {
                topLevel = asmDoc.GetComponents(true) as object[];
            }
            catch
            {
                topLevel = null;
            }

            if (topLevel == null || topLevel.Length == 0)
            {
                return result;
            }

            foreach (var item in topLevel)
            {
                var component = item as Component2;
                if (component == null || IsComponentSuppressed(component)
                    || !IsBaseAssemblyComponent(component, baseRootPath))
                {
                    continue;
                }

                ModelDoc2 componentModel;
                try
                {
                    componentModel = component.IGetModelDoc() as ModelDoc2
                                     ?? component.GetModelDoc2() as ModelDoc2;
                }
                catch
                {
                    componentModel = null;
                }

                if (componentModel == null
                    || (FindFeatureByName(componentModel, BaseLengthReferenceSketchName) == null
                        && FindFeatureByName(componentModel, BaseOpeningReferenceSketchName) == null))
                {
                    continue;
                }

                result.Add(component);
            }

            return requireFaceToFaceOwnership
                ? OrderFaceToFaceBaseComponentsByOwnership(assemblyModel, result)
                : result;
        }

        /// <summary>
        /// 面对面场景按顶层 A/B 柜及组件实际 Box 中心校验两个独立基座，并返回 A、B 顺序。
        /// </summary>
        private static List<Component2> OrderFaceToFaceBaseComponentsByOwnership(
            ModelDoc2 assemblyModel,
            IReadOnlyCollection<Component2> baseComponents)
        {
            if (assemblyModel == null || baseComponents == null || baseComponents.Count == 0)
            {
                return new List<Component2>();
            }

            try
            {
                var result = baseComponents.Where(component => component != null).ToList();
                var readOperations = new SolidWorksFaceToFaceFrameReadOperations(assemblyModel);
                var activeTopLevel = readOperations.GetTopLevelComponents()
                    .Where(component => component != null && !component.IsSuppressed)
                    .ToList();
                var baseInstanceIds = new HashSet<string>(
                    result.Select(component => component.Name2),
                    StringComparer.OrdinalIgnoreCase);
                var ordinaryBases = activeTopLevel
                    .Where(component => baseInstanceIds.Contains(component.InstanceId))
                    .ToList();
                var bCabinetBase = FaceToFaceTopLevelBaseOwnershipResolver.ResolveBCabinetBase(
                    activeTopLevel,
                    ordinaryBases);

                var bComponent = result.Single(component => string.Equals(
                    component.Name2,
                    bCabinetBase.InstanceId,
                    StringComparison.OrdinalIgnoreCase));
                var aComponent = result.Single(component => !string.Equals(
                    component.Name2,
                    bCabinetBase.InstanceId,
                    StringComparison.OrdinalIgnoreCase));
                return new List<Component2> { aComponent, bComponent };
            }
            catch
            {
                // 面对面拓扑不满足“顶层 A/B 柜 + 两个独立基座 + Box 唯一归属”时不猜测基座。
                return new List<Component2>();
            }
        }

        /// <summary>
        /// 判断顶层组件是否为基座：优先要求模型路径位于项目「基座」根目录下；
        /// 仅在无法解析基座根目录时，才以模型内存在「基座边线参考草图」作为保守识别条件。
        /// </summary>
        private static bool IsBaseAssemblyComponent(Component2 component, string baseRootPath)
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

            if (string.IsNullOrWhiteSpace(path)
                || !string.Equals(Path.GetExtension(path), ".SLDASM", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var normalizedBaseRoot = baseRootPath?.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (!string.IsNullOrWhiteSpace(normalizedBaseRoot))
            {
                return path.StartsWith(normalizedBaseRoot + "\\", StringComparison.OrdinalIgnoreCase);
            }

            ModelDoc2 componentModel = null;
            try
            {
                componentModel = component.IGetModelDoc() as ModelDoc2
                    ?? component.GetModelDoc2() as ModelDoc2;
            }
            catch
            {
                componentModel = null;
            }

            return componentModel != null
                && componentModel.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY
                && (FindFeatureByName(componentModel, BaseEdgeReferenceSketchName) != null
                    || FindFeatureByName(componentModel, BaseLengthReferenceSketchName) != null
                    || FindFeatureByName(componentModel, BaseOpeningReferenceSketchName) != null);
        }

        private static bool IsComponentSuppressed(Component2 component)
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

        private static Feature FindFeatureByName(ModelDoc2 model, string featureName)
        {
            if (model == null || string.IsNullOrWhiteSpace(featureName))
            {
                return null;
            }

            var feature = model.FirstFeature() as Feature;
            while (feature != null)
            {
                var match = FindFeatureByName(feature, featureName);
                if (match != null)
                {
                    return match;
                }

                feature = feature.GetNextFeature() as Feature;
            }

            return null;
        }

        private static Feature FindFeatureByName(Feature feature, string featureName)
        {
            if (feature == null)
            {
                return null;
            }

            if (string.Equals(feature.Name, featureName, StringComparison.OrdinalIgnoreCase))
            {
                return feature;
            }

            var child = feature.GetFirstSubFeature() as Feature;
            while (child != null)
            {
                var match = FindFeatureByName(child, featureName);
                if (match != null)
                {
                    return match;
                }

                child = child.GetNextSubFeature() as Feature;
            }

            return null;
        }

        /// <summary>
        /// 收集总装配体顶层中全部满足基架判定条件的 <see cref="Component2"/>。
        /// </summary>
        /// <param name="asmDoc">工程图视图所引用的柜体装配文档。</param>
        /// <param name="frameRootPath">
        /// <c>项目根\基架</c> 的绝对路径；存在且路径前缀匹配时优先认定为基架。
        /// 为 null 或目录不存在时仍可依赖文件名中的 <see cref="FrameAssemblyFileNameToken"/>。
        /// </param>
        private static List<Component2> CollectFrameComponents(AssemblyDoc asmDoc, string frameRootPath)
        {
            var list = new List<Component2>();
            if (asmDoc == null)
            {
                return list;
            }

            object[] all = null;
            try
            {
                all = asmDoc.GetComponents(true) as object[];
            }
            catch
            {
                all = null;
            }

            if (all == null || all.Length == 0)
            {
                return list;
            }

            foreach (var obj in all)
            {
                var comp = obj as Component2;
                if (comp == null)
                {
                    continue;
                }

                try
                {
                    if (comp.IsSuppressed())
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                if (!IsFrameAssemblyComponent(comp, frameRootPath))
                {
                    continue;
                }

                list.Add(comp);
            }

            return list;
        }

        /// <summary>
        /// 判断零部件是否为基架装配体：<c>.SLDASM</c>，且路径位于「基架」输出目录下，或文件名包含约定图号片段。
        /// </summary>
        private static bool IsFrameAssemblyComponent(Component2 component, string frameRootPath)
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

            if (!string.IsNullOrWhiteSpace(path))
            {
                var ext = Path.GetExtension(path);
                if (!ext.Equals(".SLDASM", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var normalizedFrameRoot = frameRootPath?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.IsNullOrWhiteSpace(normalizedFrameRoot)
                    && Directory.Exists(normalizedFrameRoot)
                    && path.StartsWith(normalizedFrameRoot + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var fileName = Path.GetFileName(path);
                if (!string.IsNullOrWhiteSpace(fileName)
                    && fileName.IndexOf(FrameAssemblyFileNameToken, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 用于基架实例排序：取装配变换矩阵平移项在 X 上的分量（索引依赖 SolidWorks <c>Transform2.ArrayData</c> 布局）。
        /// </summary>
        /// <remarks>
        /// 排序目的仅为从左到右一致分配退回序号「基架{n}」，以及稳定遍历顺序；不改变文件夹命名逻辑。
        /// </remarks>
        private static double GetComponentSortKey(Component2 component)
        {
            try
            {
                var data = component.Transform2?.ArrayData as double[];
                if (data != null && data.Length > 9)
                {
                    return data[9];
                }
            }
            catch
            {
                // ignore
            }

            return 0;
        }

        /// <summary>
        /// 生成自动化注解的内部键：去掉 SolidWorks 实例后缀「-<c>n</c>」后的组件名，若无则退回完整 <see cref="Component2.Name2"/>。
        /// </summary>
        private static string GetFrameAnnotationBaseKey(Component2 component)
        {
            var baseName = GetBaseComponentInstanceName(component);
            return string.IsNullOrWhiteSpace(baseName) ? (component?.Name2 ?? string.Empty) : baseName;
        }

        /// <summary>
        /// 取 <see cref="Component2.Name2"/> 去掉最后一个「-<c>n</c>」实例后缀后的前缀（若无分隔符则返回全名）。
        /// </summary>
        private static string GetBaseComponentInstanceName(Component2 component)
        {
            var name = component?.Name2 ?? string.Empty;
            var lastDash = name.LastIndexOf('-');
            return lastDash > 0 ? name.Substring(0, lastDash) : name;
        }

        /// <summary>
        /// 将注解键中的非法文件/命名字符替换为下划线，保留字母数字、点与下划线。
        /// </summary>
        private static string SanitizeAnnotationKey(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var chars = raw.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '.')
                {
                    continue;
                }

                chars[i] = '_';
            }

            return new string(chars);
        }

        /// <summary>
        /// 遍历工程图全部视图，删除此前自动化写入的尺寸（<see cref="AnnotationPrefixLen"/>、<see cref="AnnotationPrefixInsulatorEdge"/>、
        /// <see cref="AnnotationPrefixBaseInsulatorEdge"/>）与注释（<see cref="AnnotationPrefixName"/> 及长度备注前缀）。
        /// </summary>
        private static void RemoveExistingFrameAnnotations(ModelDoc2 drawingModel)
        {
            var drawingDoc = drawingModel as DrawingDoc;
            if (drawingDoc == null)
            {
                return;
            }

            var view = drawingDoc.GetFirstView() as View;
            while (view != null)
            {
                var nextView = view.GetNextView() as View;

                try
                {
                    var dispDim = view.GetFirstDisplayDimension5() as DisplayDimension;
                    while (dispDim != null)
                    {
                        var nextDim = dispDim.GetNext5() as DisplayDimension;

                        try
                        {
                            var ann = dispDim.GetAnnotation() as Annotation;
                            TryDeleteAnnotationIfPrefixed(drawingModel, ann, AnnotationPrefixLen);
                            TryDeleteAnnotationIfPrefixed(drawingModel, ann, AnnotationPrefixInsulatorEdge);
                            TryDeleteAnnotationIfPrefixed(drawingModel, ann, AnnotationPrefixBaseInsulatorEdge);
                            TryDeleteAnnotationIfPrefixed(drawingModel, ann, AnnotationPrefixBaseLength);
                            TryDeleteAnnotationIfPrefixed(drawingModel, ann, AnnotationPrefixBaseOpeningHeight);
                        }
                        catch
                        {
                            // ignore
                        }

                        dispDim = nextDim;
                    }
                }
                catch
                {
                    // ignore
                }

                try
                {
                    RemovePrefixedNotesFromView(drawingModel, view, AnnotationPrefixName);
                    RemovePrefixedNotesFromView(drawingModel, view, AnnotationPrefixLen);
                }
                catch
                {
                    // ignore
                }

                view = nextView;
            }
        }

        /// <summary>
        /// 删除指定视图中注释名称以 <paramref name="namePrefix"/> 开头的图纸注释。
        /// </summary>
        private static void RemovePrefixedNotesFromView(
            ModelDoc2 drawingModel,
            View view,
            string namePrefix)
        {
            if (drawingModel == null || view == null || string.IsNullOrWhiteSpace(namePrefix))
            {
                return;
            }

            object notesObj = null;
            try
            {
                notesObj = view.GetNotes();
            }
            catch
            {
                return;
            }

            var notes = notesObj as object[];
            if (notes == null || notes.Length == 0)
            {
                return;
            }

            foreach (var nObj in notes)
            {
                var note = nObj as Note;
                if (note == null)
                {
                    continue;
                }

                try
                {
                    var ann = note.GetAnnotation() as Annotation;
                    TryDeleteAnnotationIfPrefixed(drawingModel, ann, namePrefix);
                }
                catch
                {
                    // ignore
                }
            }
        }

        /// <summary>
        /// 若注解具备名称且以前缀开头，则尝试从工程图中删除（配合幂等自动化）。
        /// </summary>
        private static void TryDeleteAnnotationIfPrefixed(
            ModelDoc2 drawingModel,
            Annotation ann,
            string prefix)
        {
            if (drawingModel == null || ann == null || string.IsNullOrWhiteSpace(prefix))
            {
                return;
            }

            string name = null;
            try
            {
                name = ann.GetName();
            }
            catch
            {
                name = null;
            }

            if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            TryDeleteAnnotation(drawingModel, ann);
        }

        private static void TryDeleteAnnotation(ModelDoc2 drawingModel, Annotation ann)
        {
            if (drawingModel == null || ann == null)
            {
                return;
            }

            try
            {
                drawingModel.ClearSelection2(true);
            }
            catch
            {
                // ignore
            }

            try
            {
                // Annotation.Select3(Append, Data)：Append=false 清空当前选择集再选该注解；
                // Data 为 ISelectData，由 ISelectionMgr.CreateSelectData 创建。
                // https://help.solidworks.com/2023/english/api/sldworksapi/solidworks.interop.sldworks~solidworks.interop.sldworks.iselectionmgr~createselectdata.html
                SelectData selData = null;
                try
                {
                    var selMgr = drawingModel.SelectionManager as SelectionMgr;
                    if (selMgr != null)
                    {
                        selData = selMgr.CreateSelectData() as SelectData;
                    }
                }
                catch
                {
                    selData = null;
                }

                ann.Select3(false, selData);
            }
            catch
            {
                return;
            }

            try
            {
                drawingModel.EditDelete();
            }
            catch
            {
                // ignore
            }

            try
            {
                drawingModel.ClearSelection2(true);
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// 打开工程图前修复失效的外部引用：总装配体指向当前已打开的总装配体，其余文件按文件名在「柜体基座」下匹配。
        /// </summary>
        private void RepairDrawingReferencesBeforeOpen(string drawingPath, string projectPath)
        {
            if (string.IsNullOrWhiteSpace(drawingPath) || !File.Exists(drawingPath))
            {
                return;
            }

            CloseDrawingDocumentIfOpen(drawingPath);

            var activeAssemblyPath = TryGetActiveAssemblyPath();
            var cabinetBaseDirectory = ResolveCabinetBaseDirectory(drawingPath, projectPath, activeAssemblyPath);
            var cabinetBaseFileIndex = BuildCabinetBaseFileIndex(cabinetBaseDirectory);

            var dependencies = _swApp.GetDocumentDependencies2(drawingPath, true, true, false) as string[];
            if (dependencies == null || dependencies.Length == 0)
            {
                return;
            }

            for (var i = 0; i < dependencies.Length; i++)
            {
                var referencedPath = dependencies[i];
                if (string.IsNullOrWhiteSpace(referencedPath) || File.Exists(referencedPath))
                {
                    continue;
                }

                var replacementPath = ResolveBrokenReferenceReplacementPath(
                    referencedPath,
                    activeAssemblyPath,
                    cabinetBaseFileIndex);
                if (string.IsNullOrWhiteSpace(replacementPath) || !File.Exists(replacementPath))
                {
                    continue;
                }

                TryReplaceReferencedDocument(drawingPath, referencedPath, replacementPath);
            }
        }

        /// <summary>
        /// 为失效引用解析替换路径：默认总装配体名 → 当前打开的总装配体；其余 → 柜体基座目录内同名文件。
        /// </summary>
        private static string ResolveBrokenReferenceReplacementPath(
            string referencedPath,
            string activeAssemblyPath,
            IReadOnlyDictionary<string, string> cabinetBaseFileIndex)
        {
            if (string.IsNullOrWhiteSpace(referencedPath))
            {
                return null;
            }

            var fileName = Path.GetFileName(referencedPath);
            var baseName = Path.GetFileNameWithoutExtension(referencedPath);
            var extension = Path.GetExtension(referencedPath);

            if (!string.IsNullOrWhiteSpace(activeAssemblyPath)
                && string.Equals(extension, ".SLDASM", StringComparison.OrdinalIgnoreCase)
                && IsDefaultCabinetAssemblyBaseName(baseName))
            {
                return activeAssemblyPath;
            }

            if (cabinetBaseFileIndex != null
                && !string.IsNullOrWhiteSpace(fileName)
                && cabinetBaseFileIndex.TryGetValue(fileName, out var matchedPath))
            {
                return matchedPath;
            }

            return null;
        }

        private static bool IsDefaultCabinetAssemblyBaseName(string baseName)
        {
            if (string.IsNullOrWhiteSpace(baseName))
            {
                return false;
            }

            for (var i = 0; i < DefaultCabinetAssemblyBaseNames.Length; i++)
            {
                if (string.Equals(baseName, DefaultCabinetAssemblyBaseNames[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 解析「柜体基座」目录：优先工程图/当前总装配体所在目录，其次项目路径。
        /// </summary>
        private string ResolveCabinetBaseDirectory(string drawingPath, string projectPath, string activeAssemblyPath)
        {
            var drawingDirectory = Path.GetDirectoryName(drawingPath);
            if (IsCabinetBaseDirectory(drawingDirectory))
            {
                return drawingDirectory;
            }

            if (!string.IsNullOrWhiteSpace(activeAssemblyPath))
            {
                var assemblyDirectory = Path.GetDirectoryName(activeAssemblyPath);
                if (IsCabinetBaseDirectory(assemblyDirectory))
                {
                    return assemblyDirectory;
                }
            }

            if (!string.IsNullOrWhiteSpace(projectPath) && Directory.Exists(projectPath))
            {
                var projectCabinetBase = Path.Combine(projectPath, CabinetBaseFolderName);
                if (Directory.Exists(projectCabinetBase))
                {
                    return projectCabinetBase;
                }
            }

            var projectRoot = TryGetProjectRootFromDrawingPath(drawingPath);
            if (!string.IsNullOrWhiteSpace(projectRoot))
            {
                var inferredCabinetBase = Path.Combine(projectRoot, CabinetBaseFolderName);
                if (Directory.Exists(inferredCabinetBase))
                {
                    return inferredCabinetBase;
                }
            }

            return null;
        }

        private static bool IsCabinetBaseDirectory(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                return false;
            }

            var leaf = Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.Equals(leaf, CabinetBaseFolderName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 建立柜体基座目录内 SolidWorks 模型文件名索引；同名时优先使用基座根目录下的文件。
        /// </summary>
        private static Dictionary<string, string> BuildCabinetBaseFileIndex(string cabinetBaseDirectory)
        {
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(cabinetBaseDirectory) || !Directory.Exists(cabinetBaseDirectory))
            {
                return index;
            }

            foreach (var filePath in Directory.EnumerateFiles(cabinetBaseDirectory, "*.*", SearchOption.AllDirectories))
            {
                if (!IsSolidWorksModelFile(filePath))
                {
                    continue;
                }

                index[Path.GetFileName(filePath)] = filePath;
            }

            foreach (var filePath in Directory.EnumerateFiles(cabinetBaseDirectory, "*.*", SearchOption.TopDirectoryOnly))
            {
                if (!IsSolidWorksModelFile(filePath))
                {
                    continue;
                }

                index[Path.GetFileName(filePath)] = filePath;
            }

            return index;
        }

        private static bool IsSolidWorksModelFile(string filePath)
        {
            var extension = Path.GetExtension(filePath);
            return string.Equals(extension, ".SLDASM", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".SLDPRT", StringComparison.OrdinalIgnoreCase);
        }

        private string TryGetActiveAssemblyPath()
        {
            var activeModel = _swApp.ActiveDoc as ModelDoc2;
            if (activeModel == null || activeModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                return null;
            }

            return TryGetPathName(activeModel);
        }

        private void CloseDrawingDocumentIfOpen(string drawingPath)
        {
            var opened = FindOpenDocumentByPath(drawingPath);
            if (opened == null)
            {
                return;
            }

            try
            {
                _swApp.CloseDoc(opened.GetTitle());
            }
            catch
            {
                try { _swApp.CloseDoc(drawingPath); } catch { /* ignore */ }
            }
        }

        private bool TryReplaceReferencedDocument(string drawingPath, string oldReferencedPath, string newReferencedPath)
        {
            try
            {
                if (_swApp.ReplaceReferencedDocument(drawingPath, oldReferencedPath, newReferencedPath))
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
                return _swApp.ReplaceReferencedDocument(
                    drawingPath,
                    Path.GetFileName(oldReferencedPath),
                    newReferencedPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 解析柜体装配工程图完整路径：
        /// 1) 与当前激活装配体同名的工程图（装配体重命名后工程图会同步改名）；
        /// 2) 调用方传入有效的项目路径时，在「柜体基座」下按激活装配体基名查找，再回退默认文件名；
        /// 3) 否则从激活装配体所在目录向上回溯，找到首个含
        ///    <see cref="ProjectRootMarkerFolders"/> 中任一子目录的目录作为项目根后同样查找；
        /// 4) 仍未识别到时，尝试与激活装配体同目录下的同名工程图，最后回退默认文件名。
        /// </summary>
        private string ResolveDrawingPath(string projectPath)
        {
            var activeModel = _swApp.ActiveDoc as ModelDoc2;
            var activePath = TryGetPathName(activeModel);
            var activeBaseName = string.IsNullOrWhiteSpace(activePath)
                ? null
                : Path.GetFileNameWithoutExtension(activePath);

            // 优先：与激活装配体同目录、同基名的工程图。
            if (!string.IsNullOrWhiteSpace(activePath))
            {
                var companionDrawing = Path.ChangeExtension(activePath, ".SLDDRW");
                if (File.Exists(companionDrawing))
                {
                    return companionDrawing;
                }
            }

            if (!string.IsNullOrWhiteSpace(projectPath) && Directory.Exists(projectPath))
            {
                var projectCandidate = TryResolveCabinetDrawingInDirectory(
                    Path.Combine(projectPath, CabinetBaseFolderName),
                    activeBaseName);
                if (!string.IsNullOrWhiteSpace(projectCandidate))
                {
                    return projectCandidate;
                }
            }

            string activeDirectory = null;
            if (!string.IsNullOrWhiteSpace(activePath))
            {
                activeDirectory = Path.GetDirectoryName(activePath);
            }

            if (!string.IsNullOrWhiteSpace(activeDirectory) && Directory.Exists(activeDirectory))
            {
                // 从激活装配体所在目录开始，沿父目录方向回溯查找项目根。
                var current = activeDirectory;
                for (int depth = 0; depth < ProjectRootSearchMaxDepth && !string.IsNullOrWhiteSpace(current); depth++)
                {
                    if (!Directory.Exists(current)) break;

                    if (DirectoryContainsAnyMarkerFolder(current))
                    {
                        var markerCandidate = TryResolveCabinetDrawingInDirectory(
                            Path.Combine(current, CabinetBaseFolderName),
                            activeBaseName);
                        if (!string.IsNullOrWhiteSpace(markerCandidate))
                        {
                            return markerCandidate;
                        }
                    }

                    current = Path.GetDirectoryName(current);
                }

                var sameDirectoryCandidate = TryResolveCabinetDrawingInDirectory(activeDirectory, activeBaseName);
                if (!string.IsNullOrWhiteSpace(sameDirectoryCandidate))
                {
                    return sameDirectoryCandidate;
                }
            }

            return null;
        }

        /// <summary>
        /// 在指定目录下解析柜体工程图：优先 <paramref name="assemblyBaseName"/>.SLDDRW，再回退默认模板名。
        /// </summary>
        private static string TryResolveCabinetDrawingInDirectory(string directory, string assemblyBaseName)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(assemblyBaseName))
            {
                var namedCandidate = Path.Combine(directory, assemblyBaseName + ".SLDDRW");
                if (File.Exists(namedCandidate))
                {
                    return namedCandidate;
                }
            }

            var defaultCandidate = Path.Combine(directory, CabinetAssemblyDrawingFileName);
            return File.Exists(defaultCandidate) ? defaultCandidate : null;
        }

        /// <summary>
        /// 判断目录是否包含 <see cref="ProjectRootMarkerFolders"/> 中的任意一个直接子目录。
        /// </summary>
        private static bool DirectoryContainsAnyMarkerFolder(string directory)
        {
            for (int i = 0; i < ProjectRootMarkerFolders.Length; i++)
            {
                var markerPath = Path.Combine(directory, ProjectRootMarkerFolders[i]);
                if (Directory.Exists(markerPath))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 在当前 SolidWorks 会话中查找指定路径对应的已打开文档。
        /// </summary>
        /// <param name="filePath">文档完整路径。</param>
        /// <returns>已打开的文档；未找到返回 null。</returns>
        private ModelDoc2 FindOpenDocumentByPath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            var document = _swApp.GetFirstDocument() as ModelDoc2;
            while (document != null)
            {
                var documentPath = TryGetPathName(document);
                if (!string.IsNullOrWhiteSpace(documentPath)
                    && string.Equals(documentPath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    return document;
                }

                document = document.GetNext() as ModelDoc2;
            }

            return null;
        }

        /// <summary>
        /// 安全获取文档磁盘路径（未保存文档可能返回空或抛异常）。
        /// </summary>
        private static string TryGetPathName(ModelDoc2 document)
        {
            if (document == null) return null;

            try
            {
                return document.GetPathName();
            }
            catch
            {
                return null;
            }
        }
    }
}
