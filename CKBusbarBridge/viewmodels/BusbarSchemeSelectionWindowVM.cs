using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CKBusbarBridge.AlgorithmWorker;
using CKBusbarBridge.Prototype;
using CKBusbarBridge.Services;
using CKBusbarBridgePreview;
using RD.WebPreview.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CKBusbarBridge.viewmodels
{
    internal sealed class BusbarSchemeSelectionWindowVM : NotificationObject, IDisposable
    {
        private readonly BridgeUpstreamSnapshot _source;
        private readonly double _specialStart;
        private readonly BusbarAlgorithmWorkerClient _worker;
        private CancellationTokenSource _cancellation;
        private bool _busy, _disposed;
        private string _status;
        private string _previewConflict;
        private BusbarAlgorithmSchemeItem _selected;
        private readonly FaceToWallPlanningInput _baselinePlanning;
        private FaceToWallPlanningInput _searchPlanning;
        private readonly LinkageRouteWorkerSolutionDto _baseline;
        private readonly string _baselineKey, _baselineLabel;
        private readonly Func<FaceToWallPlanningInput, LinkageRouteWorkerSolutionDto, bool, WebPreviewSceneDto> _preview;
        private readonly Func<LinkageRouteWorkerRequestDto, IProgress<BusbarAlgorithmWorkerUpdate>, CancellationToken,
            Task<LinkageRouteWorkerComparisonResultDto>> _solve;
        internal long CapturedRevision { get; }
        internal BridgeUpstreamSnapshot Snapshot { get; private set; }
        internal FaceToWallPlanningInput Planning => _selected?.IsCurrentScheme == true ? _baselinePlanning : _searchPlanning;
        internal IReadOnlyList<LinkageRouteWorkerSolutionDto> Solutions => AlgorithmSchemes.Select(s => s.WorkerSolution).ToArray();
        public ObservableCollection<BusbarAlgorithmSchemeItem> AlgorithmSchemes { get; } = new ObservableCollection<BusbarAlgorithmSchemeItem>();
        public BusbarAlgorithmParameterInput AlgorithmParameterInput { get; } = new BusbarAlgorithmParameterInput();
        public ICommand GenerateAlgorithmSchemesCommand { get; }
        public ICommand StopAlgorithmSchemesCommand { get; }
        public ICommand PreviewSelectedSchemeCommand { get; }
        public event EventHandler SelectedSchemeChanged;
        public event EventHandler PreviewRequested;
        public bool CanSwapPhase => false;
        public bool IsInsulationClearanceReadOnly => true;
        public bool IsAlgorithmIdle => !_busy;
        public bool CanAccept => !_busy && !_disposed && _selected != null &&
            Planning.IsPhaseSwitched == _source.FaceToWall.IsPhaseSwitched;
        public Visibility EmptyAlgorithmSchemeVisibility => AlgorithmSchemes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        public string AlgorithmSectionCaptionText => "保留当前方案、普通段和基架；固定入口刷新特殊铜排，是否换相：" +
            (_source.FaceToWall.IsPhaseSwitched ? "是（两侧端位交换）" : "否") + "；墙端采用直头夹接。";
        public bool UseSolidWorksViewportPreview { get; set; }
        public string PreviewSelectedSchemeToolTip => UseSolidWorksViewportPreview ? "在SolidWorks视口查看选中方案" : "在Web预览中查看选中方案";
        public string SelectedSchemeDetailText => _selected?.CenterlineSummary +
            (string.IsNullOrWhiteSpace(_previewConflict) ? "" : "；支撑冲突：" + _previewConflict);
        public bool IsAlgorithmSchemeStatusError { get; private set; }
        public string AlgorithmSchemeStatusText => _status;
        public BusbarAlgorithmSchemeItem SelectedAlgorithmScheme
        {
            get => _selected;
            set { _selected = value; _previewConflict = null; Changed(); SelectedSchemeChanged?.Invoke(this, EventArgs.Empty); }
        }

        internal BusbarSchemeSelectionWindowVM(BridgeUpstreamSnapshot source, FaceToWallPlanningInput planning,
            IReadOnlyList<LinkageRouteWorkerSolutionDto> solutions, int selectedIndex, Func<string> resolveWorkerAuthorization = null,
            bool currentIsGenerated = false, long capturedRevision = 0,
            Func<FaceToWallPlanningInput, LinkageRouteWorkerSolutionDto, bool, WebPreviewSceneDto> preview = null,
            Func<LinkageRouteWorkerRequestDto, IProgress<BusbarAlgorithmWorkerUpdate>, CancellationToken,
                Task<LinkageRouteWorkerComparisonResultDto>> solve = null, bool baselineMatchesGeometry = true)
        {
            _worker = new BusbarAlgorithmWorkerClient(resolveWorkerAuthorization);
            _source = source; Snapshot = source; _baselinePlanning = planning; _searchPlanning = planning;
            _specialStart = planning.SpecialStartX; CapturedRevision = capturedRevision; _preview = preview;
            _solve = solve ?? ((requestInput, progress, token) =>
                _worker.SolveProjectAsync(requestInput, progress, token, _source.ModelPath));
            _baseline = (selectedIndex >= 0 ? solutions.ElementAtOrDefault(selectedIndex) : null) ?? solutions.FirstOrDefault();
            _baselineKey = _baseline == null ? null : baselineMatchesGeometry ? GeometryKey(_baseline) : "edited-baseline";
            _baselineLabel = currentIsGenerated ? "当前已生成" : "当前草稿";
            var request = planning.Request;
            AlgorithmParameterInput.InsulationClearanceText = Number(source.FaceToWall.Clearance);
            AlgorithmParameterInput.SwapPhaseAAndC = source.FaceToWall.IsPhaseSwitched;
            AlgorithmParameterInput.MinimumSegmentLengthText = Number(request.MinimumSegmentLength);
            AlgorithmParameterInput.BendingRadiusText = Number(request.BendingRadius);
            AlgorithmParameterInput.BendAllowanceText = Number(request.BendAllowance);
            GenerateAlgorithmSchemesCommand = new BridgeDesignCommand(async () => await GenerateAsync(), () => !_busy && !_disposed);
            StopAlgorithmSchemesCommand = new BridgeDesignCommand(() => _cancellation?.Cancel(), () => _busy);
            PreviewSelectedSchemeCommand = new BridgeDesignCommand(() => PreviewRequested?.Invoke(this, EventArgs.Empty), () => _selected != null);
            Publish(solutions, selectedIndex);
            _status = "选择方案查看Web预览，确认后载入母线桥设计。";
            AlgorithmParameterInput.PropertyChanged += ParametersChanged;
        }

        private string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        private void ParametersChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            Publish(Array.Empty<LinkageRouteWorkerSolutionDto>());
            SetStatus("工艺参数已改变，当前方案保留；请刷新候选方案。", false);
        }
        internal string GeometryKey(LinkageRouteWorkerSolutionDto solution)
        {
            // 方案序号、算法名和拓扑标签不是加工几何身份。
            var data = JArray.FromObject((solution.GroupSolutions ?? new List<LinkageRouteWorkerGroupSolutionDto>())
                .OrderBy(group => group.GroupKey, StringComparer.Ordinal).Select(group => new {
                    group.GroupKey, Members = (group.StackMembers ?? new List<LinkageRouteWorkerStackedMemberDto>())
                        .OrderBy(member => member.MemberIndex).Select(member => member.PlanarParts).ToArray() }));
            foreach (var part in data.Descendants().OfType<JObject>().Where(part => part["PointList"] != null))
                part.Remove("GroupKey");
            foreach (var value in data.Descendants().OfType<JValue>().Where(value => value.Type == JTokenType.Float))
                value.Value = Math.Round(Convert.ToDouble(value.Value, CultureInfo.InvariantCulture), 4);
            return data.ToString(Formatting.None);
        }
        private void Publish(IEnumerable<LinkageRouteWorkerSolutionDto> solutions, int index = -1)
        {
            var selectedKey = _selected == null ? null : _selected.IsCurrentScheme ? _baselineKey : GeometryKey(_selected.WorkerSolution);
            var candidates = solutions.ToArray();
            AlgorithmSchemes.Clear();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (_baseline != null)
            {
                seen.Add(_baselineKey);
                var item = BusbarAlgorithmSchemeItem.Create(_baseline); item.IsCurrentScheme = true;
                item.SchemeStatusText = _baselineLabel + (candidates.Any(candidate => GeometryKey(candidate) == _baselineKey) ? "（同形）" : "");
                item.SchemeSequence = 1; AlgorithmSchemes.Add(item);
            }
            foreach (var solution in candidates)
            {
                if (!seen.Add(GeometryKey(solution))) continue;
                var item = BusbarAlgorithmSchemeItem.Create(solution); item.SchemeSequence = AlgorithmSchemes.Count + 1;
                item.SchemeStatusText = "替换候选"; AlgorithmSchemes.Add(item);
            }
            SelectedAlgorithmScheme = AlgorithmSchemes.FirstOrDefault(item =>
                (item.IsCurrentScheme ? _baselineKey : GeometryKey(item.WorkerSolution)) == selectedKey) ?? AlgorithmSchemes.FirstOrDefault();
        }
        internal void PublishSearchResult(LinkageRouteWorkerComparisonResultDto result, FaceToWallPlanningInput planning)
        {
            if (result == null) throw new InvalidOperationException("算法未返回结果，当前方案保留。");
            if (result.IsCancelled) throw new OperationCanceledException();
            var records = result.RankedSolutions ?? new List<LinkageRouteWorkerSolutionDto>();
            var accepted = records.Where(solution => solution != null && solution.IsSuccess && !solution.IsCancelled &&
                !solution.IsRelaxedClearanceSolution && new FaceToWallWorkerSolutions().HasStraightWallLap(planning.Request, solution)).ToArray();
            _searchPlanning = planning; Publish(accepted);
            var unique = accepted.Select(GeometryKey).Distinct(StringComparer.Ordinal).Count();
            var alternatives = AlgorithmSchemes.Count(item => !item.IsCurrentScheme);
            SetStatus("搜索返回 " + records.Count + " 项，规则筛除 " + (records.Count - accepted.Length) +
                " 项，几何重复 " + (accepted.Length - unique) + " 项；可替换 " + alternatives +
                " 个方案。" + (alternatives == 0 ? "当前约束下未发现其他合格方案，当前方案保留。" : "选择候选预览后替换特殊段。"), false);
        }
        internal async Task GenerateAsync()
        {
            if (!AlgorithmParameterInput.TryCreateWorkerRequestOptions(out var options, out var error))
            { SetStatus(error, true); return; }
            // 面对墙沿用柜体驱动保存的参数，方案窗口只刷新候选。
            options.InsulationClearance = _source.FaceToWall.Clearance;
            options.SwapPhaseAAndCAtEnd = _source.FaceToWall.IsPhaseSwitched;
            var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
            _busy = true; Publish(Array.Empty<LinkageRouteWorkerSolutionDto>()); Changed();
            try
            {
                var input = _source.FaceToWall;
                var geometry = new FaceToWallUpstreamGeometry(input.Coordinates, input.Cabinet, input.WallConductors,
                    input.FixedObjects, input.WallOpeningCorners, input.Position, input.PreviewReferenceComponentName,
                    input.RootCount, options.InsulationClearance, input.OverlapLength, input.WallCabinetEccentricity, input.ModelClearance,
                    input.AppliedWallVerticalClearance, input.WallInstallation, input.IsPhaseSwitched);
                var planning = new FaceToWallPreviewPlanning().CreateInputWithOptions(geometry, null, _specialStart,
                    options, _baselinePlanning.PrefixGroups);
                Snapshot = _source; _searchPlanning = planning;
                var progress = new Progress<BusbarAlgorithmWorkerUpdate>(update =>
                {
                    if (_disposed || cancellation.IsCancellationRequested || !ReferenceEquals(_cancellation, cancellation)) return;
                    if (update.PublishedSolution != null && update.PublishedSolution.IsSuccess && !update.PublishedSolution.IsRelaxedClearanceSolution &&
                        new FaceToWallWorkerSolutions().HasStraightWallLap(planning.Request, update.PublishedSolution))
                    {
                        var published = AlgorithmSchemes.Where(item => !item.IsCurrentScheme).Select(item => item.WorkerSolution)
                            .Concat(new[] { update.PublishedSolution }).ToArray();
                        Publish(published);
                    }
                    SetStatus(update.Progress?.Message ?? "正在搜索面对墙方案…", false);
                });
                var result = await _solve(planning.Request, progress, cancellation.Token);
                if (_disposed) return;
                PublishSearchResult(result, planning);
            }
            catch (OperationCanceledException) { if (!_disposed) SetStatus("已停止搜索；当前方案和已发布的合格候选保留，草稿未改变。", false); }
            catch (Exception ex)
            { FaceToWallPreviewTrace.Error("独立方案窗口生成失败", ex); if (!_disposed) SetStatus(ex.Message, true); }
            finally { _busy = false; if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null; cancellation.Dispose(); Changed(); }
        }
        internal WebPreviewSceneDto CreateWebScene()
        {
            if (_selected == null) throw new InvalidOperationException("请选择一个方案。");
            if (_preview != null)
            {
                var scene = _preview(Planning, _selected.WorkerSolution, _selected.IsCurrentScheme);
                _previewConflict = scene.Metadata.TryGetValue("supportConflicts", out var conflicts) ? conflicts : null;
                RaisePropertyChanged(nameof(SelectedSchemeDetailText));
                return scene;
            }
            return BusbarAlgorithmWebPreviewSceneFactory.Create(Planning.Request, _selected.WorkerSolution,
                _selected.SchemeSequence, _selected.SolutionKey, true, BusbarBridgeAxisSetting.CreateDefault());
        }
        internal void SetStatus(string message, bool error)
        { _status = message; IsAlgorithmSchemeStatusError = error; Changed(); }
        private void Changed()
        {
            foreach (var name in new[] { nameof(IsAlgorithmIdle), nameof(CanAccept), nameof(SelectedAlgorithmScheme),
                nameof(SelectedSchemeDetailText), nameof(EmptyAlgorithmSchemeVisibility), nameof(AlgorithmSchemeStatusText), nameof(IsAlgorithmSchemeStatusError) })
                RaisePropertyChanged(name);
            CommandManager.InvalidateRequerySuggested();
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true; AlgorithmParameterInput.PropertyChanged -= ParametersChanged;
            _cancellation?.Cancel(); _worker.Dispose();
        }
    }
}
