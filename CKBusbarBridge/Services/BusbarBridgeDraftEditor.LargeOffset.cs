using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CKBusbarBridge.Prototype
{
    public sealed partial class BusbarBridgeDraftEditor
    {
        private sealed class LargeOffsetGroup
        {
            public BridgeDesignObject[] Baseline;
            public BridgeDesignObject[] Members;
        }

        private static BridgeEditResult LargeOffsetFailure(string reason) =>
            new BridgeEditResult(false, "大偏移整组调整未提交：" + reason);

        // 端口法向指向架外；拐角侧向口的截面跨度是Length，普通架端口的截面跨度是Width。
        private double[] LargeOffsetPortNormal(BridgeDesignObject frame, int port)
        {
            var corner = frame.CornerPreview;
            var side = corner != null && (corner.ClosedEnd > 0 ? port == 1 : port == 0);
            var x = side ? 0d : corner == null ? (port == 0 ? -1d : 1d) : -corner.ClosedEnd;
            var y = side ? -corner.ClosedSide : 0d;
            var angle = frame.DriveValue(DesignField.Direction) * Math.PI / 180d;
            return new[] { x * Math.Cos(angle) - y * Math.Sin(angle), x * Math.Sin(angle) + y * Math.Cos(angle) };
        }

        private double LargeOffsetPortSpan(BridgeDesignObject frame, int port) =>
            frame.DriveValue(frame.CornerPreview != null && (frame.CornerPreview.ClosedEnd > 0 ? port == 1 : port == 0)
                ? DesignField.Length : DesignField.Width);

        private bool LargeOffsetPortsMatch(BridgeDesignObject left, BridgeDesignObject right)
        {
            if (left.SmallOffsetPreview != null || right.SmallOffsetPreview != null ||
                left.WallFramePreview != null || right.WallFramePreview != null ||
                left.FrameTemplateKey == "SmallOffset" || right.FrameTemplateKey == "SmallOffset") return false;
            var a = _geometry.FrameInterfacePoints(left)[1]; var b = _geometry.FrameInterfacePoints(right)[0];
            var an = LargeOffsetPortNormal(left, 1); var bn = LargeOffsetPortNormal(right, 0);
            return BridgeDesignPrecision.PositionDistance(a, b) <= FrameConnectionToleranceMm &&
                an[0] * bn[0] + an[1] * bn[1] <= -Math.Cos(Math.PI / 180d) &&
                Math.Abs(LargeOffsetPortSpan(left, 1) - LargeOffsetPortSpan(right, 0)) <= FrameConnectionToleranceMm &&
                Math.Abs(left.DriveValue(DesignField.Height) - right.DriveValue(DesignField.Height)) <= FrameConnectionToleranceMm;
        }

        private bool LargeOffsetConnector(BridgeDesignObject frame) => frame.FrameTemplateKey == "Regular" &&
            frame.Role.Contains("大偏移") && frame.CornerPreview == null;

        public bool IsLargeOffsetConnector(string id) => Find(id) != null && LargeOffsetConnector(Find(id));

        public string LargeOffsetConnectorLengthHint(string id)
        {
            if (!IsLargeOffsetConnector(id)) return null;
            var result = FindLargeOffsetGroup(id, OrderedFrames(), out var group);
            if (!result.Success) return result.Message;
            var count = group.Members.Length - 2;
            if (count == 1) return "仅1节连接基架，两端拐角固定，没有长度补偿对象。";
            var terminal = group.Members[group.Members.Length - 2].Id == id;
            return "共" + count.ToString(CultureInfo.InvariantCulture) + "节连接基架；" +
                (terminal ? "固定出口，向前补偿" : "固定入口，向后补偿") +
                "，跳过长度锁定节继续分配；每节200～1400 mm，总跨度及共享宽度不变。";
        }

        private BridgeEditResult TraceLargeOffsetGroup(BridgeDesignObject start, IReadOnlyList<BridgeDesignObject> frames,
            out BridgeDesignObject[] members)
        {
            members = null;
            if (start?.CornerPreview?.ClosedEnd != 1) return LargeOffsetFailure("缺少有效B侧拐角接口，请重读并规划。");
            var chain = new List<BridgeDesignObject> { start };
            while (chain.Count <= frames.Count)
            {
                var next = frames.Where(f => f.Id != chain.Last().Id && LargeOffsetPortsMatch(chain.Last(), f)).ToArray();
                if (next.Length != 1 || chain.Any(f => f.Id == next[0].Id))
                    return LargeOffsetFailure("结构组接口缺失、重复或不相容，请先修复贯通。");
                var frame = next[0]; chain.Add(frame);
                if (frame.CornerPreview?.ClosedEnd == -1)
                {
                    if (chain.Count < 3) return LargeOffsetFailure("缺少中间连接基架。");
                    members = chain.ToArray(); return new BridgeEditResult(true);
                }
                if (!LargeOffsetConnector(frame)) return LargeOffsetFailure("拐角之间存在不支持的连接模板。");
            }
            return LargeOffsetFailure("结构组接口形成循环。");
        }

        private BridgeEditResult FindLargeOffsetGroup(string selectedId, IReadOnlyList<BridgeDesignObject> frames,
            out LargeOffsetGroup group)
        {
            group = null;
            var baseline = CurrentSample.FrameLengthBaseline.ToArray();
            foreach (var start in baseline.Where(f => f.CornerPreview?.ClosedEnd == 1))
            {
                var trace = TraceLargeOffsetGroup(start, baseline, out var original);
                if (!trace.Success) return trace;
                if (!original.Any(f => f.Id == selectedId)) continue;
                var current = original.Select(f => frames.SingleOrDefault(p => p.Id == f.Id)).ToArray();
                if (current.Any(f => f == null)) return LargeOffsetFailure("结构组成员缺失，请重读并规划。");
                group = new LargeOffsetGroup { Baseline = original, Members = current };
                return ValidateLargeOffsetGroup(group, frames);
            }
            return LargeOffsetFailure("未找到已核验的大偏移结构组，请重读并规划。");
        }

        private BridgeEditResult ValidateLargeOffsetGroup(LargeOffsetGroup group, IReadOnlyList<BridgeDesignObject> frames)
        {
            var baseline = group.Baseline; var members = group.Members;
            var center = baseline[0].X + CurrentSample.OffsetCopperSafetyShift;
            var safeLength = baseline[0].CornerPreview.MinimumSafeLengthMm ?? baseline[0].DriveValue(DesignField.Length);
            var length = members[0].DriveValue(DesignField.Length);
            foreach (var set in new[] { baseline, members })
            {
                var shared = set[0].DriveValue(DesignField.Length);
                var setCenter = set[0].X;
                for (var i = 0; i < set.Length; i++)
                {
                    var frame = set[i]; var corner = i == 0 || i == set.Length - 1;
                    var direction = Math.Abs(Math.IEEERemainder(frame.DriveValue(DesignField.Direction), 360d));
                    if (new[] { frame.X, frame.Y, frame.Z, frame.Length, frame.Width, frame.Height, direction }
                            .Any(v => double.IsNaN(v) || double.IsInfinity(v)) || frame.Length <= 0 || frame.Width <= 0 || frame.Height <= 0 ||
                        Math.Abs(frame.X - setCenter) > FrameConnectionToleranceMm ||
                        Math.Abs(frame.DriveValue(corner ? DesignField.Length : DesignField.Width) - shared) > FrameConnectionToleranceMm ||
                        Math.Abs(direction - (corner ? 0d : 90d)) > 1.0)
                        return LargeOffsetFailure("共享尺寸、桥向中心或模板方向不一致，请恢复合法规划基线。");
                    if (!corner && (frame.DriveValue(DesignField.Length) < 200d || frame.DriveValue(DesignField.Length) > 1400d))
                        return LargeOffsetFailure("连接基架长度必须在200.0～1400.0 mm范围内。");
                    if (i > 0 && !LargeOffsetPortsMatch(set[i - 1], frame))
                        return LargeOffsetFailure("组内安装接口不相容，请先修复贯通。");
                }
            }
            // 覆盖区间来自规划时的25.0 mm豁口，制造下限独立检查。
            if (length < 300d || length < safeLength - FrameConnectionToleranceMm ||
                Math.Abs(members[0].X - center) + safeLength / 2d > length / 2d + FrameConnectionToleranceMm)
                return LargeOffsetFailure("目标长度未覆盖最小安全范围，最小安全长度为" + safeLength.ToString("0.0", CultureInfo.InvariantCulture) + " mm。");
            for (var i = 0; i < members.Length; i++)
            {
                var original = baseline[i]; var current = members[i];
                // 拐角没有正反装；仅连接架需要保留当前安装方式，不能比较旧配方的拐角标记。
                if (current.CornerPreview == null)
                {
                    var installed = CurrentSample.Objects.SingleOrDefault(f => f.Id == current.Id);
                    if (installed == null || Math.Abs(installed.DriveValue(DesignField.Installation) - current.DriveValue(DesignField.Installation)) > .000001)
                        return LargeOffsetFailure("长度编辑不能改变" + current.DisplayName + "当前载入的安装方式。");
                }
                var fixedFields = new List<DesignField> { DesignField.Z, DesignField.Height };
                if (i == 0 || i == members.Length - 1) fixedFields.AddRange(new[] { DesignField.Y, DesignField.Width });
                if (fixedFields.Any(f => Math.Abs(original.DriveValue(f) - current.DriveValue(f)) > FrameConnectionToleranceMm) ||
                    Math.Abs(Math.IEEERemainder(original.Direction - current.Direction, 360d)) > 1.0 ||
                    original.CornerPreview?.ClosedEnd != current.CornerPreview?.ClosedEnd ||
                    original.CornerPreview?.ClosedSide != current.CornerPreview?.ClosedSide)
                    return LargeOffsetFailure("长度编辑不能移动拐角、改变桥向中心或外侧截面。");
                // 同一端口只能连接一个对象；不能用列表排序掩盖重叠或分支。
                if (i > 0 && frames.Count(f => f.Id != current.Id && LargeOffsetPortsMatch(f, current)) != 1 ||
                    i < members.Length - 1 && frames.Count(f => f.Id != current.Id && LargeOffsetPortsMatch(current, f)) != 1)
                    return LargeOffsetFailure("组内端口连接不唯一。");
            }
            return new BridgeEditResult(true);
        }

        private BridgeEditResult ValidateLargeOffsetGroups(IReadOnlyList<BridgeDesignObject> frames)
        {
            foreach (var corner in CurrentSample.FrameLengthBaseline.Where(f => f.CornerPreview?.ClosedEnd == 1))
            {
                var result = FindLargeOffsetGroup(corner.Id, frames, out var group);
                if (!result.Success) return result;
            }
            return new BridgeEditResult(true);
        }

        private bool LargeOffsetOrdinary(BridgeDesignObject frame) => frame.FrameTemplateKey == "Regular" && !IsSpecialFrame(frame);

        private bool LargeOffsetLengthBoundary(BridgeDesignObject frame) =>
            frame.FrameTemplateKey == "LargeOffsetCorner" || LargeOffsetConnector(frame);

        private bool IsLargeOffsetOrdinaryLengthChain(BridgeDesignObject[] frames, int first, int last) =>
            Enumerable.Range(first, last - first + 1).All(i => LargeOffsetOrdinary(frames[i])) &&
            (first > 0 && frames[first - 1].FrameTemplateKey == "LargeOffsetCorner" ||
             last + 1 < frames.Length && frames[last + 1].FrameTemplateKey == "LargeOffsetCorner");

        private BridgeEditResult TraceLargeOffsetSide(BridgeDesignObject corner, IReadOnlyList<BridgeDesignObject> frames,
            bool bSide, out BridgeDesignObject[] side)
        {
            var chain = new List<BridgeDesignObject>(); var cursor = corner;
            while (chain.Count < frames.Count)
            {
                var next = frames.Where(f => f.Id != cursor.Id && (bSide ? LargeOffsetPortsMatch(f, cursor) : LargeOffsetPortsMatch(cursor, f))).ToArray();
                if (next.Length == 0) break;
                if (next.Length != 1 || chain.Any(f => f.Id == next[0].Id) || !LargeOffsetOrdinary(next[0]))
                { side = null; return LargeOffsetFailure("两侧补偿只允许沿唯一连续普通基架链进行。"); }
                cursor = next[0]; chain.Add(cursor);
            }
            side = chain.ToArray();
            return side.Length == 0 ? LargeOffsetFailure("偏移组外侧缺少连续普通基架，不能完成对称补偿。") : new BridgeEditResult(true);
        }

        private BridgeEditResult PlanLargeOffsetSide(LargeOffsetGroup group, BridgeDesignObject[] frames,
            Dictionary<string, BridgeDesignObject> planned, bool bSide, double halfDelta)
        {
            var index = bSide ? 0 : group.Members.Length - 1;
            var result = TraceLargeOffsetSide(group.Members[index], frames, bSide, out var side);
            if (!result.Success) return result;
            var baseline = CurrentSample.FrameLengthBaseline.ToArray();
            result = TraceLargeOffsetSide(group.Baseline[index], baseline, bSide, out var originalSide);
            if (!result.Success) return result;
            var port = bSide ? 0 : 1;
            var anchor = _geometry.FrameInterfacePoints(side.Last())[port];
            var originalAnchor = _geometry.FrameInterfacePoints(originalSide.Last())[port];
            if (BridgeDesignPrecision.PositionDistance(anchor, originalAnchor) > FrameConnectionToleranceMm)
                return LargeOffsetFailure((bSide ? "B" : "A") + "侧存在缺口或固定柜侧接口已移动，请先修复贯通。");
            var remaining = halfDelta;
            foreach (var frame in side)
            {
                if (Math.Abs(remaining) < .000001) break;
                if (IsLocked(frame.Id) || !frame.EditableFields.Contains(DesignField.Length)) continue;
                var originalLength = frame.DriveValue(DesignField.Length);
                var compensation = remaining > 0 ? Math.Min(remaining, Math.Max(0, originalLength - 200d)) :
                    Math.Max(remaining, -Math.Max(0, 1400d - originalLength));
                var target = originalLength - compensation;
                var values = frame.Values.ToDictionary(p => p.Key, p => p.Value); values[DesignField.Length] = target;
                planned[frame.Id] = frame.WithValues(values);
                if (Math.Abs(planned[frame.Id].DriveValue(DesignField.Length) - target) > .000001)
                    return LargeOffsetFailure("原驱动尺寸精度无法表达本次补偿，请先核验尺寸。");
                remaining -= compensation;
            }
            if (Math.Abs(remaining) >= .000001) return LargeOffsetFailure((bSide ? "B" : "A") + "侧普通基架的安全长度余量不足。");
            var cursorX = anchor[0];
            foreach (var frame in side.Reverse())
            {
                var target = planned[frame.Id]; var length = target.DriveValue(DesignField.Length);
                var values = target.Values.ToDictionary(p => p.Key, p => p.Value);
                values[DesignField.X] = cursorX + (bSide ? 1 : -1) * length / 2;
                target = target.WithValues(values); planned[frame.Id] = target;
                cursorX += (bSide ? 1 : -1) * length;
                if (IsLocked(frame.Id) && Math.Abs(frame.DriveValue(DesignField.Length) - target.DriveValue(DesignField.Length)) > .000001)
                    return LargeOffsetFailure("补偿会改变已锁定的基架长度，请先解锁。");
                if (EffectivePlan.Any(o => o.ParentId == frame.Id && IsLocked(o.Id)) &&
                    (Math.Abs(frame.X - target.X) > .000001 || Math.Abs(frame.Length - target.Length) > .000001))
                    return LargeOffsetFailure("补偿会改变锁定绝缘子的安装位置，请先解锁。");
            }
            var ordered = bSide ? side.Reverse().Select(f => planned[f.Id]).Concat(new[] { planned[group.Members[index].Id] }).ToArray()
                : new[] { planned[group.Members[index].Id] }.Concat(side.Select(f => planned[f.Id])).ToArray();
            for (var i = 1; i < ordered.Length; i++)
                if (!LargeOffsetPortsMatch(ordered[i - 1], ordered[i])) return LargeOffsetFailure("补偿后的外侧接口不贯通。");
            return new BridgeEditResult(true);
        }

        private BridgeEditResult SetLargeOffsetLength(BridgeDesignObject node, double length, Action restoreModel)
        {
            if (double.IsNaN(length) || double.IsInfinity(length))
                return LargeOffsetFailure("共享长度必须是有效有限数值。");
            var frames = OrderedFrames();
            var validation = FindLargeOffsetGroup(node.Id, frames, out var group);
            if (!validation.Success) return validation;
            foreach (var corner in new[] { group.Members.First(), group.Members.Last() })
            {
                validation = Validate(corner, DesignField.Length, length);
                if (!validation.Success) return validation;
            }
            var delta = length - group.Members[0].DriveValue(DesignField.Length);
            if (Math.Abs(delta) < .000001) return restoreModel == null ? new BridgeEditResult(true) : CommitFrameChange(restoreModel);
            if (group.Members.Any(f => f.CornerPreview != null && IsLocked(f.Id)))
                return LargeOffsetFailure("拐角长度已锁定，请先解锁。");
            if (group.Members.Any(f => f.CornerPreview != null && EffectivePlan.Any(o => o.ParentId == f.Id && IsLocked(o.Id))))
                return LargeOffsetFailure("拐角内存在锁定绝缘子，长度修改会改变其局部安装位置，请先解锁。");
            var planned = frames.ToDictionary(f => f.Id);
            foreach (var member in group.Members)
            {
                var values = member.Values.ToDictionary(p => p.Key, p => p.Value);
                values[member.CornerPreview == null ? DesignField.Width : DesignField.Length] = length;
                planned[member.Id] = member.WithValues(values);
                if (Math.Abs(planned[member.Id].DriveValue(member.CornerPreview == null ? DesignField.Width : DesignField.Length) - length) > .000001)
                    return LargeOffsetFailure("原驱动尺寸精度无法表达目标共享长度。");
            }
            validation = PlanLargeOffsetSide(group, frames, planned, true, delta / 2);
            if (!validation.Success) return validation;
            validation = PlanLargeOffsetSide(group, frames, planned, false, delta / 2);
            if (!validation.Success) return validation;
            var after = frames.Select(f => planned[f.Id]).ToArray();
            var committed = CommitFrameChange(() =>
            {
                ApplyFrameLayout(frames, after);
                foreach (var connector in group.Members.Where(LargeOffsetConnector)) Put(connector.Id, DesignField.Width, length);
                // 保留组外模型来源；组内参数相同的多个连接架仍共用一个新来源。
                DetachLargeOffsetSources(frames, after);
                restoreModel?.Invoke();
            }, requireValidSupports: delta < 0d);
            return committed.Success ? new BridgeEditResult(true, "大偏移整组已同步；B、A两侧普通基架各" +
                (delta > 0 ? "缩短" : "加长") + Math.Abs(delta / 2).ToString("0.0", CultureInfo.InvariantCulture) + " mm。") : committed;
        }

        private void DetachLargeOffsetSources(IReadOnlyList<BridgeDesignObject> before, IReadOnlyList<BridgeDesignObject> after)
        {
            var changed = new HashSet<string>(after.Where(f => new[] { DesignField.Length, DesignField.Width, DesignField.Height }
                .Any(field => Math.Abs(before.First(o => o.Id == f.Id).Value(field) - f.Value(field)) > .000001)).Select(f => f.Id));
            var objects = EffectivePlan;
            foreach (var source in before.GroupBy(f => f.SourceName, StringComparer.OrdinalIgnoreCase))
            {
                if (!source.Any(f => !changed.Contains(f.Id))) continue;
                var representatives = new List<Tuple<BridgeDesignObject, string>>();
                foreach (var frame in source.Where(f => changed.Contains(f.Id)).Select(f => objects.Single(o => o.Id == f.Id)))
                {
                    var match = representatives.FirstOrDefault(p => _provider.SameFrameModel(p.Item1, frame, objects));
                    var name = match?.Item2 ?? NextFrameSourceName();
                    _state.SourceNames[frame.Id] = name;
                    if (match == null) representatives.Add(Tuple.Create(frame, name));
                }
            }
        }

        private BridgeEditResult SetLargeOffsetConnectorLength(BridgeDesignObject node, double length, Action restoreModel)
        {
            if (!node.EditableFields.Contains(DesignField.Length)) return LargeOffsetFailure("该连接基架长度属于只读输入。");
            if (double.IsNaN(length) || double.IsInfinity(length) || length < 200 - .000001 || length > 1400 + .000001 ||
                Math.Abs(length - BridgeDesignPrecision.OneDecimal(length)) > .000001)
                return LargeOffsetFailure("连接基架长度必须为200～1400 mm内的0.1 mm精度数值。");
            var frames = OrderedFrames();
            var result = FindLargeOffsetGroup(node.Id, frames, out var group);
            if (!result.Success) return result;
            var connectors = group.Members.Where(LargeOffsetConnector).ToArray();
            var delta = length - node.DriveValue(DesignField.Length);
            if (Math.Abs(delta) < .000001) return restoreModel == null ? new BridgeEditResult(true) : CommitFrameChange(restoreModel);
            if (IsLocked(node.Id)) return LargeOffsetFailure("所选连接基架长度已锁定。");
            if (connectors.Length < 2) return LargeOffsetFailure("仅1节连接基架，两端拐角固定，没有长度补偿对象。");
            var index = Array.FindIndex(connectors, f => f.Id == node.Id);
            var terminal = index == connectors.Length - 1;
            var planned = frames.ToDictionary(f => f.Id);
            result = PlanConnectorDimension(node, length, planned);
            if (!result.Success) return result;
            var candidates = terminal ? Enumerable.Range(0, index).Reverse() : Enumerable.Range(index + 1, connectors.Length - index - 1);
            var remaining = delta;
            foreach (var i in candidates)
            {
                if (Math.Abs(remaining) < .000001) break;
                var frame = connectors[i];
                if (IsLocked(frame.Id) || !frame.EditableFields.Contains(DesignField.Length)) continue;
                var original = frame.DriveValue(DesignField.Length);
                var compensation = remaining > 0 ? Math.Min(remaining, Math.Max(0, original - 200)) :
                    -Math.Min(-remaining, Math.Max(0, 1400 - original));
                if (Math.Abs(compensation) < .000001) continue;
                result = PlanConnectorDimension(frame, original - compensation, planned);
                if (!result.Success) return result;
                remaining -= compensation;
            }
            if (Math.Abs(remaining) >= .000001)
                return LargeOffsetFailure((terminal ? "前方" : "后方") + "未锁定连接基架的可调长度不足，不切换补偿方向。");
            // 累计实际长度变化，保留未参与段及原接口精度；总变化为零使两端拐角接口保持不动。
            var accumulated = 0d;
            foreach (var frame in connectors)
            {
                var target = planned[frame.Id];
                var change = target.DriveValue(DesignField.Length) - frame.DriveValue(DesignField.Length);
                planned[frame.Id] = FramePose(frame, target.Length, accumulated + change / 2);
                accumulated += change;
            }
            var cursor = _geometry.FrameInterfacePoints(planned[connectors.Last().Id])[1];
            var end = _geometry.FrameInterfacePoints(group.Members.Last())[0];
            if (BridgeDesignPrecision.PositionDistance(cursor, end) > FrameConnectionToleranceMm ||
                Math.Abs(connectors.Sum(f => f.DriveValue(DesignField.Length)) - connectors.Sum(f => planned[f.Id].DriveValue(DesignField.Length))) > .000001)
                return LargeOffsetFailure("连接基架补偿未保持两端接口及总跨度。");
            var after = frames.Select(f => planned[f.Id]).ToArray();
            return CommitFrameChange(() =>
            {
                ApplyFrameLayout(frames, after);
                DetachLargeOffsetSources(frames, after);
                restoreModel?.Invoke();
            });
        }

        private BridgeEditResult PlanConnectorDimension(BridgeDesignObject frame, double length,
            IDictionary<string, BridgeDesignObject> planned)
        {
            if (Math.Abs(length - BridgeDesignPrecision.OneDecimal(length)) > .000001)
                return LargeOffsetFailure("原驱动尺寸精度不能表达本次连接架补偿。");
            var values = frame.Values.ToDictionary(p => p.Key, p => p.Value); values[DesignField.Length] = length;
            var target = frame.WithValues(values);
            if (Math.Abs(target.DriveValue(DesignField.Length) - length) > .000001)
                return LargeOffsetFailure("原驱动尺寸精度不能表达目标长度。");
            planned[frame.Id] = target;
            return new BridgeEditResult(true);
        }
    }
}
