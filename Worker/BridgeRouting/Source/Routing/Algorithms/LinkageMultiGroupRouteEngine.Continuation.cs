using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;

namespace RouteLinkBar.Algorithm.Routing.Algorithms
{
    internal static partial class LinkageMultiGroupRouteEngine
    {
        private static IReadOnlyList<LinkageRouteCandidatePool> BuildCandidatePoolsContinuously(
            SearchContext searchContext, IReadOnlyList<LinkageRouteCandidatePool> retainedPools,
            IReadOnlyList<LinkageRouteSolid> fixedSolids)
        {
            var request = searchContext.RouteRequest;
            var token = searchContext.MainSearchCancellationToken;
            var currentPools = retainedPools;
            var lastCombinationCounts = string.Empty;
            var combinationState = new ContinuationCombinationState();
            Task combinationRound = null;
            var nextProgressMilliseconds = 0L;
            const int familyCount = 4;
            var turnIndex = 0;
            using (var continuation = new LinkageRoutePoolContinuation(request.Groups.Count * familyCount,
                (workIndex, checkpoint, buildToken) =>
                {
                    var group = request.Groups[workIndex / familyCount];
                    var expandedSeeds = new HashSet<LinkageRouteCandidate>();
                    Action<LinkageRouteCandidatePool> deliverSeed = pool =>
                    {
                        checkpoint(pool);
                        var seedDelta = new LinkageRouteCandidatePool { GroupRequest = group };
                        foreach (var candidate in pool.ValidCandidates)
                            if (expandedSeeds.Add(candidate)) seedDelta.ValidCandidates.Add(candidate);
                        if (seedDelta.ValidCandidates.Count > 0)
                            LinkageRouteCandidateFactory.BuildSectionPlacementSiblingSeedPool(request, group,
                                seedDelta, fixedSolids, buildToken, checkpoint);
                    };
                    switch (workIndex % familyCount)
                    {
                        case 0:
                            return LinkageRouteCandidateFactory.BuildPhysicalLayerSeedPool(
                                request, group, fixedSolids, buildToken, deliverSeed);
                        case 1:
                        case 2:
                            return LinkageRouteCandidateFactory.BuildContinuationSeedPool(
                                request, group, fixedSolids, workIndex % familyCount == 2,
                                buildToken, deliverSeed);
                        default:
                            return LinkageRouteCandidateFactory.BuildPool(request, group, fixedSolids, true,
                                LinkageRouteTraversalDirection.Forward, buildToken, deliverSeed);
                    }
                }, token, buildToken =>
                {
                    if (!searchContext.TryEnterMainSearchWorker(buildToken))
                        throw new OperationCanceledException(buildToken);
                }, searchContext.ExitMainSearchWorker))
            {
                try
                {
                    while (!token.IsCancellationRequested && continuation.HasPending)
                    {
                        // A completed round is observed here so its faults keep the
                        // original propagation; cancellation is handled below.
                        if (combinationRound?.IsCompleted == true)
                        {
                            combinationRound.GetAwaiter().GetResult();
                            combinationRound = null;
                        }

                        // A slow conversion must not form a barrier in front of
                        // another family's completed, independently validated seed.
                        var workSnapshots = continuation.CapturePools();
                        var snapshots = MergeContinuationFamilies(request, workSnapshots, familyCount);
                        currentPools = MergeFiniteAndFullCandidatePools(request,
                            retainedPools, snapshots, preserveCompletedCandidates: true);
                        var emptyIndices = Enumerable.Range(0, request.Groups.Count)
                            .Where(index => currentPools[index] == null || currentPools[index].ValidCandidates.Count == 0)
                            .ToArray();
                        var counts = string.Join("/", currentPools.Select(pool => pool?.ValidCandidates.Count ?? 0));
                        if (combinationRound == null && emptyIndices.Length == 0 &&
                            !searchContext.HasSuccessfulSolution &&
                            !string.Equals(lastCombinationCounts, counts, StringComparison.Ordinal))
                        {
                            lastCombinationCounts = counts;
                            ReportContinuationProgress(searchContext, currentPools, snapshots,
                                "空组已补齐，复用保留候选并组合；冲突按实际组回溯");
                            // 组合轮次与候选构建并行：构建线程不再停在检查点等待
                            // 组合时间片或深层修复结束。同一时刻只运行一轮，轮次
                            // 使用本次快照，组合参数与原同步调用完全相同。
                            var roundPools = currentPools;
                            combinationRound = Task.Run(() => RunContinuationCombinationRound(
                                searchContext, roundPools, combinationState));
                        }
                        if (searchContext.SearchStopwatch.ElapsedMilliseconds >= nextProgressMilliseconds)
                        {
                            ReportContinuationProgress(searchContext, currentPools, snapshots,
                                emptyIndices.Length > 0 ? "优先空组断点续搜" : "按冲突优先续搜，保留全部组候选");
                            nextProgressMilliseconds = searchContext.SearchStopwatch.ElapsedMilliseconds + 1000;
                        }
                        var conflictGroups = Volatile.Read(ref combinationState.ConflictGroupKeys);
                        var priorities = emptyIndices.Length > 0
                            ? emptyIndices
                            : Enumerable.Range(0, request.Groups.Count)
                                .OrderByDescending(index => conflictGroups.Contains(
                                    request.Groups[index].GroupKey, StringComparer.Ordinal))
                                .ToArray();
                        var workPriorities = CreateContinuationWorkPriorities(priorities, familyCount, turnIndex++);
                        // 组合轮次运行时构建只占一半名额，其余留给组合根分支；
                        // 两者仍共用同一主搜索信号量，总并行度不超过原配置。
                        var maximumParallelism = ResolveMaximumDegreeOfParallelism(request);
                        var buildConcurrency = combinationRound == null
                            ? maximumParallelism
                            : Math.Max(1, maximumParallelism / 2);
                        continuation.Advance(workPriorities, buildConcurrency);
                        // An exhausted empty pool cannot benefit from other
                        // group's work; finish remaining original families so
                        // the caller can report their exact failures normally.
                        if (!continuation.HasRunning)
                            continuation.Advance(Enumerable.Range(0, request.Groups.Count * familyCount).ToArray(),
                                buildConcurrency);
                        continuation.WaitForProgress();
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch
                {
                    // 续搜自身故障优先传播；仍须等本轮组合退出，避免其继续使用
                    // 已被调用方放弃的搜索上下文。
                    WaitContinuationCombinationRoundQuietly(combinationRound);
                    throw;
                }
                // 调用方随后会复用同一搜索上下文进入下一阶段，必须先等本轮
                // 组合结束；本轮故障照常抛出。
                CompleteContinuationCombinationRound(combinationRound, token);
                currentPools = MergeFiniteAndFullCandidatePools(request,
                    retainedPools, MergeContinuationFamilies(request, continuation.CapturePools(), familyCount),
                    preserveCompletedCandidates: true);
            }
            return currentPools;
        }

        private sealed class ContinuationCombinationState
        {
            internal string[] ConflictGroupKeys = Array.Empty<string>();
            internal int DeepRepairAttempted;
        }

        private static void RunContinuationCombinationRound(SearchContext searchContext,
            IReadOnlyList<LinkageRouteCandidatePool> roundPools, ContinuationCombinationState combinationState)
        {
            var request = searchContext.RouteRequest;
            var token = searchContext.MainSearchCancellationToken;
            try
            {
                searchContext.BeginContinuationStage();
                RunCombinationSearch(searchContext, roundPools,
                    stopAfterFirstSuccessfulSolution: true,
                    maximumStageMilliseconds: request.SearchOptions.ExtendedFastSearchStageLimitMilliseconds,
                    allowClearanceExpansionCandidates: false,
                    allowPairViolationDirectedCandidates: true,
                    allowDeepClearanceExpansionCandidates: false,
                    exploreAllPoolOrders: false);
                if (token.IsCancellationRequested || searchContext.HasSuccessfulSolution)
                    return;
                if (!searchContext.TryEnterMainSearchWorker(token))
                    return;
                try
                {
                    var conflictPair = FindFirstZeroCompatibilityPoolPair(searchContext, roundPools);
                    Volatile.Write(ref combinationState.ConflictGroupKeys, conflictPair == null
                        ? Array.Empty<string>()
                        : new[]
                        {
                            conflictPair.Item1.GroupRequest.GroupKey,
                            conflictPair.Item2.GroupRequest.GroupKey
                        });
                    if (conflictPair != null &&
                        Interlocked.Exchange(ref combinationState.DeepRepairAttempted, 1) == 0)
                    {
                        searchContext.BeginContinuationStage();
                        RunConstraintDirectedDeepRepairSearch(searchContext, roundPools);
                    }
                }
                finally { searchContext.ExitMainSearchWorker(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        private static void CompleteContinuationCombinationRound(Task combinationRound,
            CancellationToken token)
        {
            if (combinationRound == null)
                return;
            try
            {
                combinationRound.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        private static void WaitContinuationCombinationRoundQuietly(Task combinationRound)
        {
            try
            {
                combinationRound?.Wait();
            }
            catch (AggregateException)
            {
                // 外层已有待传播故障，本轮结果只等待结束、不覆盖原异常。
            }
        }

        private static int[] CreateContinuationWorkPriorities(IReadOnlyList<int> groupIndices,
            int familyCount, int turnIndex)
        {
            if (groupIndices.Count == 0) return Array.Empty<int>();
            return Enumerable.Range(0, familyCount).SelectMany(familyOffset =>
                Enumerable.Range(0, groupIndices.Count).Select(groupOffset =>
                    groupIndices[(groupOffset + turnIndex) % groupIndices.Count] * familyCount +
                    (familyOffset + turnIndex / groupIndices.Count) % familyCount)).ToArray();
        }

        private static IReadOnlyList<LinkageRouteCandidatePool> MergeContinuationFamilies(
            LinkageMultiGroupRouteRequest request, IReadOnlyList<LinkageRouteCandidatePool> workPools, int familyCount)
        {
            return Enumerable.Range(0, request.Groups.Count).Select(groupIndex =>
            {
                var familyPools = workPools.Skip(groupIndex * familyCount).Take(familyCount)
                    .Where(pool => pool != null).ToArray();
                var merged = LinkageRouteCandidateFactory.MergeTraversalPools(request, familyPools,
                    preserveCompletedCandidates: true);
                merged.GroupRequest = request.Groups[groupIndex];
                merged.EvaluatedSkeletonCount = familyPools.Sum(pool => pool.EvaluatedSkeletonCount);
                return merged;
            }).ToArray();
        }

        private static void ReportContinuationProgress(SearchContext context,
            IReadOnlyList<LinkageRouteCandidatePool> pools, IReadOnlyList<LinkageRouteCandidatePool> snapshots,
            string stage)
        {
            context.Progress?.Report(new LinkageRouteProgress
            {
                AlgorithmKind = context.AlgorithmKind,
                ElapsedMilliseconds = context.SearchStopwatch.ElapsedMilliseconds,
                ProcessedStateCount = Interlocked.Read(ref context.ProcessedStateCount),
                Message = stage + "；有效候选 " + string.Join("，", pools.Select((pool, index) =>
                    context.RouteRequest.Groups[index].GroupKey + "=" + (pool?.ValidCandidates.Count ?? 0))) +
                    "；本轮已评估骨架=" + snapshots.Where(pool => pool != null)
                        .Sum(pool => pool.EvaluatedSkeletonCount).ToString(CultureInfo.InvariantCulture) +
                    "；组合状态=" + Interlocked.Read(ref context.ProcessedStateCount).ToString(CultureInfo.InvariantCulture)
            });
        }
    }
}
