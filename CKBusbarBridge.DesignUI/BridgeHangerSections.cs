using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Prototype
{
    public enum BridgeHangerSectionKind { Whole, BeforeOffset, AcrossOffset, AfterOffset }

    internal sealed class BridgeHangerSection
    {
        internal BridgeHangerSectionKind Kind { get; }
        internal double[] Origin { get; }
        internal double[] Axis { get; }
        internal double Start { get; }
        internal double End { get; }
        internal IReadOnlyList<BridgeDesignObject> Frames { get; }

        internal BridgeHangerSection(BridgeHangerSectionKind kind, double[] origin, double[] axis, double start, double end,
            IEnumerable<BridgeDesignObject> frames)
        {
            Kind = kind; Origin = origin; Axis = axis; Start = start; End = end;
            Frames = Array.AsReadOnly(frames.ToArray());
        }

        internal double Station(double[] point) => HangerVectors.Dot(HangerVectors.Sub(point, Origin), Axis);
        internal static string Label(BridgeHangerSectionKind kind)
        {
            switch (kind)
            {
                case BridgeHangerSectionKind.BeforeOffset: return "前段";
                case BridgeHangerSectionKind.AcrossOffset: return "中间段";
                case BridgeHangerSectionKind.AfterOffset: return "后段";
                default: return "整段";
            }
        }
    }

    public sealed partial class BridgeHangerLayoutPlanner
    {
        private const double DefaultStraightHangerSpanMm = 3000.0;

        internal bool HasOffsetCorners(IReadOnlyList<BridgeDesignObject> objects) =>
            objects.Any(frame => frame.Kind == DesignObjectKind.Frame && frame.FrameTemplateKey == "LargeOffsetCorner");

        private double[][] Ports(BridgeDesignObject frame) => _frameGeometry.FrameInterfacePoints(frame, useDrivenPosition: true);

        internal BridgeHangerSection[] Sections(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects, BridgeHangerLayout layout)
        {
            if (!TryStart(sample, objects, layout, out var zero))
                throw new InvalidOperationException("原起始边无法识别，请重新确认吊架基准。");
            var frames = Frames(objects);
            var forward = Forward(layout.Direction.Value);
            Func<double[], double> station = point => HangerVectors.Dot(HangerVectors.Sub(point, zero), forward);
            var end = station(Ports(frames.Last())[1]);
            if (!HasOffsetCorners(objects))
                return new[] { new BridgeHangerSection(BridgeHangerSectionKind.Whole, zero, forward, 0.0, end, frames) };

            var corners = frames.Where(frame => frame.FrameTemplateKey == "LargeOffsetCorner").ToArray();
            if (corners.Length != 2 || corners[0].CornerPreview?.ClosedEnd != 1 || corners[1].CornerPreview?.ClosedEnd != -1)
                throw new InvalidOperationException("大偏移结构缺少一对已核验的前、后拐角。");
            var firstCorner = Array.IndexOf(frames, corners[0]);
            var lastCorner = Array.IndexOf(frames, corners[1]);
            var connectors = frames.Skip(firstCorner + 1).Take(lastCorner - firstCorner - 1).ToArray();
            if (connectors.Length == 0 || connectors.Any(frame => frame.FrameTemplateKey != "Regular"))
                throw new InvalidOperationException("大偏移中间段缺少已核验的普通连接基架。");

            var middleStart = Ports(connectors[0])[0];
            var middleEnd = Ports(connectors.Last())[1];
            var delta = HangerVectors.Sub(middleEnd, middleStart);
            var length = Math.Sqrt(HangerVectors.Dot(delta, delta));
            if (length <= Tolerance) throw new InvalidOperationException("大偏移中间段没有有效跨度。");
            var middleAxis = HangerVectors.Unit(delta);
            if (Math.Asin(Math.Min(1.0, Math.Abs(HangerVectors.Dot(forward, middleAxis)))) * 180.0 / Math.PI > Tolerance)
                throw new InvalidOperationException("大偏移中间段方向与拐角关系不符。");
            for (var index = firstCorner; index < lastCorner; index++)
            {
                var left = frames[index];
                var right = frames[index + 1];
                var seam = HangerVectors.Sub(Ports(left)[1], Ports(right)[0]);
                var leftSpan = left.DriveValue(left.CornerPreview == null ? DesignField.Width : DesignField.Length);
                var rightSpan = right.DriveValue(right.CornerPreview == null ? DesignField.Width : DesignField.Length);
                if (Math.Sqrt(HangerVectors.Dot(seam, seam)) > Tolerance || Math.Abs(leftSpan - rightSpan) > Tolerance ||
                    Math.Abs(left.DriveValue(DesignField.Height) - right.DriveValue(DesignField.Height)) > Tolerance)
                    throw new InvalidOperationException("大偏移中间段基架未贯通，请先核对安装接口。");
            }
            var beforeEnd = station(Ports(corners[0])[0]);
            var afterStart = station(Ports(corners[1])[1]);
            if (beforeEnd < -Tolerance || afterStart < beforeEnd - Tolerance || end < afterStart - Tolerance)
                throw new InvalidOperationException("大偏移前、中、后三段的接口顺序无效。");
            return new[]
            {
                new BridgeHangerSection(BridgeHangerSectionKind.BeforeOffset, zero, forward, 0.0, beforeEnd, frames.Take(firstCorner)),
                new BridgeHangerSection(BridgeHangerSectionKind.AcrossOffset, middleStart, middleAxis, 0.0, length, connectors),
                new BridgeHangerSection(BridgeHangerSectionKind.AfterOffset, zero, forward, afterStart, end, frames.Skip(lastCorner + 1))
            };
        }

        internal double EqualStation(BridgeHangerSection section, int index, int count)
        {
            var step = (section.End - section.Start) / count;
            var cellStart = section.Start + index * step;
            var cellEnd = cellStart + step;
            var nominal = cellStart + step / 2.0;
            var candidate = section.Frames.Where(frame => _hangerGeometry.RequiresHanger(frame.FrameTemplateKey)).Select(frame =>
            {
                var range = Ports(frame).Select(section.Station).ToArray();
                return new { Start = Math.Max(cellStart, range.Min() + BridgeHangerGeometry.LowerHalfLength),
                    End = Math.Min(cellEnd, range.Max() - BridgeHangerGeometry.LowerHalfLength) };
            }).Where(range => range.Start <= range.End).Select(range => Math.Max(range.Start, Math.Min(nominal, range.End)))
                .OrderBy(value => Math.Abs(value - nominal)).ThenBy(value => value).Select(value => (double?)value).FirstOrDefault();
            // 无可用区间时保留名义点，复用定位门禁说明接缝、越界或重叠原因。
            return candidate ?? nominal;
        }

        internal BridgeHangerSection SectionFor(BridgeHangerSetting setting, IReadOnlyList<BridgeHangerSection> sections)
        {
            if (sections.Count == 1) return setting.Section == BridgeHangerSectionKind.AcrossOffset ? null : sections[0];
            if (setting.Section.HasValue && setting.Section != BridgeHangerSectionKind.Whole)
                return sections.SingleOrDefault(section => section.Kind == setting.Section.Value);
            if (setting.PreferredFrameId != null)
            {
                var preferred = sections.SingleOrDefault(section => section.Frames.Any(frame => frame.Id == setting.PreferredFrameId));
                if (preferred != null) return preferred;
            }
            return sections.Where(section => section.Kind != BridgeHangerSectionKind.AcrossOffset)
                .FirstOrDefault(section => setting.Distance.HasValue && setting.Distance.Value >= section.Start - Tolerance &&
                    setting.Distance.Value <= section.End + Tolerance);
        }

        internal BridgeHangerLayout InitialLayout(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects)
        {
            // 尚未读取吊架上下文的方案不自动生成吊架；实际读取失败仍由定位校验报告。
            if (sample?.Coordinates == null ||
                (sample.HangerRoof == null && string.IsNullOrWhiteSpace(sample.HangerRoofProblem))) return null;
            var hasOffsetSections = HasOffsetCorners(objects);
            // 已反读的旧常规方案没有吊架记录时，仍按原实物零套处理。
            if (!hasOffsetSections && sample.ReadbackBaseline != null) return null;
            var start = Frames(objects).FirstOrDefault();
            if (start == null) return null;

            var layout = new BridgeHangerLayout(start.Id, start.DriveValue(DesignField.Direction), Array.Empty<BridgeHangerSetting>());
            var targetSection = hasOffsetSections ? BridgeHangerSectionKind.AcrossOffset : BridgeHangerSectionKind.Whole;
            try
            {
                var defaultCount = 1;
                if (!hasOffsetSections)
                {
                    var section = Sections(sample, objects, layout).Single();
                    var countFromSpan = Math.Ceiling((section.End - section.Start) / DefaultStraightHangerSpanMm);
                    if (!HangerVectors.Finite(countFromSpan) || countFromSpan <= 0 || countFromSpan > int.MaxValue)
                        throw new InvalidOperationException("没有可用于默认吊架布置的有效桥向跨度。");
                    defaultCount = (int)countFromSpan;
                }
                return Resize(sample, objects, layout, new Dictionary<BridgeHangerSectionKind, int>
                    { [targetSection] = defaultCount }, Array.Empty<string>());
            }
            catch (InvalidOperationException)
            {
                return new BridgeHangerLayout(layout.StartFrameId, layout.Direction, new[]
                {
                    new BridgeHangerSetting("hanger-" + Guid.NewGuid().ToString("N"), null, null, targetSection, true)
                });
            }
        }

        internal BridgeHangerLayout Resize(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects, BridgeHangerLayout layout,
            IReadOnlyDictionary<BridgeHangerSectionKind, int> counts, IReadOnlyCollection<string> remove)
        {
            if (counts.Values.Any(count => count < 0)) throw new InvalidOperationException("吊架数量必须是非负整数。");
            if (counts.Values.Aggregate(0L, (sum, count) => sum + count) > int.MaxValue)
                throw new InvalidOperationException("吊架总套数超出计数范围。");
            var expectedSections = HasOffsetCorners(objects) ? 3 : 1;
            if (counts.Count == expectedSections && counts.Values.All(count => count == 0))
                return new BridgeHangerLayout(layout?.StartFrameId, layout?.Direction, Array.Empty<BridgeHangerSetting>());
            if (layout == null)
            {
                var start = Frames(objects).FirstOrDefault();
                layout = new BridgeHangerLayout(start?.Id, start?.DriveValue(DesignField.Direction), Array.Empty<BridgeHangerSetting>());
            }
            if (layout.StartFrameId == null && layout.Settings.Count == 0)
            {
                var start = Frames(objects).FirstOrDefault();
                layout = new BridgeHangerLayout(start?.Id, start?.DriveValue(DesignField.Direction), layout.Settings);
            }
            var sections = Sections(sample, objects, layout);
            if (counts.Keys.Any(kind => sections.All(section => section.Kind != kind)))
                throw new InvalidOperationException("吊架分段已变化，请重新输入各段数量。");
            var groups = layout.Settings.ToLookup(setting => SectionFor(setting, sections)?.Kind);
            var result = new List<BridgeHangerSetting>();
            foreach (var section in sections)
            {
                var existing = groups[section.Kind].ToArray();
                if (!counts.TryGetValue(section.Kind, out var count) || count == existing.Length && existing.All(item => item.Distance.HasValue))
                { result.AddRange(existing); continue; }
                if (count == 0) continue;
                if (section.End - section.Start <= Tolerance || section.Frames.Count == 0)
                    throw new InvalidOperationException(BridgeHangerSection.Label(section.Kind) + "没有可用于布置吊架的直段。");
                // 数量可通过实物安装占位证明上界；不存在固定工艺上限。
                if (count > (section.End - section.Start) / (2.0 * BridgeHangerGeometry.LowerHalfLength - Tolerance))
                    throw new InvalidOperationException(BridgeHangerSection.Label(section.Kind) + "的吊架数量无法容纳模板实际占位。");
                var retained = count < existing.Length ? existing.Where(setting => !remove.Contains(setting.Id)).ToList() : existing.ToList();
                if (count < existing.Length && retained.Count != count)
                    throw new InvalidOperationException("请在" + BridgeHangerSection.Label(section.Kind) + "勾选正确数量的待移除吊架。");
                while (retained.Count < count)
                    retained.Add(new BridgeHangerSetting("hanger-" + Guid.NewGuid().ToString("N"), null, null, section.Kind, true));
                for (var index = 0; index < retained.Count; index++)
                {
                    var item = retained[index];
                    result.Add(item.IsAutomatic
                        ? new BridgeHangerSetting(item.Id, EqualStation(section, index, count), null, section.Kind, true)
                        : item);
                }
            }
            result.AddRange(groups[null].Where(setting => !remove.Contains(setting.Id)));
            return new BridgeHangerLayout(layout.StartFrameId, layout.Direction, result);
        }
    }
}
