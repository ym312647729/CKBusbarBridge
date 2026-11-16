using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CKBusbarBridge.Prototype
{
    internal sealed class BridgeSupportConflict
    {
        internal string FrameId { get; set; }
        internal string FrameLabel { get; set; }
        internal string ObjectLabel { get; set; }
        internal string Reason { get; set; }
        internal string[] MemberIds { get; set; }
        internal bool CanReleaseManual { get; set; }
        internal string Description => FrameLabel + " / " + ObjectLabel + "：" + Reason;
    }

    internal sealed class BridgeSupportConflictException : InvalidOperationException
    {
        internal IReadOnlyList<BridgeSupportConflict> Conflicts { get; }
        internal BridgeSupportConflictException(IEnumerable<BridgeSupportConflict> conflicts)
            : this(conflicts.ToArray()) { }
        private BridgeSupportConflictException(BridgeSupportConflict[] conflicts)
            : base("人工调整与新方案冲突：\n" + string.Join("\n", conflicts.Select(c => c.Description)) +
                "\n冲突的人工定位由同步或“修复贯通”自动解除并重新规划；已锁定对象仍需单独处理，当前修改尚未提交。")
        { Conflicts = Array.AsReadOnly(conflicts); }
    }

    // 同步与修复共享同一对应关系；重新铺排的序号不能单独证明是原来的安装对象。
    internal sealed class BridgeSupportTransition
    {
        internal BridgeDesignSample Plan(BridgeDesignSample actual, Func<BridgeDesignSample, BridgeDesignSample> plan,
            Action<string> progress)
        {
            var effective = actual;
            var released = new List<string>();
            while (true)
            {
                try
                {
                    var target = plan(effective);
                    if (released.Count == 0) return target;
                    var notice = "已自动解除冲突支撑的人工定位并重新规划：" + string.Join("、", released.Distinct()) +
                        "；未受影响的人工调整已保留。";
                    progress?.Invoke(notice);
                    return target.WithReadbackBaselineWarning(string.IsNullOrWhiteSpace(target.ReadbackBaselineWarning)
                        ? notice : target.ReadbackBaselineWarning + "\n" + notice);
                }
                catch (BridgeSupportConflictException error)
                {
                    var conflicts = error.Conflicts.Where(c => c.CanReleaseManual).ToArray();
                    if (conflicts.Length == 0) throw;
                    var ids = new HashSet<string>(conflicts.SelectMany(c => c.MemberIds), StringComparer.Ordinal);
                    var retained = effective.SavedManualInsulatorIds.Where(id => !ids.Contains(id)).ToArray();
                    if (retained.Length == effective.SavedManualInsulatorIds.Count) throw;
                    // 每轮至少减少一个人工标记；只改分离的规划输入，失败不改原草稿、实物或恢复基线。
                    var detail = string.Join("；", conflicts.Select(c => c.Description));
                    CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("冲突支撑人工定位自动解除规划", detail);
                    progress?.Invoke("正在解除冲突支撑人工定位并重新规划：" + detail);
                    released.AddRange(conflicts.Select(c => c.FrameLabel + "/" + c.ObjectLabel));
                    effective = effective.WithSavedManualInsulators(retained);
                }
            }
        }

        internal BridgeDesignSample Preserve(BridgeDesignSample actual, BridgeDesignSample target, bool explicitEditsOnly = false)
        {
            var frames = MatchFrames(actual, target);
            var objects = target.Objects.ToDictionary(o => o.Id, StringComparer.Ordinal);
            foreach (var old in actual.Objects.Where(o => o.Kind == DesignObjectKind.Frame))
            {
                if (!frames.TryGetValue(old.Id, out var next) || ShapeChanges(old, next).Count > 0) continue;
                if (!SameSpecification(actual, target)) continue;
                var children = actual.Objects.Where(o => o.ParentId == old.Id).ToArray();
                if (children.Any(o => o.Kind != DesignObjectKind.Insulator || !CanRebind(o, next.Id))) continue;
                if (explicitEditsOnly)
                {
                    var frameProtected = actual.SavedLockedObjectIds.Contains(old.Id);
                    var protectedChildren = children.Where(child => frameProtected || actual.SavedLockedObjectIds.Contains(child.Id) ||
                        actual.SavedManualInsulatorIds.Contains(child.Id)).ToArray();
                    foreach (var child in protectedChildren)
                    {
                        var identity = ReboundId(child.Id, old.Id, next.Id);
                        if (!objects.ContainsKey(identity)) continue;
                        var rebound = child.WithIdentity(identity, next.Id, ReboundId(child.InsulatorGroupId, old.Id, next.Id));
                        if (next.FramePositionIndex > 0) rebound = rebound.WithFramePosition(next.FramePositionIndex);
                        objects[identity] = rebound;
                    }
                    // An automatic placement, source name or child count is not evidence of an edit.
                    continue;
                }
                var values = next.Values.ToDictionary(p => p.Key, p => p.Value);
                // 安装方式、数量及架内定位属于原有编辑；宿主的新安装变换仍由规划器确定。
                values[DesignField.Installation] = old.Value(DesignField.Installation);
                values[DesignField.InsulatorCount] = old.Value(DesignField.InsulatorCount);
                objects[next.Id] = next.WithValues(values).WithSourceName(old.SourceName).WithComponentBinding(old.ComponentBinding);
                foreach (var child in objects.Values.Where(o => o.ParentId == next.Id).ToArray()) objects.Remove(child.Id);
                foreach (var child in children)
                {
                    var rebound = child.WithIdentity(ReboundId(child.Id, old.Id, next.Id), next.Id,
                        ReboundId(child.InsulatorGroupId, old.Id, next.Id));
                    if (next.FramePositionIndex > 0) rebound = rebound.WithFramePosition(next.FramePositionIndex);
                    objects.Add(rebound.Id, rebound);
                }
            }
            var originalIds = new HashSet<string>(target.Objects.Select(o => o.Id), StringComparer.Ordinal);
            var ordered = target.Objects.Where(o => objects.ContainsKey(o.Id)).Select(o => objects[o.Id])
                .Concat(objects.Values.Where(o => !originalIds.Contains(o.Id)).OrderBy(o => o.ParentId, StringComparer.Ordinal)
                    .ThenBy(o => o.Id, StringComparer.Ordinal));
            var result = target.WithGeometry(ordered, target.Routes);
            return result.WithSavedLocks(RemapIds(actual, result, actual.SavedLockedObjectIds))
                .WithSavedManualInsulators(RemapIds(actual, result, actual.SavedManualInsulatorIds));
        }

        internal IReadOnlyList<BridgeSupportConflict> Inspect(BridgeDesignSample actual, BridgeDesignSample target)
        {
            var frames = MatchFrames(actual, target);
            var objects = target.Objects.ToDictionary(o => o.Id, StringComparer.Ordinal);
            var oldObjects = actual.Objects.ToDictionary(o => o.Id, StringComparer.Ordinal);
            var incompatibleIndependent = actual.IsSideToSide && target.IsSideToSide && actual.ScenarioKey != target.ScenarioKey
                ? actual.SavedFrameLengthEditing?.IndependentFrames ?? Array.Empty<string>() : Array.Empty<string>();
            var protectedIds = new HashSet<string>(actual.SavedLockedObjectIds.Concat(actual.SavedManualInsulatorIds)
                .Concat(incompatibleIndependent), StringComparer.Ordinal);
            var groups = actual.Objects.Where(o => protectedIds.Contains(o.Id)).GroupBy(o =>
                o.Kind == DesignObjectKind.Insulator ? o.InsulatorGroupId ?? o.Id : o.Id, StringComparer.Ordinal);
            var conflicts = new List<BridgeSupportConflict>();
            foreach (var group in groups)
            {
                var old = group.First();
                var members = old.Kind == DesignObjectKind.Insulator
                    ? actual.Objects.Where(o => o.Kind == DesignObjectKind.Insulator &&
                        (o.InsulatorGroupId ?? o.Id) == group.Key).ToArray() : group.ToArray();
                var reasons = new List<string>();
                foreach (var member in members.Where(o => protectedIds.Contains(o.Id)))
                {
                    var nextId = MapId(member, frames);
                    if (nextId == null || !objects.TryGetValue(nextId, out var next))
                    { reasons.Add(member.Kind == DesignObjectKind.Insulator ? "所属基架或绝缘子安装组在新结构中没有可核验的对应项" : "新方案中对象已移除或结构身份改变"); continue; }
                    if (member.Kind != next.Kind) { reasons.Add("对象类型改变"); continue; }
                    if (member.Kind == DesignObjectKind.Frame)
                    {
                        if (Math.Abs(member.DriveValue(DesignField.Length) - next.DriveValue(DesignField.Length)) > .000001)
                            reasons.Add(Change("锁定长度(mm)", member.DriveValue(DesignField.Length), next.DriveValue(DesignField.Length)));
                        continue;
                    }
                    if (member.Kind == DesignObjectKind.Insulator && oldObjects.TryGetValue(member.ParentId ?? "", out var owner))
                    {
                        if (!frames.TryGetValue(owner.Id, out var newOwner)) reasons.Add("所属基架身份无法核验");
                        else reasons.AddRange(ShapeChanges(owner, newOwner));
                    }
                    foreach (var field in member.Values.Keys.Union(next.Values.Keys).OrderBy(f => f))
                        if (!Near(member.Value(field), next.Value(field)))
                            reasons.Add(Change(FieldLabel(field), member.Value(field), next.Value(field)));
                }
                if (reasons.Count == 0) continue;
                var frameId = old.Kind == DesignObjectKind.Frame ? old.Id : old.ParentId;
                oldObjects.TryGetValue(frameId ?? "", out var frame);
                var locked = members.Any(o => actual.SavedLockedObjectIds.Contains(o.Id));
                conflicts.Add(new BridgeSupportConflict { FrameId = frameId,
                    FrameLabel = frame?.DisplayName ?? "独立对象", ObjectLabel = old.Name,
                    MemberIds = members.Select(o => o.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                    CanReleaseManual = old.Kind == DesignObjectKind.Insulator && !locked &&
                        members.Any(o => actual.SavedManualInsulatorIds.Contains(o.Id)),
                    Reason = (locked ? "已锁定；" : "人工定位；") + string.Join("；", reasons.Distinct()) });
            }
            return conflicts.OrderBy(c => c.FrameLabel, StringComparer.Ordinal).ThenBy(c => c.ObjectLabel, StringComparer.Ordinal).ToArray();
        }

        internal void Require(BridgeDesignSample actual, BridgeDesignSample target)
        {
            var conflicts = Inspect(actual, target);
            if (conflicts.Count == 0) return;
            var error = new BridgeSupportConflictException(conflicts);
            if (conflicts.Any(c => c.CanReleaseManual)) throw error;
            throw new InvalidOperationException(error.Message);
        }

        internal IEnumerable<string> RemapIds(BridgeDesignSample actual, BridgeDesignSample target, IEnumerable<string> ids)
        {
            var frames = MatchFrames(actual, target);
            var old = actual.Objects.ToDictionary(o => o.Id, StringComparer.Ordinal);
            var current = new HashSet<string>(target.Objects.Select(o => o.Id), StringComparer.Ordinal);
            return ids.Where(id => id != null).Select(id => old.TryGetValue(id, out var node) ? MapId(node, frames) : null)
                .Where(id => id != null && current.Contains(id)).Distinct().ToArray();
        }

        private string MapId(BridgeDesignObject old, IReadOnlyDictionary<string, BridgeDesignObject> frames)
        {
            if (old.Kind == DesignObjectKind.Frame) return frames.TryGetValue(old.Id, out var frame) ? frame.Id : null;
            if (old.Kind != DesignObjectKind.Insulator) return old.Id;
            return frames.TryGetValue(old.ParentId ?? "", out var owner) && CanRebind(old, owner.Id)
                ? ReboundId(old.Id, old.ParentId, owner.Id) : null;
        }

        private Dictionary<string, BridgeDesignObject> MatchFrames(BridgeDesignSample actual, BridgeDesignSample target)
        {
            var before = actual.Objects.Where(o => o.Kind == DesignObjectKind.Frame).ToArray();
            var after = target.Objects.Where(o => o.Kind == DesignObjectKind.Frame).ToArray();
            var oldRoles = ChainRoles(before); var newRoles = ChainRoles(after);
            var sameTopology = before.Length == after.Length && before.All(old => after.Any(next =>
                next.Id == old.Id && next.FrameTemplateKey == old.FrameTemplateKey && Near(next.Direction, old.Direction) &&
                next.FramePositionIndex == old.FramePositionIndex));
            var result = new Dictionary<string, BridgeDesignObject>(StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var old in before.OrderBy(o => o.FramePositionIndex).ThenBy(o => o.Id, StringComparer.Ordinal))
            {
                var candidates = after.Where(o => SameBinding(old, o)).ToArray();
                if (candidates.Length == 0 && SameCoordinates(actual, target))
                    candidates = after.Where(o => o.FrameTemplateKey == old.FrameTemplateKey && SamePose(old, o) && ShapeChanges(old, o).Count == 0).ToArray();
                if (candidates.Length == 0 && SameCoordinates(actual, target) && oldRoles.TryGetValue(old.Id, out var role))
                    candidates = after.Where(o => newRoles.TryGetValue(o.Id, out var newRole) && newRole == role).ToArray();
                if (candidates.Length == 0 && SameCoordinates(actual, target) && sameTopology)
                    candidates = after.Where(o => o.Id == old.Id).ToArray();
                // 无有效链序时，仅接纳位置、模板均一致的同ID对象，不能按文件名或最近距离猜对应。
                if (candidates.Length == 0 && SameCoordinates(actual, target))
                    candidates = after.Where(o => o.Id == old.Id && o.FrameTemplateKey == old.FrameTemplateKey && SamePose(old, o)).ToArray();
                if (candidates.Length == 1 && used.Add(candidates[0].Id)) result.Add(old.Id, candidates[0]);
            }
            return result;
        }

        private Dictionary<string, string> ChainRoles(BridgeDesignObject[] frames)
        {
            var roles = new Dictionary<string, string>(StringComparer.Ordinal);
            if (frames.Any(o => o.FramePositionIndex <= 0) || frames.Select(o => o.FramePositionIndex).Distinct().Count() != frames.Length) return roles;
            var ordered = frames.OrderBy(o => o.FramePositionIndex).ToArray();
            var special = ordered.Where(o => o.FrameTemplateKey != "Regular" || Math.Abs(o.Direction) > .01).ToArray();
            if (special.Length == 0) return roles;
            var first = special.First().FramePositionIndex; var last = special.Last().FramePositionIndex;
            foreach (var frame in ordered)
            {
                string role;
                if (frame.FramePositionIndex < first) role = "B/" + Array.IndexOf(ordered, frame);
                else if (frame.FramePositionIndex > last) role = "A/" + (ordered.Length - 1 - Array.IndexOf(ordered, frame));
                else role = "结构/" + frame.FrameTemplateKey + "/" +
                    ordered.Count(o => o.FramePositionIndex < frame.FramePositionIndex && o.FramePositionIndex >= first && o.FrameTemplateKey == frame.FrameTemplateKey);
                roles.Add(frame.Id, role + "/" + frame.FrameTemplateKey);
            }
            return roles;
        }

        private bool SameCoordinates(BridgeDesignSample a, BridgeDesignSample b) => a.ScenarioKey == b.ScenarioKey &&
            (a.Coordinates == null && b.Coordinates == null || a.Coordinates != null && a.Coordinates.Matches(b.Coordinates));
        private bool SameSpecification(BridgeDesignSample a, BridgeDesignSample b) =>
            Near(a.Source.BusbarWidth, b.Source.BusbarWidth) && Near(a.Source.BusbarThickness, b.Source.BusbarThickness);
        private bool SameBinding(BridgeDesignObject a, BridgeDesignObject b) => a.ComponentBinding != null && b.ComponentBinding != null &&
            !string.IsNullOrWhiteSpace(a.ComponentBinding.Instance) && a.ComponentBinding.Instance == b.ComponentBinding.Instance &&
            a.ComponentBinding.DocumentKey == b.ComponentBinding.DocumentKey;
        private bool SamePose(BridgeDesignObject a, BridgeDesignObject b) => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z) && Near(a.Direction, b.Direction);
        private bool Near(double a, double b) => Math.Abs(a - b) < .01;
        private bool CanRebind(BridgeDesignObject child, string parent) => child.ParentId == parent ||
            child.Id.StartsWith(child.ParentId + "/", StringComparison.Ordinal) &&
            (child.InsulatorGroupId == null || child.InsulatorGroupId.StartsWith(child.ParentId + "/", StringComparison.Ordinal));
        private string ReboundId(string id, string oldParent, string parent) => id == null || oldParent == parent ? id : parent + id.Substring(oldParent.Length);
        private string Change(string field, double before, double after) => field + " " + before.ToString("0.###", CultureInfo.InvariantCulture) +
            " → " + after.ToString("0.###", CultureInfo.InvariantCulture);
        private string FieldLabel(DesignField field)
        {
            switch (field)
            {
                case DesignField.X: return "架内X定位(mm)";
                case DesignField.Y: return "架内Y定位(mm)";
                case DesignField.Z: return "架内Z定位(mm)";
                case DesignField.Length: return "长度(mm)";
                case DesignField.Width: return "宽度(mm)";
                case DesignField.Height: return "高度(mm)";
                case DesignField.Direction: return "方向(°)";
                default: return field.ToString();
            }
        }

        private IReadOnlyList<string> ShapeChanges(BridgeDesignObject a, BridgeDesignObject b)
        {
            var changes = new List<string>();
            if (a.FrameTemplateKey != b.FrameTemplateKey) changes.Add("基架模板 " + a.FrameTemplateKey + " → " + b.FrameTemplateKey);
            foreach (var field in new[] { DesignField.Length, DesignField.Width, DesignField.Height })
                if (!Near(a.Value(field), b.Value(field))) changes.Add("宿主" + Change(FieldLabel(field), a.Value(field), b.Value(field)));
            if (a.UseOffsetInsulators != b.UseOffsetInsulators || a.InsulatorPadCount != b.InsulatorPadCount ||
                !Near(a.InsulatorPadThickness, b.InsulatorPadThickness) || !Near(a.InsulatorPhaseSpacing, b.InsulatorPhaseSpacing) ||
                !Near(a.InsulatorAssemblyHeight, b.InsulatorAssemblyHeight)) changes.Add("宿主绝缘子叠装参数改变");
            if ((a.SmallOffsetPreview == null) != (b.SmallOffsetPreview == null) || a.SmallOffsetPreview != null &&
                (a.SmallOffsetPreview.InletDirection != b.SmallOffsetPreview.InletDirection || a.SmallOffsetPreview.OutletDirection != b.SmallOffsetPreview.OutletDirection ||
                 !Near(a.SmallOffsetPreview.InletSize, b.SmallOffsetPreview.InletSize) || !Near(a.SmallOffsetPreview.OutletSize, b.SmallOffsetPreview.OutletSize))) changes.Add("进出孔接口改变");
            if ((a.CornerPreview == null) != (b.CornerPreview == null) || a.CornerPreview != null &&
                (a.CornerPreview.ClosedEnd != b.CornerPreview.ClosedEnd || a.CornerPreview.ClosedSide != b.CornerPreview.ClosedSide)) changes.Add("拐角接口改变");
            if ((a.WallFramePreview == null) != (b.WallFramePreview == null) || a.WallFramePreview != null &&
                (!Near(a.WallFramePreview.InletOffset, b.WallFramePreview.InletOffset) || !Near(a.WallFramePreview.InletHeight, b.WallFramePreview.InletHeight) ||
                 !Near(a.WallFramePreview.InletWidth, b.WallFramePreview.InletWidth))) changes.Add("墙端接口改变");
            return changes;
        }
    }
}
