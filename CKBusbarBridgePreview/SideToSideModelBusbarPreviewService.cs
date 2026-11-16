using GeometRi;
using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// 从当前侧对侧总装严格读取左右开孔（基座1）的固定换相结构，
    /// 并生成 B 柜到 A 柜的完整固定铜排计划。
    /// </summary>
    public static class SideToSideModelBusbarPreviewService
    {
        public static bool TryResolveFixedPhaseChangeFromPhysicalModel(
            ISldWorks swApp,
            out bool isFixedPhaseChange)
        {
            if (swApp == null)
            {
                throw new ArgumentNullException(nameof(swApp));
            }

            isFixedPhaseChange = false;
            var partCatalog = AssemblyPartCatalog.Create(swApp);
            var frameComponents = partCatalog
                .GetTopLevelComponentsByConfiguredRdType(
                    SystemPartType_e.Frame)
                .ToList();
            if (frameComponents.Count == 0)
            {
                return false;
            }

            var extensionBusbars = CabinetTopExtensionBusbarReader.Read(
                partCatalog,
                swApp,
                BusbarBridgeAxisSetting.CreateDefault());
            var extensionThicknessDirection = AverageAlignedDirection(
                extensionBusbars.Select(extensionBusbar =>
                    extensionBusbar.CenterlineSetting.StartBusbarVector));
            var extensionLengthDirection = AverageAlignedDirection(
                extensionBusbars.Select(GetLongestCenterlineDirection));
            if (extensionThicknessDirection == null ||
                extensionLengthDirection == null)
            {
                throw new InvalidOperationException(
                    "侧对侧无法从柜顶伸出排确定排厚方向和排长方向。");
            }

            var frameDescendantNames = new HashSet<string>(
                frameComponents.SelectMany(frameComponent =>
                        partCatalog.GetAllDescendants(frameComponent))
                    .Select(BusbarGeometryUtility.SafeName),
                StringComparer.OrdinalIgnoreCase);
            var frameInsulators = FaceToWallModelBusbarPlanner
                .ReadAvailableConnectionInsulators(partCatalog, swApp)
                .Where(insulator => insulator?.Component != null &&
                    insulator.ConnectionPlane?.Normal != null &&
                    frameDescendantNames.Contains(
                        BusbarGeometryUtility.SafeName(insulator.Component)))
                .ToList();
            if (frameInsulators.Count == 0)
            {
                throw new InvalidOperationException(
                    "侧对侧中间基架中没有读取到可用于区分第一、第二种的绝缘子配合面。");
            }

            var allNormalsParallelThickness = frameInsulators.All(
                frameInsulator => BusbarGeometryUtility.AreParallel(
                    frameInsulator.ConnectionPlane.Normal,
                    extensionThicknessDirection));
            var allNormalsParallelExtensionLength = frameInsulators.All(
                frameInsulator => BusbarGeometryUtility.AreParallel(
                    frameInsulator.ConnectionPlane.Normal,
                    extensionLengthDirection));
            if (allNormalsParallelThickness ==
                allNormalsParallelExtensionLength)
            {
                throw new InvalidOperationException(
                    "侧对侧中间基架绝缘子法向无法唯一判定场景：必须统一平行于柜顶排厚方向（第一种），或统一平行于柜顶排长方向（第二种）。");
            }

            isFixedPhaseChange = allNormalsParallelThickness;
            FaceToWallPreviewTrace.Info(
                "侧对侧类型-物理绝缘子法向判定",
                "基架=" + frameComponents.Count +
                "，基架绝缘子=" + frameInsulators.Count +
                "，柜顶排厚方向=" + extensionThicknessDirection +
                "，柜顶排长方向=" + extensionLengthDirection +
                "，结果=" + (isFixedPhaseChange
                    ? "第一种固定换相"
                    : "第二种七点首段"));
            return true;
        }

        private static Vector3d GetLongestCenterlineDirection(
            ModeledBusbar modeledBusbar)
        {
            var points = modeledBusbar?.CenterlineSetting?.PointList;
            if (points == null || points.Count < 2)
            {
                return null;
            }

            return Enumerable.Range(0, points.Count - 1)
                .Select(pointIndex => new Vector3d(
                    points[pointIndex],
                    points[pointIndex + 1]))
                .Where(segmentDirection => segmentDirection.Norm > 0.001d)
                .OrderByDescending(segmentDirection => segmentDirection.Norm)
                .Select(segmentDirection => segmentDirection.Normalized)
                .FirstOrDefault();
        }

        private static Vector3d AverageAlignedDirection(
            IEnumerable<Vector3d> sourceDirections)
        {
            var validDirections = (sourceDirections ??
                    Enumerable.Empty<Vector3d>())
                .Where(direction => direction != null &&
                    direction.Norm > 0.001d)
                .Select(direction => direction.Normalized)
                .ToList();
            if (validDirections.Count == 0)
            {
                return null;
            }

            var referenceDirection = validDirections[0];
            var alignedDirections = validDirections
                .Select(direction => BusbarGeometryUtility.Align(
                    direction,
                    referenceDirection))
                .ToList();
            var averageDirection = new Vector3d(
                alignedDirections.Average(direction => direction.X),
                alignedDirections.Average(direction => direction.Y),
                alignedDirections.Average(direction => direction.Z));
            return averageDirection.Norm <= 0.001d
                ? null
                : averageDirection.Normalized;
        }

        public static FaceToFaceModelBusbarPlan CreateFixedPhaseChangePlan(
            ISldWorks swApp,
            BusbarBridgeAxisSetting axisSetting,
            int singlePhaseStackCount,
            IReadOnlyList<double> breakpointPositions,
            double bendingRadius = 10d,
            double bendAllowance = 20d)
        {
            if (swApp == null)
            {
                throw new ArgumentNullException(nameof(swApp));
            }

            var normalizedAxisSetting = axisSetting ??
                BusbarBridgeAxisSetting.CreateDefault();
            FaceToWallPreviewTrace.Info(
                "侧对侧固定换相模型规划-开始",
                "长度=" + normalizedAxisSetting.LengthDirection +
                "，高度=" + normalizedAxisSetting.HeightDirection +
                "，宽度=" + normalizedAxisSetting.WidthDirection +
                "，单相叠排数=" + singlePhaseStackCount +
                "，断点=" + string.Join(
                    "/",
                    breakpointPositions ?? Array.Empty<double>()));

#if DEBUG
            var debugCaptureSession =
                new SideToSideFixedPhaseChangeDebugCaptureSession(
                    normalizedAxisSetting,
                    singlePhaseStackCount,
                    breakpointPositions,
                    bendingRadius,
                    bendAllowance);
#endif
            try
            {
                var partCatalog = AssemblyPartCatalog.Create(swApp);
#if DEBUG
                debugCaptureSession.AttachCatalog(partCatalog);
#endif
                var extensionBusbars = CabinetTopExtensionBusbarReader.Read(
                    partCatalog,
                    swApp,
                    normalizedAxisSetting);
#if DEBUG
                debugCaptureSession.AttachCabinetTopExtensionBusbars(
                    extensionBusbars);
                var debugConnectionInsulators =
                    FaceToWallModelBusbarPlanner
                        .ReadAvailableConnectionInsulators(
                            partCatalog,
                            swApp);
                debugCaptureSession.AttachInsulators(
                    debugConnectionInsulators);
                var busbarPlan = FaceToFaceModelBusbarPlanner
                    .CreateSideToSideFixedPhaseChange(
                        partCatalog,
                        swApp,
                        normalizedAxisSetting,
                        extensionBusbars,
                        breakpointPositions,
                        singlePhaseStackCount,
                        bendingRadius,
                        bendAllowance,
                        debugConnectionInsulators);
                // 规划器会根据 B 柜柜顶伸出排重新解析业务轴并重新读取端头；
                // 快照必须保留这份最终几何，而不是初始默认轴读取结果。
                debugCaptureSession.AttachCabinetTopExtensionBusbars(
                    busbarPlan.CabinetTopExtensionBusbars);
                debugCaptureSession.AttachPlan(busbarPlan);
                debugCaptureSession.MarkSuccess();
                return busbarPlan;
#else
                return FaceToFaceModelBusbarPlanner
                    .CreateSideToSideFixedPhaseChange(
                        partCatalog,
                        swApp,
                        normalizedAxisSetting,
                        extensionBusbars,
                        breakpointPositions,
                        singlePhaseStackCount,
                        bendingRadius,
                        bendAllowance);
#endif
            }
            catch (Exception planningException)
            {
#if DEBUG
                debugCaptureSession.MarkFailure(planningException);
#endif
                FaceToWallPreviewTrace.Error(
                    "侧对侧固定换相模型规划-失败",
                    planningException);
                throw;
            }
#if DEBUG
            finally
            {
                debugCaptureSession.Save();
            }
#endif
        }

        public static List<FreeDrawBody> PreviewFixedPhaseChange(
            SldWorks swApp,
            BusbarBridgeAxisSetting axisSetting,
            int singlePhaseStackCount,
            IReadOnlyList<double> breakpointPositions,
            double bendingRadius = 10d,
            double bendAllowance = 20d)
        {
            if (swApp == null)
            {
                throw new ArgumentNullException(nameof(swApp));
            }

            var busbarPlan = CreateFixedPhaseChangePlan(
                swApp,
                axisSetting,
                singlePhaseStackCount,
                breakpointPositions,
                bendingRadius,
                bendAllowance);
            return FaceToWallModelBusbarPreviewService.CreatePreviewBodies(
                swApp,
                busbarPlan.GeneratedBusbars,
                busbarPlan.CabinetTopExtensionBusbars,
                "侧对侧固定换相路径已经生成，但未创建出铜排临时实体。");
        }

        /// <summary>
        /// 规划侧对侧第二种（前后开孔、基座2）的完整铜排：
        /// 两柜柜顶采用七点首段，中间区间复用面对面全路径规划。
        /// </summary>
        public static FaceToFaceModelBusbarPlan
            CreateSecondTypeFirstSegmentPlan(
            ISldWorks swApp,
            BusbarBridgeAxisSetting axisSetting,
            int singlePhaseStackCount,
            IReadOnlyList<double> breakpointPositions,
            double bendingRadius = 10d,
            double bendAllowance = 20d,
            double insulationClearance = 125d,
            bool isLargeOffset = false,
            bool isSmallOffset = false,
            double? generatedBusbarWidth = null,
            double? generatedBusbarThickness = null,
            int? requestedPhaseChangeIntervalIndex = null,
            IReadOnlyList<FaceToFaceLargeOffsetBreakpointProtectionRange>
                largeOffsetBreakpointProtectionRanges = null)
        {
            if (swApp == null)
            {
                throw new ArgumentNullException(nameof(swApp));
            }

            var normalizedAxisSetting = axisSetting ??
                BusbarBridgeAxisSetting.CreateDefault();
            try
            {
                var partCatalog = AssemblyPartCatalog.Create(swApp);
                var extensionBusbars = CabinetTopExtensionBusbarReader.Read(
                    partCatalog,
                    swApp,
                    normalizedAxisSetting);
                return FaceToFaceModelBusbarPlanner
                    .CreateSideToSideSecondTypeFirstSegment(
                        partCatalog,
                        swApp,
                        normalizedAxisSetting,
                        extensionBusbars,
                        breakpointPositions,
                        singlePhaseStackCount,
                        bendingRadius,
                        bendAllowance,
                        insulationClearance,
                        null,
                        isLargeOffset,
                        isSmallOffset,
                        generatedBusbarWidth,
                        generatedBusbarThickness,
                        requestedPhaseChangeIntervalIndex,
                        largeOffsetBreakpointProtectionRanges);
            }
            catch (Exception planningException)
            {
                FaceToWallPreviewTrace.Error(
                    "侧对侧第二种柜顶首段模型规划-失败",
                    planningException);
                throw;
            }
        }

        public static List<FreeDrawBody> PreviewSecondTypeFirstSegment(
            SldWorks swApp,
            BusbarBridgeAxisSetting axisSetting,
            int singlePhaseStackCount,
            IReadOnlyList<double> breakpointPositions,
            double bendingRadius = 10d,
            double bendAllowance = 20d,
            double insulationClearance = 125d,
            bool isLargeOffset = false,
            bool isSmallOffset = false,
            double? generatedBusbarWidth = null,
            double? generatedBusbarThickness = null,
            int? requestedPhaseChangeIntervalIndex = null,
            IReadOnlyList<FaceToFaceLargeOffsetBreakpointProtectionRange>
                largeOffsetBreakpointProtectionRanges = null)
        {
            var busbarPlan = CreateSecondTypeFirstSegmentPlan(
                swApp,
                axisSetting,
                singlePhaseStackCount,
                breakpointPositions,
                bendingRadius,
                bendAllowance,
                insulationClearance,
                isLargeOffset,
                isSmallOffset,
                generatedBusbarWidth,
                generatedBusbarThickness,
                requestedPhaseChangeIntervalIndex,
                largeOffsetBreakpointProtectionRanges);
            return FaceToWallModelBusbarPreviewService.CreatePreviewBodies(
                swApp,
                busbarPlan.GeneratedBusbars,
                busbarPlan.CabinetTopExtensionBusbars,
                "侧对侧第二种柜顶七点首段已经生成，但未创建出铜排临时实体。");
        }
    }
}
