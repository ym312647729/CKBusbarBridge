using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CKBusbarBridge.Prototype
{
    // 安全范围来自已核验的工艺规划基线，不能用人工加长后的基架外形替代。
    internal sealed class BridgeOffsetCopperChange
    {
        internal string SectionId { get; private set; }
        internal string[] FrameIds { get; private set; }
        internal double Shift { get; private set; }
        internal double StartX { get; private set; }
        internal double EndX { get; private set; }

        // 侧对侧基座1的零偏移固定换相保留独立竖排工艺及反读编辑入口。
        internal static bool SupportsMiddleEditing(BridgeDesignSample sample) => sample != null &&
            (sample.ScenarioKey == "FaceToFace" || sample.ScenarioKey == "SideToSideBase2");

        internal static BridgeOffsetCopperChange Create(BridgeDesignSample sample,
            IReadOnlyList<BridgeDesignObject> proposed)
        {
            if (!SupportsMiddleEditing(sample)) return null;
            var route = sample.CopperEditRoutes.SingleOrDefault(r => r.Phase == "B");
            if (route == null) return null;
            var before = sample.Objects.ToDictionary(o => o.Id);
            var after = proposed.ToDictionary(o => o.Id);
            var offsets = Enumerable.Range(1, route.PointIds.Count - 1).Where(i =>
                Math.Abs(before[route.PointIds[i]].Y - before[route.PointIds[i - 1]].Y) > .001d).ToArray();
            if (offsets.Length == 0) return null;
            if (offsets.Length != 1) throw new InvalidOperationException("偏移段必须唯一，不能按位置猜测所属基架。");
            var index = offsets[0];
            if (CKBusbarBridge.DesignGeometry.BridgeOffsetRules.IsWidened(
                before[route.PointIds[index]].Y - before[route.PointIds[index - 1]].Y)) return null;
            var start = route.PointIds[index - 1]; var end = route.PointIds[index];
            if (!after.ContainsKey(start) || !after.ContainsKey(end))
                throw new InvalidOperationException("偏移段边界缺失，不能修改安全范围。");
            var baseline = sample.FrameLengthBaseline.Where(IsMember).ToArray();
            var corners = baseline.Where(f => f.CornerPreview != null).ToArray();
            var references = corners.Length == 0 ? baseline : corners;
            if (references.Length != (corners.Length == 0 ? 1 : 2) ||
                references.Any(f => Math.Abs(f.X - references[0].X) > BridgeDesignPrecision.LengthComparisonToleranceMm ||
                    Math.Abs(f.DriveValue(DesignField.Length) - references[0].DriveValue(DesignField.Length)) > BridgeDesignPrecision.LengthComparisonToleranceMm) ||
                baseline.Any(f => !after.ContainsKey(f.Id)))
                throw new InvalidOperationException("偏移基架或工艺安全基线缺失、不唯一，请重读并规划。");
            var length = references[0].DriveValue(DesignField.Length);
            var lap = sample.Parameters.OverlapLengthMm;
            var width = sample.Source.BusbarWidth;
            var minimum = sample.SavedFrameLengthEditing?.OffsetCopperMinimumLengthMm ?? length + 2d * width + lap;
            if (after[end].X - after[start].X + lap < minimum - .001d)
                throw new InvalidOperationException("偏移段排最小长度为" + minimum.ToString("0.0", CultureInfo.InvariantCulture) +
                    " mm（含搭接），本次联动未提交。");
            // 断点按0.1mm显示，原始工艺中心可能在0.05mm上。沿用实际基线并累加编辑位移，
            // 与铜排重规划保留原坐标精度的规则一致，不能把显示取整尾差再次计入补偿。
            var shift = ((after[start].X - before[start].X) + (after[end].X - before[end].X)) / 2d;
            var center = references[0].X + sample.OffsetCopperSafetyShift + shift;
            var coverage = references[0].CornerPreview?.MinimumSafeLengthMm ?? length;
            return new BridgeOffsetCopperChange
            {
                SectionId = BridgeCopperSection.IdFor(index), FrameIds = baseline.Select(f => f.Id).ToArray(),
                Shift = shift, StartX = center - coverage / 2d, EndX = center + coverage / 2d
            };
        }

        internal static bool IsMember(BridgeDesignObject frame) => frame.Kind == DesignObjectKind.Frame &&
            (frame.FrameTemplateKey == "SmallOffset" && !IsWidenedTerminal(frame) || frame.CornerPreview != null ||
             frame.FrameTemplateKey == "Regular" && frame.Role.Contains("大偏移"));

        internal static bool IsWidenedTerminal(BridgeDesignObject frame)
        {
            if (frame?.FrameTemplateKey != "SmallOffset" || frame.SmallOffsetPreview == null) return false;
            var offset = frame.SmallOffsetPreview.InletSize + frame.SmallOffsetPreview.OutletSize;
            // 一侧基座与扩宽架等宽时，该032端头的两孔都可能居中。
            return offset <= BridgeDesignPrecision.LengthComparisonToleranceMm ||
                CKBusbarBridge.DesignGeometry.BridgeOffsetRules.IsWidened(offset);
        }

        internal static double? CaptureMinimum(BridgeDesignSample sample)
        {
            var members = sample.Objects.Where(IsMember).ToArray();
            var references = members.Any(f => f.CornerPreview != null) ? members.Where(f => f.CornerPreview != null).ToArray() : members;
            return references.Length == 0 ? (double?)null : references[0].DriveValue(DesignField.Length) +
                2 * sample.Source.BusbarWidth + sample.Parameters.OverlapLengthMm;
        }

        internal BridgeFrameLengthEditing PreserveFrameBaseline(BridgeDesignSample sample)
        {
            var editing = sample.SavedFrameLengthEditing;
            return new BridgeFrameLengthEditing(sample.FrameLengthBaseline, editing?.IndependentFrames ?? Array.Empty<string>(),
                editing?.FixedEndFrames ?? Array.Empty<string>(), editing?.LengthAdjustedFrames ?? Array.Empty<string>(), editing?.OffsetCopperMinimumLengthMm);
        }
    }
}
