using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;

namespace RouteLinkBar.Algorithm.Routing.Algorithms
{
    internal static partial class LinkageMultiGroupRouteEngine
    {
        // 顺序避让重规划（Prioritized Planning + 失败组前移重启，参考 MAPF 的 PP/PBS 与布线 rip-up&reroute）。
        // 普通组合失败的根因是各组候选彼此独立生成：每组都走自己最短的通道，交叉处必然 0 mm 相撞。
        // 本阶段按优先级逐组落位：已落位组的实际绝缘实体作为后续组的避让实体，由避让网格 A* 重新生成本组候选；
        // 某组无法落位时把它前移到最前并重启。完整一套直接进入组合递归叶子，最终后处理、搭接、绝缘和发布门禁不变。
        // 串行运行（实测与原搜索并行会使两者都变慢）；不可能成功或停滞时立即退出，把时间还给原搜索。
        //
        // 不退化保证（原搜索有解的用例不能因本阶段变无解）：
        // 1. 只在快速层未成套后运行，返回值不并入原搜索的中断标志；
        // 2. 前后保存/恢复原搜索的查重状态与失败记录，任何异常隔离在本阶段内；
        // 3. 原搜索存在时间窗时不运行（孔组居中首发窗、自动总时限、异规格并行分支），
        //    因此本阶段对原搜索唯一的影响是推迟至多一个硬预算的时间。
        private static string GetPrioritizedObstacleReplanningSkipReason(LinkageMultiGroupRouteRequest request)
        {
            if (request == null) return "请求为空";
            if (request.RequireCenteredTerminalHoleGroups)
                return "孔组居中优先阶段有首发时间窗，留给原搜索；偏心阶段再运行";
            if (request.SearchOptions.EnableAutomaticTotalSearchLimit && !request.SearchOptions.PrioritizeAllSolutions)
                return "已启用自动总时限，不占用原搜索时间";
            if (request.IsSectionSpecificationAuxiliarySearch ||
                request.Groups.Any(group => group?.HasSectionSpecificBusbarSettings(request) == true))
                return "异规格多分支并行搜索，不推迟、不争用其它分支";
            return null;
        }

        private static void RunPrioritizedObstacleReplanning(
            SearchContext context,
            IReadOnlyList<LinkageRouteCandidatePool> seedPools,
            int budgetMilliseconds)
        {
            if (budgetMilliseconds <= 0) return;
            var snapshot = context.CaptureAuxiliaryStageState();
            try
            {
                // 硬预算：到时同时取消正在进行的逐组候选生成，不让阶段超出预算拖慢后续原搜索。
                using (var stageCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    context.MainSearchCancellationToken))
                {
                    stageCancellation.CancelAfter(budgetMilliseconds);
                    RunPrioritizedObstacleReplanningCore(context, seedPools, budgetMilliseconds,
                        stageCancellation.Token);
                }
            }
            catch (Exception exception)
            {
                // 附加阶段的任何异常都不能中断原搜索；用户取消由原搜索随后按主令牌处理。
                if (!context.MainSearchCancellationToken.IsCancellationRequested)
                {
                    context.Progress?.Report(new LinkageRouteProgress
                    {
                        AlgorithmKind = context.AlgorithmKind,
                        ElapsedMilliseconds = context.SearchStopwatch.ElapsedMilliseconds,
                        Message = "顺序避让重规划异常已隔离，交原搜索继续：" +
                            exception.GetType().Name + "：" + exception.Message
                    });
                }
            }
            finally
            {
                context.RestoreAuxiliaryStageState(snapshot);
            }
        }

