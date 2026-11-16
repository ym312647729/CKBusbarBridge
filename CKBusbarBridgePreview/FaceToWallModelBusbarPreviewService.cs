using GeometRi;
using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace CKBusbarBridgePreview
{
    /// <summary>基于当前实际装配体识别面对墙柜顶端铜排并生成实体预览。</summary>
    public static class FaceToWallModelBusbarPreviewService
    {
        public static List<FreeDrawBody> Preview(
            SldWorks swApp,
            BusbarBridgeAxisSetting axisSetting,
            int singlePhaseStackCount,
            IReadOnlyList<double> breakpointPositions,
            double bendingRadius = 10d,
            double bendAllowance = 20d,
            double insulationClearance = 125d)
        {
            if (swApp == null) throw new ArgumentNullException(nameof(swApp));
            axisSetting = axisSetting ?? BusbarBridgeAxisSetting.CreateDefault();

            FaceToWallPreviewTrace.Info(
                "面对墙完整预览-开始",
                FormatAxisSetting(axisSetting));
            try
            {
                var busbarPlan = CreatePlan(
                    swApp,
                    axisSetting,
                    singlePhaseStackCount,
                    breakpointPositions,
                    bendingRadius,
                    bendAllowance,
                    insulationClearance);

                var previewBodies = CreatePreviewBodies(
                    swApp,
                    busbarPlan.GeneratedBusbars,
                    busbarPlan.CabinetTopExtensionBusbars,
                    "模型识别和固定普通段规划已完成，但未创建出铜排临时实体。");
                FaceToWallPreviewTrace.Info(
                    "面对墙完整预览-完成",
                    "临时实体数=" + previewBodies.Count);
                return previewBodies;
            }
            catch (Exception completePreviewException)
            {
                FaceToWallPreviewTrace.Error(
                    "面对墙完整预览-失败",
                    completePreviewException,
                    FormatAxisSetting(axisSetting));
                throw;
            }
        }

        /// <summary>
        /// 从当前实际装配体一次性解析柜顶伸出排、全部柜侧固定普通段和穿墙套管铜排。
        /// 返回值只包含纯几何与组件引用，供预览和独立 Worker 请求适配共同使用。
        /// </summary>
        public static FaceToWallModelBusbarPlan CreatePlan(
            ISldWorks swApp,
            BusbarBridgeAxisSetting axisSetting,
            int singlePhaseStackCount,
            IReadOnlyList<double> breakpointPositions,
            double bendingRadius = 10d,
            double bendAllowance = 20d,
            double insulationClearance = 125d)
        {
            if (swApp == null) throw new ArgumentNullException(nameof(swApp));
            axisSetting = axisSetting ?? BusbarBridgeAxisSetting.CreateDefault();

            FaceToWallPreviewTrace.Info(
                "面对墙模型算法数据-开始",
                FormatAxisSetting(axisSetting) +
                "，单相叠排数=" + singlePhaseStackCount +
                "，断点=" + string.Join(
                    "/",
                    breakpointPositions ?? Array.Empty<double>()) +
                "，折弯半径=" + bendingRadius.ToString("0.###") +
                "，折弯余量=" + bendAllowance.ToString("0.###") +
                "，绝缘距离=" + insulationClearance.ToString("0.###"));
            var partCatalog = AssemblyPartCatalog.Create(swApp);
            var extensionBusbars = CabinetTopExtensionBusbarReader.Read(
                partCatalog,
                swApp,
                axisSetting);
            var wallBushingBusbars = WallBushingBusbarReader.Read(
                partCatalog,
                swApp,
                axisSetting);
            var busbarPlan = FaceToWallModelBusbarPlanner.Create(
                partCatalog,
                swApp,
                axisSetting,
                extensionBusbars,
                wallBushingBusbars,
                breakpointPositions,
                singlePhaseStackCount,
                bendingRadius,
                bendAllowance,
                insulationClearance);
            FaceToWallPreviewTrace.Info(
                "面对墙模型算法数据-完成",
                "固定普通段=" +
                busbarPlan.GeneratedOrdinaryBusbarGroups.Count +
                "，固定铜排=" + busbarPlan.GeneratedBusbars.Count +
                "，穿墙套管铜排=" + busbarPlan.WallBushingBusbars.Count);
            return busbarPlan;
        }

        /// <summary>
        /// 解析当前实际柜顶伸出排、绝缘子和断点，预览算法前的全部固定普通段。
        /// 柜顶伸出排本体仅作为定位和截面基准，不参与预览。
        /// </summary>
        public static List<FreeDrawBody> PreviewOrdinaryBusbarGroups(
            SldWorks swApp,
            BusbarBridgeAxisSetting axisSetting,
            int singlePhaseStackCount,
            IReadOnlyList<double> breakpointPositions,
            double bendingRadius,
            double bendAllowance,
            double insulationClearance = 125d)
        {
            if (swApp == null) throw new ArgumentNullException(nameof(swApp));
            axisSetting = axisSetting ?? BusbarBridgeAxisSetting.CreateDefault();

            FaceToWallPreviewTrace.Info(
                "面对墙固定普通段预览-开始",
                FormatAxisSetting(axisSetting) +
                "，单相叠排数=" + singlePhaseStackCount +
                "，断点=" + string.Join(
                    "/",
                    breakpointPositions ?? Array.Empty<double>()) +
                "，折弯半径=" + bendingRadius.ToString("0.###") +
                "，折弯余量=" + bendAllowance.ToString("0.###") +
                "，绝缘距离=" + insulationClearance.ToString("0.###") +
                "，日志=" + FaceToWallPreviewTrace.LogFilePath);
            try
            {
                var cabinetTopPartCatalog = AssemblyPartCatalog.Create(swApp);
                var cabinetTopExtensionBusbars = CabinetTopExtensionBusbarReader.Read(
                    cabinetTopPartCatalog,
                    swApp,
                    axisSetting);
                FaceToWallPreviewTrace.Info(
                    "面对墙固定普通段预览-模型解析完成",
                    "柜顶伸出排数量=" + cabinetTopExtensionBusbars.Count);

                var cabinetTopConnectionSettings =
                    FaceToWallModelBusbarPlanner.CreateCabinetTopConnectionGroups(
                        cabinetTopPartCatalog,
                        swApp,
                        axisSetting,
                        cabinetTopExtensionBusbars,
                        breakpointPositions,
                        singlePhaseStackCount,
                        bendingRadius,
                        bendAllowance,
                        insulationClearance);
                FaceToWallPreviewTrace.Info(
                    "面对墙固定普通段预览-路径规划完成",
                    "铜排路径数量=" + cabinetTopConnectionSettings.Count);

                var cabinetTopPreviewBodies = CreatePreviewBodies(
                    swApp,
                    cabinetTopConnectionSettings,
                    cabinetTopExtensionBusbars,
                    "柜顶伸出排、绝缘子和断点已解析，但未创建出固定普通段临时实体。");
                FaceToWallPreviewTrace.Info(
                    "面对墙固定普通段预览-完成",
                    "临时实体数=" + cabinetTopPreviewBodies.Count);
                return cabinetTopPreviewBodies;
            }
            catch (Exception cabinetTopPreviewException)
            {
                FaceToWallPreviewTrace.Error(
                    "面对墙固定普通段预览-失败",
                    cabinetTopPreviewException,
                    FormatAxisSetting(axisSetting));
                throw;
            }
        }

        internal static List<FreeDrawBody> CreatePreviewBodies(
            SldWorks swApp,
            IEnumerable<PlanarBendBusbarSetting> generatedBusbars,
            IReadOnlyList<ModeledBusbar> extensionBusbars,
            string emptyPreviewMessage)
        {
            var previewReferenceComponent = extensionBusbars
                .Select(busbar => busbar.Component)
                .FirstOrDefault(component => component != null);
            if (previewReferenceComponent == null)
            {
                throw new InvalidOperationException("无法确定铜排预览的坐标转换组件。");
            }

            return CreatePreviewBodies(swApp, generatedBusbars, previewReferenceComponent, emptyPreviewMessage);
        }

        /// <summary>直接显示当前草稿的几何参数，不反读模型或重新规划断点。</summary>
        public static List<FreeDrawBody> CreatePreviewBodies(SldWorks swApp,
            IEnumerable<PlanarBendBusbarSetting> generatedBusbars, Component2 previewReferenceComponent,
            string emptyPreviewMessage)
        {
            if (swApp == null || previewReferenceComponent == null)
                throw new ArgumentException("缺少SolidWorks预览上下文。");
            var generatedBusbarList = generatedBusbars?.Select(p => p?.Copy() ??
                throw new ArgumentException("铜排生成参数不能为空。")).ToList()
                ?? throw new ArgumentNullException(nameof(generatedBusbars));
            var previewBodies = new List<FreeDrawBody>();
            for (var generatedBusbarIndex = 0;
                 generatedBusbarIndex < generatedBusbarList.Count;
                 generatedBusbarIndex++)
            {
                var generatedBusbar = generatedBusbarList[generatedBusbarIndex];
                if (generatedBusbar.IsVerticalBend)
                {
                    var verticalBendSetting = VerticalBendPreviewAdapter.Create(
                        generatedBusbar);
                    var verticalBendPreviewBodies = verticalBendSetting
                        .GetVerticalBendPreviewBodies(
                            swApp,
                            ResolvePhaseColor(generatedBusbar.GroupKey),
                            previewReferenceComponent);
                    if (verticalBendPreviewBodies == null ||
                        verticalBendPreviewBodies.Count == 0)
                    {
                        throw new InvalidOperationException(
                            "立弯“" +
                            (generatedBusbar.GroupKey ?? "未命名") +
                            "”没有生成有效预览 Body。");
                    }

                    for (var bodyIndex = 0; bodyIndex < verticalBendPreviewBodies.Count; bodyIndex++)
                    {
                        if (verticalBendPreviewBodies[bodyIndex]?.Body == null)
                            throw new InvalidOperationException("立弯分段为空：" + generatedBusbar.GroupKey);
                        verticalBendPreviewBodies[bodyIndex].Name = generatedBusbar.GroupKey + "/body-" + bodyIndex;
                    }
                    previewBodies.AddRange(verticalBendPreviewBodies);
                    FaceToWallPreviewTrace.Info(
                        "创建铜排大师立弯预览-完成",
                        "组=" +
                        (generatedBusbar.GroupKey ?? "未命名") +
                        "，分段数=" +
                        verticalBendSetting.SegmentationPoints.Count +
                        "，Body数=" + verticalBendPreviewBodies.Count);
                    continue;
                }
                if (generatedBusbar.IsMilledBar)
                {
                    var milledBarSegments = CollectMilledBarSegments(
                        generatedBusbarList,
                        generatedBusbarIndex);
                    var milledBarPreviewBody = CreateMilledBarPreviewBody(
                        swApp,
                        milledBarSegments,
                        previewReferenceComponent,
                        generatedBusbarIndex);
                    if (milledBarPreviewBody?.Body == null)
                        throw new InvalidOperationException("铣排未生成有效实体：" + generatedBusbar.GroupKey);
                    milledBarPreviewBody.Name = generatedBusbar.GroupKey;
                    previewBodies.Add(milledBarPreviewBody);
                    generatedBusbarIndex += milledBarSegments.Count - 1;
                    continue;
                }

                var ordinaryPreviewBody = CreateSinglePreviewBody(
                    swApp,
                    generatedBusbar,
                    previewReferenceComponent,
                    generatedBusbarIndex);
                if (ordinaryPreviewBody != null)
                {
                    ordinaryPreviewBody.Name = generatedBusbar.GroupKey;
                    previewBodies.Add(ordinaryPreviewBody);
                }
                else throw new InvalidOperationException("铜排未生成有效实体：" + generatedBusbar.GroupKey);
            }

            if (previewBodies.Count == 0)
            {
                throw new InvalidOperationException(emptyPreviewMessage);
            }

            return previewBodies;
        }

        private static IReadOnlyList<PlanarBendBusbarSetting>
            CollectMilledBarSegments(
            IReadOnlyList<PlanarBendBusbarSetting> generatedBusbars,
            int firstSegmentIndex)
        {
            var firstSegment = generatedBusbars[firstSegmentIndex];
            if (string.IsNullOrWhiteSpace(firstSegment.GroupKey))
            {
                throw new InvalidOperationException(
                    "铣排预览段缺少组合 GroupKey，无法把三段 Body 识别为同一根排。");
            }

            var milledBarSegments = new List<PlanarBendBusbarSetting>();
            for (var candidateIndex = firstSegmentIndex;
                 candidateIndex < generatedBusbars.Count;
                 candidateIndex++)
            {
                var candidateSegment = generatedBusbars[candidateIndex];
                if (!candidateSegment.IsMilledBar ||
                    !string.Equals(
                        candidateSegment.GroupKey,
                        firstSegment.GroupKey,
                        StringComparison.Ordinal))
                {
                    break;
                }

                milledBarSegments.Add(candidateSegment);
            }

            if (milledBarSegments.Count != 3)
            {
                throw new InvalidOperationException(
                    "铣排“" + firstSegment.GroupKey +
                    "”必须恰好由三个连续组成段构成，当前识别到 " +
                    milledBarSegments.Count + " 段。" );
            }
            if (milledBarSegments[0].PointList == null ||
                milledBarSegments[0].PointList.Count < 2 ||
                milledBarSegments[1].PointList == null ||
                milledBarSegments[1].PointList.Count != 2 ||
                milledBarSegments[2].PointList == null ||
                milledBarSegments[2].PointList.Count < 2)
            {
                throw new InvalidOperationException(
                    "铣排“" + firstSegment.GroupKey +
                    "”必须由两侧有效路径和中间两点加宽直排组成。");
            }

            return milledBarSegments.AsReadOnly();
        }

        private static FreeDrawBody CreateMilledBarPreviewBody(
            SldWorks swApp,
            IReadOnlyList<PlanarBendBusbarSetting> milledBarSegments,
            Component2 previewReferenceComponent,
            int firstGeneratedBusbarIndex)
        {
            var middleSegmentLength =
                milledBarSegments[1].PointList[0].DistanceTo(
                    milledBarSegments[1].PointList[1]);

            var segmentPreviewBodies = new List<FreeDrawBody>();
            try
            {
                for (var milledBarSegmentIndex = 0;
                     milledBarSegmentIndex < milledBarSegments.Count;
                     milledBarSegmentIndex++)
                {
                    var segmentPreviewBody = CreateSinglePreviewBody(
                        swApp,
                        milledBarSegments[milledBarSegmentIndex],
                        previewReferenceComponent,
                        firstGeneratedBusbarIndex + milledBarSegmentIndex);
                    if (segmentPreviewBody?.Body == null)
                    {
                        throw new InvalidOperationException(
                            "铣排第" + (milledBarSegmentIndex + 1) +
                            "段没有生成有效 Body。" );
                    }

                    segmentPreviewBodies.Add(segmentPreviewBody);
                }

                var mergedMilledBarBody = UseBendToPreviewFreeBusBar
                    .MergePreviewBodySegments(
                        segmentPreviewBodies
                            .Select(segmentPreviewBody =>
                                segmentPreviewBody.Body)
                            .ToList()
                            .AsReadOnly());
                var firstPreviewBody = segmentPreviewBodies[0];
                var lastPreviewBody = segmentPreviewBodies[
                    segmentPreviewBodies.Count - 1];
                var mergedMilledBarPreviewBody = new FreeDrawBody(
                    firstPreviewBody.Color,
                    mergedMilledBarBody,
                    firstPreviewBody.Component2,
                    lastPreviewBody.LastVector3d,
                    segmentPreviewBodies.Sum(segmentPreviewBody =>
                        segmentPreviewBody.LastPointCount),
                    milledBarSegments[0].GroupKey + "-铣排",
                    1,
                    firstPreviewBody.TempMater)
                {
                    IsMilledBar = true
                };
                FaceToWallPreviewTrace.Info(
                    "创建铣排临时实体-完成",
                    "组=" + milledBarSegments[0].GroupKey +
                    "，组成段数=" + milledBarSegments.Count +
                    "，合并Body数=1" +
                    "，中间段长度=" +
                    middleSegmentLength.ToString("0.###") + " mm" +
                    "，标记=铣排");
                return mergedMilledBarPreviewBody;
            }
            catch (Exception milledBarPreviewException)
            {
                FaceToWallPreviewTrace.Error(
                    "创建铣排临时实体-异常",
                    milledBarPreviewException,
                    "组=" + (milledBarSegments.FirstOrDefault()?.GroupKey ??
                        "未命名") +
                    "，分段数=" + milledBarSegments.Count);
                throw;
            }
        }

        private static FreeDrawBody CreateSinglePreviewBody(
            SldWorks swApp,
            PlanarBendBusbarSetting generatedBusbar,
            Component2 previewReferenceComponent,
            int generatedBusbarIndex)
        {
            var generatedBusbarDetail =
                "序号=" + (generatedBusbarIndex + 1) +
                "，组=" + (generatedBusbar.GroupKey ?? "未命名") +
                "，点数=" + (generatedBusbar.PointList?.Count ?? 0) +
                "，规格=" + generatedBusbar.BusBarWidth.ToString("0.###") +
                "x" + generatedBusbar.BusBarThickness.ToString("0.###") +
                "，折弯半径=" + generatedBusbar.BarBendingRadius.ToString("0.###") +
                "，铣排段=" + generatedBusbar.IsMilledBar;
            try
            {
                FaceToWallPreviewTrace.Info(
                    "创建铜排临时实体-开始",
                    generatedBusbarDetail);
                var previewSetting = PlanarBendPreviewAdapter.Create(
                    generatedBusbar);
                if (previewSetting == null)
                {
                    FaceToWallPreviewTrace.Warning(
                        "创建铜排临时实体-适配失败",
                        generatedBusbarDetail);
                    return null;
                }

                if (previewSetting.PointGather.Count > 2 &&
                    (double.IsNaN(previewSetting.BarBendingRadius) || double.IsInfinity(previewSetting.BarBendingRadius) ||
                     previewSetting.BarBendingRadius <= 0d))
                    throw new InvalidOperationException("铜排“" + generatedBusbar.GroupKey +
                        "”折弯半径缺失或无效，请重读原始加工参数后预览。");

                FaceToWallPreviewTrace.Info(
                    "铜排预览实际线段",
                    generatedBusbarDetail +
                    "；预览排厚方向=" +
                    FormatVector(previewSetting.StartBusbarVector) +
                    "；预览排宽方向=" +
                    FormatVector(previewSetting.WidthVector) +
                    "；预览点集合=" +
                    string.Join(
                        " -> ",
                        (previewSetting.PointGather ?? new List<Point3d>())
                            .Select(FormatPoint)));

                var previewBody = UseBendToPreviewFreeBusBar.PreviewBendBody(
                    swApp,
                    previewSetting,
                    ResolvePhaseColor(generatedBusbar.GroupKey),
                    previewReferenceComponent);
                if (previewBody?.Body == null)
                {
                    FaceToWallPreviewTrace.Warning(
                        "创建铜排临时实体-返回空",
                        generatedBusbarDetail);
                    return null;
                }

                // SolidWorks Body2.Display3 的 ConSelect 参数固定为 1，
                // 便于在临时预览中直接选中实体测量。
                previewBody.CanSelect = 1;
                FaceToWallPreviewTrace.Info(
                    "创建铜排临时实体-完成",
                    generatedBusbarDetail);
                return previewBody;
            }
            catch (Exception previewBodyException)
            {
                FaceToWallPreviewTrace.Error(
                    "创建铜排临时实体-异常",
                    previewBodyException,
                    generatedBusbarDetail);
                throw;
            }
        }

        private static string FormatAxisSetting(BusbarBridgeAxisSetting axisSetting)
        {
            return axisSetting == null
                ? "坐标设置=null"
                : "长度=" + axisSetting.LengthDirection +
                  "，高度=" + axisSetting.HeightDirection +
                  "，宽度=" + axisSetting.WidthDirection;
        }

        private static string FormatPoint(Point3d point)
        {
            return point == null
                ? "null"
                : string.Format(
                    "({0:0.###},{1:0.###},{2:0.###})",
                    point.X,
                    point.Y,
                    point.Z);
        }

        private static string FormatVector(Vector3d vector)
        {
            return vector == null
                ? "null"
                : string.Format(
                    "({0:0.###},{1:0.###},{2:0.###})",
                    vector.X,
                    vector.Y,
                    vector.Z);
        }

        private static Color ResolvePhaseColor(string groupKey)
        {
            if (!string.IsNullOrWhiteSpace(groupKey))
            {
                if (groupKey.StartsWith("A", StringComparison.OrdinalIgnoreCase))
                {
                    return UseBendToPreviewFreeBusBar.GetBusbarPhaseColor(BusbarPhase_e.A);
                }

                if (groupKey.StartsWith("B", StringComparison.OrdinalIgnoreCase))
                {
                    return UseBendToPreviewFreeBusBar.GetBusbarPhaseColor(BusbarPhase_e.B);
                }

                if (groupKey.StartsWith("C", StringComparison.OrdinalIgnoreCase))
                {
                    return UseBendToPreviewFreeBusBar.GetBusbarPhaseColor(BusbarPhase_e.C);
                }
            }

            return Color.Cyan;
        }
    }
}
