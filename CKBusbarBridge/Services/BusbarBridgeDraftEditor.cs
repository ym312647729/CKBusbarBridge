using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CKBusbarBridge.Prototype
{
    /// <summary>唯一草稿拥有者。推荐方案不可变，当前方案从覆盖值派生；历史不含视口状态。</summary>
    public sealed partial class BusbarBridgeDraftEditor
    {
        private const double FrameConnectionToleranceMm = BridgeDesignPrecision.LengthComparisonToleranceMm;
        private sealed class EditState
        {
            public Dictionary<string, Dictionary<DesignField, double>> Overrides = new Dictionary<string, Dictionary<DesignField, double>>();
            public Dictionary<string, BridgeDesignObject> Added = new Dictionary<string, BridgeDesignObject>();
            public HashSet<string> Removed = new HashSet<string>();
            public HashSet<string> AutomaticallySkipped = new HashSet<string>();
            public HashSet<string> ManualInsulatorPositions = new HashSet<string>();
            public HashSet<string> ManualJointPositions = new HashSet<string>();
            public HashSet<string> ReleasedManualInsulatorPositions = new HashSet<string>();
            public bool PreserveInsulatorLayout;
            public bool DeriveHangingSupports;
            public HashSet<string> HangingSupportEdits = new HashSet<string>();
            public HashSet<string> Locks = new HashSet<string>();
            public Dictionary<string, string> SourceNames = new Dictionary<string, string>();
            public Dictionary<string, BridgeAngleNotchPlan> TemplateNotches = new Dictionary<string, BridgeAngleNotchPlan>();
            public HashSet<string> AutomaticallyPositionedFrames = new HashSet<string>();
            public HashSet<string> LengthAdjustedFrames = new HashSet<string>();
            public HashSet<string> IndependentFrames = new HashSet<string>();
            public HashSet<string> FixedEndFrames = new HashSet<string>();
            public string Selection;
            public BridgeHangerLayout Hangers;
            public BridgeDesignSample CopperGeometry;
            public bool CopperTopologyChanged;
            public BridgeDesignSample TopologyBaseline;
            public EditState Copy() => new EditState
            {
                Overrides = Overrides.ToDictionary(p => p.Key, p => new Dictionary<DesignField, double>(p.Value)),
                Added = new Dictionary<string, BridgeDesignObject>(Added), Removed = new HashSet<string>(Removed),
                AutomaticallySkipped = new HashSet<string>(AutomaticallySkipped),
                ManualInsulatorPositions = new HashSet<string>(ManualInsulatorPositions),
                ManualJointPositions = new HashSet<string>(ManualJointPositions),
                ReleasedManualInsulatorPositions = new HashSet<string>(ReleasedManualInsulatorPositions),
                PreserveInsulatorLayout = PreserveInsulatorLayout,
                DeriveHangingSupports = DeriveHangingSupports,
                HangingSupportEdits = new HashSet<string>(HangingSupportEdits),
                Locks = new HashSet<string>(Locks), SourceNames = new Dictionary<string, string>(SourceNames),
                TemplateNotches = new Dictionary<string, BridgeAngleNotchPlan>(TemplateNotches),
                AutomaticallyPositionedFrames = new HashSet<string>(AutomaticallyPositionedFrames),
                IndependentFrames = new HashSet<string>(IndependentFrames), FixedEndFrames = new HashSet<string>(FixedEndFrames),
                LengthAdjustedFrames = new HashSet<string>(LengthAdjustedFrames), Selection = Selection,
                CopperGeometry = CopperGeometry, CopperTopologyChanged = CopperTopologyChanged, TopologyBaseline = TopologyBaseline,
                Hangers = Hangers
            };
        }

        private sealed class BaseJointRange
        {
            public double BoundaryX { get; }
            public double IntoBaseSign { get; }
            public double MinimumPenetration { get; }
            public double MaximumPenetration { get; }
            public BaseJointRange(double boundaryX, double intoBaseSign, double minimumPenetration, double maximumPenetration)
            {
                BoundaryX = boundaryX;
                IntoBaseSign = intoBaseSign;
                MinimumPenetration = minimumPenetration;
                MaximumPenetration = maximumPenetration;
            }
            public double PenetrationAt(double x) => BridgeDesignPrecision.OneDecimal((x - BoundaryX) * IntoBaseSign);
            public double XAt(double penetration) => BoundaryX + penetration * IntoBaseSign;
        }

        private EditState _state = new EditState();
        private EditState _loadedState = new EditState();
        private bool _editing;
        private IReadOnlyList<BridgeDesignObject> _effectivePlan;
        private Dictionary<string, BridgeDesignObject> _objectsById;
        private IReadOnlyList<BridgeCopperSection> _copperSections;
        private IReadOnlyList<BridgeDesignIssue> _frameConnectionIssues;
        private readonly Stack<EditState> _undo = new Stack<EditState>();
        private readonly Stack<EditState> _redo = new Stack<EditState>();
        private readonly PrototypeBusbarBridgeDesignProvider _provider = new PrototypeBusbarBridgeDesignProvider();
        private readonly BusbarBridgeInsulatorClearance _clearance = new BusbarBridgeInsulatorClearance();
        private readonly BusbarBridgeDraftPreviewSceneFactory _geometry = new BusbarBridgeDraftPreviewSceneFactory();
        public IReadOnlyList<BridgeDesignIssue> ClearanceIssues { get; private set; } = Array.Empty<BridgeDesignIssue>();
        // 接缝和换相吊装随草稿调整重新校验，规划时的同类诊断不参与当前门禁。
        internal IEnumerable<BridgeDesignIssue> PlanningIssues => (CurrentSample?.Issues ?? Array.Empty<BridgeDesignIssue>())
            .Where(issue => issue.Source != "基架连接避让" && issue.Source != BridgePhaseChangeSupportPlanner.IssueSource);
        public bool HasBlockingIssues => PlanningIssues.Any(issue => issue.BlocksGeneration) ||
            ClearanceIssues.Any(issue => issue.BlocksGeneration) || FrameConnectionIssues.Count > 0 || HangerIssues.Any();
        public IReadOnlyList<BridgeDesignIssue> FrameConnectionIssues
        {
            get
            {
                if (!_editing && _frameConnectionIssues != null) return _frameConnectionIssues;
                var issues = new List<BridgeDesignIssue>();
                var baseline = CurrentSample?.FrameLengthBaseline.Select(WallSafetyBaseline).ToArray() ?? Array.Empty<BridgeDesignObject>();
                var current = OrderedFrames();
                if (BridgeOffsetCopperChange.SupportsMiddleEditing(CurrentSample) && baseline.Any(BridgeOffsetCopperChange.IsMember))
                    return CacheFrameConnectionIssues(OffsetFrameConnectionIssues(current));
                var visited = new HashSet<string>();
                foreach (var origin in baseline)
                {
                    if (visited.Contains(origin.Id)) continue;
                    var line = baseline.Where(f => SameFrameLine(origin, f)).ToArray();
                    foreach (var frame in line) visited.Add(frame.Id);
                    var covered = current.Where(f => SameFrameLine(origin, f))
                        .OrderBy(f => FrameAlong(f, origin) - f.Length / 2).ToArray();
                    var missing = new List<double[]>();
                    var ranges = line.Select(frame => new[] { FrameAlong(frame, origin) - frame.Length / 2,
                        FrameAlong(frame, origin) + frame.Length / 2 }).ToArray();
                    if (CurrentSample.ScenarioKey == "FaceToWall")
                        ranges = new[] { new[] { ranges.Min(range => range[0]), ranges.Max(range => range[1]) } };
                    foreach (var range in ranges)
                    {
                        var cursor = range[0];
                        var end = range[1];
                        foreach (var active in covered)
                        {
                            var start = FrameAlong(active, origin) - active.Length / 2;
                            var finish = start + active.Length;
                            if (finish <= cursor + .01) continue;
                            if (start >= end - .01) break;
                            if (start > cursor + FrameConnectionToleranceMm) missing.Add(new[] { cursor, Math.Min(start, end) });
                            cursor = Math.Max(cursor, finish);
                            if (cursor >= end - .01) break;
                        }
                        if (cursor < end - FrameConnectionToleranceMm) missing.Add(new[] { cursor, end });
                    }
                    var merged = new List<double[]>();
                    foreach (var gap in missing.OrderBy(p => p[0]))
                    {
                        if (merged.Count > 0 && gap[0] <= merged[merged.Count - 1][1] + .01)
                            merged[merged.Count - 1][1] = Math.Max(merged[merged.Count - 1][1], gap[1]);
                        else merged.Add(gap);
                    }
                    foreach (var gap in merged)
                        issues.Add(new BridgeDesignIssue("基架安装链存在" + (gap[1] - gap[0]).ToString("0.0", CultureInfo.InvariantCulture) +
                            " mm空隙，请补齐连接后生成正式模型。", covered.FirstOrDefault()?.Id, "基架连接", false, true));
                }
                for (var i = 1; i < current.Length; i++)
                    if (!FramesConnected(current[i - 1], current[i]) && !TryGetFrameGap(current[i - 1], current[i], out var gap))
                        issues.Add(new BridgeDesignIssue("相邻基架安装接口的位置、方向或截面不兼容，请修复贯通后生成正式模型。",
                            current[i].Id, "基架连接", false, true));
                return CacheFrameConnectionIssues(issues.AsReadOnly());
            }
        }
        private IReadOnlyList<BridgeDesignIssue> CacheFrameConnectionIssues(IReadOnlyList<BridgeDesignIssue> issues)
        {
            // 临时事务仍在变更，不能让界面后续查询复用中途的校验结果。
            if (!_editing) _frameConnectionIssues = issues;
            return issues;
        }
        public IReadOnlyCollection<string> LastAutoMovedFrameIds { get; private set; } = Array.Empty<string>();
        public IReadOnlyCollection<string> LastLengthAdjustedFrameIds { get; private set; } = Array.Empty<string>();
        public event EventHandler Changed;
        public BridgeDesignSample Sample { get; private set; }
        public BridgeDesignSample CurrentSample => _state.CopperGeometry ?? Sample;
        internal BridgeFrameLengthEditing CaptureFrameLengthEditing()
        {
            var frames = new HashSet<string>(EffectivePlan.Where(o => o.Kind == DesignObjectKind.Frame).Select(o => o.Id));
            return new BridgeFrameLengthEditing(CurrentSample.FrameLengthBaseline.Select(WallSafetyBaseline),
                _state.IndependentFrames.Where(frames.Contains), _state.FixedEndFrames.Where(id => frames.Contains(id) && _state.IndependentFrames.Contains(id)),
                _state.LengthAdjustedFrames.Where(frames.Contains), CurrentSample.SavedFrameLengthEditing?.OffsetCopperMinimumLengthMm);
        }
        internal BridgeDesignSample CaptureSavedSample()
        {
            var objects = EffectivePlan.ToArray();
            return CurrentSample.WithGeometry(objects, CurrentSample.Routes).WithFrameLengthEditing(CaptureFrameLengthEditing())
                .WithSavedLocks(objects.Where(o => IsLocked(o.Id)).Select(o => o.Id))
                .WithSavedManualJoints(_state.ManualJointPositions)
                .WithSavedManualInsulators(objects.Where(o => o.Kind == DesignObjectKind.Insulator &&
                    IsManuallyPositionedInsulator(o.Id)).Select(o => o.Id))
                .WithHangers(HangerLayout).WithHangerRoof(Sample.HangerRoof, Sample.HangerRoofProblem);
        }
        internal bool HasPendingFrameLayoutChanges => Sample != null &&
            (!_state.IndependentFrames.SetEquals(Sample.SavedFrameLengthEditing?.IndependentFrames ?? Array.Empty<string>()) ||
             !_state.FixedEndFrames.SetEquals(Sample.SavedFrameLengthEditing?.FixedEndFrames ?? Array.Empty<string>()) ||
             !new HashSet<string>(EffectivePlan.Where(o => o.Kind == DesignObjectKind.Frame && IsLocked(o.Id)).Select(o => o.Id))
                .SetEquals(Sample.Objects.Where(o => o.Kind == DesignObjectKind.Frame && Sample.SavedLockedObjectIds.Contains(o.Id)).Select(o => o.Id)) ||
             !SameFrameLayout(Sample.Objects, EffectivePlan));

        private static bool SameFrameLayout(IReadOnlyList<BridgeDesignObject> before, IReadOnlyList<BridgeDesignObject> after)
        {
            var frames = before.Where(o => o.Kind == DesignObjectKind.Frame).ToDictionary(o => o.Id);
            var next = after.Where(o => o.Kind == DesignObjectKind.Frame).ToArray();
            return frames.Count == next.Length && next.All(o => frames.TryGetValue(o.Id, out var old) &&
                old.FrameTemplateKey == o.FrameTemplateKey && (old.FramePositionIndex <= 0 || old.FramePositionIndex == o.FramePositionIndex) &&
                new[] { DesignField.X, DesignField.Y, DesignField.Z,
                    DesignField.Length, DesignField.Width, DesignField.Height, DesignField.Direction, DesignField.Installation }
                    .All(field => Math.Abs(old.Value(field) - o.Value(field)) < .000001));
        }
        internal bool MatchesFrameReloadState(BridgeDesignSample restored)
        {
            if (restored == null || !SameFrameLayout(EffectivePlan, restored.Objects)) return false;
            var editing = CaptureFrameLengthEditing(); var incoming = restored.SavedFrameLengthEditing;
            return SameFrameLayout(editing.BaselineFrames, restored.FrameLengthBaseline) &&
                _state.IndependentFrames.SetEquals(incoming?.IndependentFrames ?? Array.Empty<string>()) &&
                _state.FixedEndFrames.SetEquals(incoming?.FixedEndFrames ?? Array.Empty<string>()) &&
                new HashSet<string>(editing.LengthAdjustedFrames).SetEquals(incoming?.LengthAdjustedFrames ?? Array.Empty<string>()) &&
                new HashSet<string>(EffectivePlan.Where(o => o.Kind == DesignObjectKind.Frame && IsLocked(o.Id)).Select(o => o.Id))
                    .SetEquals(restored.Objects.Where(o => o.Kind == DesignObjectKind.Frame && restored.SavedLockedObjectIds.Contains(o.Id)).Select(o => o.Id));
        }
        public DesignSessionMode Mode => DesignSessionMode.Prototype;
        public long Revision { get; private set; }
        public string SelectedId { get => _state.Selection; set => _state.Selection = JointRepresentative(value)?.Id; }
        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public bool HasChanges => !ReferenceEquals(_state.Hangers, _loadedState.Hangers) ||
            _state.CopperTopologyChanged || HasOverrideChanges || _state.Added.Count > 0 || _state.Removed.Count > 0 ||
            !_state.Locks.SetEquals(Sample?.SavedLockedObjectIds ?? Array.Empty<string>()) || HasSourceNameChanges || _state.ManualInsulatorPositions.Count > 0 || _state.ReleasedManualInsulatorPositions.Count > 0 ||
            !_state.IndependentFrames.SetEquals(Sample?.SavedFrameLengthEditing?.IndependentFrames ?? Array.Empty<string>()) ||
            !_state.FixedEndFrames.SetEquals(Sample?.SavedFrameLengthEditing?.FixedEndFrames ?? Array.Empty<string>());
        // 加载时的自动避让和同源拆分是初始派生布局，不能造成未编辑即提示保存。
        private bool HasOverrideChanges => _state.Overrides.Count != _loadedState.Overrides.Count || _state.Overrides.Any(pair =>
            !_loadedState.Overrides.TryGetValue(pair.Key, out var original) || pair.Value.Count != original.Count ||
            pair.Value.Any(value => !original.TryGetValue(value.Key, out var old) || Math.Abs(value.Value - old) > .000001));
        private bool HasSourceNameChanges => _state.SourceNames.Count != _loadedState.SourceNames.Count || _state.SourceNames.Any(pair =>
            !_loadedState.SourceNames.TryGetValue(pair.Key, out var original) || pair.Value != original);
        public bool IsIndependentFrame(string id) => id != null && _state.IndependentFrames.Contains(id);
        public bool IsFrameEndFixed(string id)
        {
            if (id == null) return false;
            if (IsLargeOffsetConnector(id) && FindLargeOffsetGroup(id, OrderedFrames(), out var group).Success)
                return group.Members[group.Members.Length - 2].Id == id;
            var frame = Find(id);
            if (FrameAtPlannedBoundary(frame, 1)) return true;
            if (FrameAtPlannedBoundary(frame, 0)) return false;
            if (IsIndependentFrame(id)) return _state.FixedEndFrames.Contains(id);
            var frames = OrderedFrames(); var index = Array.FindIndex(frames, f => f.Id == id);
            if (index >= 0 && LargeOffsetOrdinary(frame) && index + 1 < frames.Length &&
                frames[index + 1].FrameTemplateKey == "LargeOffsetCorner" && FramePortsTouch(frame, frames[index + 1])) return true;
            var gap = index < 0 ? null : FindFrameGapAnchor(frames, index);
            return gap != null && !gap.RequiresChoice ? gap.FixedEnd : _state.FixedEndFrames.Contains(id);
        }

        public BridgeEditResult SetIndependentFrame(string id, bool independent)
        {
            var frame = Find(id);
            if (frame?.Kind != DesignObjectKind.Frame) return new BridgeEditResult(false, "请选择基架。");
            if (IsIndependentFrame(id) == independent) return new BridgeEditResult(true);
            var fixedEnd = IsFrameEndFixed(id);
            BeginEdit();
            if (independent)
            {
                _state.IndependentFrames.Add(id);
                if (fixedEnd) _state.FixedEndFrames.Add(id); else _state.FixedEndFrames.Remove(id);
            }
            else { _state.IndependentFrames.Remove(id); _state.FixedEndFrames.Remove(id); }
            Notify(); return new BridgeEditResult(true);
        }

        public BridgeEditResult SetFrameFixedEnd(string id, bool fixedEnd)
        {
            if (IsLargeOffsetConnector(id)) return new BridgeEditResult(false, "连接基架按实际安装顺序确定固定端，不允许切换默认补偿方向。");
            if (!IsIndependentFrame(id)) return new BridgeEditResult(false, "请先勾选独立更改。");
            if (IsFrameEndFixed(id) == fixedEnd) return new BridgeEditResult(true);
            var frame = Find(id);
            if (FrameAtPlannedBoundary(frame, fixedEnd ? 0 : 1))
                return new BridgeEditResult(false, "实际柜端或墙端接口必须固定，不能切换为移动该接口的方向。");
            BeginEdit();
            if (fixedEnd) _state.FixedEndFrames.Add(id); else _state.FixedEndFrames.Remove(id);
            Notify(); return new BridgeEditResult(true);
        }
        public bool IsLocked(string id) => id != null && _state.Locks.Contains(id);
        // 基架的锁只保护Length；其他对象保留原有位置/参数锁定语义。
        public bool IsPositionLocked(string id) => IsLocked(id) && Find(id)?.Kind != DesignObjectKind.Frame;
        public bool CanEditFrameLength(string id)
        {
            var frame = Find(id);
            if (frame?.Kind != DesignObjectKind.Frame || !frame.EditableFields.Contains(DesignField.Length) || IsLocked(id)) return false;
            return !LargeOffsetConnector(frame) || FindLargeOffsetGroup(id, OrderedFrames(), out var group).Success && group.Members.Length > 3;
        }

        private BridgeEditResult ValidateLengthLocks(IReadOnlyList<BridgeDesignObject> before, IReadOnlyList<BridgeDesignObject> after)
        {
            foreach (var original in before.Where(o => o.Kind == DesignObjectKind.Frame && IsLocked(o.Id)))
            {
                var current = after.FirstOrDefault(o => o.Id == original.Id);
                if (current == null || !(Math.Abs(current.DriveValue(DesignField.Length) - original.DriveValue(DesignField.Length)) <= .000001))
                    return new BridgeEditResult(false, original.DisplayName + "长度已锁定，修改未提交。");
            }
            return new BridgeEditResult(true);
        }

        private HashSet<string> EditedFrameTopologyIds()
        {
            var current = OrderedFrames(); var present = new HashSet<string>(current.Select(o => o.Id));
            // 原规划边界同时保留安装身份；保存重读后不能只依赖已清空的覆盖字典。
            var baseline = FramesInInstallationOrder(CurrentSample.FrameLengthBaseline)
                .Where(o => present.Contains(o.Id)).ToArray();
            var originals = baseline.ToDictionary(o => o.Id);
            return new HashSet<string>(current.Select((frame, index) => new { frame, index }).Where(pair =>
                pair.index >= baseline.Length || baseline[pair.index].Id != pair.frame.Id ||
                !originals.TryGetValue(pair.frame.Id, out var original) ||
                Math.Abs(original.Value(DesignField.Installation) - pair.frame.Value(DesignField.Installation)) >= .000001)
                .Select(pair => pair.frame.Id));
        }

        private BridgeEditResult ValidateEditedFrameTopology(IReadOnlyList<BridgeDesignObject> before,
            IReadOnlyList<BridgeDesignObject> after, ISet<string> edited)
        {
            if (edited.Count == 0) return new BridgeEditResult(true);
            var frames = before.Where(o => o.Kind == DesignObjectKind.Frame).OrderBy(o => o.FramePositionIndex).ToArray();
            var next = after.Where(o => o.Kind == DesignObjectKind.Frame).OrderBy(o => o.FramePositionIndex).ToArray();
            if (!frames.Select(o => o.Id).SequenceEqual(next.Select(o => o.Id)) || frames.Where(o => edited.Contains(o.Id)).Any(o =>
                !next.Any(n => n.Id == o.Id && Math.Abs(n.Value(DesignField.Installation) - o.Value(DesignField.Installation)) < .000001)))
                return new BridgeEditResult(false, "铜排重规划会覆盖人工基架安装顺序或反装状态，当前草稿保留；未提交修改。");
            return new BridgeEditResult(true);
        }
        public bool IsManuallyPositionedInsulator(string id) => id != null &&
            !_state.ReleasedManualInsulatorPositions.Contains(id) &&
            (_state.ManualInsulatorPositions.Contains(id) || CurrentSample?.SavedManualInsulatorIds.Contains(id) == true);

        private void ReleaseManualInsulatorPosition(string id)
        {
            _state.ManualInsulatorPositions.Remove(id);
            // 已保存的人工定位也必须能解除；撤销保留完整标记，不能只清本轮新增的覆盖值。
            if (CurrentSample?.SavedManualInsulatorIds.Contains(id) == true) _state.ReleasedManualInsulatorPositions.Add(id);
        }
        public bool HasIndependentLengthAdjustment(string id) => id != null && _state.LengthAdjustedFrames.Contains(id);
        public bool IsAdjusted(string id) => id != null && (_state.Overrides.ContainsKey(id) || _state.Added.ContainsKey(id) || _state.SourceNames.ContainsKey(id));
        public bool CanRestore(string id) => id != null && (IsAdjusted(id) || IsLocked(id) || IsManuallyPositionedInsulator(id) ||
            (Find(id)?.IsInsulatorGroup == true && Affected(id, false).Any(o => IsAdjusted(o.Id) || IsLocked(o.Id) || IsManuallyPositionedInsulator(o.Id))) ||
            BaseObjects().Any(o => o.ParentId == id && (IsAdjusted(o.Id) || IsLocked(o.Id) || _state.Removed.Contains(o.Id))));
        public IReadOnlyList<BridgeDesignObject> EffectivePlan
        {
            get
            {
                // 中途编辑与自动避让会继续改写状态，只缓存已完成校验的版本。
                if (!_editing && _effectivePlan != null) return _effectivePlan;
                var objects = BaseObjects().Where(o => !_state.Removed.Contains(o.Id) &&
                    !_state.AutomaticallySkipped.Contains(o.Id)).Select(Effective).ToArray();
                // 窗口初次绑定及重读前清空草稿都会访问此属性，此时尚无场景。
                if (CurrentSample?.ScenarioKey == "FaceToWall") objects = ApplyWallPreviewParameters(objects);
                var skippedFrames = new HashSet<string>(BaseObjects()
                    .Where(o => _state.AutomaticallySkipped.Contains(o.Id)).Select(o => o.ParentId));
                objects = objects.Select(o =>
                {
                    if (o.Kind != DesignObjectKind.Frame || !skippedFrames.Contains(o.Id)) return o;
                    var values = o.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
                    values[DesignField.InsulatorCount] = objects.Where(child => child.Kind == DesignObjectKind.Insulator &&
                        child.ParentId == o.Id).Select(child => child.InsulatorGroupId ?? child.Id).Distinct().Count();
                    return o.WithValues(values);
                }).ToArray();
                // 沿用旧界面的 B→A 位置1～n；位置顺序与模型名称、稳定实例ID分离。
                // 大偏移组同X，反向偏移必须从B侧沿负横向排序，不能按Y升序把整组倒装。
                var frameObjects = objects.Where(o => o.Kind == DesignObjectKind.Frame).ToArray();
                var positions = FramesInInstallationOrder(frameObjects)
                    .Select((o, index) => new { o.Id, Index = index + 1 }).ToDictionary(o => o.Id, o => o.Index);
                var result = Array.AsReadOnly(objects.Select(o =>
                    positions.TryGetValue(o.Kind == DesignObjectKind.Frame ? o.Id : o.ParentId ?? "", out var index) &&
                    (o.Kind == DesignObjectKind.Frame || o.Kind == DesignObjectKind.Insulator) ? o.WithFramePosition(index) : o).ToArray());
                result = Array.AsReadOnly(NumberBreakpoints(result));
                if (CurrentSample?.ReadbackBaseline == null || _state.CopperGeometry != null)
                    result = Array.AsReadOnly(new BridgeSmallOffsetSupportPlanner().Apply(CurrentSample, result));
                if (CurrentSample?.ScenarioKey == "FaceToFace" && (CurrentSample.ReadbackBaseline == null ||
                    _state.DeriveHangingSupports || _state.HangingSupportEdits.Count > 0))
                    result = Array.AsReadOnly(new BridgePhaseChangeSupportPlanner(CurrentSample, result).Apply(result,
                        CurrentSample.ReadbackBaseline == null || _state.DeriveHangingSupports ? null : _state.HangingSupportEdits));
                if (!_editing) _effectivePlan = result;
                return result;
            }
        }
        private BridgeDesignObject[] NumberBreakpoints(IReadOnlyList<BridgeDesignObject> objects)
        {
            var byId = objects.ToDictionary(o => o.Id, StringComparer.Ordinal);
            var sample = CurrentSample;
            var routes = sample?.CopperEditRoutes.Count > 0 ? sample.CopperEditRoutes : sample?.Routes;
            var route = routes?.FirstOrDefault(r => r.Phase == "B" && r.LSegmentStartIds.Count == 0) ??
                routes?.FirstOrDefault(r => r.LSegmentStartIds.Count == 0);
            // 编号来自当前路径顺序；稳定ID与ABC组归属保持不变，撤销/重做自然重新编号。
            var ordered = (route?.PointIds ?? Array.Empty<string>()).Where(byId.ContainsKey).Select(id => byId[id])
                .Concat(objects.OrderBy(o => o.X).ThenBy(o => o.Id, StringComparer.Ordinal));
            var numbers = ordered.Where(o => o.Kind == DesignObjectKind.Breakpoint)
                .Select(o => o.JointGroupId ?? o.Id).Distinct(StringComparer.Ordinal)
                .Select((id, index) => new { Id = id, Number = index + 1 }).ToDictionary(o => o.Id, o => o.Number, StringComparer.Ordinal);
            return objects.Select(o => o.Kind == DesignObjectKind.Breakpoint && numbers.TryGetValue(o.JointGroupId ?? o.Id, out var number)
                ? o.WithBreakpointNumber(number) : o).ToArray();
        }
        public BridgeDesignObject Recommended(string id)
        {
            var support = RecommendedSupportObjects.Count > 0 && _recommendedSupportsById.TryGetValue(id ?? "", out var recommendedSupport)
                ? recommendedSupport : null;
            var frameBaseline = CurrentSample?.FrameLengthBaseline.FirstOrDefault(o => o.Id == id);
            if (frameBaseline != null && (support == null || frameBaseline.FrameTemplateKey == support.FrameTemplateKey)) return frameBaseline;
            return support ?? (Sample?.Objects ?? Array.Empty<BridgeDesignObject>()).Where(o => ReferenceEquals(Sample, CurrentSample) ||
                    o.Kind != DesignObjectKind.Frame && o.Kind != DesignObjectKind.Insulator)
                .Concat(_state.Added.Values).Concat(_state.TopologyBaseline?.Objects ?? Array.Empty<BridgeDesignObject>())
                .Concat(CurrentSample?.Objects ?? Array.Empty<BridgeDesignObject>()).FirstOrDefault(o => o.Id == id);
        }
        public BridgeDesignObject Find(string id)
        {
            if (id == null) return null;
            var objects = EffectivePlan;
            if (_editing) return objects.FirstOrDefault(o => o.Id == id);
            if (_objectsById == null) _objectsById = objects.GroupBy(o => o.Id)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            return _objectsById.TryGetValue(id, out var node) ? node : null;
        }
        public IReadOnlyList<BridgeCopperSection> CopperSections
        {
            get
            {
                if (_editing) return _geometry.CopperSections(CurrentSample, EffectivePlan);
                return _copperSections ?? (_copperSections = _geometry.CopperSections(CurrentSample, EffectivePlan));
            }
        }
        public BridgeCopperSection FindCopperSection(string id) => CopperSections.FirstOrDefault(section => section.Id == id);
        public bool CanAddBreakpoint(string sectionId) => FindCopperSection(sectionId) != null &&
            !IsSpecialCopperSection(sectionId) && !SectionJointsLocked(FindCopperSection(sectionId));

        private bool SectionJointsLocked(BridgeCopperSection section) =>
            Affected(section.StartPointId, false).Concat(Affected(section.EndPointId, false)).Any(o => IsLocked(o.Id));

        public BridgeEditResult AddBreakpoint(string sectionId) => AddBreakpointCore(sectionId, null);
        internal BridgeEditResult RestoreAddedDraftJoint(IReadOnlyList<BridgeDesignObject> originalMembers)
        {
            if (originalMembers.Count == 0) return new BridgeEditResult(false, "人工断点缺少相别成员。");
            var x = (float)originalMembers[0].X;
            var section = CopperSections.FirstOrDefault(item => CanAddBreakpoint(item.Id) &&
                x > Math.Min(Find(item.StartPointId).X, Find(item.EndPointId).X) + 1.0f &&
                x < Math.Max(Find(item.StartPointId).X, Find(item.EndPointId).X) - 1.0f);
            return section == null ? new BridgeEditResult(false, "人工断点已不在当前普通铜排段内。") : AddBreakpointCore(section.Id, originalMembers);
        }
        private BridgeEditResult AddBreakpointCore(string sectionId, IReadOnlyList<BridgeDesignObject> originalMembers)
        {
            var section = FindCopperSection(sectionId);
            if (!CanAddBreakpoint(sectionId)) return new BridgeEditResult(false, "请选择未锁定的普通段排；特殊段排不允许增加断点。");
            var route = EditingRoutes.First(r => r.PointIds.Contains(section.StartPointId) && r.PointIds.Contains(section.EndPointId));
            var index = route.PointIds.ToList().IndexOf(section.StartPointId);
            if (route.PointIds[index + 1] != section.EndPointId)
                return new BridgeEditResult(false, "该段排包含特殊路径点，不允许增加断点。");
            var groupId = originalMembers == null ? "user-joint-" + Guid.NewGuid().ToString("N") : originalMembers[0].JointGroupId;
            var additions = new List<BridgeDesignObject>();
            var phaseRoutes = EditingRoutes.Where(r => r.LSegmentStartIds.Count == 0).ToArray();
            var sideToSide = CurrentSample.IsSideToSide;
            var sectionStart = Find(section.StartPointId);
            var sectionEnd = Find(section.EndPointId);
            var commonX = (sectionStart.X + sectionEnd.X) / 2d;
            if (sideToSide && (phaseRoutes.Length != 3 || phaseRoutes.Select(r => r.Phase).Distinct().Count() != 3 ||
                !phaseRoutes.All(r => r.Phase == "A" || r.Phase == "B" || r.Phase == "C")))
                return new BridgeEditResult(false, "侧对侧段排缺少完整的ABC三相路径。");
            foreach (var phaseRoute in phaseRoutes)
            {
                if (phaseRoute.PointIds.Count <= index + 1)
                    return new BridgeEditResult(false, "该段排缺少对应的三相路径。");
                var start = Find(phaseRoute.PointIds[index]); var end = Find(phaseRoute.PointIds[index + 1]);
                if (sideToSide && (start?.JointGroupId != sectionStart.JointGroupId ||
                    end?.JointGroupId != sectionEnd.JointGroupId || start == null || end == null))
                    return new BridgeEditResult(false, phaseRoute.Phase + "相未找到与B相对应的段排。");
                var values = start.Values.ToDictionary(p => p.Key, p => p.Value);
                values[DesignField.X] = sideToSide ? commonX : BridgeDesignPrecision.OneDecimal((start.X + end.X) / 2d);
                values[DesignField.Y] = (start.Y + end.Y) / 2d;
                values[DesignField.Z] = (start.Z + end.Z) / 2d;
                var originalMember = originalMembers?.SingleOrDefault(member => member.Phase == phaseRoute.Phase);
                if (originalMembers != null)
                {
                    if (originalMember == null) return new BridgeEditResult(false, "人工断点缺少" + phaseRoute.Phase + "相原始身份。");
                    var fraction = ((float)originalMember.X - (float)start.X) / ((float)end.X - (float)start.X);
                    values[DesignField.X] = (float)originalMember.X;
                    values[DesignField.Y] = (float)start.Y + ((float)end.Y - (float)start.Y) * fraction;
                    values[DesignField.Z] = (float)start.Z + ((float)end.Z - (float)start.Z) * fraction;
                }
                var minimum = sideToSide ? BridgeDesignPrecision.LengthToleranceMm : .1;
                if (sideToSide ? values[DesignField.X] - start.X <= minimum || end.X - values[DesignField.X] <= minimum :
                    Math.Abs(values[DesignField.X] - start.X) < minimum || Math.Abs(values[DesignField.X] - end.X) < minimum)
                    return new BridgeEditResult(false, "该段排过短，不能在中间增加断点。");
                additions.Add(new BridgeDesignObject(originalMember?.Id ?? groupId + "-" + phaseRoute.Phase, originalMember?.Name ?? "新增断点", DesignObjectKind.Breakpoint,
                    values, new[] { DesignField.X }, "普通段排中间断点；ABC三相联动", phase: phaseRoute.Phase).WithJointGroup(groupId));
            }
            var proposed = EffectivePlan.Concat(additions).ToArray();
            return CommitBreakpointTopology(proposed, routes => routes.Select(r =>
            {
                var member = additions.SingleOrDefault(o => o.Phase == r.Phase);
                if (r.LSegmentStartIds.Count > 0 || member == null) return r;
                var ids = r.PointIds.ToList(); ids.Insert(index + 1, member.Id);
                return new BridgeDesignRoute(r.Id, r.Phase, ids, r.LSegmentStartIds, r.StraightLapStartIds);
            }).ToArray(), additions.Single(o => o.Phase == "B").Id);
        }

        private IReadOnlyList<BridgeDesignRoute> EditingRoutes =>
            CurrentSample.CopperEditRoutes.Count > 0 ? CurrentSample.CopperEditRoutes : CurrentSample.Routes;

        public bool CanRemoveBreakpoint(string id) => ValidateBreakpointRemoval(id).Success;

        private BridgeEditResult ValidateBreakpointRemoval(string id)
        {
            var node = JointRepresentative(id);
            if (node?.IsJointGroup != true || !node.IsEditable ||
                (node.Kind != DesignObjectKind.Breakpoint && node.Kind != DesignObjectKind.Lap))
                return new BridgeEditResult(false, "固定接口不能移除，请选择桥内普通断点。");
            if (Affected(node.Id, false).Any(o => IsLocked(o.Id))) return new BridgeEditResult(false, "断点已锁定，不能移除。");
            var adjacent = CopperSections.Where(s => s.StartPointId == node.Id || s.EndPointId == node.Id).ToArray();
            if (adjacent.Length != 2 || adjacent.Any(s => IsSpecialCopperSection(s.Id)))
                return new BridgeEditResult(false, "特殊段排边界断点不允许移除。");
            var tolerance = CurrentSample.IsSideToSide ? BridgeDesignPrecision.LengthToleranceMm : .001d;
            if (adjacent.Sum(s => s.Length) - CurrentSample.Parameters.OverlapLengthMm > 3000d + tolerance)
                return new BridgeEditResult(false, "合并后的段排长度超过3000.0 mm限制，禁止移除该断点。");
            return new BridgeEditResult(true);
        }

        public BridgeEditResult RemoveBreakpoint(string id)
        {
            var validation = ValidateBreakpointRemoval(id);
            if (!validation.Success) return validation;
            var removed = new HashSet<string>(Affected(id, false).Select(o => o.Id));
            var representative = JointRepresentative(id);
            var mergedIndex = CopperSections.Where(s => s.StartPointId == representative.Id || s.EndPointId == representative.Id).Min(s => s.Index);
            return CommitBreakpointTopology(EffectivePlan.Where(o => !removed.Contains(o.Id)).ToArray(),
                routes => routes.Select(r => new BridgeDesignRoute(r.Id, r.Phase,
                    r.PointIds.Where(point => !removed.Contains(point)), r.LSegmentStartIds, r.StraightLapStartIds)).ToArray(), null, mergedIndex);
        }

        private BridgeEditResult CommitBreakpointTopology(IReadOnlyList<BridgeDesignObject> proposed,
            Func<IReadOnlyList<BridgeDesignRoute>, IReadOnlyList<BridgeDesignRoute>> changeRoutes, string selection, int? mergedIndex = null)
        {
            BridgeDesignSample geometry;
            if (CurrentSample.ReplanCopper != null)
            {
                var result = TryReplanCopper(proposed, out geometry);
                if (!result.Success) return result;
            }
            else
            {
                if (CurrentSample.Routes.Any(r => r.IsPhysicalGeometry))
                    return new BridgeEditResult(false, "实际铜排缺少重新分节入口，未修改草稿。");
                geometry = CurrentSample.WithGeometry(proposed, changeRoutes(CurrentSample.Routes));
            }
            if (mergedIndex.HasValue && geometry.Routes.Where(r => r.CopperSectionId == BridgeCopperSection.IdFor(mergedIndex.Value))
                .Select(r => r.CopyNativePreview()?.GroupKey).Any(key => key != null && (key.Contains("/split") || key.Contains("/tail"))))
                return new BridgeEditResult(false, "合并后的实际展开长度需要再次分件，超过单段限制，禁止移除该断点。");
            var previousPointIds = new HashSet<string>(EffectivePlan.Where(point => point.IsJointGroup).Select(point => point.Id));
            var nextPointIds = new HashSet<string>(geometry.Objects.Where(point => point.IsJointGroup).Select(point => point.Id));
            if (geometry.IsSideToSide)
                return CommitFrameChange(Apply, allowUnchangedFrameAvoidance: true);
            BeginEdit(); Apply(); Notify(); return new BridgeEditResult(true);

            void Apply()
            {
                ApplyReplannedGeometry(geometry);
                _state.ManualJointPositions.UnionWith(previousPointIds.Except(nextPointIds).Concat(nextPointIds.Except(previousPointIds)));
                _state.TopologyBaseline = geometry;
                var baselineRoutes = Sample.CopperEditRoutes.Count > 0 ? Sample.CopperEditRoutes : Sample.Routes;
                var nextRoutes = geometry.CopperEditRoutes.Count > 0 ? geometry.CopperEditRoutes : geometry.Routes;
                _state.CopperTopologyChanged = !baselineRoutes.SelectMany(r => r.PointIds).SequenceEqual(nextRoutes.SelectMany(r => r.PointIds));
                var currentIds = new HashSet<string>(geometry.Objects.Select(o => o.Id));
                foreach (var included in _state.Added.Keys.Where(currentIds.Contains).ToArray()) _state.Added.Remove(included);
                foreach (var obsolete in _state.Overrides.Keys.Where(key => !currentIds.Contains(key)).ToArray()) _state.Overrides.Remove(obsolete);
                _state.Selection = selection != null && currentIds.Contains(selection) ? selection : null;
            }
        }
        public BridgeCopperSection RecommendedCopperSection(string id)
        {
            var section = FindCopperSection(id); var controls = CurrentSample?.CopperLengthBaseline;
            var start = controls?.FirstOrDefault(o => o.Id == section?.StartPointId);
            var end = controls?.FirstOrDefault(o => o.Id == section?.EndPointId);
            return start != null && end != null ? new BridgeCopperSection(section.Index, start.Id, end.Id,
                CurrentSample.CopperSectionLength(start, end), section.CanAdjustLength, CurrentSample.IsSideToSide) :
                Sample == null ? null : _geometry.CopperSections(Sample, Sample.Objects).FirstOrDefault(s => s.Id == id);
        }
        public bool CanAdjustCopperSectionLength(string id)
        {
            var section = FindCopperSection(id);
            var end = Find(section?.EndPointId);
            if (section?.CanAdjustLength == true && end != null &&
                !Affected(end.Id, false).Any(node => IsLocked(node.Id))) return true;
            var start = Find(section?.StartPointId);
            return section != null && !section.CanAdjustLength &&
                start?.IsJointGroup == true && (start.IsEditable || start.Role == "特殊段重算入口") &&
                (IsSpecialCopperSection(id) ||
                    ((start.Kind == DesignObjectKind.Breakpoint || start.Kind == DesignObjectKind.Lap) &&
                     start.EditableFields.Contains(DesignField.X) && CopperSections.Any(previous => previous.EndPointId == start.Id))) &&
                !Affected(start.Id, false).Any(node => IsLocked(node.Id));
        }

        public bool IsSpecialCopperSection(string id)
        {
            var section = FindCopperSection(id);
            if (section == null) return false;
            if (CurrentSample.SpecialCopperSectionIds != null) return CurrentSample.SpecialCopperSectionIds.Contains(id);
            // 旧面对面及侧对侧基座2记录未保存段类型。编辑路径保留工艺边界，物理排还包含普通搭接桥头。
            // 必须检查ABC三相：换相段的B相可能仍为直线，不能只用界面代表相判断。
            if (BridgeOffsetCopperChange.SupportsMiddleEditing(CurrentSample) &&
                CurrentSample.CopperEditRoutes.Count == 3)
            {
                var segments = CurrentSample.CopperEditRoutes.Select(r =>
                    _geometry.CopperSegments(r, EffectivePlan).ElementAtOrDefault(section.Index - 1)).ToArray();
                if (segments.All(editSegment => editSegment != null))
                    return segments.Any(editSegment => editSegment.Points.Any(p =>
                        Math.Abs(p.Y - editSegment.Points[0].Y) >= .1 || Math.Abs(p.Z - editSegment.Points[0].Z) >= .1));
            }
            var physical = CurrentSample.Routes.Where(r => r.CopperSectionId == id).ToArray();
            // 面对墙普通排也带搭接桥头的小折弯，不能用坐标变化识别Worker特殊排。
            if (CurrentSample.ScenarioKey == "FaceToWall" && physical.Length > 0)
                return physical.Any(r => r.PointIds.Select(Find).Any(p => p?.Role == "Worker特殊段加工几何"));
            if (physical.Length > 0)
                return physical.Any(r =>
                {
                    var points = r.PointIds.Select(Find).ToArray();
                    return points.Length > 1 && points.All(p => p != null) &&
                        points.Any(p => Math.Abs(p.Y - points[0].Y) >= .1 || Math.Abs(p.Z - points[0].Z) >= .1);
                });
            var route = CurrentSample.Routes.Where(r => r.LSegmentStartIds.Count == 0)
                .OrderBy(r => r.Phase == "B" ? 0 : 1).ThenBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault();
            var segment = route == null ? null : _geometry.CopperSegments(route, EffectivePlan).ElementAtOrDefault(section.Index - 1);
            return segment != null && segment.Points.Any(p =>
                Math.Abs(p.Y - segment.Points[0].Y) >= .1 || Math.Abs(p.Z - segment.Points[0].Z) >= .1);
        }
        public bool IsCopperSectionAdjusted(string id)
        {
            var section = FindCopperSection(id);
            return section != null && IsAdjusted(section.CanAdjustLength ? section.EndPointId : section.StartPointId);
        }

        // 保留已有代表选择入口名；断点和绝缘子均按显式组ID定位，不能按名称或坐标猜测归属。
        public BridgeDesignObject JointRepresentative(string id)
        {
            var node = Find(id);
            if (node?.IsInsulatorGroup == true)
            {
                var members = EffectivePlan.Where(o => o.InsulatorGroupId == node.InsulatorGroupId).ToArray();
                if (BridgeInsulatorObjects.GroupProblem(members) != null) return null;
                // 落地组按本轮启用状态确定代表，不依赖压缩位置的推荐坐标。
                if (members.Any(BridgeSmallOffsetSupportPlanner.IsSupport))
                    return members.Single(o => !o.IsSuppressedInsulator);
                var vertical = Find(node.ParentId)?.FrameTemplateKey == "SideToSidePhaseChange029";
                var recommended = members.Select(o => new { Node = o, Baseline = Recommended(o.Id) }).ToArray();
                if (recommended.Any(o => o.Baseline == null)) return null;
                members = recommended.OrderBy(o => vertical ? o.Baseline.Z : o.Baseline.Y)
                    .ThenBy(o => o.Node.Id, StringComparer.Ordinal).Select(o => o.Node).ToArray();
                // 默认三只选中间位置；数量变化时选中间成员，代表身份不随整组平移变化。
                return members.FirstOrDefault(o => o.Phase == "B") ?? members[members.Length / 2];
            }
            if (node == null || !node.IsJointGroup) return node;
            var group = EffectivePlan.Where(o => o.JointGroupId == node.JointGroupId);
            if (CurrentSample.IsSideToSide)
            {
                var reference = group.Where(o => o.Phase == "B").Take(2).ToArray();
                return reference.Length == 1 ? reference[0] : null;
            }
            return group
                .OrderBy(o => o.Phase == "B" ? 0 : 1).ThenBy(o => o.Id, StringComparer.Ordinal).First();
        }

        public void Load(BridgeDesignSample sample)
        {
            Sample = sample; _state = new EditState(); _undo.Clear(); _redo.Clear();
            _state.Hangers = sample?.SavedHangers;
            if (sample != null) _state.Locks.UnionWith(sample.SavedLockedObjectIds);
            if (sample != null) _state.ManualJointPositions.UnionWith(sample.SavedManualJointIds);
            if (sample?.SavedFrameLengthEditing != null)
            {
                _state.IndependentFrames.UnionWith(sample.SavedFrameLengthEditing.IndependentFrames);
                _state.FixedEndFrames.UnionWith(sample.SavedFrameLengthEditing.FixedEndFrames);
                _state.LengthAdjustedFrames.UnionWith(sample.SavedFrameLengthEditing.LengthAdjustedFrames);
            }
            if (sample?.ReadbackBaseline != null)
            {
                // 反读是加载已保存事实，不能在这里自动避让、跳过绝缘子或拆分同源。
                _state.PreserveInsulatorLayout = true;
                LastLengthAdjustedFrameIds = Array.Empty<string>();
                LastAutoMovedFrameIds = Array.Empty<string>();
                ClearanceIssues = Array.AsReadOnly(new BusbarBridgeFrameJointClearance().ValidateBaseJoints(sample, sample.Objects)
                    .Concat(new BridgeRaisedLapPlanner().Apply(sample, sample.Objects, allowLift: false, keepNotchPlan: true).Issues).ToArray());
                _loadedState = _state.Copy();
                InvalidateSnapshot();
                if (_state.Hangers == null && sample.HangerRoof != null) _state.Hangers = _hangerPlanner.InitialLayout(CurrentSample, EffectivePlan);
                Revision++; Changed?.Invoke(this, EventArgs.Empty); return;
            }
            LastLengthAdjustedFrameIds = Array.Empty<string>(); Notify(false, initialLoad: true);
            _loadedState = _state.Copy();
            if (_state.Hangers == null) _state.Hangers = _hangerPlanner.InitialLayout(CurrentSample, EffectivePlan);
            InvalidateHangerPlacement();
            Revision++; Changed?.Invoke(this, EventArgs.Empty);
        }

        internal BridgeEditResult PlanAndApplyRepair(Func<BridgeDesignSample, BridgeDesignSample> planner)
        {
            if (planner == null) throw new ArgumentNullException(nameof(planner));
            if (CurrentSample?.ReadbackBaseline == null)
                return new BridgeEditResult(false, "修复需要当前实物反读基线，请重新读取模型。");
            // 自动同步和窗口按钮都捕获同一份编辑状态，不能直接 Load 规划结果丢掉人工定位或锁定。
            var actual = CaptureSavedSample();
            var target = planner(actual);
            if (ReferenceEquals(target, actual) && target.ReadbackBaseline.RepairPlanned != true)
                return new BridgeEditResult(true);
            var result = ApplyRepairTarget(target);
            return result.Success ? new BridgeEditResult(true, target.ReadbackBaselineWarning) : result;
        }

        internal BridgeEditResult ApplyRepairTarget(BridgeDesignSample target)
        {
            if (target?.ReadbackBaseline?.RepairPlanned != true || CurrentSample?.ReadbackBaseline == null ||
                target.ReadbackBaseline.Signature != CurrentSample.ReadbackBaseline.Signature)
                return new BridgeEditResult(false, "修复目标与当前实物基线不匹配。");
            try
            {
                target = CompleteSupportTargets(target);
                BridgeCopperManufacturingValidation.RequireValid(target);
            }
            catch (InvalidOperationException error) { return new BridgeEditResult(false, error.Message); }
            var actual = CaptureSavedSample();
            var transition = new BridgeSupportTransition();
            var lockedObjects = actual.Objects.Where(o => IsLocked(o.Id)).Select(o => o.Id);
            var lockConflicts = transition.Inspect(actual.WithSavedLocks(lockedObjects).WithSavedManualInsulators(Array.Empty<string>()), target);
            if (lockConflicts.Count > 0) return new BridgeEditResult(false, string.Join("；", lockConflicts.Select(c => c.Description)));
            var previous = _state;
            var existing = new HashSet<string>(transition.RemapIds(actual, target, actual.Objects.Select(o => o.Id)));
            var next = new EditState { CopperGeometry = target, CopperTopologyChanged = true, PreserveInsulatorLayout = true,
                Selection = transition.RemapIds(actual, target, new[] { previous.Selection }).FirstOrDefault(), Hangers = previous.Hangers };
            // 反读后尚未保存的选择也属于本次编辑，不能被修复目标携带的旧标记覆盖。
            next.Locks.UnionWith(transition.RemapIds(actual, target, previous.Locks));
            next.Locks.UnionWith(target.SavedLockedObjectIds.Where(id => !existing.Contains(id)));
            next.IndependentFrames.UnionWith(transition.RemapIds(actual, target, previous.IndependentFrames));
            next.FixedEndFrames.UnionWith(transition.RemapIds(actual, target, previous.FixedEndFrames));
            next.LengthAdjustedFrames.UnionWith(transition.RemapIds(actual, target, previous.LengthAdjustedFrames));
            if (target.SavedFrameLengthEditing != null)
            {
                next.IndependentFrames.UnionWith(target.SavedFrameLengthEditing.IndependentFrames.Where(id => !existing.Contains(id)));
                next.FixedEndFrames.UnionWith(target.SavedFrameLengthEditing.FixedEndFrames.Where(id => !existing.Contains(id)));
                next.LengthAdjustedFrames.UnionWith(target.SavedFrameLengthEditing.LengthAdjustedFrames.Where(id => !existing.Contains(id)));
            }
            _state = next;
            _undo.Push(previous); _redo.Clear();
            LastLengthAdjustedFrameIds = Array.Empty<string>(); LastAutoMovedFrameIds = Array.Empty<string>();
            ClearanceIssues = Array.Empty<BridgeDesignIssue>(); InvalidateSnapshot();
            if (_state.Hangers == null) _state.Hangers = _hangerPlanner.InitialLayout(CurrentSample, EffectivePlan);
            Revision++;
            Changed?.Invoke(this, EventArgs.Empty);
            return new BridgeEditResult(true);
        }

        internal BridgeEditResult ReplaceWallSpecialScheme(BridgeDesignSample geometry, bool preserveSupports = false,
            bool renewSupportBaseline = false)
        {
            if (CurrentSample?.ScenarioKey != "FaceToWall" || geometry?.ScenarioKey != "FaceToWall")
                return new BridgeEditResult(false, "只能替换当前面对墙草稿的特殊铜排。");
            var current = EffectivePlan;
            var editedTopology = EditedFrameTopologyIds();
            var fixedPorts = current.Where(o => o.Kind == DesignObjectKind.Frame && IsIndependentFrame(o.Id))
                .ToDictionary(o => o.Id, o => IsFrameEndFixed(o.Id) ? 1 : 0);
            var hasDeletedFrames = CurrentSample.FrameLengthBaseline.Any(o => !current.Any(frame => frame.Id == o.Id));
            if (preserveSupports)
                geometry = PreserveWallSupports(geometry, CurrentSample.Objects.Where(o =>
                    o.Kind == DesignObjectKind.Frame || o.Kind == DesignObjectKind.Insulator).ToArray());
            // 只有按新铜排重新规划支撑时才更新安全范围；人工分节不能把当前尺寸变成新基线。
            var editing = CaptureFrameLengthEditing();
            var baseline = renewSupportBaseline && !preserveSupports ? geometry.FrameLengthBaseline : CurrentSample.FrameLengthBaseline;
            geometry = geometry.WithFrameLengthEditing(new BridgeFrameLengthEditing(baseline,
                    editing.IndependentFrames, editing.FixedEndFrames, editing.LengthAdjustedFrames, editing.OffsetCopperMinimumLengthMm))
                .WithReadbackBaseline(geometry.ReadbackBaseline ?? CurrentSample.ReadbackBaseline);
            var retained = new HashSet<string>(geometry.Objects.Select(o => o.Id));
            var removedLocks = current.Where(o => IsLocked(o.Id) && !retained.Contains(o.Id) &&
                o.Kind != DesignObjectKind.Frame && o.Kind != DesignObjectKind.Insulator).ToArray();
            if (removedLocks.Length > 0)
                return new BridgeEditResult(false, "特殊段含已锁定断点，请先解锁；当前草稿保留：" +
                    string.Join("、", removedLocks.Select(o => o.DisplayName)));
            if (current.Where(o => o.IsJointGroup && IsLocked(o.Id)).Any(o =>
                geometry.Objects.Any(next => next.Id == o.Id && new[] { DesignField.X, DesignField.Y, DesignField.Z }
                    .Any(field => Math.Abs(next.Value(field) - o.Value(field)) >= .01))))
                return new BridgeEditResult(false, "特殊段重规划会移动已锁定断点，当前草稿保留。");
            var previous = _state; var previousIssues = ClearanceIssues;
            var previousMoved = LastAutoMovedFrameIds; var previousLengths = LastLengthAdjustedFrameIds;
            _state = previous.Copy(); _editing = true; InvalidateSnapshot();
            var committed = false;
            try
            {
                _state.CopperGeometry = geometry; _state.CopperTopologyChanged = true;
                var supportIds = new HashSet<string>(current.Where(o => o.Kind == DesignObjectKind.Frame ||
                    o.Kind == DesignObjectKind.Insulator).Select(o => o.Id));
                foreach (var id in _state.Added.Keys.Where(id => retained.Contains(id) && !supportIds.Contains(id)).ToArray())
                    _state.Added.Remove(id);
                foreach (var id in _state.Overrides.Keys.Where(id => !retained.Contains(id) && !supportIds.Contains(id)).ToArray())
                    _state.Overrides.Remove(id);
                // 新支撑仍须满足人工长度、固定端及安装状态约束，失败时连同铜排一起回滚。
                var lengthLocks = ValidateLengthLocks(current, EffectivePlan);
                if (!lengthLocks.Success) return lengthLocks;
                var topology = ValidateEditedFrameTopology(current, EffectivePlan, editedTopology);
                if (!topology.Success) return topology;
                foreach (var frame in current.Where(o => o.Kind == DesignObjectKind.Frame &&
                    (HasIndependentLengthAdjustment(o.Id) || fixedPorts.ContainsKey(o.Id))))
                {
                    var next = EffectivePlan.FirstOrDefault(o => o.Id == frame.Id);
                    if (next == null || Math.Abs(next.Length - frame.Length) >= .000001 ||
                        fixedPorts.TryGetValue(frame.Id, out var port) && Enumerable.Range(0, 3).Any(axis =>
                            Math.Abs(_geometry.FrameInterfacePoints(frame)[port][axis] - _geometry.FrameInterfacePoints(next)[port][axis]) > FrameConnectionToleranceMm))
                        return new BridgeEditResult(false, "特殊段重规划会覆盖人工基架长度或固定端，当前草稿保留。");
                }
                if (hasDeletedFrames && !SameFrameLayout(current, EffectivePlan))
                    return new BridgeEditResult(false, "特殊段重规划会覆盖已删除基架的缺口或人工布置，当前草稿保留。");
                var layout = ValidateFrameLayout(OrderedFrames());
                if (!layout.Success) return layout;
                Notify(false, new HashSet<string>());
                var introduced = ClearanceIssues.FirstOrDefault(issue => issue.BlocksGeneration &&
                    !previousIssues.Any(old => old.BlocksGeneration && old.TargetId == issue.TargetId && old.Source == issue.Source && old.Message == issue.Message));
                if (introduced != null) return new BridgeEditResult(false, "特殊段替换未提交：" + introduced.Message);
                _undo.Push(previous); _redo.Clear(); Revision++; committed = true;
                Changed?.Invoke(this, EventArgs.Empty);
                return new BridgeEditResult(true);
            }
            finally
            {
                if (!committed)
                {
                    _state = previous; ClearanceIssues = previousIssues; LastAutoMovedFrameIds = previousMoved;
                    LastLengthAdjustedFrameIds = previousLengths; _editing = false; InvalidateSnapshot();
                }
            }
        }

        private BridgeDesignSample PreserveWallSupports(BridgeDesignSample geometry, IReadOnlyList<BridgeDesignObject> supports)
        {
            var planner = geometry.ReplanCopper;
            return geometry.WithGeometry(geometry.Objects.Where(o => o.Kind != DesignObjectKind.Frame && o.Kind != DesignObjectKind.Insulator)
                .Concat(supports), geometry.Routes).WithCopperEditing(geometry.CopperEditRoutes,
                    planner == null ? null : new Func<IReadOnlyList<BridgeDesignObject>, BridgeDesignSample>(proposed =>
                        PreserveWallSupports(planner(proposed), supports)));
        }

        private IEnumerable<BridgeDesignObject> BaseObjects() =>
            (CurrentSample?.Objects ?? Array.Empty<BridgeDesignObject>()).Concat(_state.Added.Values);

        private BridgeDesignObject Effective(BridgeDesignObject node)
        {
            if (_state.TemplateNotches.TryGetValue(node.Id, out var notches)) node = node.WithAngleNotches(notches);
            if (_state.AutomaticallyPositionedFrames.Contains(node.Id)) node = node.WithAutomaticInsulatorPlacement();
            if (_state.SourceNames.TryGetValue(node.Id, out var sourceName)) node = node.WithSourceName(sourceName);
            if (!_state.Overrides.TryGetValue(node.Id, out var changes)) return node;
            var values = node.Values.ToDictionary(p => p.Key, p => p.Value);
            foreach (var change in changes) values[change.Key] = change.Value;
            return node.WithValues(values);
        }

        public IReadOnlyList<BridgeDesignObject> Affected(string id, bool sameSource)
        {
            var node = Find(id);
            if (node == null) return Array.Empty<BridgeDesignObject>();
            if (node.IsJointGroup) return EffectivePlan.Where(o => o.JointGroupId == node.JointGroupId).ToArray();
            if (node.IsInsulatorGroup)
            {
                var parent = Find(node.ParentId);
                if (sameSource && parent?.Kind == DesignObjectKind.Frame)
                {
                    var parentIds = EffectivePlan.Where(o => o.SharesFrameSourceWith(parent)).Select(o => o.Id).ToArray();
                    var assemblySuffix = node.InsulatorGroupId.Substring(parent.Id.Length);
                    return EffectivePlan.Where(o => o.Kind == DesignObjectKind.Insulator && parentIds.Contains(o.ParentId) &&
                        o.InsulatorGroupId == o.ParentId + assemblySuffix).ToArray();
                }
                return EffectivePlan.Where(o => o.InsulatorGroupId == node.InsulatorGroupId).ToArray();
            }
            if (!sameSource) return new[] { node };
            var frame = node.Kind == DesignObjectKind.Frame ? node : Find(node.ParentId);
            if (frame?.Kind != DesignObjectKind.Frame) return new[] { node };
            var frames = EffectivePlan.Where(o => o.SharesFrameSourceWith(frame)).ToArray();
            if (node.Kind == DesignObjectKind.Frame) return frames;
            var suffix = node.Id.Substring(node.ParentId.Length);
            return frames.Select(f => Find(f.Id + suffix)).Where(o => o != null).ToArray();
        }

        public bool CanDragObject(string id, bool sameSource)
        {
            var node = Find(id);
            return node != null && node.IsEditable && !BridgeSmallOffsetSupportPlanner.IsSupport(node) &&
                (node.Kind == DesignObjectKind.Lap || node.Kind == DesignObjectKind.Breakpoint ||
                 node.Kind == DesignObjectKind.FixedJoint || node.Kind == DesignObjectKind.Insulator) &&
                !Affected(id, sameSource && node.Kind != DesignObjectKind.Insulator).Any(o => IsPositionLocked(o.Id) || IsPositionLocked(o.ParentId));
        }

        public Dictionary<DesignField, double> ConstrainDragValues(string id, IDictionary<DesignField, double> proposed, bool sameSource)
        {
            if (proposed == null || !CanDragObject(id, sameSource)) return new Dictionary<DesignField, double>();
            var node = Find(id);
            // 断点及绝缘子均只允许X向拖动；绝缘子使用所属基架的局部X（定位轴）。
            // 临时预览与最终提交共用边界，禁止横跨相间方向或改变底板贴合高度。
            var values = proposed.Where(p => node.EditableFields.Contains(p.Key) && p.Key == DesignField.X)
                .ToDictionary(p => p.Key, p => p.Value);
            if (node.Kind == DesignObjectKind.FixedJoint && values.TryGetValue(DesignField.X, out var x) &&
                TryGetBaseJointRange(node.Id, out var range))
            {
                var penetration = Math.Max(range.MinimumPenetration,
                    Math.Min(range.MaximumPenetration, range.PenetrationAt(x)));
                values[DesignField.X] = range.XAt(penetration);
            }
            return values;
        }

        public BridgeEditResult ApplyDrag(string id, IDictionary<DesignField, double> proposed, bool sameSource)
        {
            if (Find(id)?.Kind == DesignObjectKind.Insulator) sameSource = false;
            if (!CanDragObject(id, sameSource)) return new BridgeEditResult(false, "该对象不允许拖动，或调整范围包含锁定对象。");
            return SetValues(id, ConstrainDragValues(id, proposed, sameSource), sameSource);
        }

        public BridgeEditResult SetText(string id, DesignField field, string text, bool sameSource)
        {
            if (field == DesignField.Installation)
            {
                if (text != "正装" && text != "反装") return new BridgeEditResult(false, "安装方式请选择正装或反装。");
                return SetValues(id, new Dictionary<DesignField, double> { [field] = text == "反装" ? 1 : 0 }, sameSource);
            }
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return new BridgeEditResult(false, "请输入数字（小数点为 .），当前输入未提交。");
            var node = Find(id);
            if (node?.Kind == DesignObjectKind.Frame && (field == DesignField.Length || field == DesignField.Width || field == DesignField.Height))
                value = BridgeDesignPrecision.OneDecimal(value);
            if (field == DesignField.WallEntryDistance || field == DesignField.WallVerticalClearance || field == DesignField.WallHorizontalClearance)
                return SetWallParameter(node, field, value);
            if (field == DesignField.X && node?.Kind == DesignObjectKind.FixedJoint && node.EditableFields.Contains(DesignField.X))
                return new BridgeEditResult(false, "基座L排断点不直接输入X坐标，请使用“距基座边距离”。");
            if (field == DesignField.CopperSegmentLength) return SetCopperSectionLength(id, value);
            if (field == DesignField.JointDistanceFromPrevious) return SetJointDistanceFromPrevious(id, value);
            if (field == DesignField.BaseJointPenetrationDistance) return SetBaseJointPenetration(id, value);
            if (field == DesignField.BaseJointEdgeDistance) return SetBaseJointPenetration(id, -value);
            if (field == DesignField.InsulatorPosition) return SetInsulatorPosition(id, value, sameSource);
            if (field == DesignField.InsulatorDistanceFromPrevious) return SetInsulatorDistanceFromPrevious(id, value);
            return SetValues(id, new Dictionary<DesignField, double> { [field] = value }, sameSource);
        }

        public double? MeasureBaseJointEdgeDistance(string id) => -MeasureBaseJointPenetration(id);
        public double? MeasureRecommendedBaseJointEdgeDistance(string id) => -MeasureRecommendedBaseJointPenetration(id);

        public double? MeasureBaseJointPenetration(string id)
        {
            var node = JointRepresentative(id);
            if (node == null || !TryGetBaseJointRange(node.Id, out var range)) return null;
            return range.PenetrationAt(node.X);
        }

        public double? MeasureRecommendedBaseJointPenetration(string id)
        {
            var node = JointRepresentative(id);
            var recommended = Recommended(node?.Id);
            if (recommended == null || !TryGetBaseJointRange(recommended.Id, out var range)) return null;
            return range.PenetrationAt(recommended.X);
        }

        public double? MaximumBaseJointPenetration(string id) =>
            TryGetBaseJointRange(id, out var range) ? (double?)range.MaximumPenetration : null;

        public BridgeEditResult SetBaseJointPenetration(string id, double distance)
        {
            distance = BridgeDesignPrecision.OneDecimal(distance);
            var node = JointRepresentative(id);
            if (node == null || !TryGetBaseJointRange(node.Id, out var range))
                return new BridgeEditResult(false, "请选择基座L排断点。");
            if (double.IsNaN(distance) || double.IsInfinity(distance) ||
                distance < range.MinimumPenetration || distance > range.MaximumPenetration)
                return new BridgeEditResult(false, "距基座边距离必须为" +
                    (-range.MaximumPenetration).ToString("0.0", CultureInfo.InvariantCulture) + "～" +
                    (-range.MinimumPenetration).ToString("0.0", CultureInfo.InvariantCulture) +
                    " mm；远离基座为正，进入基座为负，且不得越过相邻断点或L排折弯余量边界。");
            return SetValues(node.Id, new Dictionary<DesignField, double>
            {
                [DesignField.X] = range.XAt(distance)
            }, false);
        }

        public BridgeJointMeasurements MeasureJoint(string id)
        {
            var representative = JointRepresentative(id);
            return representative == null ? null : _geometry.MeasureJoint(CurrentSample, EffectivePlan, representative.Id);
        }

        public BridgeJointMeasurements MeasureRecommendedJoint(string id)
        {
            var representative = JointRepresentative(id);
            if (representative == null || Sample == null) return null;
            var baseline = Sample.Objects.Any(o => o.Id == representative.Id) ? Sample : _state.TopologyBaseline ?? CurrentSample;
            return _geometry.MeasureJoint(baseline, baseline.Objects, representative.Id);
        }

        private BridgeEditResult SetJointDistanceFromPrevious(string id, double distance, bool normalizeInput = true)
        {
            if (normalizeInput) distance = BridgeDesignPrecision.OneDecimal(distance);
            var node = JointRepresentative(id);
            if (node == null || !node.IsJointGroup ||
                (node.Kind != DesignObjectKind.Lap && node.Kind != DesignObjectKind.Breakpoint))
                return new BridgeEditResult(false, "请选择可调整的桥内断点。");
            if (double.IsNaN(distance) || double.IsInfinity(distance) || distance <= 0)
                return new BridgeEditResult(false, "距前断点距离必须为大于0的有限数值。");
            var route = (CurrentSample.CopperEditRoutes.Count > 0 ? CurrentSample.CopperEditRoutes : CurrentSample.Routes)
                .FirstOrDefault(r => r.LSegmentStartIds.Count == 0 && r.PointIds.Contains(node.Id));
            if (route == null) return new BridgeEditResult(false, "未找到断点所属的主铜排路径。");
            var index = route.PointIds.ToList().IndexOf(node.Id);
            if (index <= 0 || index >= route.PointIds.Count - 1)
                return new BridgeEditResult(false, "固定端点不支持设置距前断点距离。");
            var previous = Find(route.PointIds[index - 1]);
            var next = Find(route.PointIds[index + 1]);
            if (previous == null || next == null) return new BridgeEditResult(false, "断点相邻节点缺失，未提交修改。");
            var direction = Math.Sign(next.X - previous.X);
            if (direction == 0) return new BridgeEditResult(false, "相邻断点的X向间距为0，不能设置距离。");
            if (distance >= Math.Abs(next.X - previous.X))
                return new BridgeEditResult(false, "距前断点距离必须小于前后相邻断点的X向总距离。");
            return SetValues(node.Id, new Dictionary<DesignField, double>
            {
                [DesignField.X] = previous.X + direction * distance
            }, false);
        }

        public BridgeEditResult SetCopperSectionLength(string id, double length)
        {
            length = BridgeDesignPrecision.OneDecimal(length);
            var section = FindCopperSection(id);
            if (section == null) return new BridgeEditResult(false, "段排不存在，未提交修改。");
            if (!CanAdjustCopperSectionLength(id))
                return new BridgeEditResult(false, IsSpecialCopperSection(id) ? "特殊段相邻断点固定或已锁定，不能调整跨度。" :
                    section.CanAdjustLength ? "段排右端断点已锁定，铜排长度只读。" : "末端段排缺少可调整的前段断点，或该断点已锁定，不能调整长度。");
            var overlap = Sample.Parameters.OverlapLengthMm;
            if (double.IsNaN(length) || double.IsInfinity(length) || length <= overlap)
                return new BridgeEditResult(false, "铜排长度必须大于搭接长度。");
            if (!section.CanAdjustLength)
            {
                var start = Find(section.StartPointId);
                if (!IsSpecialCopperSection(id))
                {
                    var previous = CopperSections.First(s => s.EndPointId == start.Id);
                    // 末端接口不动，移动入口ABC断点；复用距离校验，不能把前段压到相邻断点之外。
                    return SetJointDistanceFromPrevious(start.Id, previous.Length - overlap - (length - section.Length),
                        normalizeInput: !CurrentSample.IsSideToSide);
                }
                var end = Find(section.EndPointId);
                var direction = Math.Sign(end.X - start.X);
                if (direction == 0) return new BridgeEditResult(false, "特殊段桥向跨度为0，不能调整长度。");
                // 墙端接口保持固定；特殊末段通过入口搭接补偿前段，改变桥向跨度而非展开长度。
                return SetValues(start.Id, new Dictionary<DesignField, double>
                {
                    [DesignField.X] = start.X - direction * (length - section.Length)
                }, false);
            }
            // 改本段右端断点，后一段自然反向补偿同一差值；两个搭接量不变，因此两段合计保持不变。
            return SetJointDistanceFromPrevious(section.EndPointId, length - overlap,
                normalizeInput: !CurrentSample.IsSideToSide);
        }

        public BridgeEditResult SetInsulatorPosition(string id, double position, bool sameSource)
        {
            position = BridgeDesignPrecision.OneDecimal(position);
            var node = JointRepresentative(id); var frame = Find(node?.ParentId);
            if (node?.Kind != DesignObjectKind.Insulator || frame?.Kind != DesignObjectKind.Frame)
                return new BridgeEditResult(false, "请选择有效的架内绝缘子定位项。");
            if (double.IsNaN(position) || double.IsInfinity(position) || position < 0 || position > frame.Length)
                return new BridgeEditResult(false, "绝缘子定位必须为0～基架长度之间的有限数值。");
            foreach (var member in Affected(node.Id, sameSource))
            {
                var owner = Find(member.ParentId);
                if (position > owner.Length || EffectivePlan.Any(o => o.ParentId == owner.Id && o.Kind == DesignObjectKind.Insulator &&
                    o.InsulatorGroupId != member.InsulatorGroupId && Math.Abs(o.X + owner.Length / 2 - position) < .01))
                    return new BridgeEditResult(false, "绝缘子定位不能超出基架长度或与本架其他定位重复。");
            }
            // 定位以模型起始端为基准；安装正反只影响投影，不改变模型定位尺寸。
            return SetValues(node.Id, new Dictionary<DesignField, double> { [DesignField.X] = position - frame.Length / 2 }, sameSource);
        }

        public BridgeEditResult SetValues(string id, IDictionary<DesignField, double> changes, bool sameSource)
        {
            var node = Find(id);
            if (node == null) return new BridgeEditResult(false, "对象不存在，未提交修改。");
            if (BridgeSmallOffsetSupportPlanner.IsSupport(node) || node.Kind == DesignObjectKind.Frame &&
                changes.ContainsKey(DesignField.InsulatorCount) && EffectivePlan.Any(o => o.ParentId == node.Id && BridgeSmallOffsetSupportPlanner.IsSupport(o)))
                return new BridgeEditResult(false, "小偏移搭接的三组单相绝缘子由中间搭接排中心定位，请调整铜排参数。");
            if (node.Kind == DesignObjectKind.Frame && IsLocked(node.Id) && changes.ContainsKey(DesignField.Length))
                return new BridgeEditResult(false, "当前基架长度已锁定，请先解锁再修改长度。");
            if (IsPositionLocked(node.Id) || IsPositionLocked(node.ParentId))
                return new BridgeEditResult(false, "当前对象或所属基架已锁定，请先解锁再修改。");
            if (node.Kind == DesignObjectKind.Frame && changes.Keys.Any(f => f == DesignField.Width || f == DesignField.Height))
                return new BridgeEditResult(false, "基架宽度和高度为只读参数，不允许覆盖。");
            if (changes.ContainsKey(DesignField.Group))
                return new BridgeEditResult(false, "同源关系由基架名称确定，不支持编辑同源组编号。");
            if (IsIndependentFrame(node.Kind == DesignObjectKind.Frame ? node.Id : node.ParentId)) sameSource = false;
            if (node.Kind == DesignObjectKind.Frame && changes.ContainsKey(DesignField.Length))
            {
                if (changes.Count != 1) return new BridgeEditResult(false, "基架长度请单独提交，以便完成总长补偿。");
                return SetFrameLength(node, changes[DesignField.Length], sameSource);
            }
            var targets = Affected(id, sameSource);
            foreach (var pair in changes)
            {
                var validation = Validate(node, pair.Key, pair.Value);
                if (!validation.Success) return validation;
            }
            if (targets.Any(o => IsPositionLocked(o.Id) || IsPositionLocked(o.ParentId)))
                return new BridgeEditResult(false, "影响范围包含锁定对象，请先明确解锁再调整。");
            if (changes.Keys.Any(f => f == DesignField.InsulatorCount || f == DesignField.Installation) && targets.Any(f =>
                EffectivePlan.Any(o => o.ParentId == f.Id && IsLocked(o.Id))))
                return new BridgeEditResult(false, "架内绝缘子已锁定，请先解锁再修改安装方式或数量。");
            var mutations = new List<Tuple<BridgeDesignObject, DesignField, double>>();
            foreach (var target in targets)
            foreach (var pair in changes)
            {
                // 同源节位置用位移量同步，不能把不同节重叠到同一绝对坐标。
                var coordinate = pair.Key == DesignField.X || pair.Key == DesignField.Y || pair.Key == DesignField.Z;
                var sharedSideToSideX = CurrentSample.IsSideToSide && node.IsJointGroup && pair.Key == DesignField.X;
                var value = !sharedSideToSideX && coordinate && (node.Kind == DesignObjectKind.Frame || node.IsGrouped)
                    ? target.Value(pair.Key) + pair.Value - node.Value(pair.Key) : pair.Value;
                var validation = Validate(target, pair.Key, value);
                if (!validation.Success) return validation;
                if (sharedSideToSideX ? value != target.Value(pair.Key) : Math.Abs(value - target.Value(pair.Key)) >= .01)
                    mutations.Add(Tuple.Create(target, pair.Key, value));
            }
            if (mutations.Count == 0) return new BridgeEditResult(true);
            BridgeDesignSample copperGeometry = null;
            if (node.IsJointGroup)
            {
                var proposed = EffectivePlan.Select(o =>
                {
                    var values = o.Values.ToDictionary(p => p.Key, p => p.Value);
                    foreach (var mutation in mutations.Where(m => m.Item1.Id == o.Id)) values[mutation.Item2] = mutation.Item3;
                    return o.WithValues(values);
                }).ToArray();
                var validation = CurrentSample.ReplanCopper != null ? TryReplanCopper(proposed, out copperGeometry) :
                    new BusbarBridgeFrameJointClearance().Validate(CurrentSample, proposed).Count > 0 ?
                    PlanCopperFrameSeams(CurrentSample.WithGeometry(proposed, CurrentSample.Routes), out copperGeometry) : new BridgeEditResult(true);
                if (!validation.Success) return validation;
            }
            Action apply = () =>
            {
                if (copperGeometry != null) ApplyReplannedGeometry(copperGeometry);
                if (!sameSource) MakeIndependentForEdit(node);
                foreach (var mutation in mutations)
                {
                    Put(mutation.Item1.Id, mutation.Item2, mutation.Item3);
                    if (mutation.Item1.IsJointGroup && (mutation.Item2 == DesignField.X || mutation.Item2 == DesignField.Y || mutation.Item2 == DesignField.Z))
                        _state.ManualJointPositions.Add(mutation.Item1.Id);
                    if (mutation.Item1.Kind == DesignObjectKind.Insulator &&
                        (mutation.Item2 == DesignField.X || mutation.Item2 == DesignField.Y || mutation.Item2 == DesignField.Z))
                    {
                        _state.ReleasedManualInsulatorPositions.Remove(mutation.Item1.Id);
                        _state.ManualInsulatorPositions.Add(mutation.Item1.Id);
                    }
                    if (mutation.Item2 == DesignField.InsulatorCount) ResizeInsulators(mutation.Item1, (int)mutation.Item3);
                }
            };
            if (node.Kind == DesignObjectKind.Frame || copperGeometry != null)
                return CommitFrameChange(apply, allowUnchangedFrameAvoidance: copperGeometry != null);
            if (node.Kind == DesignObjectKind.Insulator && changes.Count == 1 && changes.ContainsKey(DesignField.X))
                return CommitInsulatorChange(apply, new HashSet<string>(targets.Select(o => o.Id)));
            BeginEdit(); apply();
            Notify(); return new BridgeEditResult(true);
        }

        private BridgeEditResult TryReplanCopper(IReadOnlyList<BridgeDesignObject> proposed,
            out BridgeDesignSample geometry)
        {
            geometry = null;
            try
            {
                BridgePhaseChangeCopperChange.Create(CurrentSample.WithGeometry(EffectivePlan, CurrentSample.Routes), proposed);
                geometry = CurrentSample.ReplanCopper(proposed);
                if (geometry == null) return new BridgeEditResult(false, "铜排重规划未返回有效方案，当前草稿保留。");
                if (geometry.IsSideToSide)
                {
                    var problem = BridgeSideToSideCopperEditor.PlanProblem(geometry);
                    if (problem != null) return new BridgeEditResult(false, problem);
                }
                geometry = geometry.WithCopperLengthBaseline(CurrentSample.CopperLengthBaseline);
                var offsetChange = BridgeOffsetCopperChange.Create(CurrentSample.WithGeometry(EffectivePlan, CurrentSample.Routes), proposed);
                if (offsetChange != null)
                {
                    var offsetValidation = PlanOffsetCopperSupports(offsetChange, proposed, ref geometry);
                    if (!offsetValidation.Success) return offsetValidation;
                    return PlanCopperFrameSeams(geometry, out geometry);
                }
                var seamValidation = PlanCopperFrameSeams(geometry, out var seamGeometry);
                if (!seamValidation.Success) return seamValidation;
                geometry = seamGeometry;
                var affectedSupports = ChangedSupportIds(geometry);
                var plannedIds = new HashSet<string>(geometry.Objects.Select(o => o.Id));
                var resultingObjects = geometry.Objects.Where(o => !_state.Removed.Contains(o.Id) || affectedSupports.Contains(o.Id))
                    .Concat(_state.Added.Values.Where(o => !plannedIds.Contains(o.Id)))
                    .Select(o => affectedSupports.Contains(o.Id) ? o : Effective(o)).ToArray();
                var lengthLocks = ValidateLengthLocks(EffectivePlan, resultingObjects);
                if (!lengthLocks.Success) return lengthLocks;
                // 受影响的人工基架不能被算法重铺覆盖；包括删除后已保存的端部缺口。
                var protectedFrames = new HashSet<string>(_state.Removed.Concat(_state.Added.Keys)
                    .Concat(_state.LengthAdjustedFrames).Concat(_state.IndependentFrames).Concat(_state.FixedEndFrames));
                protectedFrames.UnionWith(EditedFrameTopologyIds());
                if (affectedSupports.Any(protectedFrames.Contains))
                    return new BridgeEditResult(false, "铜排重规划会覆盖人工基架安装、长度、缺口或固定端，请先调整冲突的基架；未提交修改。");
                var baselineFrames = new HashSet<string>(CurrentSample.FrameLengthBaseline.Select(o => o.Id));
                var presentFrames = new HashSet<string>(EffectivePlan.Where(o => o.Kind == DesignObjectKind.Frame).Select(o => o.Id));
                if (baselineFrames.Except(presentFrames).Any() && geometry.Objects.Any(o =>
                    o.Kind == DesignObjectKind.Frame && affectedSupports.Contains(o.Id)))
                    return new BridgeEditResult(false, "铜排重规划需要重铺包含已删除基架的安装链，缺口和人工布置保留；未提交修改。");
                if (affectedSupports.Any(IsPositionLocked))
                    return new BridgeEditResult(false, "铜排调整需要重新铺排已锁定的基架或绝缘子，请先解锁；未提交修改。");
                // 未重铺的基架沿用当前安装顺序，断点规划器不能恢复换位前的位置号。
                var positions = EffectivePlan.Where(o => o.Kind == DesignObjectKind.Frame).ToDictionary(o => o.Id, o => o.FramePositionIndex);
                geometry = geometry.WithGeometry(geometry.Objects.Select(o => o.Kind == DesignObjectKind.Frame &&
                    !affectedSupports.Contains(o.Id) && positions.TryGetValue(o.Id, out var position)
                        ? o.WithFramePosition(position) : o), geometry.Routes);
                var editing = CaptureFrameLengthEditing();
                geometry = geometry.WithFrameLengthEditing(new BridgeFrameLengthEditing(
                    CurrentSample.FrameLengthBaseline.Where(o => !affectedSupports.Contains(o.Id))
                        .Concat(geometry.FrameLengthBaseline.Where(o => affectedSupports.Contains(o.Id))),
                    editing.IndependentFrames, editing.FixedEndFrames, editing.LengthAdjustedFrames, editing.OffsetCopperMinimumLengthMm))
                    .WithReadbackBaseline(geometry.ReadbackBaseline ?? CurrentSample.ReadbackBaseline);
                return new BridgeEditResult(true);
            }
            catch (InvalidOperationException ex) { return new BridgeEditResult(false, "断点调整未提交：" + ex.Message); }
            catch (ArgumentException ex) { return new BridgeEditResult(false, "断点调整未提交：" + ex.Message); }
        }

        private BridgeEditResult PlanCopperFrameSeams(BridgeDesignSample geometry, out BridgeDesignSample planned)
        {
            planned = geometry;
            var clearance = new BusbarBridgeFrameJointClearance();
            var changedSupports = ChangedSupportIds(geometry);
            var ids = new HashSet<string>(geometry.Objects.Select(o => o.Id));
            var effective = geometry.Objects.Where(o => !_state.Removed.Contains(o.Id) || changedSupports.Contains(o.Id))
                .Concat(_state.Added.Values.Where(o => !ids.Contains(o.Id)))
                .Select(o => changedSupports.Contains(o.Id) || o.IsJointGroup ? o : Effective(o)).ToArray();
            if (clearance.Validate(geometry, effective).Count == 0) return new BridgeEditResult(true);
            // 人工分节、固定端和锁定的子件保持实际安装位；父架长度锁只固定长度。
            var fixedFrames = new HashSet<string>(_state.Added.Keys.Concat(_state.LengthAdjustedFrames)
                .Concat(_state.IndependentFrames).Concat(_state.FixedEndFrames).Concat(EditedFrameTopologyIds()));
            fixedFrames.UnionWith(effective.Where(o => o.ParentId != null && IsPositionLocked(o.Id)).Select(o => o.ParentId));
            var context = geometry.WithGeometry(effective, geometry.Routes).WithSavedLocks(
                effective.Where(o => IsLocked(o.Id)).Select(o => o.Id));
            var result = clearance.Plan(context, fixedFrames);
            if (result.Issues.Count > 0)
            {
                var previousJoints = EffectivePlan.Where(o => o.IsJointGroup).ToDictionary(o => o.Id);
                var fixedJoints = new HashSet<string>(effective.Where(o => o.IsJointGroup &&
                    (_state.Overrides.ContainsKey(o.Id) || !previousJoints.TryGetValue(o.Id, out var previousJoint) ||
                        Math.Abs(o.X - previousJoint.X) > .000001)).Select(o => o.Id));
                Func<IReadOnlyList<BridgeDesignObject>, BridgeDesignSample> replan = context.ReplanCopper == null ? null :
                    new Func<IReadOnlyList<BridgeDesignObject>, BridgeDesignSample>(proposed =>
                    {
                        var next = context.ReplanCopper(proposed);
                        if (next?.IsSideToSide == true && BridgeSideToSideCopperEditor.PlanProblem(next) != null) return null;
                        var change = BridgeOffsetCopperChange.Create(CurrentSample.WithGeometry(EffectivePlan, CurrentSample.Routes), proposed);
                        if (change != null)
                        {
                            var validation = PlanOffsetCopperSupports(change, proposed, ref next);
                            if (!validation.Success) return null;
                        }
                        else next = next.WithGeometry(next.Objects.Where(o => o.Kind != DesignObjectKind.Frame && o.Kind != DesignObjectKind.Insulator)
                            .Concat(effective.Where(o => o.Kind == DesignObjectKind.Frame || o.Kind == DesignObjectKind.Insulator)), next.Routes);
                        return next;
                    });
                var fallback = clearance.PlanWithCopper(context, fixedFrames, fixedJoints, replan, false);
                if (clearance.Validate(fallback, fallback.Objects).Count == 0)
                {
                    planned = fallback;
                    return new BridgeEditResult(true);
                }
            }
            if (result.Issues.Count > 0) return new BridgeEditResult(false, "基架接缝规划无可行方案：" + result.Issues[0].Message);
            var previous = effective.Where(o => o.Kind == DesignObjectKind.Frame).ToDictionary(o => o.Id);
            var moved = new HashSet<string>(result.Objects.Where(o => o.Kind == DesignObjectKind.Frame &&
                new[] { DesignField.X, DesignField.Y, DesignField.Length, DesignField.Width }.Any(f => Math.Abs(o.Value(f) - previous[o.Id].Value(f)) > .000001))
                .Select(o => o.Id));
            var replacements = result.Objects.Where(o => moved.Contains(o.Id) || o.ParentId != null && moved.Contains(o.ParentId))
                .ToDictionary(o => o.Id);
            planned = geometry.WithGeometry(geometry.Objects.Select(o => replacements.TryGetValue(o.Id, out var replacement) ? replacement : o), geometry.Routes);
            return new BridgeEditResult(true);
        }

        private HashSet<string> ChangedSupportIds(BridgeDesignSample geometry)
        {
            var previous = CurrentSample.Objects.Where(o => o.Kind == DesignObjectKind.Frame).ToDictionary(o => o.Id);
            var next = geometry.Objects.Where(o => o.Kind == DesignObjectKind.Frame).ToDictionary(o => o.Id);
            var fields = new[] { DesignField.X, DesignField.Y, DesignField.Z, DesignField.Length,
                DesignField.Width, DesignField.Height, DesignField.Direction, DesignField.Installation };
            var frames = new HashSet<string>(previous.Keys.Union(next.Keys).Where(id =>
                !previous.ContainsKey(id) || !next.ContainsKey(id) ||
                previous[id].FrameTemplateKey != next[id].FrameTemplateKey ||
                fields.Any(field => Math.Abs(previous[id].Value(field) - next[id].Value(field)) >= .01d)));
            var ids = new HashSet<string>(frames);
            foreach (var child in BaseObjects().Concat(geometry.Objects).Where(o =>
                o.ParentId != null && frames.Contains(o.ParentId))) ids.Add(child.Id);
            return ids;
        }

        private void ApplyReplannedGeometry(BridgeDesignSample geometry, bool preserveFrameEditing = false)
        {
            var currentJoints = EffectivePlan.Where(o => o.IsJointGroup).ToDictionary(o => o.Id);
            foreach (var joint in geometry.Objects.Where(o => o.IsJointGroup && currentJoints.TryGetValue(o.Id, out var current) &&
                Math.Abs(o.X - current.X) > .000001)) _state.Overrides.Remove(joint.Id);
            preserveFrameEditing |= BridgeOffsetCopperChange.SupportsMiddleEditing(CurrentSample) &&
                CurrentSample.FrameLengthBaseline.Any(BridgeOffsetCopperChange.IsMember);
            // 顺序ID可能在重新分节后指向另一节；旧覆盖值不能盖回新规划位置。
            // 未改变的基架保留人工设置，受影响对象的原状态已由BeginEdit纳入撤销。
            foreach (var id in ChangedSupportIds(geometry))
            {
                _state.Overrides.Remove(id);
                _state.Added.Remove(id);
                _state.Removed.Remove(id);
                _state.AutomaticallySkipped.Remove(id);
                if (!preserveFrameEditing) ReleaseManualInsulatorPosition(id);
                if (!preserveFrameEditing) _state.SourceNames.Remove(id);
                _state.AutomaticallyPositionedFrames.Remove(id);
                if (!preserveFrameEditing) _state.LengthAdjustedFrames.Remove(id);
            }
            _state.CopperGeometry = geometry;
        }

        private bool IsSafeFrameLength(BridgeDesignObject frame, double length)
        {
            if (frame.CornerPreview != null)
            {
                var planned = Recommended(frame.Id);
                return planned != null && length >= 300d &&
                    length + FrameConnectionToleranceMm >= (planned.CornerPreview?.MinimumSafeLengthMm ?? planned.Length);
            }
            if (BridgeOffsetCopperChange.IsWidenedTerminal(frame)) return length >= 200d - .001d && length <= 1400d + .001d;
            if (LargeOffsetConnector(frame)) return length >= 200 - .000001 && length <= 1400 + .000001;
            if ((frame.FrameTemplateKey == "Regular" && !frame.Role.Contains("大偏移")) ||
                frame.FrameTemplateKey == "SideToSidePhaseChange029") return true;
            var baseline = Recommended(frame.Id);
            if (baseline != null) baseline = WallSafetyBaseline(baseline);
            return baseline != null && length >= baseline.Length - .01;
        }

        internal static BridgeDesignObject[] FramesInInstallationOrder(IEnumerable<BridgeDesignObject> source)
        {
            var frames = source.ToArray();
            var bCorner = frames.FirstOrDefault(o => o.CornerPreview?.ClosedEnd > 0);
            var transverseOrder = bCorner == null ? 1 : -bCorner.CornerPreview.ClosedSide;
            var numbered = frames.All(o => o.FramePositionIndex > 0) &&
                frames.Select(o => o.FramePositionIndex).Distinct().Count() == frames.Length;
            return frames.OrderBy(o => numbered ? o.FramePositionIndex : o.X)
                .ThenBy(o => transverseOrder * o.Y).ThenBy(o => o.Id, StringComparer.Ordinal).ToArray();
        }

        private BridgeDesignObject[] OrderedFrames() => EffectivePlan.Where(o => o.Kind == DesignObjectKind.Frame)
            .OrderBy(o => o.FramePositionIndex).ToArray();

        private static bool IsSpecialFrame(BridgeDesignObject frame) => frame.Kind == DesignObjectKind.Frame &&
            frame.FrameTemplateKey != "SideToSidePhaseChange029" && !BridgeOffsetCopperChange.IsWidenedTerminal(frame) &&
            (frame.FrameTemplateKey != "Regular" || frame.Role.Contains("大偏移"));

        private static double FrameAlong(BridgeDesignObject frame, BridgeDesignObject origin)
        {
            var radians = origin.Direction * Math.PI / 180;
            return (frame.X - origin.X) * Math.Cos(radians) + (frame.Y - origin.Y) * Math.Sin(radians);
        }

        private bool SameFrameLine(BridgeDesignObject first, BridgeDesignObject second)
        {
            var wall = CurrentSample?.ScenarioKey == "FaceToWall";
            var offsetChain = CurrentSample?.Objects.Any(o => o.Kind == DesignObjectKind.Frame && o.FrameTemplateKey == "SmallOffset") == true;
            if (!wall && !offsetChain) return SameStraightFrameLine(first, second);
            if (first.Id == second.Id) return SameStraightFrameLine(first, second);
            // 安装链来自保存的安装顺序和模板接口，不以壳体中心相同作为连接条件。
            var frames = CurrentSample.FrameLengthBaseline.Select(WallSafetyBaseline).ToList();
            var firstBaseline = frames.FirstOrDefault(o => o.Id == first.Id);
            var secondBaseline = frames.FirstOrDefault(o => o.Id == second.Id);
            if ((firstBaseline != null && !SameStraightFrameLine(first, firstBaseline)) ||
                (secondBaseline != null && !SameStraightFrameLine(second, secondBaseline))) return false;
            var firstIds = FrameChainAnchors(first, frames);
            var secondIds = FrameChainAnchors(second, frames);
            frames = frames.OrderBy(o => o.FramePositionIndex).ThenBy(o => o.X).ToList();
            var visited = new HashSet<string>(firstIds);
            for (var changed = true; changed;)
            {
                changed = false;
                for (var i = 1; i < frames.Count; i++)
                {
                    var left = frames[i - 1]; var right = frames[i];
                    var ordered = left.FramePositionIndex > 0 && right.FramePositionIndex == left.FramePositionIndex + 1;
                    var touching = Math.Abs(FrameAlong(right, left) - (left.Length + right.Length) / 2) <= FrameConnectionToleranceMm;
                    var connected = wall ? (ordered || touching) && WallInterfacesAligned(left, right) : OffsetInterfacesConnected(left, right);
                    if (!connected) continue;
                    if (visited.Contains(left.Id)) changed |= visited.Add(right.Id);
                    if (visited.Contains(right.Id)) changed |= visited.Add(left.Id);
                }
            }
            return secondIds.Any(visited.Contains);
        }

        private string[] FrameChainAnchors(BridgeDesignObject frame, IReadOnlyList<BridgeDesignObject> baseline)
        {
            if (baseline.Any(o => o.Id == frame.Id)) return new[] { frame.Id };
            // 补入缺口的新实例没有原始ID，按相同模板及实际占用区间归入原安装位置。
            return baseline.Where(o => o.FrameTemplateKey == frame.FrameTemplateKey && SameStraightFrameLine(o, frame) &&
                Math.Abs(o.Width - frame.Width) <= FrameConnectionToleranceMm && Math.Abs(o.Height - frame.Height) <= FrameConnectionToleranceMm &&
                Math.Min(o.Length / 2, FrameAlong(frame, o) + frame.Length / 2) -
                Math.Max(-o.Length / 2, FrameAlong(frame, o) - frame.Length / 2) > FrameConnectionToleranceMm)
                .Select(o => o.Id).ToArray();
        }

        private bool WallInterfacesAligned(BridgeDesignObject left, BridgeDesignObject right)
        {
            if (left.WallFramePreview == null && right.WallFramePreview == null)
                return !IsSpecialFrame(left) && !IsSpecialFrame(right) && SameStraightFrameLine(left, right) &&
                    Math.Abs(left.Width - right.Width) <= FrameConnectionToleranceMm &&
                    Math.Abs(left.Height - right.Height) <= FrameConnectionToleranceMm;
            var radians = left.Direction * Math.PI / 180;
            if (Math.Abs(Math.Sin((left.Direction - right.Direction) * Math.PI / 180)) >= .000001 ||
                Math.Cos((left.Direction - right.Direction) * Math.PI / 180) <= 0) return false;
            var inlet = right.WallFramePreview;
            var across = -(right.X - left.X) * Math.Sin(radians) + (right.Y - left.Y) * Math.Cos(radians);
            var inletHeight = inlet?.InletHeight ?? right.Height;
            var inletWidth = inlet?.InletWidth ?? right.Width;
            var inletZ = right.Z - right.Height / 2 + inletHeight / 2;
            return Math.Abs(across + (inlet?.InletOffset ?? 0)) <= FrameConnectionToleranceMm &&
                Math.Abs(inletZ - left.Z) <= FrameConnectionToleranceMm &&
                Math.Abs(inletHeight - left.Height) <= FrameConnectionToleranceMm &&
                Math.Abs(inletWidth - left.Width) <= FrameConnectionToleranceMm;
        }

        private static bool SameStraightFrameLine(BridgeDesignObject first, BridgeDesignObject second)
        {
            var radians = first.Direction * Math.PI / 180;
            return Math.Abs(Math.Sin((first.Direction - second.Direction) * Math.PI / 180)) < .000001 &&
                Math.Cos((first.Direction - second.Direction) * Math.PI / 180) > 0 && Math.Abs(first.Z - second.Z) <= FrameConnectionToleranceMm &&
                Math.Abs(-(second.X - first.X) * Math.Sin(radians) + (second.Y - first.Y) * Math.Cos(radians)) <= FrameConnectionToleranceMm;
        }

        private bool OffsetInterfacesConnected(BridgeDesignObject left, BridgeDesignObject right)
        {
            // 小偏移箱体中心不同；连接必须使用与正式生成、模型反读一致的进出孔接口。
            if ((IsSpecialFrame(left) && left.SmallOffsetPreview == null) ||
                (IsSpecialFrame(right) && right.SmallOffsetPreview == null)) return false;
            if (!FramePortSectionsMatch(left, right)) return false;
            var outlet = _geometry.FacingFrameInterfacePoints(left)[1];
            var inlet = _geometry.FacingFrameInterfacePoints(right)[0];
            return Enumerable.Range(0, 3).All(i => Math.Abs(outlet[i] - inlet[i]) <= FrameConnectionToleranceMm);
        }

        private bool FramesConnected(BridgeDesignObject first, BridgeDesignObject second) => FramePortsTouch(first, second);

        private double FramePortWidth(BridgeDesignObject frame, int port)
        {
            if (port == 0 && frame.WallFramePreview != null) return frame.WallFramePreview.InletWidth;
            if (frame.SmallOffsetPreview != null)
                return frame.DriveValue(DesignField.Width) - 2 * Math.Abs(frame.SmallOffsetPreview.InstalledPortOffset(port, frame.IsReversed));
            return LargeOffsetPortSpan(frame, port);
        }

        private bool FramePortSectionsMatch(BridgeDesignObject left, BridgeDesignObject right)
        {
            if (new[] { left, right }.Any(frame =>
                frame.FrameTemplateKey == "SmallOffset" && frame.SmallOffsetPreview == null ||
                frame.FrameTemplateKey == "LargeOffsetCorner" && frame.CornerPreview == null ||
                (frame.FrameTemplateKey == "FaceToWallLarge1" || frame.FrameTemplateKey == "FaceToWallLarge2") && frame.WallFramePreview == null)) return false;
            var leftWidth = FramePortWidth(left, 1); var rightWidth = FramePortWidth(right, 0);
            var leftHeight = left.DriveValue(DesignField.Height);
            var rightHeight = right.WallFramePreview?.InletHeight ?? right.DriveValue(DesignField.Height);
            var a = LargeOffsetPortNormal(left, 1); var b = LargeOffsetPortNormal(right, 0);
            return leftWidth > 0 && rightWidth > 0 && leftHeight > 0 && rightHeight > 0 &&
                Math.Abs(leftWidth - rightWidth) <= FrameConnectionToleranceMm && Math.Abs(leftHeight - rightHeight) <= FrameConnectionToleranceMm &&
                Enumerable.Range(0, 2).All(axis => Math.Abs(a[axis] + b[axis]) < .000001);
        }

        // 端部身份来自已规划的固定接口；删除后的当前列表首末项不能替代原固定接口。
        private bool FrameAtPlannedBoundary(BridgeDesignObject frame, int port)
        {
            if (frame?.Kind != DesignObjectKind.Frame) return false;
            var boundary = PlannedFrameBoundary(port);
            if (boundary == null || new[] { boundary, frame }.Any(o => o.FrameTemplateKey == "SmallOffset" && o.SmallOffsetPreview == null)) return false;
            var expected = _geometry.FrameInterfacePoints(boundary)[port]; var actual = _geometry.FrameInterfacePoints(frame)[port];
            var a = LargeOffsetPortNormal(boundary, port); var b = LargeOffsetPortNormal(frame, port);
            return Enumerable.Range(0, 3).All(axis => Math.Abs(expected[axis] - actual[axis]) <= FrameConnectionToleranceMm) &&
                Enumerable.Range(0, 2).All(axis => Math.Abs(a[axis] - b[axis]) < .000001);
        }

        internal bool HasFixedFrameLengthAnchor(string id) => FrameAtPlannedBoundary(Find(id), 0) || FrameAtPlannedBoundary(Find(id), 1);

        private BridgeDesignObject PlannedFrameBoundary(int port)
        {
            var baseline = FramesInInstallationOrder(CurrentSample.FrameLengthBaseline.Select(WallSafetyBaseline));
            return port == 0 ? baseline.FirstOrDefault() : baseline.LastOrDefault();
        }

        private bool TryGetPlannedBoundaryGap(BridgeDesignObject frame, int port, out double gap)
        {
            gap = 0; var boundary = PlannedFrameBoundary(port);
            if (boundary == null || new[] { frame, boundary }.Any(o => o.CornerPreview != null ||
                o.FrameTemplateKey == "SmallOffset" && o.SmallOffsetPreview == null)) return false;
            var a = LargeOffsetPortNormal(frame, port); var b = LargeOffsetPortNormal(boundary, port);
            var width = FramePortWidth(frame, port); var boundaryWidth = FramePortWidth(boundary, port);
            var height = port == 0 ? frame.WallFramePreview?.InletHeight ?? frame.DriveValue(DesignField.Height) : frame.DriveValue(DesignField.Height);
            var boundaryHeight = port == 0 ? boundary.WallFramePreview?.InletHeight ?? boundary.DriveValue(DesignField.Height) : boundary.DriveValue(DesignField.Height);
            if (!(width > 0 && boundaryWidth > 0 && height > 0 && boundaryHeight > 0) ||
                !(Math.Abs(width - boundaryWidth) <= FrameConnectionToleranceMm) || !(Math.Abs(height - boundaryHeight) <= FrameConnectionToleranceMm) ||
                Enumerable.Range(0, 2).Any(axis => Math.Abs(a[axis] - b[axis]) >= .000001)) return false;
            var endpoint = _geometry.FrameInterfacePoints(boundary)[port]; var point = _geometry.FrameInterfacePoints(frame)[port];
            var sign = port == 0 ? 1d : -1d;
            var x = sign * (point[0] - endpoint[0]); var y = sign * (point[1] - endpoint[1]);
            var angle = frame.DriveValue(DesignField.Direction) * Math.PI / 180;
            gap = x * Math.Cos(angle) + y * Math.Sin(angle);
            return gap > FrameConnectionToleranceMm && Math.Abs(-x * Math.Sin(angle) + y * Math.Cos(angle)) <= FrameConnectionToleranceMm &&
                Math.Abs(point[2] - endpoint[2]) <= FrameConnectionToleranceMm;
        }

        private sealed class FrameGapAnchor
        {
            public bool FixedEnd;
            public double Available;
            public bool RequiresChoice;
        }

        private bool TryGetFrameGap(BridgeDesignObject left, BridgeDesignObject right, out double gap)
        {
            gap = 0;
            if (!FramePortSectionsMatch(left, right) || left.CornerPreview != null || right.CornerPreview != null ||
                left.FrameTemplateKey == "SmallOffset" && left.SmallOffsetPreview == null ||
                right.FrameTemplateKey == "SmallOffset" && right.SmallOffsetPreview == null) return false;
            var angle = left.Direction * Math.PI / 180;
            var difference = (right.Direction - left.Direction) * Math.PI / 180;
            if (Math.Abs(Math.Sin(difference)) >= .000001 || Math.Cos(difference) <= 0) return false;
            var outlet = _geometry.FrameInterfacePoints(left)[1]; var inlet = _geometry.FrameInterfacePoints(right)[0];
            var x = inlet[0] - outlet[0]; var y = inlet[1] - outlet[1];
            gap = x * Math.Cos(angle) + y * Math.Sin(angle);
            return gap > FrameConnectionToleranceMm &&
                Math.Abs(-x * Math.Sin(angle) + y * Math.Cos(angle)) <= FrameConnectionToleranceMm &&
                Math.Abs(inlet[2] - outlet[2]) <= FrameConnectionToleranceMm;
        }

        private FrameGapAnchor FindFrameGapAnchor(IReadOnlyList<BridgeDesignObject> frames, int index)
        {
            var before = 0d; var after = 0d;
            var hasBefore = index > 0 ? TryGetFrameGap(frames[index - 1], frames[index], out before) : TryGetPlannedBoundaryGap(frames[index], 0, out before);
            var hasAfter = index + 1 < frames.Count ? TryGetFrameGap(frames[index], frames[index + 1], out after) : TryGetPlannedBoundaryGap(frames[index], 1, out after);
            if (!hasBefore && !hasAfter) return null;
            if (hasBefore && hasAfter && !IsIndependentFrame(frames[index].Id))
                return new FrameGapAnchor { RequiresChoice = true };
            var fixedEnd = hasBefore && (!hasAfter || _state.FixedEndFrames.Contains(frames[index].Id));
            return new FrameGapAnchor { FixedEnd = fixedEnd, Available = fixedEnd ? before : after };
        }

        private bool FramePortsTouch(BridgeDesignObject left, BridgeDesignObject right)
        {
            if (!FramePortSectionsMatch(left, right)) return false;
            var outlet = _geometry.FrameInterfacePoints(left)[1]; var inlet = _geometry.FrameInterfacePoints(right)[0];
            return Enumerable.Range(0, 3).All(axis => Math.Abs(outlet[axis] - inlet[axis]) <= FrameConnectionToleranceMm);
        }

        internal string FrameGapHint(string id)
        {
            if (IsIndependentFrame(id)) return null;
            var frames = OrderedFrames(); var index = Array.FindIndex(frames, f => f.Id == id);
            var gap = index < 0 ? null : FindFrameGapAnchor(frames, index);
            if (gap == null) return null;
            if (gap.RequiresChoice) return "两端均有缺口，请先选择独立更改及固定端。";
            return (gap.FixedEnd ? "固定出口，优先补齐入口侧缺口" : "固定入口，优先补齐出口侧缺口") +
                "（" + gap.Available.ToString("0.0", CultureInfo.InvariantCulture) + " mm）；其他基架不联动。";
        }

        private static BridgeDesignObject FramePose(BridgeDesignObject frame, double length, double shift)
        {
            var radians = frame.Direction * Math.PI / 180;
            var values = frame.Values.ToDictionary(p => p.Key, p => p.Value);
            values[DesignField.Length] = length;
            values[DesignField.X] = frame.X + shift * Math.Cos(radians);
            values[DesignField.Y] = frame.Y + shift * Math.Sin(radians);
            return frame.WithValues(values);
        }

        private BridgeDesignObject WallParameterOwner() => CurrentSample?.ScenarioKey == "FaceToWall"
            ? CurrentSample.Objects.SingleOrDefault(o => o.FrameTemplateKey == "FaceToWallLarge1") : null;

        private BridgeDesignObject WallSafetyBaseline(BridgeDesignObject frame)
        {
            var owner = WallParameterOwner();
            if (owner == null || frame.Kind != DesignObjectKind.Frame) return frame;
            var current = Effective(owner);
            var vertical = current.Value(DesignField.WallVerticalClearance) - owner.Value(DesignField.WallVerticalClearance);
            var values = frame.Values.ToDictionary(p => p.Key, p => p.Value);
            values[DesignField.Height] = frame.Height - vertical * 2;
            if (frame.Id == owner.Id)
            {
                var entry = current.Value(DesignField.WallEntryDistance) - owner.Value(DesignField.WallEntryDistance);
                values[DesignField.X] = frame.X + entry / 2;
                values[DesignField.Length] = frame.Length + entry;
                values[DesignField.Width] = frame.Width - 2 * (current.Value(DesignField.WallHorizontalClearance) - owner.Value(DesignField.WallHorizontalClearance));
            }
            var adjusted = frame.WithValues(values);
            return frame.WallFramePreview == null || Math.Abs(vertical) < .000001 ? adjusted :
                adjusted.WithWallFramePreview(new BridgeWallFramePreview(frame.WallFramePreview.InletWidth,
                    frame.WallFramePreview.InletHeight - 2 * vertical, frame.WallFramePreview.InletOffset));
        }

        private BridgeDesignObject[] ApplyWallPreviewParameters(BridgeDesignObject[] objects)
        {
            var owner = WallParameterOwner();
            if (owner == null) return objects;
            var vertical = Effective(owner).Value(DesignField.WallVerticalClearance) - owner.Value(DesignField.WallVerticalClearance);
            if (Math.Abs(vertical) < .000001) return objects;
            return objects.Select(node => {
                if (node.Kind == DesignObjectKind.Frame && node.WallFramePreview != null)
                    return node.WithWallFramePreview(new BridgeWallFramePreview(node.WallFramePreview.InletWidth,
                        node.WallFramePreview.InletHeight - 2 * vertical, node.WallFramePreview.InletOffset));
                if (node.Id != "wall-base" || node.Kind != DesignObjectKind.Base) return node;
                var values = node.Values.ToDictionary(p => p.Key, p => p.Value);
                values[DesignField.Height] = node.Height - vertical;
                values[DesignField.Z] = node.Z - vertical / 2;
                return node.WithValues(values);
            }).ToArray();
        }

        internal BridgeEditResult CommitWallVerticalClearance(string id, double value, BridgeDesignSample copper)
        {
            if (copper == null || copper.ScenarioKey != "FaceToWall")
                return new BridgeEditResult(false, "上下墙距修改缺少重新规划的实际铜排，修改未提交。");
            if (copper.Issues.Any(issue => issue.BlocksGeneration))
                return new BridgeEditResult(false, "铜排重规划未通过校验，修改未提交：" + copper.Issues.First(issue => issue.BlocksGeneration).Message);
            return SetWallParameter(Find(id), DesignField.WallVerticalClearance, value, copper);
        }

        internal BridgeEditResult CheckWallVerticalClearance(string id, double value) =>
            SetWallParameter(Find(id), DesignField.WallVerticalClearance, value, null, true);

        private BridgeEditResult SetWallParameter(BridgeDesignObject node, DesignField field, double value,
            BridgeDesignSample copper = null, bool validateOnly = false)
        {
            if (node?.Values.ContainsKey(DesignField.WallTopClearance) == true)
                return new BridgeEditResult(false, "墙端安装参数已前置，请在柜体驱动的墙孔参数页修改后重新规划。");
            var owner = WallParameterOwner();
            if (owner == null || node?.Id != owner.Id || OrderedFrames().LastOrDefault()?.Id != node.Id)
                return new BridgeEditResult(false, "墙端参数仅适用于面对墙末端大基架1。");
            if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 1000000)
                return new BridgeEditResult(false, "墙端参数必须为有效有限数值。");
            if (IsLocked(node.Id) && field == DesignField.WallEntryDistance)
                return new BridgeEditResult(false, "末端大基架长度已锁定，入墙距离会改变长度，请先解锁。");
            value = BridgeDesignPrecision.OneDecimal(value);
            var delta = value - node.Value(field);
            if (Math.Abs(delta) < .01) return new BridgeEditResult(true);
            if (!validateOnly && field == DesignField.WallVerticalClearance && CurrentSample.Routes.Any(r => r.IsPhysicalGeometry) && copper == null)
                return new BridgeEditResult(false, "上下墙距需重新规划铜排后一起提交，修改未提交。");
            if (field == DesignField.WallVerticalClearance && EffectivePlan.Any(o => o.JointGroupId != null && IsLocked(o.Id)))
                return new BridgeEditResult(false, "上下墙距会重新规划铜排，存在锁定断点，修改未提交。");
            var frames = OrderedFrames();
            if (field == DesignField.WallVerticalClearance &&
                EffectivePlan.Any(o => o.Kind == DesignObjectKind.Insulator && frames.Any(f => f.Id == o.ParentId) && IsLocked(o.Id)))
                return new BridgeEditResult(false, "上下墙距会联动整条基架链，存在锁定基架，修改未提交。");
            if (field == DesignField.WallVerticalClearance && frames.Any(f => f.Height - 2 * delta < 80 ||
                    f.WallFramePreview == null || f.WallFramePreview.InletHeight - 2 * delta <= 0))
                return new BridgeEditResult(false, "上下墙距导致基架或入口高度无效，修改未提交。");
            if (field == DesignField.WallHorizontalClearance && (node.WallFramePreview == null ||
                    node.Width - 2 * delta < node.WallFramePreview.InletWidth + 2 * Math.Abs(node.WallFramePreview.InletOffset) + 4) ||
                field == DesignField.WallEntryDistance && node.Length + delta < 300 - .01)
                return new BridgeEditResult(false, "墙端参数导致大基架尺寸低于制造要求，修改未提交。");
            var baseNode = EffectivePlan.SingleOrDefault(o => o.Id == "wall-base" && o.Kind == DesignObjectKind.Base);
            if (field == DesignField.WallVerticalClearance && (baseNode == null || baseNode.Height - delta <= 0))
                return new BridgeEditResult(false, "缺少有效普通基座或补偿后基座高度无效，修改未提交。");
            if (validateOnly) return new BridgeEditResult(true);
            return CommitFrameChange(() => {
                if (copper != null)
                {
                    // 新铜排拓扑不能沿用旧断点坐标覆盖；基架基线和人工支撑设置继续保留。
                    var points = new HashSet<string>(CurrentSample.CopperEditRoutes.SelectMany(r => r.PointIds));
                    foreach (var id in points)
                    { _state.Overrides.Remove(id); _state.Added.Remove(id); _state.Removed.Remove(id); }
                    _state.CopperGeometry = copper;
                    _state.TopologyBaseline = copper;
                    _state.CopperTopologyChanged = true;
                    InvalidateSnapshot();
                }
                Put(node.Id, field, value);
                if (field == DesignField.WallEntryDistance)
                {
                    var after = frames.Select(f => f.Id == node.Id ? FramePose(f, f.Length + delta, delta / 2) : f).ToArray();
                    ApplyFrameLayout(frames, after);
                }
                else if (field == DesignField.WallHorizontalClearance) Put(node.Id, DesignField.Width, node.Width - 2 * delta);
                else
                {
                    var children = EffectivePlan.Where(o => o.Kind == DesignObjectKind.Insulator && frames.Any(f => f.Id == o.ParentId)).ToArray();
                    foreach (var frame in frames) Put(frame.Id, DesignField.Height, frame.Height - 2 * delta);
                    foreach (var child in children) Put(child.Id, DesignField.Z, child.Z + delta);
                }
                MakeIndependentForEdit(node);
            }, copper != null);
        }

        private BridgeEditResult ValidateFrameLayout(IReadOnlyList<BridgeDesignObject> frames)
        {
            var targets = CurrentSample.FrameLengthBaseline.Select(WallSafetyBaseline).ToArray();
            foreach (var frame in frames)
            {
                if (frame.FrameTemplateKey == "LargeOffsetCorner" && frame.CornerPreview == null)
                    return LargeOffsetFailure("拐角缺少模板接口参数，请重读并规划。");
                // 大偏移连接架仍是普通模板；其Y向安全区间另行保持，制造下限不能套用拐角的300mm。
                var minimum = LargeOffsetConnector(frame) ? 200d : IsSpecialFrame(frame) ? 300d : 200d;
                if (frame.Length < minimum - .01)
                    return new BridgeEditResult(false, frame.DisplayName + "短于模板最低制造长度" + minimum + " mm，修改未提交。");
                var baseline = targets.FirstOrDefault(o => o.Id == frame.Id);
                // 大偏移成员的安装链及25.0 mm安全区间由整组校验负责。
                if (frame.CornerPreview != null || LargeOffsetConnector(frame)) continue;
                if (IsSpecialFrame(frame))
                {
                    if (baseline != null && BridgeOffsetCopperChange.IsMember(frame))
                    {
                        var values = baseline.Values.ToDictionary(p => p.Key, p => p.Value);
                        values[DesignField.X] = baseline.X + CurrentSample.OffsetCopperSafetyShift;
                        baseline = baseline.WithValues(values);
                    }
                    // 实物安装链按0.1mm核验；驱动长度取整产生的亚毫米端点差不能在此被重复判为越界。
                    if (baseline == null || !SameFrameLine(baseline, frame) ||
                        FrameAlong(frame, baseline) - frame.Length / 2 > -baseline.Length / 2 + FrameConnectionToleranceMm ||
                        FrameAlong(frame, baseline) + frame.Length / 2 < baseline.Length / 2 - FrameConnectionToleranceMm)
                        return new BridgeEditResult(false, frame.DisplayName + "未完整覆盖初始规划安全范围，修改未提交。");
                }
                var line = targets.Where(t => SameFrameLine(frame, t)).ToArray();
                if (line.Length == 0) return new BridgeEditResult(false, frame.DisplayName + "不在已规划的安装链上，修改未提交。");
                var start = line.Min(t => FrameAlong(t, frame) - t.Length / 2);
                var end = line.Max(t => FrameAlong(t, frame) + t.Length / 2);
                if (-frame.Length / 2 < start - FrameConnectionToleranceMm || frame.Length / 2 > end + FrameConnectionToleranceMm)
                    return new BridgeEditResult(false, frame.DisplayName + (CurrentSample.ScenarioKey == "FaceToWall"
                        ? "超出柜端起点或墙端目标，修改未提交。" : "超出B侧起点或目标末端，修改未提交。"));
            }
            for (var i = 0; i < frames.Count; i++)
            for (var j = i + 1; j < frames.Count; j++)
            {
                var left = frames[i]; var right = frames[j];
                if (!SameFrameLine(left, right)) continue;
                var distance = Math.Abs(FrameAlong(right, left));
                var overlap = (left.Length + right.Length) / 2 - distance;
                if (overlap > FrameConnectionToleranceMm)
                    return new BridgeEditResult(false, left.DisplayName + "与" + right.DisplayName + "重叠" +
                        overlap.ToString("0.0", CultureInfo.InvariantCulture) + " mm，修改未提交。");
            }
            return new BridgeEditResult(true);
        }

        // 基架操作先在状态副本上计算、避让和校验，成功后才发布事件和加入历史。
        private BridgeEditResult CommitFrameChange(Action apply, bool requireValidSupports = false, bool allowUnchangedFrameAvoidance = false)
        {
            var previous = _state; var previousIssues = ClearanceIssues;
            var previousObjects = EffectivePlan.ToDictionary(o => o.Id);
            var previousMoved = LastAutoMovedFrameIds; var previousLengths = LastLengthAdjustedFrameIds;
            _state = previous.Copy(); _editing = true; InvalidateSnapshot();
            var committed = false;
            try
            {
                apply();
                var lockValidation = ValidateLengthLocks(previousObjects.Values.ToArray(), EffectivePlan);
                if (!lockValidation.Success) return lockValidation;
                var groupValidation = ValidateLargeOffsetGroups(OrderedFrames());
                if (!groupValidation.Success) return groupValidation;
                var validation = ValidateFrameLayout(OrderedFrames());
                if (!validation.Success) return validation;
                var stagedObjects = EffectivePlan.ToArray();
                var changedFrames = new HashSet<string>(stagedObjects.Where(o => o.Kind == DesignObjectKind.Frame &&
                    (!previousObjects.TryGetValue(o.Id, out var old) || o.Values.Any(p => Math.Abs(old.Value(p.Key) - p.Value) >= .01) ||
                    stagedObjects.Where(child => child.ParentId == o.Id).Any(child =>
                        !previousObjects.TryGetValue(child.Id, out var original) || child.Values.Any(p => Math.Abs(original.Value(p.Key) - p.Value) >= .01)) ||
                    stagedObjects.Count(child => child.ParentId == o.Id) != previousObjects.Values.Count(child => child.ParentId == o.Id)))
                    .Select(o => o.Id));
                Notify(false, allowUnchangedFrameAvoidance ? null : changedFrames);
                lockValidation = ValidateLengthLocks(previousObjects.Values.ToArray(), EffectivePlan);
                if (!lockValidation.Success) return lockValidation;
                groupValidation = ValidateLargeOffsetGroups(OrderedFrames());
                if (!groupValidation.Success) return groupValidation;
                if (requireValidSupports && ClearanceIssues.Any(issue => issue.BlocksGeneration))
                    return new BridgeEditResult(false, "铜排重规划未提交：" + ClearanceIssues.First(issue => issue.BlocksGeneration).Message);
                var introduced = ClearanceIssues.FirstOrDefault(issue => issue.BlocksGeneration &&
                    !previousIssues.Any(old => old.BlocksGeneration && old.Source == issue.Source && old.TargetId == issue.TargetId && old.Message == issue.Message));
                if (introduced != null) return new BridgeEditResult(false, "基架调整未提交：" + introduced.Message);
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

        private void ApplyFrameLayout(IReadOnlyList<BridgeDesignObject> before, IReadOnlyList<BridgeDesignObject> after)
        {
            var children = EffectivePlan.Where(o => o.ParentId != null).ToArray();
            var changedLengths = new List<string>();
            foreach (var frame in after)
            {
                var original = before.First(o => o.Id == frame.Id);
                var delta = frame.Length - original.Length;
                if (Math.Abs(delta) > .000001)
                {
                    changedLengths.Add(frame.Id); _state.LengthAdjustedFrames.Add(frame.Id);
                    foreach (var child in children.Where(o => o.ParentId == frame.Id)) Put(child.Id, DesignField.X, child.X - delta / 2);
                }
                foreach (var field in new[] { DesignField.X, DesignField.Y, DesignField.Length })
                    if (Math.Abs(frame.Value(field) - original.Value(field)) > .000001) Put(frame.Id, field, frame.Value(field));
            }
            LastLengthAdjustedFrameIds = changedLengths.AsReadOnly();
        }

        private void AddFrameAt(BridgeDesignObject template, double center, double length)
        {
            var id = "frame-user-" + Guid.NewGuid().ToString("N");
            var frame = FramePose(template, length, center).WithSourceName(NextFrameSourceName());
            // 新实例复用明确模板信息，身份和编辑状态必须独立于模板实例。
            var values = frame.Values.ToDictionary(p => p.Key, p => p.Value);
            frame = new BridgeDesignObject(id, frame.Name, DesignObjectKind.Frame, values, template.EditableFields, template.Role)
                .WithFrameTemplate(template.FrameTemplateKey).WithSourceName(frame.SourceName).WithOffsetInsulators(template.UseOffsetInsulators);
            if (template.InsulatorPadThickness > 0)
                frame = frame.WithInsulatorStack(template.InsulatorPadCount, template.InsulatorPadThickness, template.InsulatorPhaseSpacing, template.InsulatorAssemblyHeight);
            InsertFrame(frame);
            ResizeInsulators(frame, (int)frame.Value(DesignField.InsulatorCount));
        }

        private void FillNewTailGaps(IReadOnlyList<BridgeDesignObject> before, IReadOnlyList<BridgeDesignObject> after)
        {
            if (before.Count == 0) return;
            var original = before[before.Count - 1]; var tail = after[after.Count - 1];
            var gap = FrameAlong(original, tail) + original.Length / 2 - tail.Length / 2;
            if (gap < 200 - .01 || IsSpecialFrame(tail)) return;
            var cursor = tail.Length / 2;
            while (gap >= 200 - .01)
            {
                var length = PlanNewFrameLength(gap);
                AddFrameAt(tail, cursor + length / 2, length);
                cursor += length; gap -= length;
            }
        }

        private BridgeEditResult SetFrameLength(BridgeDesignObject node, double length, bool sameSource = false, Action restoreModel = null)
        {
            if (node.FrameTemplateKey == "LargeOffsetCorner") return SetLargeOffsetLength(node, length, restoreModel);
            if (LargeOffsetConnector(node)) return SetLargeOffsetConnectorLength(node, length, restoreModel);
            length = BridgeDesignPrecision.OneDecimal(length);
            if (IsLocked(node.Id)) return new BridgeEditResult(false, "当前基架已锁定，请先解锁再修改长度。");
            var validation = Validate(node, DesignField.Length, length);
            if (!validation.Success || Math.Abs(length - node.Length) < .01) return validation;
            var frames = OrderedFrames(); var planned = frames.ToArray();
            var independent = IsIndependentFrame(node.Id);
            var targets = new HashSet<string>(frames.Where(f => f.Id == node.Id ||
                (!independent && sameSource && f.SharesFrameSourceWith(node) && !IsLocked(f.Id))).Select(f => f.Id));
            if (frames.Any(f => f.Id != node.Id && targets.Contains(f.Id) && IsIndependentFrame(f.Id)))
                return new BridgeEditResult(false, "同源范围含使用独立更改及固定端的基架，请分别修改长度；未提交修改。");
            if (!independent && sameSource && frames.Any(f => f.SharesFrameSourceWith(node) && LargeOffsetLengthBoundary(f)))
                return new BridgeEditResult(false, "同源范围包含大偏移组成员，请取消同源同步后分别修改；未提交修改。");
            foreach (var target in frames.Where(f => targets.Contains(f.Id)))
            {
                var result = Validate(target, DesignField.Length, length);
                if (!result.Success) return result;
            }
            var gaps = new Dictionary<string, FrameGapAnchor>();
            for (var i = 0; i < frames.Length; i++)
            {
                if (!targets.Contains(frames[i].Id)) continue;
                var gap = FindFrameGapAnchor(frames, i);
                if (gap == null) continue;
                if (gap.RequiresChoice) return new BridgeEditResult(false, "当前基架两端均有缺口，请先选择独立更改及固定端。");
                if (length - frames[i].Length > gap.Available + FrameConnectionToleranceMm)
                    return new BridgeEditResult(false, "加长量超过剩余缺口" + gap.Available.ToString("0.0", CultureInfo.InvariantCulture) +
                        " mm，请先按缺口长度补齐后再编辑，修改未提交。");
                gaps[frames[i].Id] = gap;
            }
            for (var first = 0; first < frames.Length;)
            {
                var last = first;
                // 拐角及连接架是固定边界；普通长度补偿不能跨入偏移组或到另一柜侧。
                while (last + 1 < frames.Length && !LargeOffsetLengthBoundary(frames[last]) &&
                    !LargeOffsetLengthBoundary(frames[last + 1]) && FramesConnected(frames[last], frames[last + 1])) last++;
                var changed = Enumerable.Range(first, last - first + 1).Where(i => targets.Contains(frames[i].Id)).ToArray();
                if (changed.Length > 0)
                {
                    foreach (var index in changed) planned[index] = FramePose(frames[index], length, 0);
                    var result = PlanConnectedFrameLengths(frames, planned, first, last, changed, targets, gaps, independent);
                    if (!result.Success) return result;
                }
                first = last + 1;
            }
            var lengthLocks = ValidateLengthLocks(frames, planned);
            if (!lengthLocks.Success) return lengthLocks;
            validation = ValidateFrameLayout(planned);
            if (!validation.Success) return validation;
            var resultCommit = CommitFrameChange(() =>
            {
                foreach (var frame in frames.Where(f => Math.Abs(planned.First(p => p.Id == f.Id).Length - f.Length) >= .01 &&
                    (!sameSource || !targets.Contains(f.Id)))) MakeIndependentForEdit(frame);
                ApplyFrameLayout(frames, planned);
                if (!independent && gaps.Count == 0) FillNewTailGaps(frames, planned);
                restoreModel?.Invoke();
            });
            return resultCommit;
        }

        private BridgeEditResult PlanConnectedFrameLengths(BridgeDesignObject[] frames, BridgeDesignObject[] planned,
            int first, int last, int[] changed, ISet<string> targets, IDictionary<string, FrameGapAnchor> gaps, bool independent)
        {
            if (independent)
            {
                var index = changed[0]; var delta = planned[index].Length - frames[index].Length;
                if (FrameAtPlannedBoundary(frames[index], 0) && FrameAtPlannedBoundary(frames[index], 1))
                    return new BridgeEditResult(false, "本节两端均连接固定接口，没有可调整跨度，修改未提交。");
                planned[index] = FramePose(frames[index], planned[index].Length, (IsFrameEndFixed(frames[index].Id) ? -1 : 1) * delta / 2);
                return new BridgeEditResult(true);
            }
            // 缺口边界的长度直接填补缺失覆盖；只有链内修改量才参与反向补偿。
            var compensated = changed.Where(i => !gaps.ContainsKey(frames[i].Id)).ToArray();
            if (compensated.Contains(first) && first > 0 && !FramePortsTouch(frames[first - 1], frames[first]) ||
                compensated.Contains(last) && last + 1 < frames.Length && !FramePortsTouch(frames[last], frames[last + 1]))
                return new BridgeEditResult(false, "相邻安装接口不兼容，不能按末端或链内基架补偿，请先修复贯通，修改未提交。");
            var remaining = compensated.Sum(i => planned[i].Length - frames[i].Length);
            var sameSide = IsLargeOffsetOrdinaryLengthChain(frames, first, last);
            var terminalChange = compensated.Contains(last);
            var candidates = terminalChange ? Enumerable.Range(first, last - first).Reverse() :
                compensated.Length == 0 ? Enumerable.Empty<int>() : Enumerable.Range(compensated.Min() + 1, last - compensated.Min());
            if (sameSide && !terminalChange && compensated.Length > 0)
                candidates = candidates.Concat(Enumerable.Range(first, compensated.Min() - first).Reverse());
            foreach (var i in candidates)
            {
                if (Math.Abs(remaining) < .01) break;
                if (targets.Contains(frames[i].Id) || IsLocked(frames[i].Id) || !frames[i].EditableFields.Contains(DesignField.Length)) continue;
                var minimum = 200d;
                if (IsSpecialFrame(frames[i]))
                {
                    var baseline = CurrentSample.FrameLengthBaseline.FirstOrDefault(o => o.Id == frames[i].Id);
                    if (baseline == null) return new BridgeEditResult(false, frames[i].DisplayName + "缺少原规划安全范围，不能补偿长度。");
                    minimum = Math.Max(300d, WallSafetyBaseline(baseline).Length);
                }
                var compensation = remaining > 0 ? Math.Min(remaining, Math.Max(0, frames[i].Length - minimum)) : remaining;
                planned[i] = FramePose(frames[i], frames[i].Length - compensation, 0); remaining -= compensation;
            }
            if (Math.Abs(remaining) >= .01)
                return new BridgeEditResult(false, sameSide ? "同侧普通基架无法在锁定和安全长度范围内完成补偿，修改未提交。" :
                    (terminalChange ? "前端" : "后端") + "基架无法在锁定和安全长度范围内完成补偿，修改未提交。");
            var accumulated = gaps.TryGetValue(frames[first].Id, out var startGap) && startGap.FixedEnd
                ? -(planned[first].Length - frames[first].Length) : 0d;
            for (var i = first; i <= last; i++)
            {
                var delta = planned[i].Length - frames[i].Length;
                planned[i] = FramePose(frames[i], planned[i].Length, accumulated + delta / 2); accumulated += delta;
                if (!gaps.TryGetValue(frames[i].Id, out var gap)) continue;
                var port = gap.FixedEnd ? 1 : 0;
                var original = _geometry.FrameInterfacePoints(frames[i])[port]; var current = _geometry.FrameInterfacePoints(planned[i])[port];
                if (Enumerable.Range(0, 3).Any(axis => Math.Abs(original[axis] - current[axis]) > FrameConnectionToleranceMm))
                    return new BridgeEditResult(false, "同源长度联动会移动缺口边界的固定接口，请分别调整，修改未提交。");
            }
            return new BridgeEditResult(true);
        }

        private void MakeIndependentForEdit(BridgeDesignObject node)
        {
            var frame = node.Kind == DesignObjectKind.Frame ? node : Find(node.ParentId);
            if (frame?.Kind != DesignObjectKind.Frame || !EffectivePlan.Any(o => o.Id != frame.Id && o.SharesFrameSourceWith(frame))) return;
            // 与本次修改共用历史快照；验证失败、无变化或仅切换勾选状态均不拆分。
            _state.SourceNames[frame.Id] = NextFrameSourceName();
        }

        private string NextFrameSourceName() => NextFrameSourceName(
            BaseObjects().Where(o => o.Kind == DesignObjectKind.Frame).Select(o => o.SourceName).Concat(_state.SourceNames.Values));

        internal static string NextFrameSourceName(IEnumerable<string> names)
        {
            var max = names.Select(name => name != null && name.StartsWith("基架", StringComparison.Ordinal) &&
                int.TryParse(name.Substring(2).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0).DefaultIfEmpty(0).Max();
            return "基架" + (max + 1).ToString(CultureInfo.InvariantCulture);
        }

        public BridgeEditResult SetFramesSameSource(IEnumerable<string> selectedIds)
        {
            var ids = (selectedIds ?? Array.Empty<string>()).Distinct().ToArray();
            var objects = EffectivePlan; var frames = ids.Select(id => objects.FirstOrDefault(o => o.Id == id)).ToArray();
            if (frames.Length < 2 || frames.Any(o => o?.Kind != DesignObjectKind.Frame))
                return new BridgeEditResult(false, "请按Ctrl选择至少两个有效基架，再设置同源。");
            if (frames.Any(o => string.IsNullOrWhiteSpace(o.SourceName)))
                return new BridgeEditResult(false, "基架缺少模型名称，不能设置同源。");
            var target = frames.OrderBy(o => SourceNameNumber(o.SourceName)).ThenBy(o => o.SourceName, StringComparer.OrdinalIgnoreCase).First();
            // 仅改变选中实例；目标名称已被其他位置使用时，也校验其实际模型参数。
            if (frames.Concat(objects.Where(o => o.SharesFrameSourceWith(target)))
                .Any(o => !_provider.SameFrameModel(target, o, objects)))
                return new BridgeEditResult(false, "仅模板、基架参数和内部绝缘子布置相同的基架才能设为同源；未修改任何基架。");
            if (frames.All(o => o.SharesFrameSourceWith(target))) return new BridgeEditResult(true);
            if (frames.Any(o => objects.Any(child => child.ParentId == o.Id && IsLocked(child.Id))))
                return new BridgeEditResult(false, "选中基架或内部绝缘子已锁定，请先解锁再设置同源。");
            BeginEdit();
            foreach (var frame in frames) _state.SourceNames[frame.Id] = target.SourceName;
            Notify(); return new BridgeEditResult(true);
        }

        private int SourceNameNumber(string name) => name.StartsWith("基架", StringComparison.Ordinal) &&
            int.TryParse(name.Substring(2).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : int.MaxValue;

        private bool TryGetBaseJointRange(string id, out BaseJointRange result)
        {
            result = null;
            var node = JointRepresentative(id);
            if (Sample == null || node?.Kind != DesignObjectKind.FixedJoint ||
                !node.EditableFields.Contains(DesignField.X)) return false;
            var recommended = Recommended(node.Id);
            if (Sample.Source.IsModelDerived)
            {
                // 来自已核验A/B映射的显式归属；模型方案禁止按距离或Box猜测基座。
                var constraint = CurrentSample.BaseJointConstraints.SingleOrDefault(c => c.JointGroupId == node.JointGroupId);
                var route = CurrentSample.CopperEditRoutes.FirstOrDefault(r => r.PointIds.Contains(node.Id));
                if (constraint == null || route == null ||
                    !Sample.Objects.Any(o => o.Id == constraint.BaseId && o.Kind == DesignObjectKind.Base)) return false;
                var position = route.PointIds.ToList().IndexOf(node.Id);
                if (position != 0 && position != route.PointIds.Count - 1) return false;
                var adjacent = Find(position == 0 ? route.PointIds[1] : route.PointIds[position - 1]);
                if (adjacent == null) return false;
                var minimumX = position == 0 ? constraint.MinimumX : Math.Max(constraint.MinimumX, adjacent.X + .1d);
                var maximumX = position == 0 ? Math.Min(constraint.MaximumX, adjacent.X - .1d) : constraint.MaximumX;
                if (maximumX < minimumX) return false;
                var firstDistance = (minimumX - constraint.BoundaryX) * -constraint.OutwardSign;
                var lastDistance = (maximumX - constraint.BoundaryX) * -constraint.OutwardSign;
                result = new BaseJointRange(constraint.BoundaryX, -constraint.OutwardSign,
                    Math.Min(firstDistance, lastDistance), Math.Max(firstDistance, lastDistance));
                return true;
            }
            var mainRoute = CurrentSample.Routes.FirstOrDefault(route => route.LSegmentStartIds.Count == 0 && route.PointIds.Contains(node.Id));
            if (recommended == null || mainRoute == null) return false;
            var index = mainRoute.PointIds.ToList().IndexOf(node.Id);
            if (index != 0 && index != mainRoute.PointIds.Count - 1) return false;
            var adjacentMainId = index == 0 ? mainRoute.PointIds[1] : mainRoute.PointIds[index - 1];
            var adjacentMain = Find(adjacentMainId);
            if (adjacentMain == null) return false;
            var bridgeDirection = Math.Sign(index == 0 ? adjacentMain.X - recommended.X : recommended.X - adjacentMain.X);
            if (bridgeDirection == 0) return false;
            var intoBaseSign = index == 0 ? -bridgeDirection : bridgeDirection;
            var baseCandidate = Sample.Objects.Where(item => item.Kind == DesignObjectKind.Base)
                .Select(item => new
                {
                    Base = item,
                    Boundary = intoBaseSign < 0 ? item.X + item.Length / 2 : item.X - item.Length / 2
                })
                .OrderBy(item => Math.Abs(item.Boundary - recommended.X))
                .ThenBy(item => item.Base.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (baseCandidate == null) return false;
            var lRoute = Sample.Routes.FirstOrDefault(route => route.LSegmentStartIds.Count > 0 && route.PointIds.Contains(node.Id));
            if (lRoute == null) return false;
            var lIndex = lRoute.PointIds.ToList().IndexOf(node.Id);
            var adjacentLId = lIndex == 0 ? lRoute.PointIds[1] : lIndex == lRoute.PointIds.Count - 1 ? lRoute.PointIds[lIndex - 1] : null;
            var adjacentL = Recommended(adjacentLId);
            if (adjacentL == null) return false;
            var availableToL = (adjacentL.X - baseCandidate.Boundary) * intoBaseSign;
            var availableToBridge = (adjacentMain.X - baseCandidate.Boundary) * -intoBaseSign;
            var maximum = BridgeDesignPrecision.OneDecimal(Math.Min(baseCandidate.Base.Length, availableToL) - .1);
            var maximumExtension = BridgeDesignPrecision.OneDecimal(availableToBridge - .1);
            if (maximum < 0 || maximumExtension < 0) return false;
            result = new BaseJointRange(baseCandidate.Boundary, intoBaseSign, -maximumExtension, maximum);
            return true;
        }

        private BridgeEditResult Validate(BridgeDesignObject node, DesignField field, double value)
        {
            if (node.Kind == DesignObjectKind.Frame && field == DesignField.Width
                && CurrentSample?.FrameLengthBaseline.Any(BridgeOffsetCopperChange.IsWidenedTerminal) == true
                && Math.Abs(value - CurrentSample.FrameLengthBaseline.First(BridgeOffsetCopperChange.IsWidenedTerminal).Width) > .001d)
                return new BridgeEditResult(false, "整段扩宽场景的基架宽度由两端连接基座平均宽度加偏移值统一确定。");
            if (field == DesignField.Installation && BridgeOffsetCopperChange.IsWidenedTerminal(node))
                return new BridgeEditResult(false, "连接基座的扩宽端头安装方式固定，不支持修改。");
            if (field == DesignField.Installation && node.FrameTemplateKey == "LargeOffsetCorner")
                return new BridgeEditResult(false, "拐角基架没有正反装定义，不支持修改安装方式。");
            if (field == DesignField.Installation && BridgeOffsetCopperChange.IsMember(node))
                return new BridgeEditResult(false, "偏移基架采用模板固定安装方向，不支持切换正反装。");
            if (!node.EditableFields.Contains(field)) return new BridgeEditResult(false, "该字段属于只读输入，不允许覆盖。");
            if (double.IsNaN(value) || double.IsInfinity(value)) return new BridgeEditResult(false, "数值必须有限。");
            if (node.Kind == DesignObjectKind.FixedJoint && field == DesignField.X)
            {
                if (!TryGetBaseJointRange(node.Id, out var range))
                    return new BridgeEditResult(false, "未找到基座或L排边界，不能调整该固定断点。");
                var penetration = range.PenetrationAt(value);
                if (penetration < range.MinimumPenetration || penetration > range.MaximumPenetration)
                    return new BridgeEditResult(false, "距基座边距离必须为" +
                        (-range.MaximumPenetration).ToString("0.0", CultureInfo.InvariantCulture) + "～" +
                        (-range.MinimumPenetration).ToString("0.0", CultureInfo.InvariantCulture) +
                        " mm（远离基座为正，进入基座为负）。");
            }
            if ((field == DesignField.Length || field == DesignField.Width) && value <= 0)
                return new BridgeEditResult(false, "长度和宽度必须大于0。");
            if (CurrentSample?.ScenarioKey == "FaceToWall" && node.Kind == DesignObjectKind.Frame && field == DesignField.Length &&
                value < (node.FrameTemplateKey == "Regular" ? 200d : 300d))
                return new BridgeEditResult(false, "面对墙常规基架至少200mm，大基架至少300mm。");
            if (node.Kind == DesignObjectKind.Frame && field == DesignField.Length && !IsSafeFrameLength(node, value))
                return new BridgeEditResult(false, node.CornerPreview != null
                    ? (Recommended(node.Id)?.CornerPreview?.MinimumSafeLengthMm is double minimumSafe
                        ? "大偏移拐角长度必须满足300.0 mm制造下限并覆盖" + minimumSafe.ToString("0.0", CultureInfo.InvariantCulture) + " mm安全范围。"
                        : "旧方案缺少25.0 mm豁口安全基线，请重读并规划。")
                    : BridgeOffsetCopperChange.IsWidenedTerminal(node) || LargeOffsetConnector(node)
                    ? "该基架的长度必须在200～1400 mm范围内。"
                    : "特殊基架不能短于初始规划的最小安全长度，只能在该长度基础上增加。");
            if (node.Kind == DesignObjectKind.Frame && field == DesignField.Length &&
                node.FrameTemplateKey == "SmallOffset" && node.SmallOffsetPreview == null)
                return new BridgeEditResult(false, "小偏移基架缺少进出孔接口参数，不能调整长度，请重读并规划。");
            if (field == DesignField.InsulatorCount && value != Math.Truncate(value))
                return new BridgeEditResult(false, "数量必须为整数。");
            if (field == DesignField.InsulatorCount && (value < 0 || value > 3))
                return new BridgeEditResult(false, "沿用原模板，每节支持0～3套绝缘子支架装配体，每套固定3个绝缘子。");
            if (field == DesignField.Installation && (value != Math.Truncate(value) || value < 0 || value > 1))
                return new BridgeEditResult(false, "安装方式仅支持正装或反装。");
            if (Math.Abs(value) > 1000000)
                return new BridgeEditResult(false, "超出原型显示范围±1000000；不是工程合格判定。");
            return new BridgeEditResult(true);
        }

        private void Put(string id, DesignField field, double value)
        {
            // 覆盖相对于当前载入事实保存；推荐尺寸可能属于更早的规划约束。
            var baseline = BaseObjects().First(o => o.Id == id);
            value = BridgeDesignPrecision.Normalize(baseline.Kind, field, value);
            if (!_state.Overrides.TryGetValue(id, out var fields))
                _state.Overrides[id] = fields = new Dictionary<DesignField, double>();
            if (baseline.Kind == DesignObjectKind.Frame ? baseline.Value(field) == value : Math.Abs(baseline.Value(field) - value) < .01) fields.Remove(field);
            else fields[field] = value;
            if (fields.Count == 0) _state.Overrides.Remove(id);
        }

        private void ResizeInsulators(BridgeDesignObject frame, int count, bool preserveExistingGroups = true)
        {
            frame = Find(frame.Id);
            var previousSelection = SelectedId;
            var selectedInGroup = Find(previousSelection)?.ParentId == frame.Id;
            var children = BaseObjects().Where(o => o.ParentId == frame.Id).ToArray();
            var active = EffectivePlan.Where(o => o.ParentId == frame.Id).ToArray();
            var positions = active.GroupBy(o => o.InsulatorGroupId).ToDictionary(g => g.Key, g => g.First().X + frame.Length / 2);
            // 删除可以留下编号空位；增减数量时保留存续套，不能复活前面的套并误删后面的套。
            var assemblies = Enumerable.Range(1, 3).OrderBy(i => preserveExistingGroups &&
                positions.ContainsKey(frame.Id + (i == 1 ? "/insulators" : "/insulators-" + i)) ? 0 : 1)
                .ThenBy(i => i).Take(count).ToArray();
            var retainedIds = new HashSet<string>();
            foreach (var assembly in assemblies)
            {
                var groupId = frame.Id + (assembly == 1 ? "/insulators" : "/insulators-" + assembly);
                var retained = positions.ContainsKey(groupId);
                if (!retained) positions[groupId] = DefaultInsulatorPosition(frame.Length, positions.Values);
                for (var phaseIndex = 0; phaseIndex < PrototypeBusbarBridgeDesignProvider.InsulatorsPerAssembly; phaseIndex++)
                {
                    var i = (assembly - 1) * PrototypeBusbarBridgeDesignProvider.InsulatorsPerAssembly + phaseIndex + 1;
                    var id = frame.Id + "/ins-" + i;
                    retainedIds.Add(id);
                    var phaseField = frame.FrameTemplateKey == "SideToSidePhaseChange029" ? DesignField.Z : DesignField.Y;
                    var y = active.FirstOrDefault(o => o.Id.EndsWith("/ins-" + (phaseIndex + 1), StringComparison.Ordinal))?.Value(phaseField)
                        ?? active.OrderBy(o => o.Id, StringComparer.Ordinal).Skip(phaseIndex).FirstOrDefault()?.Value(phaseField)
                        ?? (phaseIndex - 1) * (frame.InsulatorPhaseSpacing > 0 ? frame.InsulatorPhaseSpacing : 200);
                    if (children.All(o => o.Id != id))
                    {
                        var child = _provider.CreateInsulator(frame, i, y);
                        var values = child.Values.ToDictionary(p => p.Key, p => p.Value);
                        values[DesignField.X] = positions[groupId] - frame.Length / 2;
                        _state.Added[id] = child.WithValues(values);
                    }
                    else if (!retained) { Put(id, DesignField.X, positions[groupId] - frame.Length / 2); Put(id, phaseField, y); }
                    _state.Removed.Remove(id);
                }
            }
            foreach (var child in children.Where(o => !retainedIds.Contains(o.Id)))
            {
                if (!_state.Added.Remove(child.Id)) _state.Removed.Add(child.Id);
                _state.Overrides.Remove(child.Id); _state.Locks.Remove(child.Id);
                ReleaseManualInsulatorPosition(child.Id);
            }
            if (selectedInGroup) SelectedId = Find(previousSelection) != null ? previousSelection : count == 0 ? frame.Id : frame.Id + "/ins-1";
        }

        private double DefaultInsulatorPosition(double length, IEnumerable<double> existing)
        {
            var positions = existing.Where(p => p >= 0 && p <= length).OrderBy(p => p).Concat(new[] { length });
            var previous = 0d; var maximumGap = -1d; var result = length / 2;
            foreach (var position in positions)
            {
                if (position - previous > maximumGap) { maximumGap = position - previous; result = (position + previous) / 2; }
                previous = position;
            }
            return BridgeDesignPrecision.OneDecimal(result);
        }

        public BridgeEditResult ToggleLock(string id)
        {
            var node = Find(id);
            if (node?.Kind == DesignObjectKind.FixedJoint)
                return new BridgeEditResult(false, "基座L排断点不单独设置锁定状态；其可调范围由相邻桥内断点、基座及L排边界约束。");
            if (node == null || !node.IsEditable) return new BridgeEditResult(false, "固定输入不能变更权限。");
            var targets = node.IsGrouped ? Affected(id, false) : new[] { node };
            if (targets.Any(o => !o.IsEditable)) return new BridgeEditResult(false, "组内含固定输入，不能变更权限。");
            var unlock = targets.All(o => IsLocked(o.Id));
            BeginEdit();
            foreach (var target in targets)
                if (unlock) _state.Locks.Remove(target.Id); else _state.Locks.Add(target.Id);
            Notify(); return new BridgeEditResult(true);
        }

        public void Undo() => RestoreHistory(_undo, _redo);
        public void Redo() => RestoreHistory(_redo, _undo);

        // 保留原合法入口，新界面通过三参入口显式传入勾选范围。
        public BridgeEditResult Restore(string id, bool confirmed) => Restore(id, confirmed, false);

        public BridgeEditResult Restore(string id, bool confirmed, bool sameSource)
        {
            if (!confirmed) return new BridgeEditResult(false, "恢复推荐需要明确确认。");
            if (string.IsNullOrWhiteSpace(id)) return new BridgeEditResult(false, "请选择需要恢复推荐的对象。");
            var node = Find(id);
            if (node == null) return new BridgeEditResult(false, "目标不存在。");
            if (node.Kind == DesignObjectKind.Frame) return RestoreFrameModel(node, sameSource);
            if (node.IsInsulatorGroup)
            {
                var problem = BridgeInsulatorObjects.GroupProblem(EffectivePlan.Where(o => o.InsulatorGroupId == node.InsulatorGroupId).ToArray());
                if (problem != null) return new BridgeEditResult(false, "无法恢复推荐：" + problem);
                var members = Affected(id, sameSource);
                var original = Recommended(node.Id);
                if (original == null) return new BridgeEditResult(false, "绝缘子推荐目标缺失：" + node.Id);
                BeginEdit();
                if (!sameSource) MakeIndependentForEdit(node);
                // 恢复本组位置/锁定不改变架内数量，不删除增补的成员或复活已移除成员。
                foreach (var member in members)
                {
                    foreach (var field in new[] { DesignField.X, DesignField.Y, DesignField.Z })
                        Put(member.Id, field, member.Value(field) + original.Value(field) - node.Value(field));
                    _state.Locks.Remove(member.Id);
                    ReleaseManualInsulatorPosition(member.Id);
                }
                Notify(); return new BridgeEditResult(true);
            }
            var ids = BaseObjects().Where(o => o.Id == id || o.ParentId == id ||
                (node.IsJointGroup && o.JointGroupId == node.JointGroupId)).Select(o => o.Id).ToArray();
            BridgeDesignSample copperGeometry = null;
            if (node.IsJointGroup)
            {
                // 自动避让的其他断点跟随本次推荐恢复重新求解；人工调整和锁定断点继续保留。
                var proposed = EffectivePlan.Select(o => ids.Contains(o.Id) || o.IsJointGroup &&
                    !_state.Overrides.ContainsKey(o.Id) && !IsLocked(o.Id) ? Recommended(o.Id) ?? o : o).ToArray();
                var validation = CurrentSample.ReplanCopper != null ? TryReplanCopper(proposed, out copperGeometry) :
                    new BusbarBridgeFrameJointClearance().Validate(CurrentSample, proposed).Count > 0 ?
                    PlanCopperFrameSeams(CurrentSample.WithGeometry(proposed, CurrentSample.Routes), out copperGeometry) : new BridgeEditResult(true);
                if (!validation.Success) return validation;
            }
            Action restore = () =>
            {
                if (copperGeometry != null) ApplyReplannedGeometry(copperGeometry);
                foreach (var target in ids)
                {
                    _state.Overrides.Remove(target); _state.Locks.Remove(target); _state.Removed.Remove(target);
                    ReleaseManualInsulatorPosition(target);
                    if (node.IsJointGroup && copperGeometry == null)
                    {
                        // 接缝重排可能已把断点写入派生几何，清覆盖值后仍须写回原推荐值。
                        var recommended = Recommended(target);
                        foreach (var field in recommended.EditableFields) Put(target, field, recommended.Value(field));
                    }
                    // 新增基架保留其初始草稿基线；推荐基架恢复时移除后来增加的子项。
                    if (!_state.Added.ContainsKey(id)) _state.Added.Remove(target);
                }
            };
            if (copperGeometry != null) return CommitFrameChange(restore, allowUnchangedFrameAvoidance: true);
            BeginEdit(); restore(); Notify(); return new BridgeEditResult(true);
        }

        private BridgeEditResult RestoreFrameModel(BridgeDesignObject node, bool sameSource)
        {
            if (IsIndependentFrame(node.Id) || LargeOffsetConnector(node)) sameSource = false;
            var baseline = WallSafetyBaseline(Recommended(node.Id));
            var targets = Affected(node.Id, sameSource).Where(o => !IsLocked(o.Id) || Math.Abs(o.Length - baseline.Length) < .01).ToArray();
            if (IsLocked(node.Id) && Math.Abs(node.Length - baseline.Length) >= .01)
                return new BridgeEditResult(false, "当前基架长度已锁定，请先解锁再恢复长度。");
            if (EffectivePlan.Any(o => targets.Any(f => f.Id == o.ParentId) && IsLocked(o.Id)))
                return new BridgeEditResult(false, "待恢复基架内有锁定的绝缘子，请先解锁再恢复。");
            var children = BaseObjects().Where(o => o.ParentId == node.Id).ToDictionary(o => o.Id.Substring(node.Id.Length));
            Action restore = () =>
            {
                if (!sameSource) MakeIndependentForEdit(node);
                foreach (var frame in targets)
                {
                    // 模型推荐不能撤销用户已经核验的换位和反装；长度仍走当前固定端补偿。
                    foreach (var field in new[] { DesignField.Width, DesignField.InsulatorCount })
                    {
                        if (field == DesignField.Width && LargeOffsetConnector(frame)) continue;
                        Put(frame.Id, field, baseline.Value(field));
                    }
                    ResizeInsulators(frame, (int)baseline.Value(DesignField.InsulatorCount), false);
                    foreach (var child in EffectivePlan.Where(o => o.ParentId == frame.Id).ToArray())
                    {
                        ReleaseManualInsulatorPosition(child.Id);
                        if (children.TryGetValue(child.Id.Substring(frame.Id.Length), out var original))
                            foreach (var field in new[] { DesignField.X, DesignField.Y, DesignField.Z })
                                Put(child.Id, field, original.Value(field) + (field == DesignField.Z && CurrentSample.ScenarioKey == "FaceToWall"
                                    ? (Recommended(frame.Id).Height - frame.Height) / 2 : 0));
                    }
                }
            };
            return Math.Abs(baseline.Length - node.Length) >= .01
                ? SetFrameLength(node, baseline.Length, sameSource, restore)
                : CommitFrameChange(restore);
        }
        public bool CanAddFrame => PlanFrameGap(out var template, out var center, out var length).Success;

        private BridgeEditResult PlanFrameGap(out BridgeDesignObject template, out double center, out double length)
        {
            template = null; center = length = 0;
            var targets = CurrentSample?.FrameLengthBaseline.Select(WallSafetyBaseline)
                .OrderBy(o => o.FramePositionIndex).ThenBy(o => o.X).ToArray() ?? Array.Empty<BridgeDesignObject>();
            var frames = EffectivePlan.Where(o => o.Kind == DesignObjectKind.Frame).ToArray();
            if (targets.Length == 0) return new BridgeEditResult(false, "缺少已规划的目标基架安装链，不能新增基架。");
            foreach (var target in targets.Where(t => !IsSpecialFrame(t)))
            {
                // 普通节只补同一实际直段；不能跨偏移孔口把另一侧的横向坐标和截面复制过来。
                var plannedLine = targets.Where(t => SameStraightFrameLine(target, t)).ToArray();
                var start = plannedLine.Min(t => FrameAlong(t, target) - t.Length / 2);
                var end = plannedLine.Max(t => FrameAlong(t, target) + t.Length / 2);
                // 偏移基架的箱体中心不在普通节直线上，但其当前长度仍占用同一安装链；
                // 必须一并扣除，避免把偏移架加长或平移后占用的区间误判为缺口。
                var intervals = frames.Where(f => SameFrameLine(target, f))
                    .Select(f => new { Start = FrameAlong(f, target) - f.Length / 2,
                        End = FrameAlong(f, target) + f.Length / 2 })
                    .Where(p => p.End > start + .01 && p.Start < end - .01)
                    .OrderBy(p => p.Start).ToArray();
                var cursor = start;
                foreach (var interval in intervals)
                {
                    if (interval.Start - cursor >= 200 - .01) { length = Math.Min(interval.Start, end) - cursor; break; }
                    cursor = Math.Max(cursor, interval.End);
                }
                if (length < .01) length = end - cursor;
                if (length < 200 - .01) { length = 0; continue; }
                length = PlanNewFrameLength(length);
                var gapLength = length;
                if (plannedLine.Where(IsSpecialFrame).Any(s =>
                    Math.Min(cursor + gapLength, FrameAlong(s, target) + s.Length / 2) -
                    Math.Max(cursor, FrameAlong(s, target) - s.Length / 2) > .01))
                    return new BridgeEditResult(false, "缺口涉及特殊基架安全范围，请重新规划。");
                template = target; center = cursor + length / 2;
                return new BridgeEditResult(true);
            }
            return new BridgeEditResult(false, "实际总长不足，但未找到安装链上的可补齐缺口，请检查基架连接位置。");
        }

        public BridgeEditResult AddFrame()
        {
            var result = PlanFrameGap(out var template, out var center, out var length);
            if (!result.Success) return result;
            var id = "frame-user-" + Guid.NewGuid().ToString("N");
            var values = template.Values.ToDictionary(p => p.Key, p => p.Value);
            var angle = template.Direction * Math.PI / 180;
            values[DesignField.X] = template.X + center * Math.Cos(angle);
            values[DesignField.Y] = template.Y + center * Math.Sin(angle);
            values[DesignField.Length] = length;
            var frame = new BridgeDesignObject(id, NextFrameSourceName(), DesignObjectKind.Frame, values,
                template.EditableFields, template.Role).WithFrameTemplate(BridgeOffsetCopperChange.IsWidenedTerminal(template) ? "Regular" : template.FrameTemplateKey)
                .WithOffsetInsulators(template.UseOffsetInsulators);
            if (template.InsulatorPadThickness > 0)
                frame = frame.WithInsulatorStack(template.InsulatorPadCount, template.InsulatorPadThickness, template.InsulatorPhaseSpacing, template.InsulatorAssemblyHeight);
            // Height已经按当前墙距补偿，孔口预览仍由EffectivePlan统一叠加一次墙距变化。
            if (template.WallFramePreview != null) frame = frame.WithWallFramePreview(
                CurrentSample.FrameLengthBaseline.Single(o => o.Id == template.Id).WallFramePreview);
            if (template.SmallOffsetPreview != null && !BridgeOffsetCopperChange.IsWidenedTerminal(template)) frame = frame.WithSmallOffsetPreview(template.SmallOffsetPreview);
            if (template.CornerPreview != null) frame = frame.WithCornerPreview(template.CornerPreview);
            return CommitFrameChange(() =>
            {
                InsertFrame(frame);
                ResizeInsulators(frame, (int)frame.Value(DesignField.InsulatorCount));
                _state.Selection = id;
            });
        }

        private void InsertFrame(BridgeDesignObject frame)
        {
            var frames = OrderedFrames();
            var outlet = _geometry.FrameInterfacePoints(frame)[1]; var angle = frame.Direction * Math.PI / 180;
            var index = Array.FindIndex(frames, next =>
            {
                var inlet = _geometry.FrameInterfacePoints(next)[0];
                return (inlet[0] - outlet[0]) * Math.Cos(angle) + (inlet[1] - outlet[1]) * Math.Sin(angle) >= -FrameConnectionToleranceMm;
            });
            var order = frames.Select(o => o.Id).ToList(); order.Insert(index < 0 ? order.Count : index, frame.Id);
            _state.Added[frame.Id] = frame;
            SetFrameOrder(order);
        }

        private double PlanNewFrameLength(double gap)
        {
            var length = BridgeDesignPrecision.OneDecimal(Math.Min(1000d, gap));
            var remaining = gap - length;
            // 默认每节不超过1000mm；余量不足最低制造长度时给下一节保留200mm。
            return remaining > FrameConnectionToleranceMm && remaining < 200 - .01
                ? BridgeDesignPrecision.OneDecimal(gap - 200) : length;
        }

        private void SetFrameOrder(IReadOnlyList<string> order)
        {
            var positions = order.Select((id, index) => new { id, Position = index + 1 }).ToDictionary(p => p.id, p => p.Position);
            var sample = CurrentSample; var editing = CaptureFrameLengthEditing();
            _state.CopperGeometry = sample.WithGeometry(sample.Objects.Select(o => o.Kind == DesignObjectKind.Frame &&
                positions.TryGetValue(o.Id, out var position) ? o.WithFramePosition(position) : o), sample.Routes)
                .WithFrameLengthEditing(new BridgeFrameLengthEditing(sample.FrameLengthBaseline,
                    editing.IndependentFrames, editing.FixedEndFrames, editing.LengthAdjustedFrames, editing.OffsetCopperMinimumLengthMm));
            foreach (var id in _state.Added.Keys.Where(positions.ContainsKey).ToArray())
                _state.Added[id] = _state.Added[id].WithFramePosition(positions[id]);
            InvalidateSnapshot();
        }

        public BridgeEditResult AddInsulator(string frameId)
        {
            var frame = Find(frameId);
            if (frame?.Kind != DesignObjectKind.Frame)
                return new BridgeEditResult(false, "请先选择桥体基架。");
            return SetValues(frameId, new Dictionary<DesignField, double>
            {
                [DesignField.InsulatorCount] = frame.Value(DesignField.InsulatorCount) + 1
            }, false);
        }

        public BridgeEditResult DeleteInsulator(string id)
        {
            var node = Find(id);
            if (node != null && BridgeSmallOffsetSupportPlanner.IsSupport(node))
                return new BridgeEditResult(false, "小偏移搭接需要三组单相支撑，不能单独删除绝缘子。");
            var frame = Find(node?.ParentId);
            if (node?.Kind != DesignObjectKind.Insulator || frame?.Kind != DesignObjectKind.Frame)
                return new BridgeEditResult(false, "只允许删除桥体基架内的绝缘子，基座固定绝缘子为只读。");
            var targets = Affected(id, false);
            if (targets.Any(o => IsLocked(o.Id)))
                return new BridgeEditResult(false, "基架或选中绝缘子已锁定，请先解锁。");
            BeginEdit();
            MakeIndependentForEdit(frame);
            foreach (var target in targets)
            {
                if (!_state.Added.Remove(target.Id)) _state.Removed.Add(target.Id);
                _state.Overrides.Remove(target.Id);
                ReleaseManualInsulatorPosition(target.Id);
            }
            var count = BaseObjects().Where(o => o.ParentId == frame.Id && o.Kind == DesignObjectKind.Insulator &&
                !_state.Removed.Contains(o.Id)).Select(o => o.InsulatorGroupId).Distinct().Count();
            Put(frame.Id, DesignField.InsulatorCount, count);
            SelectedId = frame.Id;
            Notify(); return new BridgeEditResult(true);
        }

        public BridgeEditResult DeleteFrame(string id)
        {
            var node = Find(id);
            if (node == null || node.Kind != DesignObjectKind.Frame) return new BridgeEditResult(false, "只允许删除桥体基架，不能删除柜体或基座。");
            if (BridgeOffsetCopperChange.IsWidenedTerminal(node)) return new BridgeEditResult(false, "连接基座的扩宽端头不能删除；可调整长度或重新规划。");
            if (IsSpecialFrame(node)) return new BridgeEditResult(false, "特殊基架必须覆盖初始安全范围，不能直接删除；请重新规划。");
            if (CurrentSample?.ScenarioKey?.StartsWith("SideToSide", StringComparison.Ordinal) == true &&
                EffectivePlan.Count(o => o.Kind == DesignObjectKind.Frame) <= 1)
                return new BridgeEditResult(false, "侧对侧完整方案至少保留一节基架，不能删除最后一架。");
            var targets = EffectivePlan.Where(o => o.Id == id || o.ParentId == id).ToArray();
            if (targets.Any(o => IsLocked(o.Id))) return new BridgeEditResult(false, "基架或其绝缘子已锁定，请先解锁。");
            var frames = OrderedFrames(); var index = Array.FindIndex(frames, f => f.Id == id);
            return CommitFrameChange(() =>
            {
                foreach (var target in targets)
                {
                    if (!_state.Added.Remove(target.Id)) _state.Removed.Add(target.Id);
                    _state.Overrides.Remove(target.Id);
                    _state.IndependentFrames.Remove(target.Id); _state.FixedEndFrames.Remove(target.Id);
                    _state.LengthAdjustedFrames.Remove(target.Id); _state.SourceNames.Remove(target.Id);
                    ReleaseManualInsulatorPosition(target.Id); _state.AutomaticallySkipped.Remove(target.Id);
                    _state.AutomaticallyPositionedFrames.Remove(target.Id);
                }
                _state.Selection = frames.Where(f => f.Id != id).Skip(Math.Max(0, index - 1)).FirstOrDefault()?.Id;
            });
        }

        public bool CanDeleteFrame(string id)
        {
            var frame = Find(id);
            return frame?.Kind == DesignObjectKind.Frame && !IsSpecialFrame(frame) && !BridgeOffsetCopperChange.IsWidenedTerminal(frame) &&
                !EffectivePlan.Any(o => (o.Id == id || o.ParentId == id) && IsLocked(o.Id)) &&
                !(CurrentSample?.ScenarioKey?.StartsWith("SideToSide", StringComparison.Ordinal) == true && OrderedFrames().Length <= 1);
        }

        public bool CanMoveFrame(string id, int direction) => ValidateFrameMove(id, direction, out var left, out var right).Success;

        public bool IsFramePositionFixed(string id)
        {
            var frame = Find(id);
            return frame != null && (IsSpecialFrame(frame) || BridgeOffsetCopperChange.IsWidenedTerminal(frame));
        }

        private BridgeEditResult ValidateFrameMove(string id, int direction,
            out BridgeDesignObject left, out BridgeDesignObject right)
        {
            left = right = null;
            var frames = EffectivePlan.Where(o => o.Kind == DesignObjectKind.Frame)
                .OrderBy(o => o.FramePositionIndex).ToArray();
            var index = Array.FindIndex(frames, f => f.Id == id);
            var targetIndex = index + Math.Sign(direction);
            if (index < 0 || targetIndex < 0 || targetIndex >= frames.Length || direction == 0)
                return new BridgeEditResult(false, "该方向没有可交换位置的相邻基架。");
            var current = frames[index]; var other = frames[targetIndex];
            // 特殊段的支撑位置由铜排几何确定，不能主动换位，也不能被相邻普通基架挤走。
            if (IsFramePositionFixed(current.Id) || IsFramePositionFixed(other.Id))
                return new BridgeEditResult(false, "特殊铜排段对应的基架位置固定，不能换位或与其交换位置。");
            left = frames[Math.Min(index, targetIndex)]; right = frames[Math.Max(index, targetIndex)];
            if (!FramesConnected(left, right))
                return new BridgeEditResult(false, "换位对象之间存在空隙，不能保证连接；请先闭合空隙。");
            var radians = left.Direction * Math.PI / 180;
            var crossOffset = (right.Y - left.Y) * Math.Cos(radians) - (right.X - left.X) * Math.Sin(radians);
            var alongOffset = (right.X - left.X) * Math.Cos(radians) + (right.Y - left.Y) * Math.Sin(radians);
            if (left.Length <= 0 || right.Length <= 0 || alongOffset <= 0 ||
                Math.Abs(left.Direction - right.Direction) >= .01 || Math.Abs(left.Z - right.Z) >= .01 || Math.Abs(crossOffset) >= .01)
                return new BridgeEditResult(false, "仅允许沿同一直线、同方向安装的普通基架换位。");
            var movedLeft = FramePose(left, left.Length, right.Length);
            var movedRight = FramePose(right, right.Length, -left.Length);
            var originalInlet = _geometry.FrameInterfacePoints(left)[0]; var originalOutlet = _geometry.FrameInterfacePoints(right)[1];
            var newInlet = _geometry.FrameInterfacePoints(movedRight)[0]; var newOutlet = _geometry.FrameInterfacePoints(movedLeft)[1];
            if (!FramesConnected(movedRight, movedLeft) || Enumerable.Range(0, 3).Any(axis =>
                Math.Abs(originalInlet[axis] - newInlet[axis]) > FrameConnectionToleranceMm ||
                Math.Abs(originalOutlet[axis] - newOutlet[axis]) > FrameConnectionToleranceMm))
                return new BridgeEditResult(false, "换位不能保持实际进出接口及缺口边界，修改未提交。");
            return new BridgeEditResult(true);
        }

        public BridgeEditResult MoveFrame(string id, int direction)
        {
            var result = ValidateFrameMove(id, direction, out var left, out var right);
            if (!result.Success) return result;
            var radians = left.Direction * Math.PI / 180;
            // 按长度交换安装区间，保留两端及原接缝；直接交换中心会让不同长度的基架断开。
            var shift = (right.Length - left.Length) / 2;
            var order = OrderedFrames().Select(o => o.Id).ToArray();
            var leftIndex = Array.IndexOf(order, left.Id); var rightIndex = Array.IndexOf(order, right.Id);
            order[leftIndex] = right.Id; order[rightIndex] = left.Id;
            return CommitFrameChange(() =>
            {
                Put(right.Id, DesignField.X, left.X + shift * Math.Cos(radians));
                Put(right.Id, DesignField.Y, left.Y + shift * Math.Sin(radians));
                Put(left.Id, DesignField.X, right.X + shift * Math.Cos(radians));
                Put(left.Id, DesignField.Y, right.Y + shift * Math.Sin(radians));
                SetFrameOrder(order);
            });
        }

        public BridgeEditResult RequestModelGeneration() => new BridgeEditResult(false, "原型只允许内存草稿，禁止生成或改写模型。");

        private void InvalidateSnapshot()
        {
            InvalidateHangerPlacement();
            _effectivePlan = null; _objectsById = null; _copperSections = null; _frameConnectionIssues = null;
            _recommendedSupportObjects = null; _recommendedSupportsById = null;
        }
        private void BeginEdit()
        {
            _editing = true; InvalidateSnapshot();
            _undo.Push(_state.Copy()); _state.DeriveHangingSupports = true;
            _redo.Clear(); LastLengthAdjustedFrameIds = Array.Empty<string>();
        }
        private void Notify(bool publish = true, ISet<string> changedFrames = null, bool initialLoad = false)
        {
            _editing = true; InvalidateSnapshot();
            // 派生抬高先于支承避让；最终原生折弯同时供预览、保存和正式生成使用。
            var allowRaisedChange = Sample?.ReadbackBaseline == null || _state.CopperGeometry != null || _state.CopperTopologyChanged ||
                _state.Overrides.Any(pair => BaseObjects().Any(o => o.Id == pair.Key &&
                    (o.Kind == DesignObjectKind.Frame || o.IsJointGroup))) ||
                _state.Added.Values.Any(o => o.Kind == DesignObjectKind.Frame || o.IsJointGroup) ||
                BaseObjects().Any(o => _state.Removed.Contains(o.Id) && (o.Kind == DesignObjectKind.Frame || o.IsJointGroup));
            var raised = CurrentSample == null ? null :
                new BridgeRaisedLapPlanner().Apply(CurrentSample, EffectivePlan, IsPositionLocked, allowRaisedChange,
                    keepNotchPlan: !allowRaisedChange);
            _state.TemplateNotches.Clear();
            if (raised != null)
                foreach (var frame in raised.Sample.Objects.Where(o => o.Kind == DesignObjectKind.Frame && o.AngleNotches != null))
                    _state.TemplateNotches[frame.Id] = frame.AngleNotches;
            InvalidateSnapshot();
            if (raised?.HasChanges == true && raised.Issues.Count == 0)
            {
                // 支撑增删、覆盖和自动跳过仍由编辑状态拥有，不能烘焙EffectivePlan而丢失恢复来源或重复Added。
                var objects = CurrentSample.Objects.Where(o => o.Kind != DesignObjectKind.FixedRoutePoint)
                    .Concat(raised.Sample.Objects.Where(o => o.Kind == DesignObjectKind.FixedRoutePoint && !_state.Added.ContainsKey(o.Id)));
                _state.CopperGeometry = raised.Sample.WithGeometry(objects, raised.Sample.Routes);
                InvalidateSnapshot();
            }
            // 前组落位变化会改变后续1000mm目标；后续曾跳过的候选也要重新参与规划。
            var adjustableFrames = changedFrames == null ? null : new HashSet<string>(FramesInInstallationOrder(
                EffectivePlan.Where(o => o.Kind == DesignObjectKind.Frame)).SkipWhile(o => !changedFrames.Contains(o.Id)).Select(o => o.Id));
            // 跳过是本版本的派生结果，不是用户删除；条件改变时允许重新布置。
            _state.AutomaticallySkipped.RemoveWhere(id => adjustableFrames == null || adjustableFrames.Contains(BaseObjects().FirstOrDefault(o => o.Id == id)?.ParentId));
            var current = EffectivePlan;
            var before = current.ToDictionary(o => o.Id);
            Func<string, bool> placementLocked = id => IsPositionLocked(id) || (changedFrames != null &&
                (before.TryGetValue(id, out var node) && (node.Kind == DesignObjectKind.Frame ? !adjustableFrames.Contains(id) :
                    node.ParentId != null && !adjustableFrames.Contains(node.ParentId))));
            var hangingTargets = CurrentSample?.ReadbackBaseline != null && !_state.DeriveHangingSupports ? _state.HangingSupportEdits : null;
            var placement = _clearance.PlaceIndependently(CurrentSample, current, placementLocked, IsManuallyPositionedInsulator, hangingTargets,
                preferSharedWidenedEnds: initialLoad && Sample?.Source.IsModelDerived == true);
            var retainedIds = new HashSet<string>(placement.Objects.Select(o => o.Id));
            foreach (var skipped in current.Where(o => o.Kind == DesignObjectKind.Insulator && !retainedIds.Contains(o.Id)))
                _state.AutomaticallySkipped.Add(skipped.Id);
            var movedFrames = new HashSet<string>(placement.Objects.Where(o => o.Kind == DesignObjectKind.Insulator &&
                Math.Abs(before[o.Id].X - o.X) >= .000001).Select(o => o.ParentId));
            // 避让结果可能在重规划或反读后已成为当前值，此时本次没有新位移，仍须拆开同名异模。
            // 已独立的名称保持不变；实际参数相同的位置继续共用同一物理模型。
            ReconcileFrameSourceNames(placement.Objects);
            foreach (var node in placement.Objects.Where(o => o.Kind == DesignObjectKind.Insulator))
            {
                if (Math.Abs(before[node.Id].Y - node.Y) >= .000001)
                    Put(node.Id, DesignField.Y, node.Y);
                if (Math.Abs(before[node.Id].X - node.X) >= .000001)
                {
                    Put(node.Id, DesignField.X, node.X);
                    _state.AutomaticallyPositionedFrames.Add(node.ParentId);
                }
            }
            LastAutoMovedFrameIds = Array.AsReadOnly(movedFrames.OrderBy(id => id, StringComparer.Ordinal).ToArray());
            ClearanceIssues = Array.AsReadOnly((raised?.Issues ?? Array.Empty<BridgeDesignIssue>()).Concat(placement.Issues).Concat(
                new FaceToWallSupportValidation().Validate(CurrentSample, EffectivePlan)).Concat(
                new BridgeSmallOffsetSupportPlanner().Validate(CurrentSample, EffectivePlan)).Concat(
                new BusbarBridgeFrameJointClearance().Validate(CurrentSample, EffectivePlan)).ToArray());
            if (Find(_state.Selection) == null) _state.Selection = null;
            // Changed 订阅者只能读取最终避让、命名和校验全部完成后的快照。
            InvalidateSnapshot(); _editing = false;
            if (publish) { Revision++; Changed?.Invoke(this, EventArgs.Empty); }
        }

        private void ReconcileFrameSourceNames(IReadOnlyList<BridgeDesignObject> objects)
        {
            foreach (var group in objects.Where(o => o.Kind == DesignObjectKind.Frame &&
                    !string.IsNullOrWhiteSpace(o.SourceName))
                .GroupBy(o => o.SourceName, StringComparer.OrdinalIgnoreCase))
            {
                var representatives = new List<Tuple<BridgeDesignObject, string>>();
                foreach (var frame in group.OrderBy(o => o.FramePositionIndex).ThenBy(o => o.Id, StringComparer.Ordinal))
                {
                    var matching = representatives.FirstOrDefault(pair =>
                        _provider.SameFrameModel(pair.Item1, frame, objects));
                    if (matching != null)
                    {
                        if (!string.Equals(frame.SourceName, matching.Item2, StringComparison.OrdinalIgnoreCase))
                            _state.SourceNames[frame.Id] = matching.Item2;
                        continue;
                    }

                    var sourceName = representatives.Count == 0 ? frame.SourceName : NextFrameSourceName();
                    representatives.Add(Tuple.Create(frame, sourceName));
                    if (!string.Equals(frame.SourceName, sourceName, StringComparison.OrdinalIgnoreCase))
                        _state.SourceNames[frame.Id] = sourceName;
                }
            }
        }
    }
}
