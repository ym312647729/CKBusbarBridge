using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Prototype
{
    public sealed partial class BusbarBridgeDraftEditor
    {
        private IReadOnlyList<BridgeDesignIssue> OffsetFrameConnectionIssues(IReadOnlyList<BridgeDesignObject> frames)
        {
            var issues = new List<BridgeDesignIssue>();
            if (frames.Count == 0)
            {
                issues.Add(new BridgeDesignIssue("基架安装链缺少基架，请补齐连接后生成正式模型。", null, "基架连接", false, true));
                return issues.AsReadOnly();
            }
            // 工艺安全基线保留原始推荐位置；联动后的连接由当前模板接口及两侧固定端判断。
            // 按旧占用区间查覆盖会把整体平移后的横向连接架误报为整段缺失。
            foreach (var port in new[] { 0, 1 })
            {
                var frame = port == 0 ? frames[0] : frames[frames.Count - 1];
                var boundary = PlannedFrameBoundary(port);
                if (FrameAtPlannedBoundary(frame, port) && boundary != null &&
                    Math.Abs(FramePortWidth(frame, port) - FramePortWidth(boundary, port)) <= FrameConnectionToleranceMm &&
                    Math.Abs(frame.DriveValue(DesignField.Height) - boundary.DriveValue(DesignField.Height)) <= FrameConnectionToleranceMm) continue;
                var hasGap = TryGetPlannedBoundaryGap(frame, port, out var gap);
                issues.Add(OffsetFrameConnectionIssue(frame.Id, hasGap ? (double?)gap : null));
            }
            for (var i = 1; i < frames.Count; i++)
            {
                var left = frames[i - 1]; var right = frames[i];
                if (FramePortsTouch(left, right)) continue;
                var hasGap = TryGetOffsetFrameInterfaceGap(left, right, out var gap);
                issues.Add(OffsetFrameConnectionIssue(right.Id, hasGap ? (double?)gap : null));
            }
            if (issues.Count == 0)
            {
                // 接口闭合不能代替安全覆盖；反读的实物也须通过原有安全基线校验。
                var safety = ValidateLargeOffsetGroups(frames);
                if (safety.Success) safety = ValidateFrameLayout(frames);
                if (!safety.Success) issues.Add(new BridgeDesignIssue(safety.Message, frames[0].Id, "基架安全范围", false, true));
            }
            return issues.AsReadOnly();
        }

        private BridgeDesignIssue OffsetFrameConnectionIssue(string id, double? gap) => new BridgeDesignIssue(gap.HasValue
            ? "基架安装链存在" + gap.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                " mm空隙，请补齐连接后生成正式模型。"
            : "基架安装接口的位置、方向或截面不兼容，请修复贯通后生成正式模型。", id, "基架连接", false, true);

        private bool TryGetOffsetFrameInterfaceGap(BridgeDesignObject left, BridgeDesignObject right, out double gap)
        {
            gap = 0d;
            if (!FramePortSectionsMatch(left, right)) return false;
            var outlet = _geometry.FrameInterfacePoints(left)[1]; var inlet = _geometry.FrameInterfacePoints(right)[0];
            var normal = LargeOffsetPortNormal(left, 1);
            var x = inlet[0] - outlet[0]; var y = inlet[1] - outlet[1];
            // 拐角与连接架的本体方向可以不同；缺口沿实际出口法向量测，不能套本体长度方向。
            gap = x * normal[0] + y * normal[1];
            return gap > FrameConnectionToleranceMm &&
                Math.Abs(-x * normal[1] + y * normal[0]) <= FrameConnectionToleranceMm &&
                Math.Abs(inlet[2] - outlet[2]) <= FrameConnectionToleranceMm;
        }

        private BridgeEditResult PlanOffsetCopperSupports(BridgeOffsetCopperChange change,
            IReadOnlyList<BridgeDesignObject> proposed, ref BridgeDesignSample geometry)
        {
            var frames = OrderedFrames();
            var members = change.FrameIds.Select(id => frames.Single(f => f.Id == id)).ToArray();
            var previousCenter = change.StartX + (change.EndX - change.StartX) / 2d - change.Shift;
            var installationTail = members[0].X - previousCenter;
            // 反读后保留已在安装公差内的中心尾差，补偿仍按可保存的0.1mm位移进行。
            // 超出公差的位置不能移入安全范围来掩盖原有错位。
            if (Math.Abs(installationTail) > FrameConnectionToleranceMm) installationTail = 0d;
            var minimumShift = double.NegativeInfinity; var maximumShift = double.PositiveInfinity;
            foreach (var member in members)
            {
                var span = member.DriveValue(member.CornerPreview != null || member.FrameTemplateKey == "SmallOffset"
                    ? DesignField.Length : DesignField.Width);
                minimumShift = Math.Max(minimumShift, change.EndX + installationTail - member.X - span / 2d);
                maximumShift = Math.Min(maximumShift, change.StartX + installationTail - member.X + span / 2d);
            }
            var exactMinimumShift = minimumShift; var exactMaximumShift = maximumShift;
            // 优先保持原有精确补偿；实物尺寸在0.1mm公差内时，规划也须使用与最终覆盖校验相同的范围。
            if (minimumShift > maximumShift + .000001)
            {
                minimumShift = exactMinimumShift - installationTail - FrameConnectionToleranceMm;
                maximumShift = exactMaximumShift - installationTail + FrameConnectionToleranceMm;
            }
            if (minimumShift > maximumShift + .000001)
                return new BridgeEditResult(false, "当前定长偏移基架无法容纳偏移铜排安全范围，修改未提交。");
            var planned = frames.ToDictionary(f => f.Id);
            if (minimumShift > .000001 || maximumShift < -.000001)
            {
                var first = members.SingleOrDefault(f => f.CornerPreview?.ClosedEnd == 1) ?? members.First();
                var last = members.SingleOrDefault(f => f.CornerPreview?.ClosedEnd == -1) ?? members.Last();
                var bSide = OffsetCompensationSide(first, frames, true);
                var aSide = OffsetCompensationSide(last, frames, false);
                var minimumCapacityShift = Math.Max(-OffsetCapacity(bSide, false), -OffsetCapacity(aSide, true));
                var maximumCapacityShift = Math.Min(OffsetCapacity(bSide, true), OffsetCapacity(aSide, false));
                if (EffectivePlan.Any(o => o.Kind == DesignObjectKind.Insulator && change.FrameIds.Contains(o.ParentId) && IsLocked(o.Id)))
                { minimumCapacityShift = Math.Max(minimumCapacityShift, 0d); maximumCapacityShift = Math.Min(maximumCapacityShift, 0d); }
                // 补偿长度写入精度为0.1mm；朝安全区内取最近可表达的位移，不能向外舍入。
                minimumShift = Math.Ceiling(Math.Max(minimumShift, minimumCapacityShift) * 10d - .000001) / 10d;
                maximumShift = Math.Floor(Math.Min(maximumShift, maximumCapacityShift) * 10d + .000001) / 10d;
                if (minimumShift > maximumShift + .000001)
                {
                    minimumShift = Math.Ceiling(Math.Max(exactMinimumShift - installationTail - FrameConnectionToleranceMm, minimumCapacityShift) * 10d - .000001) / 10d;
                    maximumShift = Math.Floor(Math.Min(exactMaximumShift - installationTail + FrameConnectionToleranceMm, maximumCapacityShift) * 10d + .000001) / 10d;
                }
                if (minimumShift > maximumShift + .000001)
                    return new BridgeEditResult(false, "两侧普通基架无法在锁长、安装约束和长度范围内完成偏移基架最小位移补偿，修改未提交。");
                var shift = Math.Max(minimumShift, Math.Min(0d, maximumShift));
                foreach (var member in members)
                {
                    var values = member.Values.ToDictionary(p => p.Key, p => p.Value);
                    values[DesignField.X] = member.X + shift; planned[member.Id] = member.WithValues(values);
                }
                var validation = PackOffsetSide(bSide, planned, first, true, shift);
                if (!validation.Success) return validation;
                validation = PackOffsetSide(aSide, planned, last, false, shift);
                if (!validation.Success) return validation;
            }
            var before = CurrentSample.WithGeometry(EffectivePlan, CurrentSample.Routes);
            var editing = CaptureFrameLengthEditing();
            geometry = geometry.WithFrameLengthEditing(new BridgeFrameLengthEditing(
                change.PreserveFrameBaseline(before).BaselineFrames, editing.IndependentFrames,
                editing.FixedEndFrames, editing.LengthAdjustedFrames, editing.OffsetCopperMinimumLengthMm))
                .WithOffsetCopperSafetyShift(before.OffsetCopperSafetyShift + change.Shift);
            var supports = proposed.Where(o => o.Kind == DesignObjectKind.Frame || o.Kind == DesignObjectKind.Insulator)
                .Select(o =>
                {
                    if (o.Kind == DesignObjectKind.Frame) return planned[o.Id];
                    if (!planned.TryGetValue(o.ParentId, out var parent)) return o;
                    var original = frames.Single(f => f.Id == o.ParentId);
                    var delta = parent.Length - original.Length;
                    if (Math.Abs(delta) < .000001) return o;
                    var values = o.Values.ToDictionary(p => p.Key, p => p.Value);
                    values[DesignField.X] = o.X - delta / 2d;
                    return o.WithValues(values);
                });
            geometry = geometry.WithGeometry(geometry.Objects.Where(o => o.Kind != DesignObjectKind.Frame &&
                o.Kind != DesignObjectKind.Insulator).Concat(supports), geometry.Routes);
            return ValidateLengthLocks(frames, planned.Values.ToArray());
        }

        private BridgeDesignObject[] OffsetCompensationSide(BridgeDesignObject owner,
            IReadOnlyList<BridgeDesignObject> frames, bool bSide)
        {
            var chain = new List<BridgeDesignObject>(); var cursor = owner;
            while (chain.Count < frames.Count)
            {
                var next = frames.Where(f => f.Id != cursor.Id &&
                    (bSide ? FramePortsTouch(f, cursor) : FramePortsTouch(cursor, f))).ToArray();
                if (next.Length == 0) break;
                if (next.Length != 1 || chain.Any(f => f.Id == next[0].Id))
                    throw new InvalidOperationException("偏移基架两侧安装链不唯一，不能按距离猜测补偿对象。");
                var frame = next[0];
                if (!LargeOffsetOrdinary(frame) || IsIndependentFrame(frame.Id) ||
                    EffectivePlan.Any(o => o.ParentId == frame.Id && IsLocked(o.Id))) break;
                var ports = _geometry.FrameInterfacePoints(frame);
                if (ports[1][0] <= ports[0][0] || Math.Abs(ports[1][1] - ports[0][1]) > FrameConnectionToleranceMm)
                    throw new InvalidOperationException("偏移基架外侧普通链的模板方向不支持桥向补偿。");
                chain.Add(frame); cursor = frame;
            }
            return chain.ToArray();
        }

        private double OffsetCapacity(IEnumerable<BridgeDesignObject> side, bool grow) => side.Sum(frame =>
            IsLocked(frame.Id) || HasIndependentLengthAdjustment(frame.Id) || !frame.EditableFields.Contains(DesignField.Length)
                ? 0d : Math.Max(0d, grow ? 1400d - frame.DriveValue(DesignField.Length) : frame.DriveValue(DesignField.Length) - 200d));

        private BridgeEditResult PackOffsetSide(BridgeDesignObject[] side, Dictionary<string, BridgeDesignObject> planned,
            BridgeDesignObject owner, bool bSide, double shift)
        {
            var remaining = bSide ? shift : -shift;
            foreach (var frame in side)
            {
                var capacity = OffsetCapacity(new[] { frame }, remaining > 0d);
                var delta = Math.Sign(remaining) * Math.Min(Math.Abs(remaining), capacity);
                var values = frame.Values.ToDictionary(p => p.Key, p => p.Value);
                values[DesignField.Length] = frame.DriveValue(DesignField.Length) + delta;
                planned[frame.Id] = frame.WithValues(values); remaining -= delta;
            }
            if (Math.Abs(remaining) > .000001) return new BridgeEditResult(false, "普通基架补偿量无法完整保存，修改未提交。");
            var cursor = _geometry.FrameInterfacePoints(planned[owner.Id])[bSide ? 0 : 1][0];
            var previous = _geometry.FrameInterfacePoints(owner)[bSide ? 0 : 1][0];
            foreach (var frame in side)
            {
                // 保留既有接口精度内的尾差，避免从偏移架向外铺排时将尾差压到固定端。
                var originalPorts = _geometry.FrameInterfacePoints(frame);
                cursor += originalPorts[bSide ? 1 : 0][0] - previous;
                var target = planned[frame.Id]; var length = target.DriveValue(DesignField.Length);
                var values = target.Values.ToDictionary(p => p.Key, p => p.Value);
                values[DesignField.X] = cursor + (bSide ? -1d : 1d) * length / 2d;
                target = target.WithValues(values); planned[frame.Id] = target;
                cursor += (bSide ? -1d : 1d) * length;
                previous = originalPorts[bSide ? 0 : 1][0];
            }
            if (side.Length > 0 && Math.Abs(cursor - _geometry.FrameInterfacePoints(side.Last())[bSide ? 0 : 1][0]) > FrameConnectionToleranceMm)
                return new BridgeEditResult(false, "普通基架补偿会移动外侧固定接口，修改未提交。");
            return new BridgeEditResult(true);
        }
    }
}
