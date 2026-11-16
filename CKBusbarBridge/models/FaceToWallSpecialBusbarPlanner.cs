using CKBusbarBridge.Helpers;
using System;
using System.Collections.Generic;
using System.IO;

namespace CKBusbarBridge.models
{
    /// <summary>
    /// 面对墙末段特殊铜排逐相计划构建器；只进行业务计算，不调用 SolidWorks。
    /// </summary>
    public static class FaceToWallSpecialBusbarPlanner
    {
        private const double RaiseHeightConstant = 68.5;
        private const double OffsetConnectionOccupiedWidthThreshold = 160;
        private const double MilledBarWidthThreshold = 80;

        public static IReadOnlyList<FaceToWallSpecialBusbarPhasePlan> BuildPlans(
            int segmentIndex,
            double busbarLength,
            double wallEntryBusbarCenterToHoleBottomDistance,
            double insulatorHeight,
            double busbarWidth,
            double busbarThickness,
            double exposedBusbarPhaseSpacing,
            double wallBushingPhaseSpacing1,
            double wallBushingPhaseSpacing2,
            double rawWallCabinetEccentricity,
            string baseDrawingNumber,
            string segmentOutputDirectoryPath)
        {
            var geometries = BuildGeometryPlans(
                segmentIndex,
                busbarLength,
                wallEntryBusbarCenterToHoleBottomDistance,
                insulatorHeight,
                busbarWidth,
                busbarThickness,
                exposedBusbarPhaseSpacing,
                wallBushingPhaseSpacing1,
                wallBushingPhaseSpacing2,
                rawWallCabinetEccentricity);

            if (!DrawingNumberHelper.TryParse(baseDrawingNumber, out _, out _))
            {
                throw new ArgumentException($"起始图号格式无效：{baseDrawingNumber}", nameof(baseDrawingNumber));
            }

            if (string.IsNullOrWhiteSpace(segmentOutputDirectoryPath))
            {
                throw new ArgumentException("特殊段输出目录不能为空。", nameof(segmentOutputDirectoryPath));
            }

            var plans = new List<FaceToWallSpecialBusbarPhasePlan>(geometries.Count);
            for (var index = 0; index < geometries.Count; index++)
            {
                var geometry = geometries[index];
                plans.Add(new FaceToWallSpecialBusbarPhasePlan(
                    geometry.SegmentIndex,
                    geometry.Phase,
                    DrawingNumberHelper.Offset(baseDrawingNumber, segmentIndex + index),
                    geometry.DeltaX,
                    geometry.DeltaY,
                    geometry.DeltaZ,
                    geometry.OffsetDirection,
                    geometry.OffsetSize,
                    geometry.TemplateKind,
                    geometry.HeadKind,
                    Path.Combine(segmentOutputDirectoryPath, geometry.Phase + "相")));
            }

            return plans;
        }