        private static bool RunPrioritizedObstacleReplanningCore(
            SearchContext context,
            IReadOnlyList<LinkageRouteCandidatePool> seedPools,
            int budgetMilliseconds,
            CancellationToken token)
        {
            var request = context.RouteRequest;
            var groupCount = request.Groups.Count;
            if (groupCount < 2 || budgetMilliseconds <= 0 || token.IsCancellationRequested)
            {
                return false;
            }

            // 可行性前检：某组既无有效候选也无任何可复用模板骨架时，本阶段不可能为它落位，直接跳过，
            // 不占用原搜索时间（由原搜索后续层补候选）。
            var hopelessGroups = Enumerable.Range(0, groupCount)
                .Where(index => !(seedPools?[index]?.ValidCandidates.Count > 0) && !HasGridTemplates(seedPools?[index]))
                .Select(index => request.Groups[index].GroupKey)
                .ToList();
            if (hopelessGroups.Count > 0)
            {
                context.Progress?.Report(new LinkageRouteProgress
                {
                    AlgorithmKind = context.AlgorithmKind,
                    ElapsedMilliseconds = context.SearchStopwatch.ElapsedMilliseconds,
                    Message = "顺序避让重规划跳过：" + string.Join(",", hopelessGroups) +
                        " 无任何候选或可复用模板骨架，交原搜索继续补候选。"
                });
                return false;
            }

            var stageWatch = Stopwatch.StartNew();
            var roundsWithoutNewWork = 0;
            var options = request.SearchOptions;
            var groupSliceMilliseconds = Math.Max(1, options.FastSearchStageLimitMilliseconds);
            var prefetchWindow = ResolveGroupPrefetchWindow(request, 1);
            var regeneratedPools = new Dictionary<string, LinkageRouteCandidatePool>(StringComparer.Ordinal);
            var triedOrders = new HashSet<string>(StringComparer.Ordinal);
            var random = new Random(groupCount * 7919 + 17);

            // 初始优先级：候选越少越先落位（最受约束者先行），同数按原组序。
            var order = Enumerable.Range(0, groupCount)
                .OrderBy(index => seedPools?[index]?.ValidCandidates.Count ?? 0)
                .ThenBy(index => index)
                .ToList();
            var attempt = 0;
            while (!token.IsCancellationRequested && !context.HasSuccessfulSolution &&
                stageWatch.ElapsedMilliseconds < budgetMilliseconds)
            {
                var orderKey = string.Join(",", order);
                if (!triedOrders.Add(orderKey))
                {
                    if (triedOrders.Count >= Factorial(groupCount)) break;
                    order = order.OrderBy(index => random.Next()).ToList();
                    continue;
                }

                attempt++;
                var regeneratedBeforeRound = regeneratedPools.Count;
                var selected = new List<LinkageRouteCandidate>(groupCount);
                var chosen = new LinkageRouteCandidate[groupCount];
                var failedGroupIndex = -1;
                var failureDetail = string.Empty;
                var pickTrace = new List<string>();
                foreach (var groupIndex in order)
                {
                    if (token.IsCancellationRequested ||
                        stageWatch.ElapsedMilliseconds >= budgetMilliseconds)
                    {
                        return false;
                    }

                    var pickStart = stageWatch.ElapsedMilliseconds;
                    var pick = PickClearCandidate(context, seedPools?[groupIndex], selected, token);
                    var pickSource = "原池";
                    LinkageRouteCandidatePool regenerated = null;
                    if (pick == null)
                    {
                        regenerated = GetObstacleAwarePool(context, groupIndex, seedPools?[groupIndex], selected,
                            regeneratedPools, groupSliceMilliseconds, prefetchWindow, token);
                        pick = PickClearCandidate(context, regenerated, selected, token);
                        pickSource = (regenerated?.Diagnostics.LastOrDefault() ?? "避让")
                            .Replace("避让层=", string.Empty);
                        if (pickSource.Length > 40) pickSource = pickSource.Substring(0, 40);
                    }
                    if (pick != null)
                        pickTrace.Add(request.Groups[groupIndex].GroupKey + ":" + pickSource + "@" +
                            (stageWatch.ElapsedMilliseconds - pickStart) + "ms");

                    if (pick == null)
                    {
                        failedGroupIndex = groupIndex;
                        failureDetail = "原池" + (seedPools?[groupIndex]?.ValidCandidates.Count ?? 0) +
                            "，避让池有效" + (regenerated?.ValidCandidates.Count ?? 0) +
                            "/骨架" + (regenerated?.EvaluatedSkeletonCount ?? 0) + "，失败阶段" +
                            string.Join(",", (regenerated?.FailureStageCounts ??
                                    new Dictionary<LinkageRouteFailureStage, int>())
                                .OrderByDescending(entry => entry.Value).Take(4)
                                .Select(entry => entry.Key + "=" + entry.Value)) +
                            "，" + (regenerated?.Diagnostics.LastOrDefault() ?? string.Empty);
                        break;
                    }

                    selected.Add(pick);
                    chosen[groupIndex] = pick;
                }

                if (failedGroupIndex < 0)
                {
                    context.Progress?.Report(new LinkageRouteProgress
                    {
                        AlgorithmKind = context.AlgorithmKind,
                        ElapsedMilliseconds = context.SearchStopwatch.ElapsedMilliseconds,
                        Message = "顺序避让重规划第" + attempt + "轮已逐组落位（顺序：" +
                            string.Join("→", order.Select(index => request.Groups[index].GroupKey)) +
                            "），交原组合搜索做完整门禁。来源：" + string.Join("，", pickTrace)
                    });
                    // 完整门禁（后处理与生成顺序相关）：先按原搜索同样的方式——始端中心度选根、其余按最受约束
                    // 优先——在单候选池上走原组合递归；被拒再以落位顺序直接进入叶子（已发布拓扑检查 +
                    // TryAddCompleteSolution）。串行运行，先清空访问状态；不替换原搜索的方案优化候选池。
                    var singletonPools = order.Select(index =>
                    {
                        var pool = new LinkageRouteCandidatePool { GroupRequest = request.Groups[index] };
                        pool.TraversalDirections.Add(LinkageRouteTraversalDirection.Forward);
                        pool.ValidCandidates.Add(chosen[index]);
                        return pool;
                    }).ToList();
                    var rootPool = OrderCandidatePoolsByStartCentrality(singletonPools).First();
                    var attempts = new[]
                    {
                        Tuple.Create((IList<LinkageRouteCandidate>)new List<LinkageRouteCandidate> { rootPool.ValidCandidates[0] },
                            (IReadOnlyList<LinkageRouteCandidatePool>)singletonPools.Where(pool => !ReferenceEquals(pool, rootPool)).ToList()),
                        Tuple.Create((IList<LinkageRouteCandidate>)order.Select(index => chosen[index]).ToList(),
                            (IReadOnlyList<LinkageRouteCandidatePool>)new List<LinkageRouteCandidatePool>())
                    };
                    foreach (var validation in attempts)
                    {
                        if (token.IsCancellationRequested || context.HasSuccessfulSolution) break;
                        context.BeginContinuationStage();
                        // 用阶段总预算令牌：需要柜顶/端部输出后处理时，完整门禁在后台异步完成，
                        // 不能被短时片提前取消；随后等待后处理结束再判定是否成功。
                        using (var validationStage = new CombinationSearchStage(true, token, Timeout.Infinite,
                            false, false, false, false))
                        {
                            try
                            {
                                Interlocked.Increment(ref context.ProcessedStateCount);
                                SearchCombinations(context, validation.Item2, validation.Item1, validationStage);
                            }
                            catch (OperationCanceledException) when (validationStage.CancellationToken.IsCancellationRequested)
                            {
                            }

                            context.WaitTerminalOutputPostProcessingIdle(token);
                        }
                    }

                    if (context.HasSuccessfulSolution) return false;
                    // 逐对净距已通过但完整门禁拒绝：换一个随机顺序继续。
                    order = order.OrderBy(index => random.Next()).ToList();
                    continue;
                }

                context.Progress?.Report(new LinkageRouteProgress
                {
                    AlgorithmKind = context.AlgorithmKind,
                    ElapsedMilliseconds = context.SearchStopwatch.ElapsedMilliseconds,
                    Message = "顺序避让重规划第" + attempt + "轮：" + request.Groups[failedGroupIndex].GroupKey +
                        "在已落位" + selected.Count + "组之后无可避让候选（" + failureDetail + "），前移重启。"
                });
                order.Remove(failedGroupIndex);
                order.Insert(0, failedGroupIndex);
                // 停滞判定：连续一整轮组数的失败轮都只命中已缓存的避让池、没有任何新生成，说明在循环，停止。
                roundsWithoutNewWork = regeneratedPools.Count == regeneratedBeforeRound ? roundsWithoutNewWork + 1 : 0;
                if (roundsWithoutNewWork >= groupCount)
                {
                    context.Progress?.Report(new LinkageRouteProgress
                    {
                        AlgorithmKind = context.AlgorithmKind,
                        ElapsedMilliseconds = context.SearchStopwatch.ElapsedMilliseconds,
                        Message = "顺序避让重规划停止：连续" + roundsWithoutNewWork + "轮无新避让候选，交原搜索继续。"
                    });
                    return false;
                }
            }

            return false;
        }

