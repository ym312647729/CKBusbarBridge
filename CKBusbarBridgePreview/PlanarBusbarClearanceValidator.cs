using System;
using System.Collections;
using System.Collections.Generic;
using CKBusbarBridge.DesignGeometry;

namespace CKBusbarBridgePreview
{
    internal sealed class PlanarBusbarClearanceEvaluation
    {
        internal bool IsClear { get; set; }
        internal double MinimumDistance { get; set; }
        internal string FirstGroupKey { get; set; }
        internal string SecondGroupKey { get; set; }
        internal int FirstPieceIndex { get; set; }
        internal int SecondPieceIndex { get; set; }
        internal double[][] FirstCorners { get; set; }
        internal double[][] SecondCorners { get; set; }
    }

    // 兼容入口；正本源码位于DesignUI，预览工程链接编译，避免程序集反向依赖。
    internal static class PlanarBusbarClearanceValidator
    {
        internal static PlanarBusbarClearanceEvaluation Evaluate(IReadOnlyList<PlanarBendBusbarSetting> busbars,
            Func<PlanarBendBusbarSetting, string> selector, double clearance) =>
            Result(BridgeCopperClearance.Evaluate(busbars, selector, clearance));

        internal static PlanarBusbarClearanceEvaluation EvaluateMemberStraightSegments(
            IReadOnlyList<PlanarBendBusbarSetting> busbars, Func<PlanarBendBusbarSetting, string> selector) =>
            Result(BridgeCopperClearance.EvaluateMemberStraightSegments(busbars, selector));

        internal static double CalculateBendSweepHalfSpan(double thickness, double allowance) =>
            BridgeCopperClearance.CalculateBendSweepHalfSpan(thickness, allowance);

        private static PlanarBusbarClearanceEvaluation Result(BridgeCopperClearanceEvaluation value) =>
            new PlanarBusbarClearanceEvaluation { IsClear = value.IsClear, MinimumDistance = value.MinimumDistance,
                FirstGroupKey = value.FirstGroupKey, SecondGroupKey = value.SecondGroupKey,
                FirstPieceIndex = value.FirstPieceIndex, SecondPieceIndex = value.SecondPieceIndex,
                FirstCorners = value.FirstCorners, SecondCorners = value.SecondCorners };

        // 保留穷举对照测试的几何入口，返回同一正本生成的实际直段和折弯实体。
        private static IEnumerable CreateRouteLinkBarSolidRecords(IReadOnlyList<PlanarBendBusbarSetting> busbars,
            Func<PlanarBendBusbarSetting, string> selector, bool includeBends) =>
            BridgeCopperClearance.ReferenceSolids(busbars, selector, includeBends);
    }
}