        /// <summary>
        /// 构建不依赖图号和输出目录的面对墙 A/B/C 逐相几何计划。
        /// </summary>
        public static IReadOnlyList<FaceToWallSpecialBusbarGeometryPlan> BuildGeometryPlans(
            int segmentIndex,
            double busbarLength,
            double wallEntryBusbarCenterToHoleBottomDistance,
            double insulatorHeight,
            double busbarWidth,
            double busbarThickness,
            double exposedBusbarPhaseSpacing,
            double wallBushingPhaseSpacing1,
            double wallBushingPhaseSpacing2,
            double rawWallCabinetEccentricity)
        {
            ValidatePositive(segmentIndex, nameof(segmentIndex), "特殊段号必须大于0。");
            ValidatePositive(busbarLength, nameof(busbarLength), "特殊铜排排长必须大于0。");
            ValidatePositive(
                wallEntryBusbarCenterToHoleBottomDistance,
                nameof(wallEntryBusbarCenterToHoleBottomDistance),
                "套管中心距孔底必须大于0。");
            ValidatePositive(insulatorHeight, nameof(insulatorHeight), "绝缘子高度必须大于0。");
            ValidatePositive(busbarWidth, nameof(busbarWidth), "排宽必须大于0。");
            ValidatePositive(busbarThickness, nameof(busbarThickness), "排厚必须大于0。");
            ValidatePositive(
                exposedBusbarPhaseSpacing,
                nameof(exposedBusbarPhaseSpacing),
                "伸出排相间距必须大于0。");
            ValidatePositive(
                wallBushingPhaseSpacing1,
                nameof(wallBushingPhaseSpacing1),
                "套管相间距1(F2)必须大于0。");
            ValidatePositive(
                wallBushingPhaseSpacing2,
                nameof(wallBushingPhaseSpacing2),
                "套管相间距2(F3)必须大于0。");

            if (!IsFinite(rawWallCabinetEccentricity))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rawWallCabinetEccentricity),
                    "墙柜偏心值必须是有限数值。");
            }

            var deltaX = Math.Round(busbarLength, 1);
            var deltaY = Math.Round(
                wallEntryBusbarCenterToHoleBottomDistance
                - (insulatorHeight + RaiseHeightConstant + 3.5 * busbarThickness),
                1);
            if (deltaY < 0)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "特殊铜排抬高值不能小于0：{0:0.0}-({1:0.0}+66.5+3.5×{2:0.0})={3:0.0}",
                        wallEntryBusbarCenterToHoleBottomDistance,
                        insulatorHeight,
                        busbarThickness,
                        deltaY));
            }

            var zCompensation = -rawWallCabinetEccentricity;
            var deltaZA = wallBushingPhaseSpacing2 - exposedBusbarPhaseSpacing + zCompensation;
            var deltaZB = zCompensation;
            var deltaZC = exposedBusbarPhaseSpacing - wallBushingPhaseSpacing1 + zCompensation;

            var headKind = segmentIndex % 2 == 1
                ? FaceToWallSpecialBusbarHeadKind.StraightHead
                : FaceToWallSpecialBusbarHeadKind.BridgeHead;

            return new List<FaceToWallSpecialBusbarGeometryPlan>
            {
                BuildPhaseGeometryPlan(
                    segmentIndex,
                    BusbarPhaseSequence.A,
                    deltaX,
                    deltaY,
                    deltaZA,
                    busbarWidth,
                    headKind),
                BuildPhaseGeometryPlan(
                    segmentIndex,
                    BusbarPhaseSequence.B,
                    deltaX,
                    deltaY,
                    deltaZB,
                    busbarWidth,
                    headKind),
                BuildPhaseGeometryPlan(
                    segmentIndex,
                    BusbarPhaseSequence.C,
                    deltaX,
                    deltaY,
                    deltaZC,
                    busbarWidth,
                    headKind)
            };
        }

        private static FaceToWallSpecialBusbarGeometryPlan BuildPhaseGeometryPlan(
            int segmentIndex,
            BusbarPhaseSequence phase,
            double deltaX,
            double deltaY,
            double deltaZ,
            double busbarWidth,
            FaceToWallSpecialBusbarHeadKind headKind)
        {
            var roundedDeltaZ = Math.Round(deltaZ, 1);
            return new FaceToWallSpecialBusbarGeometryPlan(
                segmentIndex,
                phase,
                deltaX,
                deltaY,
                roundedDeltaZ,
                roundedDeltaZ < 0 ? 1 : 0,
                Math.Round(Math.Abs(roundedDeltaZ), 1),
                ResolveTemplateKind(roundedDeltaZ, busbarWidth),
                headKind);
        }

        private static FaceToWallPhaseSpecialTemplateKind ResolveTemplateKind(double deltaZ, double busbarWidth)
        {
            if (deltaZ == 0)
            {
                return FaceToWallPhaseSpecialTemplateKind.StraightRaiseOnly;
            }

            var occupiedWidth = Math.Abs(deltaZ) + busbarWidth;
            if (occupiedWidth > OffsetConnectionOccupiedWidthThreshold)
            {
                return FaceToWallPhaseSpecialTemplateKind.OffsetConnection;
            }

            return busbarWidth > MilledBarWidthThreshold
                ? FaceToWallPhaseSpecialTemplateKind.MilledBar
                : FaceToWallPhaseSpecialTemplateKind.VerticalBend;
        }

        private static void ValidatePositive(int value, string parameterName, string message)
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(parameterName, value, message);
            }
        }

        private static void ValidatePositive(double value, string parameterName, string message)
        {
            if (!IsFinite(value) || value <= 0)
            {
                throw new ArgumentOutOfRangeException(parameterName, value, message);
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
