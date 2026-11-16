using System;
using CKBusbarBridgePreview;
namespace CKBusbarBridge.Prototype
{
    internal sealed class BridgeCabinetRepairPlanner
    {
        private readonly BridgeCabinetPlanBuilder _planner = new BridgeCabinetPlanBuilder(authorization: CKBusbarBridge.Services.BusbarWorkerAuthorization.Resolve);
        internal BridgeDesignSample Create(BridgeUpstreamSnapshot upstream, BridgeDesignSample actual, Action<string> progress)
            => new BridgeSupportTransition().Plan(actual, baseline => CreateRepairCore(upstream, baseline, progress), progress);

        private BridgeDesignSample CreateRepairCore(BridgeUpstreamSnapshot upstream, BridgeDesignSample actual, Action<string> progress)
        {
            new BridgeDraftGenerationPlanFactory().EnsureCompleteUpstream(upstream);
            var repair = new BridgeRepairPlanning();
            var baseline = actual?.ReadbackBaseline;
            if (baseline == null) throw new InvalidOperationException("修复需要当前实物反读基线，请重新读取模型。");
            if (!BridgeDraftPreviewSnapshot.UpstreamFingerprintsMatch(baseline.RepairUpstreamFingerprint, BridgeDraftPreviewSnapshot.UpstreamFingerprint(upstream)))
                throw new InvalidOperationException("修复目标与反读柜端接口不一致，请重新读取模型。");
            if (baseline.RepairPlanned) return actual;
            if (!baseline.RequiresBridgeRepair && !baseline.RequiresCabinetSynchronization) return actual;
            var kind = repair.Classify(actual);
            var target = _planner.CreateTarget(upstream, actual, kind, 0, progress);
            if (actual.ScenarioKey == target.ScenarioKey &&
                !System.Linq.Enumerable.Any(actual.Issues, issue => issue.Source == BridgeCopperBusinessRecovery.SideToSideBreakpointSource))
                target = repair.RetainOrdinaryBreakpoints(actual, target);
            if (upstream.FaceToWall != null)
                target = new PrototypeBusbarBridgeDesignProvider().AdaptFaceToWallCabinetFrames(upstream, actual, target, true);
            target = repair.Complete(actual, target, upstream);
            target = _planner.PlanRepairFrameSeams(target);
            target = BusbarBridgeDraftEditor.CompleteSupportTargets(target);
            // 实物反读保持原布局；仅在修复目标的最终铜排和支撑就位后冻结各角钢豁口。
            var notches = new BridgeRaisedLapPlanner().Apply(target, target.Objects, allowLift: false, keepNotchPlan: false);
            target = repair.IsolateConflictingSources(notches.Sample);
            repair.RequireFrameContinuity(target, actual.ReadbackBaseline, upstream);
            _planner.RequireValidPlanning(target, "修复目标校验失败：", notches.Issues);
            return target;
        }

    }
}