        private static LinkageRouteCandidate PickClearCandidate(
            SearchContext context,
            LinkageRouteCandidatePool pool,
            IReadOnlyList<LinkageRouteCandidate> selected,
            CancellationToken token)
        {
            if (pool?.ValidCandidates == null) return null;
            foreach (var candidate in pool.ValidCandidates)
            {
                if (token.IsCancellationRequested) return null;
                if (selected.Count == 0) return candidate;
                if (context.EvaluateAgainstSelected(candidate, selected, token).IsClear &&
                    !token.IsCancellationRequested)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static LinkageRouteCandidatePool GetObstacleAwarePool(
            SearchContext context,
            int groupIndex,
            LinkageRouteCandidatePool seedPool,
            IReadOnlyList<LinkageRouteCandidate> selected,
            IDictionary<string, LinkageRouteCandidatePool> cache,
            int sliceMilliseconds,
            int prefetchWindow,
            CancellationToken stageToken)
        {
            var request = context.RouteRequest;
            var key = groupIndex + "|" + string.Join(",",
                selected.Select(candidate => candidate.CacheIdentity).OrderBy(identity => identity));
            if (cache.TryGetValue(key, out var cached)) return cached;

            // 搭接绝缘包络只用于逐对搭接区判定，不作为通用净距静态实体，避免重复叠加净距。
            var obstacles = context.FixedTerminalAndObstacleSolids
                .Concat(selected.SelectMany(candidate => candidate.ActualClearanceSolids)
                    .Where(solid => solid != null && !solid.IsLapClearanceEnvelope))
                .ToList();
            // 网格避让另含已落位组的搭接绝缘包络（只用于提出走向，不改变候选工厂的静态校验实体）。
            var gridAvoidance = obstacles
                .Concat(selected.SelectMany(candidate => candidate.ActualClearanceSolids)
                    .Where(solid => solid != null && solid.IsLapClearanceEnvelope))
                .ToList();
            var group = request.Groups[groupIndex];
            var pools = new List<LinkageRouteCandidatePool>();
            var forwardOnly = new[] { LinkageRouteTraversalDirection.Forward };
            // 实测所有成功落位的再生成候选都来自避让网格 A*；快速层/临界层带障碍重建几乎总是超时且不出候选，
            // 已去掉。本组没有可复用端部模板时直接判落位失败并前移，交给原搜索补候选。
            var tiers = new List<Tuple<string, LinkageRouteTraversalDirection[],
                Func<LinkageRouteTraversalDirection, CancellationToken, LinkageRouteCandidatePool>>>();
            if (HasGridTemplates(seedPool))
                tiers.Add(Tuple.Create("网格", forwardOnly, (Func<LinkageRouteTraversalDirection, CancellationToken, LinkageRouteCandidatePool>)(
                    (direction, sliceToken) => BuildObstacleAwareGridPool(context, group, seedPool, obstacles, sliceToken, gridAvoidance))));
            var reached = new List<string>();
            foreach (var tier in tiers)
            {
                foreach (var direction in tier.Item2)
                {
                    if (stageToken.IsCancellationRequested) break;
                    using (var slice = CancellationTokenSource.CreateLinkedTokenSource(stageToken))
                    {
                        slice.CancelAfter(tier.Item1 == "网格" ? 3 * sliceMilliseconds : sliceMilliseconds);
                        LinkageRouteCandidatePool pool = null;
                        try
                        {
                            pool = tier.Item3(direction, slice.Token);
                            if (pool != null) pools.Add(pool);
                        }
                        catch (OperationCanceledException) when (slice.IsCancellationRequested)
                        {
                        }
                        reached.Add(tier.Item1 + (slice.IsCancellationRequested ? "(超时)" : string.Empty) +
                            (pool?.Diagnostics.Count > 0 && tier.Item1 == "网格" ? "[" + pool.Diagnostics.Last().Substring(0, Math.Min(500, pool.Diagnostics.Last().Length)) + "]" : string.Empty));
                    }

                    if (pools.Any(pool => pool.ValidCandidates.Count > 0)) break;
                }

                if (pools.Any(pool => pool.ValidCandidates.Count > 0)) break;
            }

            var merged = pools.Count == 0 ? null : pools.Count == 1 ? pools[0]
                : LinkageRouteCandidateFactory.MergeTraversalPools(request, pools);
            if (merged != null)
            {
                if (pools.Count > 1)
                {
                    merged.FailureStageCounts.Clear();
                    foreach (var pool in pools)
                    foreach (var entry in pool.FailureStageCounts)
                    {
                        merged.FailureStageCounts.TryGetValue(entry.Key, out var count);
                        merged.FailureStageCounts[entry.Key] = count + entry.Value;
                    }
                }
                merged.Diagnostics.Add("避让层=" + string.Join("/", reached.Distinct()));
            }
            if (!stageToken.IsCancellationRequested) cache[key] = merged;
            return merged;
        }
        private static int Factorial(int value)
        {
            var result = 1;
            for (var factor = 2; factor <= value && result < 100000; factor++) result *= factor;
            return result;
        }

        private sealed class AuxiliaryStageStateSnapshot
        {
            public AuxiliaryStageStateSnapshot(KeyValuePair<string, byte>[] visitedSearchStates,
                LinkageRouteSolution bestFailureSolution, string firstFinalOutputRejectionReason)
            {
                VisitedSearchStates = visitedSearchStates ?? new KeyValuePair<string, byte>[0];
                BestFailureSolution = bestFailureSolution;
                FirstFinalOutputRejectionReason = firstFinalOutputRejectionReason;
            }

            public KeyValuePair<string, byte>[] VisitedSearchStates { get; }
            public LinkageRouteSolution BestFailureSolution { get; }
            public string FirstFinalOutputRejectionReason { get; }
        }
    }
}
