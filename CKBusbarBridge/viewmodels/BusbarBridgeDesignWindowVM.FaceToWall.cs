using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Windows.Input;
using System.Threading;
using System.Threading.Tasks;
using CKBusbarBridge.AlgorithmWorker;
using CKBusbarBridge.Services;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.Prototype
{
    public sealed partial class BusbarBridgeDesignWindowVM
    {
        private BusbarAlgorithmWorkerClient _wallWorker;
        private CancellationTokenSource _wallCancellation;
        private LinkageRouteWorkerRequestDto _wallRequest;
        private FaceToWallPlanningInput _wallPlanningInput;
        private IReadOnlyList<LinkageRouteWorkerSolutionDto> _wallSolutions = Array.Empty<LinkageRouteWorkerSolutionDto>();
        private int _wallSelectedIndex = -1;
        private bool _wallDisposed;
        private bool _planningBusy;
        private string _wallSpecialStartText;
        private string _wallCommittedSpecialStartText;
        private bool _wallBoundaryEdited;
        private BusbarAlgorithmWorkerRequestOptions _wallWorkerOptions;
        private bool _wallCurrentIsGenerated;
        internal Func<string> ResolveWorkerAuthorization { get; set; }
        internal Func<string, BridgeWallPlanningRestore> DecodeWallPlanning { get; set; }
        private void RestoreWallSelection(BridgeDesignSample sample)
        {
            var stored = (DecodeWallPlanning ?? throw new InvalidOperationException("尚未连接正式模型恢复服务。"))(sample.WallPlanningRecipe);
            if (!stored.SpecialStartX.HasValue || double.IsNaN(stored.SpecialStartX.Value) || double.IsInfinity(stored.SpecialStartX.Value))
                throw new InvalidOperationException("保存的面对墙方案缺少特殊段起点。");
            var input = Upstream.FaceToWall;
            Upstream = Upstream.WithFaceToWall(new FaceToWallUpstreamGeometry(input.Coordinates, input.Cabinet,
                input.WallConductors, input.FixedObjects, input.WallOpeningCorners, input.Position,
                input.PreviewReferenceComponentName, input.RootCount, stored.Request.InsulationClearance,
                input.OverlapLength, input.WallCabinetEccentricity, input.ModelClearance, input.AppliedWallVerticalClearance,
                input.WallInstallation, input.IsPhaseSwitched));
            _wallRequest = stored.Request; _wallSolutions = new[] { stored.Solution }; _wallSelectedIndex = 0;
            _wallCurrentIsGenerated = true;
            _wallPlanningInput = new FaceToWallPlanningInput(stored.Request,
                stored.Groups.TakeWhile(g => g.DisplayName != "算法中间段" && g.DisplayName != "特殊段入口桥头").ToArray(), stored.SpecialStartX.Value, stored.IsPhaseSwitched)
                { MinimumSpecialSpan = stored.MinimumSpecialSpan, RequestedSpecialSpan = sample.WallPlanningState?.RequestedSpecialSpan,
                    AbsorbedOrdinaryCount = sample.WallPlanningState?.AbsorbedOrdinaryCount ?? 0 };
            WallSpecialStartText = (stored.SpecialStartX.Value - Upstream.FaceToWall.Cabinet.Opening.X).ToString("0.###", CultureInfo.CurrentCulture);
            _wallCommittedSpecialStartText = WallSpecialStartText; _wallBoundaryEdited = false;
            _wallWorkerOptions = BusbarAlgorithmWorkerRequestOptions.CreateDefault();
            _wallWorkerOptions.MinimumSegmentLength = stored.Request.MinimumSegmentLength;
            _wallWorkerOptions.BendingRadius = stored.Request.BendingRadius;
            _wallWorkerOptions.BendAllowance = stored.Request.BendAllowance;
            _wallWorkerOptions.InsulationClearance = stored.Request.InsulationClearance;
            _wallWorkerOptions.SwapPhaseAAndCAtEnd = input.IsPhaseSwitched;
            NotifyWallSolutions();
        }
        internal viewmodels.BusbarSchemeSelectionWindowVM CreateWallSchemeSelector()
        {
            if (!CanSwitchWallSolution || _wallPlanningInput == null)
                throw new InvalidOperationException("面对墙方案尚未完成，或有未提交输入。");
            var current = Editor.CurrentSample;
            var objects = Editor.EffectivePlan.ToArray();
            var planning = _provider.CaptureWallReplacementInput(current, objects);
            var stored = (DecodeWallPlanning ?? throw new InvalidOperationException("尚未连接正式模型恢复服务。"))(current.WallPlanningRecipe);
            var snapshot = Upstream; var revision = Editor.Revision;
            return new viewmodels.BusbarSchemeSelectionWindowVM(snapshot, planning, new[] { stored.Solution }, 0, ResolveWorkerAuthorization,
                _wallCurrentIsGenerated && !Editor.HasChanges && !_wallBoundaryEdited, revision,
                baselineMatchesGeometry: _provider.WallSpecialMatchesStoredGeometry(current, objects));
        }
        internal void ApplyWallSchemeSelection(viewmodels.BusbarSchemeSelectionWindowVM selection)
        {
            var index = selection.AlgorithmSchemes.IndexOf(selection.SelectedAlgorithmScheme);
            var solutions = selection.Solutions;
            if (index < 0 || index >= solutions.Count) throw new InvalidOperationException("请选择有效的面对墙方案。");
            if (selection.CapturedRevision != Editor.Revision)
            { Message = "草稿已变化，请重新打开方案选择；未替换特殊段。"; return; }
            if (selection.SelectedAlgorithmScheme.IsCurrentScheme)
            { Message = "继续使用当前方案，草稿及实际模型保持原状。"; return; }
            var sample = _provider.ReplaceWallSpecialScheme(selection.Snapshot, Editor.CurrentSample, Editor.EffectivePlan,
                selection.Planning, solutions[index]);
            var replaced = Editor.ReplaceWallSpecialScheme(sample);
            if (!replaced.Success) { Message = replaced.Message; return; }
            Upstream = selection.Snapshot; _wallSolutions = new[] { solutions[index] }; _wallPlanningInput = selection.Planning;
            _wallRequest = selection.Planning.Request;
            selection.AlgorithmParameterInput.TryCreateWorkerRequestOptions(out _wallWorkerOptions, out var ignored);
            _wallSelectedIndex = 0;
            GenerationBlocked = !Editor.CurrentSample.SupportsFormalGeneration || !string.IsNullOrWhiteSpace(Editor.CurrentSample.ReadbackBaselineWarning);
            Message = "特殊铜排候选已替换到草稿；普通段、断点和基架编辑已保留，正式生成后才更新实际模型。";
            NotifyWallSolutions(); ModelPreviewReady?.Invoke(this, EventArgs.Empty);
            RaisePropertyChanged(nameof(HasUnsavedChanges));
        }
        internal BridgeDesignSample PreviewWallSchemeSelection(viewmodels.BusbarSchemeSelectionWindowVM selection)
        {
            if (selection.CapturedRevision != Editor.Revision) throw new InvalidOperationException("草稿已变化，请重新打开方案列表。");
            if (selection.SelectedAlgorithmScheme == null) throw new InvalidOperationException("请选择一个方案。");
            return selection.SelectedAlgorithmScheme.IsCurrentScheme ? Editor.CaptureSavedSample() :
                _provider.ReplaceWallSpecialScheme(selection.Snapshot, Editor.CurrentSample, Editor.EffectivePlan,
                    selection.Planning, selection.SelectedAlgorithmScheme.WorkerSolution);
        }
        private bool HasWallBoundaryChanges => _wallBoundaryEdited || (HasWallSolutions && WallSpecialStartText != _wallCommittedSpecialStartText);
        public string WallSpecialStartText
        {
            get => _wallSpecialStartText;
            set { _wallSpecialStartText = value; RaisePropertyChanged(nameof(WallSpecialStartText)); RaisePropertyChanged(nameof(HasUnsavedChanges)); }
        }
        public ICommand ReplanWallSpecialRegionCommand => new BridgeDesignCommand(ReplanWallSpecialRegion,
            () => HasWallSolutions && !PlanningBusy && !GenerationBusy && !HasPendingInput && !NativePreviewBusy);
        private void ReplanWallSpecialRegion()
        {
            if (!double.TryParse(WallSpecialStartText, NumberStyles.Float, CultureInfo.CurrentCulture, out var distance))
            { Message = "请输入有效的特殊段起点距离（mm）。"; return; }
            var start = Upstream.FaceToWall.Cabinet.Opening.X + distance;
            var planner = new FaceToWallPreviewPlanning();
            var current = _provider.CaptureWallReplacementInput(Editor.CurrentSample, Editor.EffectivePlan);
            var result = RefreshWallSpan(planner.WallCopperEnd(current, Upstream.FaceToWall.OverlapLength) - start);
            if (!result.Success) Message = result.Message;
        }
        private bool TryRefreshWallSpan(BridgeDesignFieldVM field)
        {
            if (Upstream?.FaceToWall == null || _wallPlanningInput == null) return false;
            var section = Editor.FindCopperSection(field.ObjectId);
            var joint = Editor.JointRepresentative(field.ObjectId);
            var boundaryDistance = field.Field == DesignField.JointDistanceFromPrevious && joint?.Role == "特殊段重算入口";
            var ordinaryLength = field.Field == DesignField.CopperSegmentLength && section != null &&
                Editor.Find(section.EndPointId)?.Role == "特殊段重算入口";
            var specialLength = field.Field == DesignField.CopperSegmentLength && Editor.IsSpecialCopperSection(field.ObjectId);
            var baseJoint = field.Field == DesignField.BaseJointEdgeDistance &&
                joint?.Kind == DesignObjectKind.FixedJoint && _wallPlanningInput.DirectBaseLap;
            if (!boundaryDistance && !ordinaryLength && !specialLength && !baseJoint) return false;
            if (PlanningBusy || (!baseJoint && (boundaryDistance ? Editor.Affected(joint.Id, false).Any(o => Editor.IsLocked(o.Id)) : !Editor.CanAdjustCopperSectionLength(field.ObjectId))))
            {
                field.Complete(new BridgeEditResult(false, "方案正在刷新或相邻断点已锁定，不能调整特殊段跨度。"));
                return true;
            }
            if (!double.TryParse(field.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var length) ||
                double.IsNaN(length) || double.IsInfinity(length) || length <= (boundaryDistance || baseJoint ? 0 : DisplayOverlapLengthMm))
            {
                field.Complete(new BridgeEditResult(false, boundaryDistance ? "断点距离必须为大于0的有限数值。" :
                    "铜排桥向长度必须为大于搭接长度的有限数值。"));
                return true;
            }
            length = BridgeDesignPrecision.OneDecimal(length);
            if (baseJoint)
            {
                var planner = new FaceToWallPreviewPlanning();
                var current = _provider.CaptureWallReplacementInput(Editor.CurrentSample, Editor.EffectivePlan);
                var rear = Upstream.FaceToWall.BaseInstallationSeamX + length;
                field.Complete(RefreshWallSpan(planner.SpanForBaseJoint(current, rear, Upstream.FaceToWall.OverlapLength)));
                return true;
            }
            var delta = boundaryDistance ? length - Editor.MeasureJoint(joint.Id).DistanceFromPrevious :
                ordinaryLength ? length - section.Length : section.Length - length;
            field.Complete(RefreshWallBoundary(delta));
            return true;
        }

        private BridgeEditResult RefreshWallBoundary(double delta)
        {
            if (PlanningBusy || _wallPlanningInput == null || Upstream?.FaceToWall == null)
                return new BridgeEditResult(false, "方案正在刷新或规划输入缺失，不能调整断点。");
            var planner = new FaceToWallPreviewPlanning();
            var current = _provider.CaptureWallReplacementInput(Editor.CurrentSample, Editor.EffectivePlan);
            return RefreshWallSpan(planner.SpecialSpan(current, Upstream.FaceToWall.OverlapLength) - delta);
        }
        public Task PlanningCompletion { get; private set; } = Task.CompletedTask;
        public event EventHandler ModelPreviewReady;
        public bool PlanningBusy
        {
            get => _planningBusy;
            private set { _planningBusy = value; RaisePropertyChanged(nameof(PlanningBusy)); RaisePropertyChanged(nameof(IsEditingEnabled)); RaisePropertyChanged(nameof(CanSwitchWallSolution)); PendingChanged(); }
        }
        public IReadOnlyList<string> WallSolutionNames => Array.AsReadOnly(_wallSolutions.Select((s, i) =>
            "方案 " + (i + 1) + (s.IsRelaxedClearanceSolution ? "（绝缘距离不足）" : "")).ToArray());
        public bool HasWallSolutions => _wallSolutions.Count > 0;
        public bool CanSwitchWallSolution => HasWallSolutions && !PlanningBusy && !GenerationBusy && !HasPendingInput && !NativePreviewBusy &&
            Editor.CurrentSample?.ReadbackBaseline?.RequiresWallCopperReplan != true;
        public int SelectedWallSolutionIndex
        {
            get => _wallSelectedIndex;
            set
            {
                if (value == _wallSelectedIndex) return;
                if (!CanSwitchWallSolution || value < 0 || value >= _wallSolutions.Count)
                { RaisePropertyChanged(nameof(SelectedWallSolutionIndex)); return; }
                try { LoadWallSolution(Upstream, value); }
                catch (Exception error)
                {
                    FaceToWallPreviewTrace.Error("切换面对墙 Worker 方案失败", error);
                    Message = "方案切换失败，当前草稿保留：" + error.Message;
                    RaisePropertyChanged(nameof(SelectedWallSolutionIndex));
                }
            }
        }
        private void CancelWallPlanning()
        {
            var cancellation = _wallCancellation;
            _wallCancellation = null;
            cancellation?.Cancel();
            // 异步任务在 finally 中释放自己的 token；重读不会销毁仍被 Worker 使用的句柄。
            _wallSolutions = Array.Empty<LinkageRouteWorkerSolutionDto>(); _wallRequest = null; _wallPlanningInput = null; _wallSelectedIndex = -1;
            _wallWorkerOptions = null;
            _wallSyncedState = null;
            _wallBoundaryEdited = false; _wallCommittedSpecialStartText = null; WallSpecialStartText = null;
            PlanningBusy = false;
            NotifyWallSolutions();
        }
        private void StartWallPlanning(BridgeUpstreamSnapshot snapshot, double? specialStartX = null)
        {
            var cancellation = new CancellationTokenSource();
            _wallCancellation = cancellation;
            PlanningBusy = true; GenerationBlocked = true;
            Message = "正在计算特殊段所需最小长度，向上取整到10mm后确定普通段分界。";
            PlanningCompletion = PlanWallAsync(snapshot, cancellation, specialStartX);
        }

        private bool TryRefreshWallClearance(BridgeDesignFieldVM field)
        {
            if (field.Field != DesignField.WallVerticalClearance || Editor.CurrentSample?.ScenarioKey != "FaceToWall" ||
                !Editor.CurrentSample.Routes.Any(r => r.IsPhysicalGeometry)) return false;
            if (PlanningBusy || Upstream?.FaceToWall == null ||
                !double.TryParse(field.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
                double.IsNaN(value) || double.IsInfinity(value))
            { field.Complete(new BridgeEditResult(false, "上下墙距输入无效或正在规划，修改未提交。")); return true; }
            value = BridgeDesignPrecision.OneDecimal(value);
            if (Math.Abs(value - Editor.Find(field.ObjectId).Value(field.Field)) < .01)
            { field.Complete(new BridgeEditResult(true)); return true; }
            var validation = Editor.CheckWallVerticalClearance(field.ObjectId, value);
            if (!validation.Success) { field.Complete(validation); Run(validation); return true; }
            var cancellation = new CancellationTokenSource();
            _wallCancellation = cancellation;
            PlanningBusy = true;
            Message = "正在按新底板支承面重规划铜排，绝缘子高度保持不变。";
            PlanningCompletion = ReplanWallClearanceAsync(field, value, Upstream, Editor.CurrentSample,
                Editor.EffectivePlan.ToArray(), Editor.Revision, cancellation);
            return true;
        }

        private async Task ReplanWallClearanceAsync(BridgeDesignFieldVM field, double value, BridgeUpstreamSnapshot snapshot,
            BridgeDesignSample original, IReadOnlyList<BridgeDesignObject> objects, long revision, CancellationTokenSource cancellation)
        {
            try
            {
                var planning = _provider.CreateWallClearanceInput(snapshot, original, objects, value, _wallWorkerOptions);
                if (_wallWorker == null) _wallWorker = new BusbarAlgorithmWorkerClient(ResolveWorkerAuthorization);
                var result = await _wallWorker.SolveProjectAsync(planning.Request, null, cancellation.Token, snapshot.ModelPath);
                if (!IsCurrentWallPlanning(snapshot, cancellation)) return;
                if (Editor.Revision != revision)
                { field.Complete(new BridgeEditResult(false, "草稿已变化，本次铜排重规划未提交。")); return; }
                var candidates = new FaceToWallWorkerSolutions().ReadSuccessful(result, planning.Request);
                BridgeEditResult committed = null;
                foreach (var candidate in candidates.Where(s => !s.IsRelaxedClearanceSolution))
                {
                    var geometry = _provider.CreateWallClearancePreview(snapshot, original, objects, planning, candidate);
                    committed = Editor.CommitWallVerticalClearance(field.ObjectId, value, geometry);
                    if (!committed.Success) continue;
                    _wallPlanningInput = planning; _wallRequest = planning.Request;
                    _wallSolutions = new[] { candidate }; _wallSelectedIndex = 0; _wallCurrentIsGenerated = false;
                    break;
                }
                committed = committed ?? new BridgeEditResult(false, "没有满足严格绝缘和支撑要求的上下墙距方案，原草稿保留。");
                field.Complete(committed); Run(committed);
                if (committed.Success) ModelPreviewReady?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                if (ReferenceEquals(_wallCancellation, cancellation))
                { field.Complete(new BridgeEditResult(false, "铜排重规划已取消，原草稿保留。")); Message = field.Error; }
            }
            catch (Exception error)
            {
                FaceToWallPreviewTrace.Error("上下墙距铜排重规划失败", error);
                if (ReferenceEquals(_wallCancellation, cancellation))
                { var failure = new BridgeEditResult(false, "上下墙距修改未提交：" + error.Message); field.Complete(failure); Run(failure); }
            }
            finally
            {
                if (ReferenceEquals(_wallCancellation, cancellation))
                { _wallCancellation = null; PlanningBusy = false; NotifyWallSolutions(); }
                cancellation.Dispose();
            }
        }
        private async Task PlanWallAsync(BridgeUpstreamSnapshot snapshot, CancellationTokenSource cancellation, double? specialStartX)
        {
            var previous = Editor.CurrentSample == null ? null : Editor.CaptureSavedSample();
            var revision = Editor.Revision;
            try
            {
                // COM 反读已在宿主线程完成；这里仅传递值快照给独立算法进程。
                if (snapshot.FaceToWall == null)
                    throw new InvalidOperationException("面对墙固定接口反读不完整，请查看设计输入中的具体缺项及来源后重读。");
                var options = _wallWorkerOptions;
                var planning = new FaceToWallPreviewPlanning().CreateInputWithOptions(snapshot.FaceToWall, null, specialStartX, options);
                var request = planning.Request;
                if (_wallWorker == null) _wallWorker = new BusbarAlgorithmWorkerClient(ResolveWorkerAuthorization);
                var progress = new Progress<BusbarAlgorithmWorkerUpdate>(update =>
                {
                    if (IsCurrentWallPlanning(snapshot, cancellation) && update.Progress != null)
                        Message = "面对墙 Worker：" + update.Progress.Message;
                });
                var result = await _wallWorker.SolveProjectAsync(request, progress, cancellation.Token, snapshot.ModelPath);
                if (!IsCurrentWallPlanning(snapshot, cancellation)) return;
                if (Editor.Revision != revision)
                { Message = "草稿已变化，本次特殊段重规划未提交。"; return; }
                var completed = await Task.Run(() => new FaceToWallMinimumSpanPlanning().Complete(snapshot.FaceToWall,
                    planning, result, options, (finalPlanning, solution) =>
                {
                    var candidate = _provider.CreateFaceToWallPreview(snapshot, finalPlanning.Request, solution, finalPlanning);
                    return previous?.ReadbackBaseline?.RequiresCabinetSynchronization == true
                        ? _provider.CompleteFaceToWallCabinetReplan(snapshot, previous, candidate) : candidate;
                }, cancellation.Token), cancellation.Token);
                if (!IsCurrentWallPlanning(snapshot, cancellation)) return;
                if (Editor.Revision != revision)
                { Message = "草稿已变化，本次特殊段最小长度规划未提交。"; return; }
                planning = completed.Planning; request = planning.Request;
                var candidates = completed.Candidates;
                var solutions = candidates.Select(c => c.Solution).ToArray();
                FaceToWallPreviewTrace.Info("面对墙Worker结果筛选", "返回记录=" + (result.RankedSolutions?.Count ?? 0) +
                    "，通过铜排及安装校验的方案=" + solutions.Length + "；保留Worker原排序。");
                var sample = candidates[0].Sample;
                sample.WallPlanningState.RuntimeCandidates = solutions;
                var committed = CommitWallReplanning(sample, revision, specialStartX.HasValue);
                if (!committed.Success) { Message = committed.Message; return; }
                _wallRequest = request; _wallPlanningInput = planning; _wallSolutions = solutions;
                _wallBoundaryEdited = specialStartX.HasValue;
                LoadWallSampleStatus(sample, 0);
                WallSpecialStartText = (planning.SpecialStartX - snapshot.FaceToWall.Cabinet.Opening.X)
                    .ToString("0.###", CultureInfo.CurrentCulture);
                _wallCommittedSpecialStartText = WallSpecialStartText;
                RaisePropertyChanged(nameof(HasUnsavedChanges));
            }
            catch (OperationCanceledException)
            {
                if (IsCurrentWallPlanning(snapshot, cancellation)) Message = "面对墙规划已取消。";
            }
            catch (Exception error)
            {
                if (IsCurrentWallPlanning(snapshot, cancellation))
                {
                    FaceToWallPreviewTrace.Error("面对墙真实预览规划失败", error);
                    Message = "面对墙预览未生成：" + error.Message;
                    RefreshFields();
                }
            }
            finally
            {
                if (ReferenceEquals(_wallCancellation, cancellation))
                { _wallCancellation = null; PlanningBusy = false; NotifyWallSolutions(); }
                cancellation.Dispose();
            }
        }
        private bool IsCurrentWallPlanning(BridgeUpstreamSnapshot snapshot, CancellationTokenSource cancellation) =>
            !_wallDisposed && !cancellation.IsCancellationRequested && ReferenceEquals(Upstream, snapshot) &&
            ReferenceEquals(_wallCancellation, cancellation);
        private void LoadWallSolution(BridgeUpstreamSnapshot snapshot, int index)
        {
            // 完整校验成功后再替换编辑器，失败不能留下半套对象。
            var sample = _provider.ReplaceWallSpecialScheme(snapshot, Editor.CurrentSample, Editor.EffectivePlan,
                _provider.CaptureWallReplacementInput(Editor.CurrentSample, Editor.EffectivePlan), _wallSolutions[index]);
            sample.WallPlanningState.RuntimeCandidates = _wallSolutions;
            sample.WallPlanningState.RuntimeSelectedIndex = index;
            var result = Editor.ReplaceWallSpecialScheme(sample);
            if (!result.Success) throw new InvalidOperationException(result.Message);
            _wallSelectedIndex = index;
            Message = "特殊铜排方案已替换到草稿，普通段和基架编辑保留。";
            NotifyWallSolutions(); ModelPreviewReady?.Invoke(this, EventArgs.Empty);
        }
        internal BridgeEditResult CommitWallReplanning(BridgeDesignSample sample, long revision, bool replanSupports = false)
        {
            if (sample?.ScenarioKey != "FaceToWall") return new BridgeEditResult(false, "特殊段重规划未返回有效的面对墙方案，当前草稿保留。");
            if (Editor.Revision != revision) return new BridgeEditResult(false, "草稿已变化，本次特殊段重规划未提交。");
            if (Editor.CurrentSample == null)
            {
                if (ReadHangerContext != null) sample = ReadHangerContext(Upstream, sample);
                Editor.Load(sample);
                return new BridgeEditResult(true);
            }
            // 特殊段入口移动后，旧分节可能截入横移/升高区，必须同时提交按新铜排规划的支撑。
            return Editor.ReplaceWallSpecialScheme(sample,
                !replanSupports && Editor.CurrentSample.ReadbackBaseline?.RequiresCabinetSynchronization != true,
                renewSupportBaseline: replanSupports);
        }
        private void LoadWallSampleStatus(BridgeDesignSample sample, int index)
        {
            _wallCurrentIsGenerated = false;
            _selectedFrameIds.Clear(); _selectedCopperSectionId = null; _sameSource = false;
            _loadState = "面对墙真实方案已规划";
            _wallSelectedIndex = index;
            GenerationBlocked = !sample.SupportsFormalGeneration;
            Message = "面对墙真实预览已载入（方案 " + (index + 1) + "/" + _wallSolutions.Count + "）；可按有效草稿生成正式模型。";
            NotifyWallSolutions();
            ModelPreviewReady?.Invoke(this, EventArgs.Empty);
            FitRequested?.Invoke(this, EventArgs.Empty);
        }
        private void NotifyWallSolutions()
        {
            RaisePropertyChanged(nameof(WallSolutionNames)); RaisePropertyChanged(nameof(HasWallSolutions));
            RaisePropertyChanged(nameof(SelectedWallSolutionIndex)); RaisePropertyChanged(nameof(CanSwitchWallSolution));
        }
    }
}
