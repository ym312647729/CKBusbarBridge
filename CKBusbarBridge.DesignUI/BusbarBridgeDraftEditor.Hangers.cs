using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Prototype
{
    public sealed partial class BusbarBridgeDraftEditor
    {
        private IReadOnlyList<BridgeHangerPlacement> _hangerPlacements;
        private readonly BridgeHangerLayoutPlanner _hangerPlanner = new BridgeHangerLayoutPlanner();
        private double[] _hangerStart;
        internal BridgeHangerLayout HangerLayout => _state.Hangers;
        internal bool HasHangerSections => CurrentSample != null && _hangerPlanner.HasOffsetCorners(EffectivePlan);
        public IReadOnlyList<BridgeHangerPlacement> HangerPlacements => _hangerPlacements ??
            (_hangerPlacements = _hangerPlanner.Locate(CurrentSample, EffectivePlan, HangerLayout,
                Sample?.HangerRoof, Sample?.HangerRoofProblem));
        internal bool TryHangerStart(out double[] zero)
        {
            if (_hangerStart == null) _hangerPlanner.TryStart(CurrentSample, EffectivePlan, HangerLayout, out _hangerStart);
            zero = _hangerStart;
            return zero != null;
        }

        internal IEnumerable<BridgeDesignIssue> HangerIssues => HangerPlacements.Where(row => !row.IsValid).Select(row =>
            new BridgeDesignIssue(row.Name + "：" + row.Problem + (row.Frame == null ? "" : "（" + row.FrameLabel + "）"),
                row.Id, "吊架", false, true));

        internal BridgeEditResult SetHangerCount(int count, IReadOnlyCollection<string> remove)
            => SetHangerCounts(new Dictionary<BridgeHangerSectionKind, int> { [BridgeHangerSectionKind.Whole] = count }, remove);

        internal BridgeEditResult SetHangerCounts(IReadOnlyDictionary<BridgeHangerSectionKind, int> counts, IReadOnlyCollection<string> remove)
        {
            try
            {
                var next = _hangerPlanner.Resize(CurrentSample, EffectivePlan, HangerLayout, counts, remove);
                return HangerLayout != null && HangerLayout.Settings.SequenceEqual(next.Settings)
                    ? new BridgeEditResult(true) : CommitHangers(next);
            }
            catch (InvalidOperationException error) { return new BridgeEditResult(false, error.Message); }
        }

        internal BridgeEditResult ConfirmHangerStart()
        {
            var start = BridgeHangerLayoutPlanner.Frames(EffectivePlan).FirstOrDefault();
            if (start == null) return new BridgeEditResult(false, "当前没有可作为基准的首节基架。");
            var layout = new BridgeHangerLayout(start.Id, start.DriveValue(DesignField.Direction), HangerLayout.Settings);
            if (!_hangerPlanner.TryStart(CurrentSample, EffectivePlan, layout, out _))
                return new BridgeEditResult(false, "当前首节基架缺少可核验的起始边。");
            return CommitHangers(layout);
        }

        internal BridgeEditResult SetHangerDistance(string id, double distance) =>
            ChangeHanger(id, setting => distance == setting.Distance ? setting : new BridgeHangerSetting(id, distance, null,
                setting.Section ?? HangerPlacements.SingleOrDefault(row => row.Id == id && row.HasSection)?.Section));

        internal BridgeEditResult SetHangerFrame(string id, string frameId)
        {
            var row = HangerPlacements.FirstOrDefault(item => item.Id == id);
            if (row == null || !row.Candidates.Any(frame => frame.Id == frameId))
                return new BridgeEditResult(false, "只能在当前位置的候选基架中选择。");
            return ChangeHanger(id, setting => new BridgeHangerSetting(id, setting.Distance, frameId, row.Section));
        }

        private BridgeEditResult ChangeHanger(string id, Func<BridgeHangerSetting, BridgeHangerSetting> change)
        {
            var layout = HangerLayout;
            if (layout == null || !layout.Settings.Any(setting => setting.Id == id))
                return new BridgeEditResult(false, "吊架不存在，请重新选择。");
            var settings = layout.Settings.Select(setting => setting.Id == id ? change(setting) : setting).ToArray();
            if (settings.SequenceEqual(layout.Settings)) return new BridgeEditResult(true);
            return CommitHangers(new BridgeHangerLayout(layout.StartFrameId, layout.Direction, settings));
        }

        private BridgeEditResult CommitHangers(BridgeHangerLayout layout)
        {
            _undo.Push(_state.Copy());
            _redo.Clear();
            _state.Hangers = layout;
            InvalidateHangerPlacement();
            Revision++;
            Changed?.Invoke(this, EventArgs.Empty);
            return new BridgeEditResult(true);
        }
        private void InvalidateHangerPlacement() { _hangerPlacements = null; _hangerStart = null; }

        private void RestoreHistory(Stack<EditState> source, Stack<EditState> destination)
        {
            if (source.Count == 0) return;
            var previous = _state;
            destination.Push(previous.Copy());
            _state = source.Pop();
            LastLengthAdjustedFrameIds = _state.LengthAdjustedFrames.ToArray();
            if (!ReferenceEquals(previous.Hangers, _state.Hangers) && ReferenceEquals(previous.CopperGeometry, _state.CopperGeometry))
            {
                // 吊架操作不重铺铜排、基架或绝缘子。
                InvalidateHangerPlacement();
                Revision++;
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }
            Notify(true, _state.PreserveInsulatorLayout ? new HashSet<string>() : null);
        }
    }
}
