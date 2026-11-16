using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CKBusbarBridge.AlgorithmWorker;
using CKBusbarBridge.Prototype;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.Services
{
    internal sealed class FaceToWallMinimumSpanPlan
    {
        internal FaceToWallPlanningInput Planning { get; }
        internal IReadOnlyList<FaceToWallPreviewCandidate> Candidates { get; }
        internal FaceToWallMinimumSpanPlan(FaceToWallPlanningInput planning, IReadOnlyList<FaceToWallPreviewCandidate> candidates)
        { Planning = planning; Candidates = candidates; }
    }

    internal sealed class FaceToWallMinimumSpanPlanning
    {
        internal FaceToWallMinimumSpanPlan Complete(FaceToWallUpstreamGeometry upstream, FaceToWallPlanningInput search,
            LinkageRouteWorkerComparisonResultDto result, BusbarAlgorithmWorkerRequestOptions options,
            Func<FaceToWallPlanningInput, LinkageRouteWorkerSolutionDto, BridgeDesignSample> createSample,
            CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var rules = new FaceToWallWorkerSolutions();
            if (search.RequestedSpecialSpan.HasValue)
                return new FaceToWallExactSpanPlanning().Complete(upstream, search, result, createSample, cancellation);
            if (!search.RequiresMinimumSpan)
                return new FaceToWallMinimumSpanPlan(search, rules.PlanSuccessful(result, search.Request,
                    solution => createSample(search, solution)));
            var layouts = new List<Layout>();
            var failures = new List<string>();
            IReadOnlyList<LinkageRouteWorkerSolutionDto> successful;
            try { successful = rules.ReadSuccessful(result, search.Request); }
            catch (InvalidOperationException error)
            {
                throw new InvalidOperationException("尚未确定面对墙特殊段最小长度：Worker没有提供同时满足墙端直头及原绝缘距离的完整候选。" + error.Message, error);
            }
            foreach (var solution in successful.Where(s => !s.IsRelaxedClearanceSolution))
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    var minimum = new FaceToWallMinimumSpan(search.Request, solution, upstream.OverlapLength, cancellation);
                    layouts.Add(new Layout(solution, minimum));
                    FaceToWallPreviewTrace.Info("面对墙候选" + solution.SchemeIndex + "最小长度约束",
                        FormattableString.Invariant($"最小长度={minimum.MinimumLength:0.######}mm，控制约束=") +
                        string.Join(" → ", minimum.ControllingConstraints));
                }
                catch (InvalidOperationException error)
                {
                    FaceToWallPreviewTrace.Error("面对墙候选" + solution.SchemeIndex + "最小长度计算失败", error);
                    failures.Add("候选" + solution.SchemeIndex + "：" + error.Message);
                }
            }
            var planner = new FaceToWallPreviewPlanning();
            var available = planner.SpecialSpan(search, upstream.OverlapLength);
            var lengths = layouts.Select(l => FaceToWallMinimumSpan.RoundUpToTen(l.Minimum.MinimumLength))
                .Distinct().OrderBy(length => length).ToArray();
            var attempts = lengths.Select(length => (Length: length, AdjustRisingSeam: false));
            if (upstream.TargetWallHeight - upstream.Cabinet.OpeningHeight > 1.0)
            {
                // 原有分节全部不能安装时，初次规划才尝试重新分配027和026的长度。
                attempts = attempts.Concat(lengths.Select(length => (Length: length, AdjustRisingSeam: true)));
            }
            foreach (var attempt in attempts)
            {
                cancellation.ThrowIfCancellationRequested();
                var length = attempt.Length;
                if (length > available + .01d)
                {
                    failures.Add(FormattableString.Invariant($"特殊段取整后需要{length:0.###}mm，项目最多可提供{available:0.###}mm。"));
                    continue;
                }
                try
                {
                    var planning = planner.CreateInputForSpecialSpan(upstream, search, length, options);
                    planning.RequestedSpecialSpan = null;
                    planning.AdjustRisingFrameSeam = attempt.AdjustRisingSeam;
                    var eligible = layouts.Where(l => Math.Abs(FaceToWallMinimumSpan.RoundUpToTen(l.Minimum.MinimumLength) - length) < .000001d).ToArray();
                    var compressed = new List<LinkageRouteWorkerSolutionDto>();
                    foreach (var layout in eligible)
                    {
                        try { compressed.Add(layout.Minimum.CreateSolution(length)); }
                        catch (InvalidOperationException error)
                        {
                            FaceToWallPreviewTrace.Error("面对墙候选" + layout.Source.SchemeIndex + "取整布局校验失败", error);
                            failures.Add("候选" + layout.Source.SchemeIndex + "：" + error.Message);
                        }
                    }
                    planning.MinimumSpecialSpan = eligible.Min(l => l.Minimum.MinimumLength);
                    Func<LinkageRouteWorkerSolutionDto, BridgeDesignSample> create = solution =>
                    {
                        var sample = createSample(planning, solution);
                        var actual = new PrototypeBusbarBridgeDesignProvider().CaptureWallReplacementInput(sample, sample.Objects);
                        if (Math.Abs(planner.SpecialSpan(actual, upstream.OverlapLength) - length) > .00001d)
                            throw new InvalidOperationException("后续支撑或普通排优化改变了取整后的特殊段长度，不能接纳该候选。");
                        return sample;
                    };
                    var candidates = rules.PlanSuccessful(new LinkageRouteWorkerComparisonResultDto { RankedSolutions = compressed },
                        planning.Request, create);
                    var acceptedMinimum = eligible.Where(l => candidates.Any(c => c.Solution.SchemeIndex == l.Source.SchemeIndex &&
                        c.Solution.AlgorithmKind == l.Source.AlgorithmKind)).Min(l => l.Minimum.MinimumLength);
                    if (Math.Abs(acceptedMinimum - planning.MinimumSpecialSpan.Value) > .000001d)
                    {
                        planning.MinimumSpecialSpan = acceptedMinimum;
                        candidates = rules.PlanSuccessful(new LinkageRouteWorkerComparisonResultDto {
                            RankedSolutions = candidates.Select(c => c.Solution).ToArray() }, planning.Request,
                            create);
                    }
                    FaceToWallPreviewTrace.Info("面对墙特殊段最小长度",
                        FormattableString.Invariant($"范围={FaceToWallMinimumSpan.Scope}，最小长度={planning.MinimumSpecialSpan:0.######}mm，取整采用={length:0.###}mm，最大可用={available:0.###}mm，普通段分界={planning.SpecialStartX:0.######}，通过完整校验={candidates.Count}，换相={upstream.IsPhaseSwitched}"));
                    return new FaceToWallMinimumSpanPlan(planning, candidates);
                }
                catch (InvalidOperationException error)
                {
                    FaceToWallPreviewTrace.Error("面对墙取整长度" + length + "完整规划失败", error);
                    failures.Add(error.Message);
                }
            }
            throw new InvalidOperationException("尚未确定面对墙特殊段可用的最小长度：" +
                (failures.Count == 0 ? "Worker未返回可用于最小布局计算的严格绝缘候选。" : string.Join("；", failures.Distinct())));
        }

        private sealed class Layout
        {
            internal LinkageRouteWorkerSolutionDto Source { get; }
            internal FaceToWallMinimumSpan Minimum { get; }
            internal Layout(LinkageRouteWorkerSolutionDto source, FaceToWallMinimumSpan minimum)
            { Source = source; Minimum = minimum; }
        }
    }
}
