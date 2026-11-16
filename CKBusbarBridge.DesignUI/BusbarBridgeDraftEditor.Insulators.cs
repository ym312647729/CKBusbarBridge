using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Prototype
{
    public sealed partial class BusbarBridgeDraftEditor
    {
        private readonly BridgeInsulatorObjects _insulatorObjects = new BridgeInsulatorObjects();
        private readonly BridgeInsulatorObjects _recommendedInsulatorObjects = new BridgeInsulatorObjects();
        private IReadOnlyList<BridgeDesignObject> _recommendedSupportObjects;
        private Dictionary<string, BridgeDesignObject> _recommendedSupportsById;
        public IReadOnlyList<BridgeInsulatorObject> InsulatorObjects => _insulatorObjects.Read(EffectivePlan);
        internal BridgeInsulatorObject RecommendedInsulator(string id) => _recommendedInsulatorObjects
            .Read(RecommendedSupportObjects).FirstOrDefault(o => o.MemberIds.Contains(id));

        private IReadOnlyList<BridgeDesignObject> RecommendedSupportObjects
        {
            get
            {
                if (_recommendedSupportObjects != null) return _recommendedSupportObjects;
                // 推荐值来自本轮未叠加人工覆盖的目标，不能把 EffectivePlan 当前值作为恢复来源。
                var objects = BaseObjects().ToArray();
                var incomplete = objects.Where(o => o.Kind == DesignObjectKind.Frame && o.FrameTemplateKey == "SmallOffset")
                    .Any(frame => objects.Count(o => o.ParentId == frame.Id && BridgeSmallOffsetSupportPlanner.IsSupport(o)) != 9);
                if (incomplete && (CurrentSample?.ReadbackBaseline == null || _state.CopperGeometry != null))
                    objects = new BridgeSmallOffsetSupportPlanner().Apply(CurrentSample, objects);
                _recommendedSupportsById = objects.Where(o => o.Kind == DesignObjectKind.Frame || o.Kind == DesignObjectKind.Insulator)
                    .ToDictionary(o => o.Id, StringComparer.Ordinal);
                return _recommendedSupportObjects = Array.AsReadOnly(objects);
            }
        }

        internal static BridgeDesignSample CompleteSupportTargets(BridgeDesignSample target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            var duplicate = target.Objects.GroupBy(o => o.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null) throw new InvalidOperationException("支撑目标对象标识重复：" + duplicate.Key);
            // 铜排和基架目标确定后再补齐全部安装位置，包括压缩占位；原实物基线不参与补造。
            var objects = new BridgeSmallOffsetSupportPlanner().Apply(target, target.Objects);
            var frames = objects.Where(o => o.Kind == DesignObjectKind.Frame).ToDictionary(o => o.Id, StringComparer.Ordinal);
            foreach (var group in objects.Where(o => o.Kind == DesignObjectKind.Insulator)
                .GroupBy(o => o.InsulatorGroupId ?? o.Id, StringComparer.Ordinal))
            {
                var members = group.ToArray();
                var problem = BridgeInsulatorObjects.GroupProblem(members);
                if (problem == null && (members[0].ParentId == null || !frames.ContainsKey(members[0].ParentId)))
                    problem = "绝缘子所属基架缺失：" + group.Key;
                if (problem != null) throw new InvalidOperationException("支撑目标不完整：" + problem);
            }
            return target.WithGeometry(objects, target.Routes);
        }

        public BridgeEditResult SetInsulatorDistanceFromPrevious(string id, double distance,
            long? expectedRevision = null, string previousId = null)
        {
            if (expectedRevision.HasValue && expectedRevision.Value != Revision)
                return new BridgeEditResult(false, "草稿已变化，请重新输入绝缘子距离。");
            var items = InsulatorObjects;
            var item = items.FirstOrDefault(o => o.MemberIds.Contains(id));
            if (item == null) return new BridgeEditResult(false, "请选择有效的架内绝缘子。");
            if (item.Error != null) return new BridgeEditResult(false, item.Error);
            if (!item.DistanceFromPrevious.HasValue)
                return new BridgeEditResult(false, "首个绝缘子没有距前距离，请修改基架内定位。");
            if (previousId != null && item.PreviousId != previousId)
                return new BridgeEditResult(false, "绝缘子前后关系已变化，请重新输入距离。");
            distance = BridgeDesignPrecision.OneDecimal(distance);
            if (double.IsNaN(distance) || double.IsInfinity(distance) || distance <= 0)
                return new BridgeEditResult(false, "距前绝缘子距离必须为大于0的有限数值。");
            var previous = items.First(o => o.Id == item.PreviousId);
            var target = previous.Station.Value + distance;
            var next = items.SkipWhile(o => o.Id != item.Id).Skip(1).FirstOrDefault();
            if (next != null && target >= next.Station.Value - .01)
                return new BridgeEditResult(false, "绝缘子不能与后一绝缘子重合或越过后一绝缘子。");
            var frame = Find(item.FrameId);
            var position = frame.Length / 2 + (frame.IsReversed ? -1 : 1) * (target - item.CenterStation.Value);
            if (position < 0 || position > frame.Length)
                return new BridgeEditResult(false, "目标位置超出所属基架，不能通过距前距离跨架移动绝缘子。");
            return SetInsulatorPosition(item.Id, position, false);
        }

        private BridgeEditResult CommitInsulatorChange(Action apply, ISet<string> targets)
        {
            var previous = _state; var previousIssues = ClearanceIssues;
            var previousMoved = LastAutoMovedFrameIds; var previousLengths = LastLengthAdjustedFrameIds;
            _state = previous.Copy(); _state.PreserveInsulatorLayout = true;
            // 定位只派生当前安装组，其他实物安装方式不随动；撤销时一起恢复。
            _state.HangingSupportEdits.UnionWith(targets);
            _editing = true; InvalidateSnapshot();
            var committed = false;
            try
            {
                apply();
                var staged = EffectivePlan;
                var offsetFrames = staged.Where(o => o.Kind == DesignObjectKind.Frame && o.UseOffsetInsulators).ToDictionary(o => o.Id);
                if (offsetFrames.Count > 0)
                {
                    var alignment = new OffsetInsulatorCopperAlignment(CurrentSample, staged, _geometry);
                    foreach (var installation in staged.Where(o => targets.Contains(o.Id)).GroupBy(o => o.InsulatorGroupId))
                    {
                        var member = installation.First();
                        if (!offsetFrames.TryGetValue(member.ParentId, out var frame)) continue;
                        // 吊装支撑面及其阻断由当前安装计划复核；这里仅检查普通落地横移。
                        if (member.MountPlan != null || member.InsulatorMountProblem != null) continue;
                        if (!alignment.TryCenter(frame, member, 0, out var center) || Math.Abs(center - installation.Average(o => o.Y)) > .11)
                            return new BridgeEditResult(false, "目标位置不能保持绝缘子与铜排支承对齐，修改未提交。");
                    }
                }
                // 所有其他安装位都作为本次事务的固定事实；校验不能悄悄重新铺排前后绝缘子。
                Notify(false, new HashSet<string>());
                var issue = ClearanceIssues.FirstOrDefault(o => o.BlocksGeneration &&
                    (targets.Contains(o.TargetId ?? "") || !previousIssues.Any(old => old.BlocksGeneration &&
                        old.Source == o.Source && old.TargetId == o.TargetId && old.Message == o.Message)));
                if (issue != null) return new BridgeEditResult(false, "绝缘子定位未提交：" + issue.Message);
                _undo.Push(previous); _redo.Clear(); Revision++; committed = true;
                Changed?.Invoke(this, EventArgs.Empty);
                return new BridgeEditResult(true);
            }
            finally
            {
                if (!committed)
                {
                    _state = previous; ClearanceIssues = previousIssues;
                    LastAutoMovedFrameIds = previousMoved; LastLengthAdjustedFrameIds = previousLengths;
                    _editing = false; InvalidateSnapshot();
                }
            }
        }
    }
}
