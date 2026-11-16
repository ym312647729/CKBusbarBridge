using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Prototype
{
    public sealed partial class BusbarBridgeDraftEditor
    {
        // A modal dimension editor can preview repeatedly, then retain one undo step or restore the exact history.
        internal Action CaptureDraftCheckpoint()
        {
            var checkpointState = _state.Copy(); var checkpointLoaded = _loadedState.Copy(); var checkpointSample = Sample;
            var checkpointUndo = _undo.Reverse().Select(state => state.Copy()).ToArray();
            var checkpointRedo = _redo.Reverse().Select(state => state.Copy()).ToArray();
            var checkpointIssues = ClearanceIssues; var checkpointMoved = LastAutoMovedFrameIds; var checkpointLengths = LastLengthAdjustedFrameIds;
            return () =>
            {
                _state = checkpointState.Copy(); _loadedState = checkpointLoaded.Copy(); Sample = checkpointSample;
                _undo.Clear(); foreach (var state in checkpointUndo) _undo.Push(state.Copy());
                _redo.Clear(); foreach (var state in checkpointRedo) _redo.Push(state.Copy());
                ClearanceIssues = checkpointIssues; LastAutoMovedFrameIds = checkpointMoved; LastLengthAdjustedFrameIds = checkpointLengths;
                InvalidateSnapshot(); Revision++; Changed?.Invoke(this, EventArgs.Empty);
            };
        }
        internal float? PreviewBaseBoundaryX(string objectId) => TryGetBaseJointRange(objectId, out var range) ? (float?)range.BoundaryX : null;
        // Rebase every history state before installing any result. Automatic derivation adds no undo entry and retains redo.
        internal void RebaseParameterDraft(Func<BridgeDesignSample, BridgeDesignSample> planner)
        {
            if (CurrentSample == null) return;
            var original = _state; var originalSample = Sample; var snapshots = new Dictionary<EditState, EditState>();
            try
            {
                foreach (var state in new[] { _state, _loadedState }.Concat(_undo).Concat(_redo).Distinct())
                {
                    _state = state; InvalidateSnapshot();
                    var previous = CaptureSavedSample(); var target = planner(previous);
                    if (target == null) throw new InvalidOperationException("原参数联动未返回有效草稿。");
                    target = BridgeFrameSourceIsolation.NormalizeDraftNames(target);
                    var transition = new BridgeSupportTransition(); transition.Require(previous, target);
                    var next = new EditState { CopperGeometry = target, CopperTopologyChanged = state.CopperTopologyChanged,
                        PreserveInsulatorLayout = true, Selection = transition.RemapIds(previous, target, new[] { state.Selection }).FirstOrDefault(),
                        Hangers = target.SavedHangers ?? state.Hangers ?? (target.HangerRoof == null ? null : _hangerPlanner.InitialLayout(target, target.Objects)) };
                    next.Locks.UnionWith(target.SavedLockedObjectIds); next.ManualInsulatorPositions.UnionWith(target.SavedManualInsulatorIds);
                    next.ManualJointPositions.UnionWith(target.SavedManualJointIds);
                    if (target.SavedFrameLengthEditing != null)
                    {
                        next.IndependentFrames.UnionWith(target.SavedFrameLengthEditing.IndependentFrames);
                        next.FixedEndFrames.UnionWith(target.SavedFrameLengthEditing.FixedEndFrames);
                        next.LengthAdjustedFrames.UnionWith(target.SavedFrameLengthEditing.LengthAdjustedFrames);
                    }
                    snapshots.Add(state, next);
                }
                var undo = _undo.Reverse().Select(state => snapshots[state]).ToArray();
                var redo = _redo.Reverse().Select(state => snapshots[state]).ToArray();
                _state = snapshots[original]; _loadedState = snapshots[_loadedState]; Sample = _loadedState.CopperGeometry;
                _undo.Clear(); foreach (var state in undo) _undo.Push(state);
                _redo.Clear(); foreach (var state in redo) _redo.Push(state);
                InvalidateSnapshot(); Revision++;
            }
            catch { _state = original; Sample = originalSample; InvalidateSnapshot(); throw; }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
    public sealed partial class BusbarBridgeDesignWindowVM
    {
        internal void ApplyParameterDraft(BridgeUpstreamSnapshot upstream, Func<BridgeDesignSample, BridgeDesignSample> planner, bool recommend = false)
        {
            if (HasPendingInput) throw new InvalidOperationException("尚有未确认输入，参数派生结果等待提交后应用。");
            var previousUpstream = Upstream;
            Upstream = upstream;
            try { if (recommend) Editor.Load(planner(Editor.CaptureSavedSample())); else Editor.RebaseParameterDraft(planner); }
            catch { Upstream = previousUpstream; throw; }
            GenerationBlocked = false;
            RaisePropertyChanged(nameof(Upstream)); RaisePropertyChanged(nameof(SourceSummary)); RaisePropertyChanged(nameof(Status));
            RaisePropertyChanged(nameof(SceneAndSpanSummary)); RaisePropertyChanged(nameof(DisplayOverlapLengthMm));
            if (recommend) Message = Editor.CurrentSample.ReadbackBaselineWarning ?? "已按当前参数草稿重新推荐方案。";
        }
    }
}
