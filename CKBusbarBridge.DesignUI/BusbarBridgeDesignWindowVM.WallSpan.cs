using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CKBusbarBridge.AlgorithmWorker;
using CKBusbarBridge.Services;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.Prototype
{
    public sealed partial class BusbarBridgeDesignWindowVM
    {
        private BridgeWallPlanningCodec.WallPlanningData _wallSyncedState;
        // 纯值求解接缝；正式会话仍由既有Worker客户端和宿主授权解析器处理。
        internal Func<LinkageRouteWorkerRequestDto, CancellationToken, Task<LinkageRouteWorkerComparisonResultDto>> WallSpanSolver { get; set; }
        internal Action<BridgeUpstreamSnapshot, BridgeDesignSample> ValidateWallSpanGeneration { get; set; }

        private void SynchronizeWallPlanningState()
        {
            var sample = Editor.CurrentSample;
            if (sample?.ScenarioKey != "FaceToWall" || sample.WallPlanningRecipe == null) return;
            var state = sample.WallPlanningState;
            if (ReferenceEquals(state, _wallSyncedState)) return;
            _wallSyncedState = state;
            if (!state.SpecialStartX.HasValue) return; // 无重规划入口的旧只读记录仍可展示。
            _wallRequest = state.Request;
            _wallPlanningInput = new FaceToWallPlanningInput(state.Request,
                state.Groups.TakeWhile(g => g.DisplayName != "算法中间段" && g.DisplayName != "特殊段入口桥头").ToArray(),
                state.SpecialStartX.Value, state.IsPhaseSwitched) { MinimumSpecialSpan = state.MinimumSpecialSpan,
                    RequestedSpecialSpan = state.RequestedSpecialSpan, AbsorbedOrdinaryCount = state.AbsorbedOrdinaryCount };
            _wallSolutions = state.RuntimeCandidates ?? new[] { state.Solution };
            _wallSelectedIndex = state.RuntimeCandidates == null ? 0 : state.RuntimeSelectedIndex;
            _wallWorkerOptions = RestoredWallOptions(state);
            WallSpecialStartText = (state.SpecialStartX.Value - (Upstream?.FaceToWall?.Cabinet.Opening.X ?? 0d))
                .ToString("0.###", System.Globalization.CultureInfo.CurrentCulture);
            _wallCommittedSpecialStartText = WallSpecialStartText; _wallBoundaryEdited = false;
            if (!PlanningBusy)
                GenerationBlocked = !sample.SupportsFormalGeneration || Editor.HasBlockingIssues ||
                    !string.IsNullOrWhiteSpace(sample.ReadbackBaselineWarning) || sample.ReadbackBaseline?.RequiresWallCopperReplan == true;
            NotifyWallSolutions();
        }

        internal BridgeEditResult RefreshWallSpan(double targetLength)
        {
            if (PlanningBusy || GenerationBusy || Upstream?.FaceToWall == null || Editor.CurrentSample?.WallPlanningRecipe == null)
                return new BridgeEditResult(false, "方案正在刷新或固定接口缺失，当前输入未提交。");
            if (Editor.CopperSections.Where(s => Editor.IsSpecialCopperSection(s.Id)).Any(s => !Editor.CanAdjustCopperSectionLength(s.Id)))
                return new BridgeEditResult(false, "特殊段相邻断点已锁定，当前输入未提交。");
            FaceToWallPlanningInput current, target;
            try
            {
                var planner = new FaceToWallPreviewPlanning();
                current = _provider.CaptureWallReplacementInput(Editor.CurrentSample, Editor.EffectivePlan);
                if (Math.Abs(planner.SpecialSpan(current, Upstream.FaceToWall.OverlapLength) - targetLength) <= .000001d &&
                    Editor.CurrentSample.ReadbackBaseline?.RequiresWallCopperReplan != true &&
                    (Editor.CurrentSample.WallPlanningState.InputFingerprint == null || Editor.CurrentSample.WallPlanningState.InputFingerprint ==
                        new BridgeWallPlanningCodec().InputFingerprint(Upstream.FaceToWall)))
                {
                    WallSpecialStartText = (current.SpecialStartX - Upstream.FaceToWall.Cabinet.Opening.X)
                        .ToString("0.###", System.Globalization.CultureInfo.CurrentCulture);
                    _wallCommittedSpecialStartText = WallSpecialStartText;
                    return new BridgeEditResult(true);
                }
                target = planner.CreateInputForSpecialSpan(Upstream.FaceToWall, current, targetLength, _wallWorkerOptions);
                target.RetainedDraft = current.RetainedDraft;
            }
            catch (Exception error) when (error is InvalidOperationException || error is ArgumentException)
            {
                FaceToWallPreviewTrace.Error("面对墙目标长度及L排联动校验失败", error);
                return new BridgeEditResult(false, error.Message);
            }
            var cancellation = new CancellationTokenSource();
            _wallCancellation = cancellation;
            var wasBlocked = GenerationBlocked;
            PlanningBusy = true; GenerationBlocked = true;
            Message = "正在按目标长度联合规划铜排、L排断点及基架。";
            PlanningCompletion = PlanWallSpanAsync(Upstream, current, target, cancellation, wasBlocked);
            return new BridgeEditResult(true);
        }

        private async Task PlanWallSpanAsync(BridgeUpstreamSnapshot snapshot, FaceToWallPlanningInput previous,
            FaceToWallPlanningInput target, CancellationTokenSource cancellation, bool wasBlocked)
        {
            var revision = Editor.Revision;
            var original = Editor.CurrentSample;
            var stored = original.WallPlanningState;
            var committed = false;
            try
            {
                Func<FaceToWallPlanningInput, LinkageRouteWorkerSolutionDto, BridgeDesignSample> create = (planning, solution) =>
                {
                    var candidate = _provider.CreateFaceToWallPreview(snapshot, planning.Request, solution, planning);
                    if (original.ReadbackBaseline?.RequiresCabinetSynchronization == true)
                        candidate = _provider.CompleteFaceToWallCabinetReplan(snapshot, original, candidate);
                    ValidateWallSpanGeneration?.Invoke(snapshot, candidate);
                    return candidate;
                };
                var exact = new FaceToWallExactSpanPlanning();
                var completed = await Task.Run(() => exact.TryCurrent(snapshot.FaceToWall, previous, stored.Solution,
                    target, create, cancellation.Token), cancellation.Token);
                if (!IsCurrentWallPlanning(snapshot, cancellation)) return;
                if (revision != Editor.Revision) { Message = "草稿已变化，本次长度规划未提交。"; return; }
                if (completed == null)
                {
                    LinkageRouteWorkerComparisonResultDto result;
                    if (WallSpanSolver != null) result = await WallSpanSolver(target.Request, cancellation.Token);
                    else
                    {
                        if (_wallWorker == null) _wallWorker = new BusbarAlgorithmWorkerClient(ResolveWorkerAuthorization);
                        result = await _wallWorker.SolveProjectAsync(target.Request, null, cancellation.Token, snapshot.ModelPath);
                    }
                    completed = await Task.Run(() => exact.Complete(snapshot.FaceToWall, target, result, create, cancellation.Token), cancellation.Token);
                }
                if (!IsCurrentWallPlanning(snapshot, cancellation)) return;
                if (revision != Editor.Revision)
                { Message = "草稿已变化，本次长度规划未提交。"; return; }
                var sample = completed.Candidates[0].Sample;
                var resultCommit = CommitWallReplanning(sample, revision, true);
                if (!resultCommit.Success) { Message = resultCommit.Message; return; }
                committed = true;
                _wallCurrentIsGenerated = false;
                GenerationBlocked = !Editor.CurrentSample.SupportsFormalGeneration || Editor.HasBlockingIssues;
                Message = target.DirectBaseLap ? "特殊段与基座L排断点已按目标长度联动，铜排及支撑已一起提交。" :
                    "特殊段已按目标长度重新规划，铜排及支撑已一起提交。";
                ModelPreviewReady?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            { if (IsCurrentWallPlanning(snapshot, cancellation)) Message = "长度规划已取消，原方案保留。"; }
            catch (Exception error)
            {
                if (IsCurrentWallPlanning(snapshot, cancellation))
                {
                    FaceToWallPreviewTrace.Error("面对墙目标长度联合规划失败", error);
                    Message = "目标长度规划未提交，原方案保留：" + error.Message;
                }
            }
            finally
            {
                if (ReferenceEquals(_wallCancellation, cancellation))
                {
                    _wallCancellation = null; PlanningBusy = false;
                    if (!committed && ReferenceEquals(Editor.CurrentSample, original)) GenerationBlocked = wasBlocked;
                    SynchronizeWallPlanningState(); RefreshFields(); NotifyWallSolutions();
                }
                cancellation.Dispose();
            }
        }

        private BusbarAlgorithmWorkerRequestOptions RestoredWallOptions(BridgeWallPlanningCodec.WallPlanningData state)
        {
            var request = state.Request;
            var result = BusbarAlgorithmWorkerRequestOptions.CreateDefault();
            result.MinimumSegmentLength = request.MinimumSegmentLength;
            result.BendingRadius = request.BendingRadius; result.BendAllowance = request.BendAllowance;
            result.InsulationClearance = request.InsulationClearance;
            result.AllowDoubleStartMiddleLap = request.AllowDoubleStartMiddleLap;
            result.RequireSingleStartMiddleLap = request.RequireSingleStartMiddleLap;
            result.RequireAxisAlignedRoutes = request.RequireAxisAlignedRoutes;
            result.UseExplicitContainmentBox = request.UseExplicitContainmentBox;
            result.SwapPhaseAAndCAtEnd = state.IsPhaseSwitched;
            var search = request.SearchOptions;
            if (search != null)
            {
                result.FirstSolutionTargetMilliseconds = search.FirstSolutionTargetMilliseconds;
                result.TotalSearchLimitMilliseconds = search.TotalSearchLimitMilliseconds;
                result.EnableAutomaticTotalSearchLimit = search.EnableAutomaticTotalSearchLimit;
                result.MaximumCandidatesPerGroup = search.MaximumCandidatesPerGroup;
                result.MaximumClearanceRepairCandidatesPerState = search.MaximumClearanceRepairCandidatesPerState;
                result.MaximumSolutionsPerAlgorithm = search.MaximumSolutionsPerAlgorithm;
                result.BeamWidth = search.BeamWidth; result.MaximumDegreeOfParallelism = search.MaximumDegreeOfParallelism;
                result.MinimumAdaptiveRepairStep = search.MinimumAdaptiveRepairStep;
            }
            return result;
        }
    }
}
