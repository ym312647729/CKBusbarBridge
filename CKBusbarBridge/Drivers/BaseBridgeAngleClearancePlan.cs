using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridge.DesignGeometry;
using CKBusbarBridge.Prototype;

namespace CKBusbarBridge.Drivers
{
    /// <summary>一次选档的完整目标；压缩时保留合法的原深度尺寸，逻辑档位为不开。</summary>
    internal sealed class BaseBridgeAngleNotchTarget
    {
        internal double DepthMm { get; }
        internal bool Suppressed { get; }
        internal double? LengthMm { get; }
        internal double? EndOffsetMm { get; }
        internal double ActiveDepthMm => Suppressed ? 0d : DepthMm;

        internal BaseBridgeAngleNotchTarget(double depthMm, bool suppressed,
            double? lengthMm = null, double? endOffsetMm = null)
        {
            if (!Positive(depthMm) || depthMm > BridgeAngleNotchPlan.MaximumDepthMm + BridgeDesignPrecision.LengthToleranceMm ||
                lengthMm.HasValue != endOffsetMm.HasValue ||
                lengthMm.HasValue && (!Positive(lengthMm.Value) || !Positive(endOffsetMm.Value)))
                throw new InvalidOperationException("基座桥端角钢豁口目标尺寸无效。");
            DepthMm = depthMm;
            Suppressed = suppressed;
            LengthMm = lengthMm;
            EndOffsetMm = endOffsetMm;
        }

        internal static BaseBridgeAngleNotchTarget ForOpening(string template, double openingHeightMm, double retainedDepthMm)
        {
            if (!Positive(openingHeightMm)) throw new InvalidOperationException("基座桥端角钢缺少有效的基座开孔高度。");
            switch (template)
            {
                // 008/A08原模板：开孔上下保留70.0/80.0 mm，D3从角钢上端定位。
                case "020":
                case "031":
                    return new BaseBridgeAngleNotchTarget(retainedDepthMm, true, openingHeightMm - 150.0, 80.0);
                // 030是桥口上下横角钢，不套用020/031立角钢的纵向尺寸关系。
                case "030":
                    return new BaseBridgeAngleNotchTarget(retainedDepthMm, true);
                default:
                    throw new InvalidOperationException("未核验基座桥端角钢的豁口尺寸关系：" + template);
            }
        }

        internal BaseBridgeAngleNotchTarget WithDepth(double depthMm) =>
            new BaseBridgeAngleNotchTarget(depthMm == 0d ? DepthMm : depthMm, depthMm == 0d, LengthMm, EndOffsetMm);

        internal bool Matches(BaseBridgeAngleNotchTarget other) => other != null && Suppressed == other.Suppressed &&
            Math.Abs(DepthMm - other.DepthMm) <= BridgeDesignPrecision.LengthToleranceMm &&
            Same(LengthMm, other.LengthMm) && Same(EndOffsetMm, other.EndOffsetMm);

        // 候选实体及记录必须包含长度、位置和压缩状态，不能只以深度复用。
        internal string Key => (Suppressed ? "压缩" : "开启") + "|" + Format(DepthMm) + "|" + Format(LengthMm) + "|" + Format(EndOffsetMm);

        private static bool Same(double? a, double? b) => a.HasValue == b.HasValue &&
            (!a.HasValue || Math.Abs(a.Value - b.Value) <= BridgeDesignPrecision.LengthToleranceMm);
        private static bool Positive(double value) => value > 0d && !double.IsInfinity(value);
        private static string Format(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "原模板";
    }

    internal static class BaseBridgeAngleClearancePlan
    {
        internal static void PlanAndApply(IReadOnlyList<BaseBridgeAngleNotchTarget> geometry,
            IReadOnlyList<string> labels, IReadOnlyList<double> candidates,
            Func<int, BaseBridgeAngleNotchTarget, (double Distance, string Copper)> measure,
            Action<IReadOnlyList<BaseBridgeAngleNotchTarget>> apply)
        {
            if (geometry == null || geometry.Count == 0 || labels == null || labels.Count != geometry.Count ||
                geometry.Any(target => target == null) || candidates == null || candidates.Count == 0)
                throw new InvalidOperationException("基座桥端角钢完整目标或选档输入缺失。");
            if (measure == null) throw new ArgumentNullException(nameof(measure));
            if (apply == null) throw new ArgumentNullException(nameof(apply));
            var frozen = new BaseBridgeAngleNotchTarget[geometry.Count];
            for (var i = 0; i < geometry.Count; i++)
            {
                var index = i;
                var depth = SelectDepth(labels[i], candidates, candidate => measure(index, geometry[index].WithDepth(candidate)));
                frozen[i] = geometry[i].WithDepth(depth);
            }
            // 任意实例未完成选档时不会进入共享处理或正式写入。
            apply(Array.AsReadOnly(frozen));
        }

        internal static double SelectDepth(string angle, IReadOnlyList<double> candidates,
            Func<double, (double Distance, string Copper)> measure)
        {
            (double Distance, string Copper) result = default;
            foreach (var depth in candidates)
            {
                result = measure(depth);
                if (!(result.Distance >= 0d) || double.IsInfinity(result.Distance) || string.IsNullOrWhiteSpace(result.Copper))
                    throw new InvalidOperationException("无法取得基座桥端角钢的实际绝缘距离：" + angle);
                if (MeetsClearance(result.Distance)) return depth;
            }

            throw ClearanceFailure(angle, result, "最大豁口仍不足");
        }

        internal static bool MeetsClearance(double distance) =>
            distance + BridgeDesignPrecision.LengthToleranceMm >= BridgeRaisedLapGeometry.RequiredClearanceMm;

        internal static InvalidOperationException ClearanceFailure(string angle, (double Distance, string Copper) result, string stage) =>
            new InvalidOperationException(FormattableString.Invariant(
                $"基座桥端角钢绝缘距离不足，{stage}：{angle} / {result.Copper}，实际{result.Distance:0.0} mm，要求{BridgeRaisedLapGeometry.RequiredClearanceMm:0.0} mm。"));
    }
}
