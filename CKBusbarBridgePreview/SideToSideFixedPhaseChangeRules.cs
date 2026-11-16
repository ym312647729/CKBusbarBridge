using System;
using System.Collections.Generic;
using System.Linq;
using GeometRi;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// 侧对侧第一种固定换相专用物理规则。
    /// 本类不得被侧对侧第二种首段规划调用，避免两种结构的容差和点列规则互相影响。
    /// </summary>
    internal static class SideToSideFixedPhaseChangeRules
    {
        internal static IReadOnlyList<int>
            ResolveACabinetPhysicalPhaseIndexesByWidthOrder(
            IReadOnlyList<BusbarPhase_e> bCabinetWidthOrder,
            IReadOnlyList<BusbarPhase_e> aCabinetWidthOrder)
        {
            if (bCabinetWidthOrder == null ||
                aCabinetWidthOrder == null ||
                bCabinetWidthOrder.Count != 3 ||
                aCabinetWidthOrder.Count != 3 ||
                !bCabinetWidthOrder.Reverse().SequenceEqual(
                    aCabinetWidthOrder))
            {
                throw new InvalidOperationException(
                    "侧对侧第一种固定换相的 A 柜相位映射无法按宽度方向倒序确定。");
            }

            var phaseValues = new[]
            {
                BusbarPhase_e.A,
                BusbarPhase_e.B,
                BusbarPhase_e.C
            };
            return phaseValues.Select(logicalPhase =>
                {
                    var bCabinetWidthOrderIndex = bCabinetWidthOrder
                        .ToList()
                        .IndexOf(logicalPhase);
                    var aCabinetPhysicalPhase =
                        aCabinetWidthOrder[bCabinetWidthOrderIndex];
                    return Array.IndexOf(
                        phaseValues,
                        aCabinetPhysicalPhase);
                })
                .ToList()
                .AsReadOnly();
        }

        internal static int ResolveNearestBCabinetOuterPhaseIndex(
            IReadOnlyList<Point3d> bCabinetPhaseReferencePoints,
            Point3d aCabinetBaseCenter,
            Vector3d cabinetSpanDirection,
            double physicalAlignmentTolerance,
            out double phaseADistanceToACabinet,
            out double phaseCDistanceToACabinet)
        {
            if (bCabinetPhaseReferencePoints == null ||
                bCabinetPhaseReferencePoints.Count != 3 ||
                aCabinetBaseCenter == null ||
                cabinetSpanDirection == null ||
                cabinetSpanDirection.Norm <= 0d)
            {
                throw new InvalidOperationException(
                    "侧对侧第一种固定换相缺少最近外相判定所需的物理位置。");
            }

            var aCabinetSpanCoordinate = BusbarGeometryUtility.Project(
                aCabinetBaseCenter,
                cabinetSpanDirection);
            phaseADistanceToACabinet = Math.Abs(
                BusbarGeometryUtility.Project(
                    bCabinetPhaseReferencePoints[0],
                    cabinetSpanDirection) - aCabinetSpanCoordinate);
            phaseCDistanceToACabinet = Math.Abs(
                BusbarGeometryUtility.Project(
                    bCabinetPhaseReferencePoints[2],
                    cabinetSpanDirection) - aCabinetSpanCoordinate);
            if (Math.Abs(
                    phaseADistanceToACabinet -
                    phaseCDistanceToACabinet) <= physicalAlignmentTolerance)
            {
                throw new InvalidOperationException(
                    "侧对侧第一种固定换相的 B 柜 A/C 两相沿实际柜间方向到 A 柜距离差" +
                    "不超过 " + physicalAlignmentTolerance.ToString("0.###") +
                    " mm，无法唯一确定靠近 A 柜的外侧相。A相距离=" +
                    phaseADistanceToACabinet.ToString("0.###") +
                    "，C相距离=" +
                    phaseCDistanceToACabinet.ToString("0.###") + "。");
            }

            return phaseADistanceToACabinet < phaseCDistanceToACabinet
                ? 0
                : 2;
        }

    }
}
