using GeometRi;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using RouteLinkBar.Algorithm.Routing.Algorithms;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace RouteLinkBar.Algorithm.Routing
{
    /// <summary>
    /// Rebuilds a scheme-library path inside the algorithm process. The
    /// persisted path is only a topology/specification seed; current request
    /// terminals remain authoritative and candidate terminals are never copied
    /// into the output solution.
    /// </summary>
    public static partial class LinkageSchemeLibraryRouteRebuilder
    {
        private const int StartPositionType = 0;
        private const int CabinetTopPositionType = 1;
        private const int MiddlePositionType = 2;
        private static readonly ConditionalWeakTable<
            LinkageMultiGroupRouteRequest,
            OrdinaryCandidatePoolCache> OrdinaryCandidatePoolCaches =
                new ConditionalWeakTable<LinkageMultiGroupRouteRequest,
                    OrdinaryCandidatePoolCache>();

        private sealed class OrdinaryCandidatePoolCache
        {
            public IDictionary<string, LinkageRouteCandidatePool> Pools { get; set; } =
                new Dictionary<string, LinkageRouteCandidatePool>(
                    StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The standalone overload is used by callers which do not already run
        /// the ordinary search.  The Worker uses the overload accepting
        /// ordinary solutions so every library candidate is rebuilt from the
        /// same legal algorithm output instead of starting a second search.
        /// </summary>
        public static LinkageRouteSolution Rebuild(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageSchemeLibraryMatchResult matchResult,
            LinkageSchemeLibraryRouteTemplate routeTemplate,
            CancellationToken cancellationToken)
        {
            var ordinaryComparison = routeRequest == null
                ? null
                : LinkageMultiGroupRouteComparisonRunner
                    .RunAsync(routeRequest, null, cancellationToken)
                    .GetAwaiter()
                    .GetResult();
            return Rebuild(
                routeRequest,
                matchResult,
                routeTemplate,
                ordinaryComparison?.RankedSolutions,
                cancellationToken);
        }

        /// <summary>
        /// 把已经通过当前固定端完整实体化的历史路径组，嵌回同一候选的
        /// 普通合法方案。历史组只允许替换方向保持不变的直接重建结果；
        /// 跨组绝缘、叠排和铜排规则仍由最终整体复核决定，失败时保留
        /// 完整普通方案兜底，绝不发布半套结果。
        /// </summary>
        public static LinkageRouteSolution MergeHistoricalGroupsIntoOrdinarySolution(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageSchemeLibraryMatchResult matchResult,
            LinkageRouteSolution historicalPartialSolution,
            LinkageRouteSolution ordinarySolution)
        {
            if (routeRequest == null || matchResult == null ||
                ordinarySolution?.IsSuccess != true ||
                historicalPartialSolution?.GroupSolutions == null ||
                historicalPartialSolution.GroupSolutions.Count == 0)
            {
                return ordinarySolution;
            }

            var historicalGroups = historicalPartialSolution.GroupSolutions
                .Where(groupSolution => groupSolution != null &&
                    IsDirectionPreservingHistoricalGroup(groupSolution))
                .ToList();
            if (historicalGroups.Count == 0)
            {
                return ordinarySolution;
            }

            var hybridSolution = CopyRouteSolution(
                ordinarySolution,
                matchResult.CandidateSchemeRecordId);
            var replacedGroupCount = 0;
            foreach (var historicalGroup in historicalGroups)
            {
                var ordinaryGroupIndex = hybridSolution.GroupSolutions
                    .Select((groupSolution, groupIndex) => new
                    {
                        Group = groupSolution,
                        Index = groupIndex
                    })
                    .FirstOrDefault(item => string.Equals(
                        item.Group?.GroupKey,
                        historicalGroup.GroupKey,
                        StringComparison.OrdinalIgnoreCase))?.Index ?? -1;
                if (ordinaryGroupIndex < 0)
                {
                    continue;
                }

                hybridSolution.GroupSolutions[ordinaryGroupIndex] = CopyGroupSolution(
                    historicalGroup);
                replacedGroupCount++;
            }

            if (replacedGroupCount == 0)
            {
                return ordinarySolution;
            }

            hybridSolution.ActualCopperLength = hybridSolution.GroupSolutions
                .Sum(groupSolution => groupSolution?.ActualCopperLength ?? 0d);
            hybridSolution.PerpendicularLapCount = hybridSolution.GroupSolutions
                .Sum(groupSolution => groupSolution?.PerpendicularLapCount ?? 0);
            hybridSolution.OptimizationSourceTopologySignature =
                "SCHEME_LIBRARY_HISTORICAL_HYBRID:" +
                matchResult.CandidateSchemeRecordId;
            hybridSolution.IsSuccess = true;
            if (!LinkageMultiGroupRouteEngine.TryValidateSchemeLibraryOutput(
                    routeRequest,
                    hybridSolution,
                    out _))
            {
                return ordinarySolution;
            }

            return hybridSolution;
        }

        private static bool IsDirectionPreservingHistoricalGroup(
            LinkageGroupRouteSolution groupSolution)
        {
            var topologySignature = groupSolution?.TopologySignature ?? string.Empty;
            const string historicalPrefix = "LIBRARY_HISTORICAL:";
            if (!topologySignature.StartsWith(
                    historicalPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var variantName = topologySignature.Substring(historicalPrefix.Length);
            var sequenceMarkerIndex = variantName.LastIndexOf(
                "序",
                StringComparison.Ordinal);
            return sequenceMarkerIndex < 0 ||
                variantName.EndsWith("序0", StringComparison.Ordinal);
        }

        /// <summary>
        /// Rebuild a matched template by selecting a complete solution from
        /// the ordinary algorithm.  The persisted template contributes only
        /// its one-to-one transformed direction sequence; all coordinates,
        /// segment lengths, lap areas, spacers, stack members, clearances and
        /// interference checks come from the normal current-terminal solver.
        /// </summary>
        public static LinkageRouteSolution Rebuild(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageSchemeLibraryMatchResult matchResult,
            LinkageSchemeLibraryRouteTemplate routeTemplate,
            IEnumerable<LinkageRouteSolution> ordinarySolutions,
            CancellationToken cancellationToken)
        {
            var failureSolution = CreateSchemeLibrarySolutionShell(
                routeRequest,
                matchResult);
            if (routeRequest == null || matchResult == null || routeTemplate == null ||
                routeTemplate.Routes == null || routeTemplate.Routes.Count == 0)
            {
                AddFailure(failureSolution, "方案库路径模板或当前固定端请求为空。");
                return failureSolution;
            }
            if (cancellationToken.IsCancellationRequested)
            {
                AddFailure(failureSolution, "方案库路径重建已取消。");
                return failureSolution;
            }

            var routePreferences = new List<SchemeLibraryHistoricalRoutePreference>();
            foreach (var routeTemplateRoute in routeTemplate.Routes)
            {
                if (!TryCreateHistoricalRoutePreference(
                    routeRequest,
                    matchResult,
                    routeTemplateRoute,
                    out var routePreference,
                    out var routeDiagnostic))
                {
                    AddFailure(failureSolution, routeDiagnostic);
                    return failureSolution;
                }
                routePreferences.Add(routePreference);
            }

            var rankedOrdinarySolutions = (ordinarySolutions ??
                    new List<LinkageRouteSolution>())
                .Where(solution => solution?.IsSuccess == true)
                .Select(solution => new
                {
                    Solution = solution,
                    Score = ScoreOrdinarySolution(routePreferences, solution)
                })
                .Where(item => !double.IsNaN(item.Score) &&
                    !double.IsInfinity(item.Score))
                .OrderBy(item => item.Score)
                .ThenBy(item => item.Solution.ActualCopperLength)
                .ToList();
            if (rankedOrdinarySolutions.Count == 0)
            {
                AddFailure(failureSolution,
                    "方案库匹配后，普通算法没有返回可复用的完整有效方案。");
                return failureSolution;
            }

            foreach (var rankedOrdinarySolution in rankedOrdinarySolutions)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    AddFailure(failureSolution, "方案库路径重建已取消。");
                    return failureSolution;
                }
                var candidateSolution = CopyRouteSolution(
                    rankedOrdinarySolution.Solution,
                    matchResult.CandidateSchemeRecordId);
                if (!LinkageMultiGroupRouteEngine.TryValidateSchemeLibraryOutput(
                    routeRequest,
                    candidateSolution,
                    out _))
                {
                    continue;
                }
                return candidateSolution;
            }

            AddFailure(failureSolution,
                "方案库选出的普通算法方案未通过最终搭接、绝缘距离或实体重叠复核。");
            failureSolution.MinimumClearance = double.NaN;
            return failureSolution;
        }

        private sealed class SchemeLibraryHistoricalRoutePreference
        {
            public string RouteKey { get; set; }
            public string TargetGroupKey { get; set; }
            public IList<Vector3d> DirectionSequence { get; set; }
        }

        private static LinkageRouteSolution CreateSchemeLibrarySolutionShell(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageSchemeLibraryMatchResult matchResult)
        {
            return new LinkageRouteSolution
            {
                AlgorithmKind = LinkageRouteAlgorithmKind.SweptRepair,
                OptimizationSourceTopologySignature = "SCHEME_LIBRARY:" +
                    (matchResult?.CandidateSchemeRecordId.ToString() ?? "0"),
                RequestedInsulationClearance = routeRequest?.InsulationClearance ?? 0d,
                RequestedLapInsulationClearance = routeRequest?.LapInsulationClearance ?? 35d,
                SearchInsulationClearance = routeRequest?.InsulationClearance ?? 0d,
                MinimumClearance = double.NaN
            };
        }

        private static bool TryCreateHistoricalRoutePreference(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageSchemeLibraryMatchResult matchResult,
            LinkageSchemeLibraryRouteTemplateRoute routeTemplateRoute,
            out SchemeLibraryHistoricalRoutePreference routePreference,
            out string diagnostic)
        {
            routePreference = null;
            diagnostic = null;
            if (!TryResolveTargetGroup(
                routeRequest,
                matchResult,
                routeTemplateRoute,
                out var targetGroupRequest,
                out diagnostic))
            {
                return false;
            }

            var middleMembers = (routeTemplateRoute?.OrderedMembers ??
                    new List<LinkageSchemeLibraryRouteTemplateMember>())
                .Where(member => member != null &&
                    member.PositionType == MiddlePositionType)
                .ToList();
            if (middleMembers.Count == 0)
            {
                diagnostic = "方案库路径没有中间排成员：" +
                    (routeTemplateRoute?.RouteKey ?? string.Empty);
                return false;
            }

            // The transform is intentionally rebuilt for this matched route.
            // A global anchor fit can skew the route's width/thickness frame and
            // produces the wrong direction even when the overall scheme score is
            // good.  The current route's paired terminal frame is authoritative.
            var routeTransform = BuildRouteCandidateToTargetTransform(
                routeTemplateRoute,
                targetGroupRequest,
                matchResult);
            var historicalDirectionSequence = BuildHistoricalDirectionSequence(
                routeTemplateRoute,
                middleMembers,
                routeTransform);
            if (historicalDirectionSequence.Count == 0)
            {
                diagnostic = "方案库路径无法提取有效的中间排方向：" +
                    (routeTemplateRoute?.RouteKey ?? string.Empty);
                return false;
            }

            routePreference = new SchemeLibraryHistoricalRoutePreference
            {
                RouteKey = routeTemplateRoute.RouteKey,
                TargetGroupKey = targetGroupRequest.GroupKey,
                DirectionSequence = historicalDirectionSequence
            };
            return true;
        }

        private static double ScoreOrdinarySolution(
            IList<SchemeLibraryHistoricalRoutePreference> routePreferences,
            LinkageRouteSolution ordinarySolution)
        {
            if (ordinarySolution?.GroupSolutions == null ||
                routePreferences == null || routePreferences.Count == 0)
            {
                return double.MaxValue;
            }

            var totalScore = 0d;
            foreach (var routePreference in routePreferences)
            {
                var groupSolution = ordinarySolution.GroupSolutions.FirstOrDefault(
                    candidateGroup => candidateGroup != null &&
                        string.Equals(
                            candidateGroup.GroupKey,
                            routePreference.TargetGroupKey,
                            StringComparison.OrdinalIgnoreCase));
                if (groupSolution?.ManufacturableCenterline == null ||
                    groupSolution.ManufacturableCenterline.Count < 2)
                {
                    return double.MaxValue;
                }
                totalScore += ScoreHistoricalPathPreference(
                    routePreference.DirectionSequence,
                    groupSolution.ManufacturableCenterline);
            }
            return totalScore;
        }

        private static LinkageRouteSolution CopyRouteSolution(
            LinkageRouteSolution source,
            int candidateSchemeRecordId)
        {
            var copy = new LinkageRouteSolution
            {
                SchemeIndex = source.SchemeIndex,
                OptimizationSourceTopologySignature = "SCHEME_LIBRARY:" +
                    candidateSchemeRecordId,
                AlgorithmKind = source.AlgorithmKind,
                IsSuccess = source.IsSuccess,
                IsCancelled = source.IsCancelled,
                ActualCopperLength = source.ActualCopperLength,
                PerpendicularLapCount = source.PerpendicularLapCount,
                MinimumClearance = source.MinimumClearance,
                RequestedInsulationClearance = source.RequestedInsulationClearance,
                RequestedLapInsulationClearance = source.RequestedLapInsulationClearance,
                SearchInsulationClearance = source.SearchInsulationClearance,
                IsRelaxedClearanceSolution = source.IsRelaxedClearanceSolution
            };
            foreach (var sourceGroup in source.GroupSolutions ??
                new List<LinkageGroupRouteSolution>())
            {
                if (sourceGroup != null)
                {
                    copy.GroupSolutions.Add(CopyGroupSolution(sourceGroup));
                }
            }
            return copy;
        }

        private static LinkageGroupRouteSolution CopyGroupSolution(
            LinkageGroupRouteSolution source)
        {
            var copy = new LinkageGroupRouteSolution
            {
                GroupKey = source.GroupKey,
                AxisSkeleton = source.AxisSkeleton?.Select(CopyPoint).ToList() ??
                    new List<Point3d>(),
                ManufacturableCenterline = source.ManufacturableCenterline?
                    .Select(CopyPoint).ToList() ?? new List<Point3d>(),
                PlanarParts = source.PlanarParts?.Select(setting => setting.Copy()).ToList() ??
                    new List<PlanarBendBusbarSetting>(),
                StackMembers = source.StackMembers?.Select(CopyStackMember).ToList() ??
                    new List<LinkageStackedRouteMember>(),
                Accessories = source.Accessories?.Select(CopyAccessory).ToList() ??
                    new List<LinkageRouteAccessory>(),
                MinimumClearanceFaceGroups = source.MinimumClearanceFaceGroups?
                    .Select(CopyClearanceFaceGroup).ToList() ??
                    new List<LinkageRouteClearanceFaceGroup>(),
                ActualCopperLength = source.ActualCopperLength,
                PerpendicularLapCount = source.PerpendicularLapCount,
                IsConvertedShape = source.IsConvertedShape,
                TopologySignature = source.TopologySignature
            };
            foreach (var bridgeIndex in source.DoubleStartPhysicalBridgeSectionIndices ??
                new HashSet<int>())
            {
                copy.DoubleStartPhysicalBridgeSectionIndices.Add(bridgeIndex);
            }
            foreach (var bridgeMemberIndex in source.ExpectedDoubleStartBridgeMemberIndices ??
                new HashSet<int>())
            {
                copy.ExpectedDoubleStartBridgeMemberIndices.Add(bridgeMemberIndex);
            }
            return copy;
        }

        private static LinkageRouteClearanceFaceGroup CopyClearanceFaceGroup(
            LinkageRouteClearanceFaceGroup source)
        {
            return new LinkageRouteClearanceFaceGroup
            {
                GeneratedGroupKey = source.GeneratedGroupKey,
                GeneratedSegmentIndex = source.GeneratedSegmentIndex,
                StaticGroupKey = source.StaticGroupKey,
                StaticSegmentIndex = source.StaticSegmentIndex,
                Distance = source.Distance,
                GeneratedFaceCenter = CopyPoint(source.GeneratedFaceCenter),
                GeneratedFaceNormal = source.GeneratedFaceNormal == null
                    ? null
                    : new Vector3d(
                        source.GeneratedFaceNormal.X,
                        source.GeneratedFaceNormal.Y,
                        source.GeneratedFaceNormal.Z),
                StaticFaceCenter = CopyPoint(source.StaticFaceCenter),
                StaticFaceNormal = source.StaticFaceNormal == null
                    ? null
                    : new Vector3d(
                        source.StaticFaceNormal.X,
                        source.StaticFaceNormal.Y,
                        source.StaticFaceNormal.Z)
            };
        }

        private static LinkageRouteSolution RebuildLegacy(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageSchemeLibraryMatchResult matchResult,
            LinkageSchemeLibraryRouteTemplate routeTemplate,
            CancellationToken cancellationToken)
        {
            var rebuiltSolution = new LinkageRouteSolution
            {
                AlgorithmKind = LinkageRouteAlgorithmKind.SweptRepair,
                OptimizationSourceTopologySignature = "SCHEME_LIBRARY:" +
                    (matchResult?.CandidateSchemeRecordId.ToString() ?? "0"),
                RequestedInsulationClearance = routeRequest?.InsulationClearance ?? 0d,
                RequestedLapInsulationClearance = routeRequest?.LapInsulationClearance ?? 35d,
                SearchInsulationClearance = routeRequest?.InsulationClearance ?? 0d
            };

            if (routeRequest == null || matchResult == null || routeTemplate == null ||
                routeTemplate.Routes == null || routeTemplate.Routes.Count == 0)
            {
                AddFailure(rebuiltSolution, "方案库路径模板或当前固定端请求为空。");
                return rebuiltSolution;
            }

            var ordinaryCandidatePools = OrdinaryCandidatePoolCaches.GetValue(
                routeRequest,
                _ => BuildOrdinaryCandidatePoolCache(
                    routeRequest,
                    cancellationToken)).Pools;

            foreach (var routeTemplateRoute in routeTemplate.Routes)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    AddFailure(rebuiltSolution, "方案库路径重建已取消。");
                    break;
                }
                if (!TryResolveTargetGroup(routeRequest, matchResult, routeTemplateRoute,
                    out var targetGroupRequest, out var resolveDiagnostic))
                {
                    AddFailure(rebuiltSolution, resolveDiagnostic);
                    continue;
                }

                var routeCandidateToTargetTransform = BuildRouteCandidateToTargetTransform(
                    routeTemplateRoute,
                    targetGroupRequest,
                    matchResult);

                var middleMembers = (routeTemplateRoute.OrderedMembers ??
                    new List<LinkageSchemeLibraryRouteTemplateMember>())
                    .Where(member => member != null && member.PositionType == MiddlePositionType)
                    .ToList();
                if (middleMembers.Count == 0)
                {
                    AddFailure(rebuiltSolution, "方案库路径没有中间排成员：" +
                        (routeTemplateRoute.RouteKey ?? string.Empty));
                    continue;
                }

                var historicalPathSeed = BuildCurrentTerminalConstrainedCenterline(
                    routeTemplateRoute,
                    middleMembers,
                    targetGroupRequest,
                    routeCandidateToTargetTransform);
                var historicalDirectionSeed = BuildHistoricalDirectionCenterline(
                    routeTemplateRoute,
                    middleMembers,
                    routeCandidateToTargetTransform);
                if (historicalPathSeed.Count < 2)
                {
                    AddFailure(rebuiltSolution, "方案库路径无法在当前固定端之间形成中心线：" +
                        (routeTemplateRoute.RouteKey ?? string.Empty));
                    continue;
                }

                // The library template contributes only the transformed direction
                // sequence and member ordering. Every physical middle route is
                // generated by the normal current-terminal candidate builder;
                // historical segment lengths and coordinates are never copied.
                var conversion = TryConvertUsingHistoricalPathPreference(
                    routeRequest,
                    targetGroupRequest,
                    historicalPathSeed,
                    historicalDirectionSeed,
                    ordinaryCandidatePools.TryGetValue(
                        targetGroupRequest.GroupKey,
                        out var ordinaryCandidatePool)
                        ? ordinaryCandidatePool
                        : null,
                    cancellationToken,
                    out var selectedCenterline,
                    out var conversionDiagnostic);
                if (conversion == null || !conversion.IsValid ||
                    conversion.CenterlineParts.Count == 0 ||
                    conversion.StackMembers.Count == 0)
                {
                    var conversionResultDiagnostic = conversion?.Diagnostics == null
                        ? string.Empty
                        : string.Join("；", conversion.Diagnostics.Where(item =>
                            !string.IsNullOrWhiteSpace(item)).Take(2));
                    AddFailure(
                        rebuiltSolution,
                        "方案库路径的中心线转换没有生成有效中间排：" +
                        (routeTemplateRoute.RouteKey ?? string.Empty) +
                        (string.IsNullOrWhiteSpace(conversionDiagnostic) &&
                         string.IsNullOrWhiteSpace(conversionResultDiagnostic)
                            ? string.Empty
                            : "；" + string.Join("；", new[]
                                { conversionDiagnostic, conversionResultDiagnostic }
                                .Where(item => !string.IsNullOrWhiteSpace(item)))));
                    continue;
                }

                var middleCenterline = selectedCenterline == null
                    ? new List<Point3d>()
                    : selectedCenterline.Select(CopyPoint).ToList();
                if (middleCenterline.Count < 2)
                {
                    AddFailure(rebuiltSolution, "方案库中间排中心线无效：" +
                        (routeTemplateRoute.RouteKey ?? string.Empty));
                    continue;
                }

                var groupSolution = CreateGroupSolutionFromCandidate(
                    targetGroupRequest,
                    selectedCenterline,
                    conversion,
                    routeTemplateRoute.RouteKey);
                if (groupSolution == null)
                {
                    AddFailure(rebuiltSolution, "方案库算法重算结果无法形成完整输出组：" +
                        (routeTemplateRoute.RouteKey ?? string.Empty));
                    continue;
                }
                rebuiltSolution.GroupSolutions.Add(groupSolution);
                rebuiltSolution.ActualCopperLength += groupSolution.ActualCopperLength;
                rebuiltSolution.PerpendicularLapCount += conversion.CenterlineParts.Count - 1;
            }

            rebuiltSolution.IsSuccess = rebuiltSolution.GroupSolutions.Count == routeTemplate.Routes.Count &&
                rebuiltSolution.GroupSolutions.Count > 0 && rebuiltSolution.Diagnostics.Count == 0;
            if (rebuiltSolution.IsSuccess &&
                !LinkageMultiGroupRouteEngine.TryValidateSchemeLibraryOutput(
                    routeRequest,
                    rebuiltSolution,
                    out var finalOutputFailureReason))
            {
                rebuiltSolution.IsSuccess = false;
                AddFailure(rebuiltSolution, "方案库重算完整输出复核失败：" +
                    (finalOutputFailureReason ?? "未提供原因"));
            }
            if (rebuiltSolution.IsSuccess)
            {
                rebuiltSolution.MinimumClearance = routeRequest.InsulationClearance;
            }
            else
            {
                rebuiltSolution.MinimumClearance = double.NaN;
            }
            return rebuiltSolution;
        }

        private static OrdinaryCandidatePoolCache BuildOrdinaryCandidatePoolCache(
            LinkageMultiGroupRouteRequest routeRequest,
            CancellationToken cancellationToken)
        {
            var cache = new OrdinaryCandidatePoolCache();
            foreach (var routeGroupRequest in routeRequest?.Groups ??
                new List<LinkageRouteGroupRequest>())
            {
                if (cancellationToken.IsCancellationRequested ||
                    routeGroupRequest == null ||
                    string.IsNullOrWhiteSpace(routeGroupRequest.GroupKey))
                {
                    continue;
                }
                cache.Pools[routeGroupRequest.GroupKey] =
                    LinkageRouteCandidateFactory
                        .BuildSchemeLibraryOrdinaryCandidatePool(
                            routeRequest,
                            routeGroupRequest,
                            cancellationToken);
            }
            return cache;
        }

        private static LinkageGroupRouteSolution CreateGroupSolutionFromCandidate(
            LinkageRouteGroupRequest targetGroupRequest,
            IList<Point3d> selectedCenterline,
            LinkageRouteCenterlineConversionResult conversion,
            string routeKey)
        {
            if (targetGroupRequest == null || selectedCenterline == null ||
                selectedCenterline.Count < 2 || conversion?.IsValid != true ||
                conversion.CenterlineParts == null ||
                conversion.CenterlineParts.Count == 0 ||
                conversion.StackMembers == null || conversion.StackMembers.Count == 0)
            {
                return null;
            }

            return new LinkageGroupRouteSolution
            {
                GroupKey = targetGroupRequest.GroupKey,
                AxisSkeleton = selectedCenterline.Select(CopyPoint).ToList(),
                ManufacturableCenterline = selectedCenterline.Select(CopyPoint).ToList(),
                PlanarParts = conversion.CenterlineParts
                    .Select(setting => setting.Copy()).ToList(),
                StackMembers = conversion.StackMembers
                    .Select(CopyStackMember).ToList(),
                Accessories = conversion.Accessories
                    .Select(CopyAccessory).ToList(),
                ActualCopperLength = conversion.ActualCopperLength,
                TopologySignature = "LIBRARY_DERIVED:" + (routeKey ?? string.Empty)
            };
        }

        private static LinkageRouteCenterlineConversionResult
            TryConvertUsingHistoricalPathPreference(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest targetGroupRequest,
            IList<Point3d> historicalPathSeed,
            IList<Point3d> historicalDirectionSeed,
            LinkageRouteCandidatePool ordinaryCandidatePool,
            CancellationToken cancellationToken,
            out IList<Point3d> selectedCenterline,
            out string diagnostic)
        {
            selectedCenterline = null;
            diagnostic = null;
            if (cancellationToken.IsCancellationRequested ||
                historicalPathSeed == null || historicalPathSeed.Count < 2 ||
                historicalDirectionSeed == null || historicalDirectionSeed.Count < 2)
            {
                return null;
            }

            // A matching-library template contributes only its transformed
            // direction sequence. The ordinary candidate pool owns all
            // physical coordinates, lengths, lap placement, repairs,
            // clearance and interference validation.
            var normalCandidates = ordinaryCandidatePool?.ValidCandidates ??
                new List<LinkageRouteCandidate>();
            var rankedCandidates = normalCandidates
                .Where(candidate => candidate?.IsValid == true &&
                    candidate.ManufacturableCenterline != null &&
                    candidate.ManufacturableCenterline.Count >= 2)
                .Where(candidatePath => IsHistoricalDirectionCompatible(
                    historicalDirectionSeed,
                    candidatePath.ManufacturableCenterline))
                .Select(candidatePath => new
                {
                    Candidate = candidatePath,
                    Path = candidatePath.ManufacturableCenterline,
                    Score = ScoreHistoricalPathPreference(
                        historicalDirectionSeed,
                        candidatePath.ManufacturableCenterline)
                })
                .OrderBy(item => item.Score)
                .Take(128)
                .ToList();
            var candidateDiagnostics = new List<string>();
            if (rankedCandidates.Count == 0)
            {
                diagnostic = "历史方向段数=" +
                    BuildReducedPathDirections(historicalDirectionSeed).Count +
                    "，普通候选数=" + normalCandidates.Count +
                    "，历史方向=" + FormatDirectionSequence(historicalDirectionSeed) +
                    "，普通候选方向=" + string.Join("/", normalCandidates.Take(3)
                        .Select(candidate => FormatDirectionSequence(
                            candidate.ManufacturableCenterline))) +
                    "，没有方向拓扑一致的普通候选。";
                return null;
            }
            foreach (var candidate in rankedCandidates)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    var routeCandidate = LinkageRouteCandidateFactory
                        .FinalizeSchemeLibraryOrdinaryCandidate(
                            routeRequest,
                            candidate.Candidate,
                            cancellationToken);
                    if (routeCandidate?.IsValid != true ||
                        routeCandidate.PlanarConversion == null)
                    {
                        if (!string.IsNullOrWhiteSpace(routeCandidate?.Diagnostic))
                        {
                            candidateDiagnostics.Add(routeCandidate.Diagnostic);
                        }
                        continue;
                    }
                    var conversion = routeCandidate.PlanarConversion;
                    if (conversion.CenterlineParts.Count == 0 ||
                        conversion.StackMembers.Count == 0)
                    {
                        continue;
                    }
                    selectedCenterline = routeCandidate.ManufacturableCenterline
                        .Select(CopyPoint)
                        .ToList();
                    return conversion;
            }
            diagnostic = candidateDiagnostics.Count == 0
                ? "没有通过历史方向筛选的普通候选。"
                : string.Join("；", candidateDiagnostics.Distinct().Take(3));
            return null;
        }

        private static double ScoreHistoricalPathPreference(
            IList<Vector3d> historicalDirections,
            IList<Point3d> candidatePath)
        {
            if (historicalDirections == null || historicalDirections.Count == 0 ||
                candidatePath == null || candidatePath.Count < 2)
            {
                return double.MaxValue;
            }

            var candidateDirections = BuildReducedPathDirections(candidatePath);
            if (candidateDirections.Count == 0)
            {
                return double.MaxValue;
            }

            var directionError = 0d;
            for (var directionIndex = 0;
                directionIndex < candidateDirections.Count;
                directionIndex++)
            {
                var mappedReferenceIndex = historicalDirections.Count <= 1
                    ? 0
                    : (int)Math.Round(directionIndex *
                        (historicalDirections.Count - 1d) /
                        Math.Max(1, candidateDirections.Count - 1));
                var alignment = candidateDirections[directionIndex].Dot(
                    historicalDirections[Math.Max(0, Math.Min(
                        historicalDirections.Count - 1,
                        mappedReferenceIndex))]);
                directionError += 1d - alignment;
            }
            directionError /= Math.Max(1, candidateDirections.Count);
            var bendCountError = Math.Abs(
                (candidateDirections.Count - 1) -
                (historicalDirections.Count - 1));
            return directionError * 1000d + bendCountError * 100d;
        }

        private static double ScoreHistoricalPathPreference(
            IList<Point3d> historicalPath,
            IList<Point3d> candidatePath)
        {
            if (historicalPath == null || historicalPath.Count < 2 ||
                candidatePath == null || candidatePath.Count < 2)
            {
                return double.MaxValue;
            }

            // Length is deliberately not part of this score.  A matched
            // scheme fixes the direction/topology preference only; the
            // current terminal positions and normal lap rules must determine
            // every physical segment length.
            var historicalDirections = BuildReducedPathDirections(historicalPath);
            var candidateDirections = BuildReducedPathDirections(candidatePath);
            if (historicalDirections.Count == 0 || candidateDirections.Count == 0)
            {
                return double.MaxValue;
            }

            var directionError = 0d;
            for (var directionIndex = 0;
                directionIndex < candidateDirections.Count;
                directionIndex++)
            {
                var mappedReferenceIndex = historicalDirections.Count <= 1
                    ? 0
                    : (int)Math.Round(directionIndex *
                        (historicalDirections.Count - 1d) /
                        Math.Max(1, candidateDirections.Count - 1));
                var alignment = candidateDirections[directionIndex].Dot(
                    historicalDirections[Math.Max(0, Math.Min(
                        historicalDirections.Count - 1,
                        mappedReferenceIndex))]);
                directionError += 1d - alignment;
            }
            directionError /= Math.Max(1, candidateDirections.Count);

            var bendCountError = Math.Abs(
                (candidateDirections.Count - 1) -
                (historicalDirections.Count - 1));
            return directionError * 1000d + bendCountError * 100d;
        }

        private static List<Vector3d> BuildPathDirections(IList<Point3d> points)
        {
            var directions = new List<Vector3d>();
            for (var pointIndex = 0;
                pointIndex + 1 < (points?.Count ?? 0);
                pointIndex++)
            {
                if (points[pointIndex] == null || points[pointIndex + 1] == null)
                {
                    continue;
                }
                var direction = new Vector3d(
                    points[pointIndex],
                    points[pointIndex + 1]);
                if (direction.Norm > 1e-8d)
                {
                    directions.Add(direction.Normalized);
                }
            }
            return directions;
        }

        private static List<Vector3d> BuildReducedPathDirections(
            IList<Point3d> points)
        {
            var reducedDirections = new List<Vector3d>();
            foreach (var direction in BuildPathDirections(points))
            {
                if (reducedDirections.Count == 0 ||
                    reducedDirections[reducedDirections.Count - 1].Dot(direction) <
                    0.999999d)
                {
                    reducedDirections.Add(direction);
                }
            }
            return reducedDirections;
        }

        private static bool IsHistoricalDirectionCompatible(
            IList<Point3d> historicalPath,
            IList<Point3d> candidatePath)
        {
            var historicalDirections = BuildReducedPathDirections(historicalPath);
            var candidateDirections = BuildReducedPathDirections(candidatePath);
            // The normal path enumerator is used only to obtain a legal
            // topology and a length distribution.  Its XYZ directions are
            // intentionally not compared with the historical directions:
            // the latter are carried into the rebuilt path below and then
            // validated by the ordinary converter.  Comparing the two raw
            // frames here would reject valid rotated/mirrored library paths.
            return historicalDirections.Count > 0 && candidateDirections.Count > 0;
        }

        private static bool TryBuildDirectionalCenterline(
            IList<Point3d> historicalPath,
            IList<Point3d> historicalDirectionPath,
            IList<Point3d> lengthReferencePath,
            double historicalLengthRatio,
            double minimumSegmentLength,
            out IList<Point3d> directionalCenterline,
            out string diagnostic)
        {
            directionalCenterline = null;
            diagnostic = null;
            var historicalSegments = BuildReducedPathSegments(historicalDirectionPath);
            var referenceSegments = BuildReducedPathSegments(lengthReferencePath);
            if (historicalSegments.Count == 0 ||
                historicalSegments.Count != referenceSegments.Count)
            {
                diagnostic = "历史路径与普通候选的方向段数不一致。";
                return false;
            }

            var directions = historicalSegments.Select(segment => segment.Direction).ToList();
            var historicalLengths = historicalSegments.Select(segment => segment.Length).ToList();
            var referenceLengths = referenceSegments.Select(segment => segment.Length).ToList();
            var baseLengths = historicalLengths
                .Select((length, index) =>
                    length * historicalLengthRatio +
                    referenceLengths[index] * (1d - historicalLengthRatio))
                .ToList();
            var startPoint = historicalPath?.FirstOrDefault(point => point != null);
            var endPoint = historicalPath?.LastOrDefault(point => point != null);
            if (startPoint == null || endPoint == null ||
                !TryFitDirectionalLengths(
                    directions,
                    baseLengths,
                    new Vector3d(startPoint, endPoint),
                    Math.Max(0.05d, minimumSegmentLength),
                    out var fittedLengths))
            {
                diagnostic = "历史方向无法在当前端点之间求出有效长度。";
                return false;
            }

            var points = new List<Point3d> { CopyPoint(startPoint) };
            var currentPoint = CopyPoint(startPoint);
            for (var segmentIndex = 0; segmentIndex < directions.Count; segmentIndex++)
            {
                var direction = directions[segmentIndex];
                var length = fittedLengths[segmentIndex];
                currentPoint = new Point3d(
                    currentPoint.X + direction.X * length,
                    currentPoint.Y + direction.Y * length,
                    currentPoint.Z + direction.Z * length);
                points.Add(currentPoint);
            }

            if (points.Last().DistanceTo(endPoint) > 2d ||
                points.Zip(points.Skip(1), (first, second) => first.DistanceTo(second))
                    .Any(length => length + 1e-6d < Math.Max(0d, minimumSegmentLength)))
            {
                diagnostic = "历史方向求出的路径端点或最小线段长度不满足当前端部约束。";
                return false;
            }
            directionalCenterline = points;
            return true;
        }

        private sealed class DirectionalPathSegment
        {
            public Vector3d Direction { get; set; }
            public double Length { get; set; }
        }

        private static IList<DirectionalPathSegment> BuildReducedPathSegments(
            IList<Point3d> points)
        {
            var segments = new List<DirectionalPathSegment>();
            foreach (var pointPair in (points ?? new List<Point3d>())
                .Zip((points ?? new List<Point3d>()).Skip(1),
                    (first, second) => new { First = first, Second = second }))
            {
                if (pointPair.First == null || pointPair.Second == null)
                {
                    continue;
                }
                var vector = new Vector3d(pointPair.First, pointPair.Second);
                if (vector.Norm <= 1e-8d)
                {
                    continue;
                }
                var direction = vector.Normalized;
                if (segments.Count > 0 &&
                    segments[segments.Count - 1].Direction.Dot(direction) >= 0.999999d)
                {
                    segments[segments.Count - 1].Length += vector.Norm;
                }
                else
                {
                    segments.Add(new DirectionalPathSegment
                    {
                        Direction = direction,
                        Length = vector.Norm
                    });
                }
            }
            return segments;
        }

        private static bool TryFitDirectionalLengths(
            IList<Vector3d> directions,
            IList<double> baseLengths,
            Vector3d targetDisplacement,
            double minimumSegmentLength,
            out IList<double> fittedLengths)
        {
            fittedLengths = null;
            if (directions == null || baseLengths == null ||
                directions.Count == 0 || directions.Count != baseLengths.Count ||
                targetDisplacement == null)
            {
                return false;
            }

            var segmentCount = directions.Count;
            var regularization = 0.0001d;
            var normalMatrix = new double[segmentCount, segmentCount];
            var rightHand = new double[segmentCount];
            for (var row = 0; row < segmentCount; row++)
            {
                for (var column = 0; column < segmentCount; column++)
                {
                    normalMatrix[row, column] =
                        directions[row].Dot(directions[column]) +
                        (row == column ? regularization : 0d);
                }
                rightHand[row] = directions[row].X * targetDisplacement.X +
                    directions[row].Y * targetDisplacement.Y +
                    directions[row].Z * targetDisplacement.Z +
                    regularization * Math.Max(minimumSegmentLength, baseLengths[row]);
            }
            if (!TrySolveLinearSystem(normalMatrix, rightHand, out var solution))
            {
                return false;
            }
            for (var index = 0; index < solution.Length; index++)
            {
                if (solution[index] < minimumSegmentLength)
                {
                    solution[index] = minimumSegmentLength;
                }
            }

            var residual = new Vector3d(0d, 0d, 0d);
            for (var index = 0; index < solution.Length; index++)
            {
                residual = new Vector3d(
                    residual.X + directions[index].X * solution[index],
                    residual.Y + directions[index].Y * solution[index],
                    residual.Z + directions[index].Z * solution[index]);
            }
            var error = new Vector3d(
                residual.X - targetDisplacement.X,
                residual.Y - targetDisplacement.Y,
                residual.Z - targetDisplacement.Z).Norm;
            if (error > 2d)
            {
                return false;
            }
            fittedLengths = solution;
            return true;
        }

        private static bool TrySolveLinearSystem(
            double[,] matrix,
            double[] rightHand,
            out double[] solution)
        {
            solution = null;
            var size = rightHand?.Length ?? 0;
            if (size == 0 || matrix == null ||
                matrix.GetLength(0) != size || matrix.GetLength(1) != size)
            {
                return false;
            }
            var augmented = new double[size, size + 1];
            for (var row = 0; row < size; row++)
            {
                for (var column = 0; column < size; column++)
                {
                    augmented[row, column] = matrix[row, column];
                }
                augmented[row, size] = rightHand[row];
            }
            for (var pivot = 0; pivot < size; pivot++)
            {
                var pivotRow = pivot;
                for (var row = pivot + 1; row < size; row++)
                {
                    if (Math.Abs(augmented[row, pivot]) >
                        Math.Abs(augmented[pivotRow, pivot]))
                    {
                        pivotRow = row;
                    }
                }
                if (Math.Abs(augmented[pivotRow, pivot]) <= 1e-10d)
                {
                    return false;
                }
                if (pivotRow != pivot)
                {
                    for (var column = pivot; column <= size; column++)
                    {
                        var swap = augmented[pivot, column];
                        augmented[pivot, column] = augmented[pivotRow, column];
                        augmented[pivotRow, column] = swap;
                    }
                }
                var pivotValue = augmented[pivot, pivot];
                for (var column = pivot; column <= size; column++)
                {
                    augmented[pivot, column] /= pivotValue;
                }
                for (var row = 0; row < size; row++)
                {
                    if (row == pivot)
                    {
                        continue;
                    }
                    var factor = augmented[row, pivot];
                    if (Math.Abs(factor) <= 1e-12d)
                    {
                        continue;
                    }
                    for (var column = pivot; column <= size; column++)
                    {
                        augmented[row, column] -= factor * augmented[pivot, column];
                    }
                }
            }
            solution = new double[size];
            for (var row = 0; row < size; row++)
            {
                solution[row] = augmented[row, size];
            }
            return solution.All(value => !double.IsNaN(value) &&
                !double.IsInfinity(value));
        }

        private static string FormatDirectionSequence(IList<Point3d> points)
        {
            return string.Join(",", BuildReducedPathDirections(points).Select(direction =>
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:0.0}/{1:0.0}/{2:0.0}",
                    direction.X,
                    direction.Y,
                    direction.Z)));
        }

        private static string GetMemberPositionToken(string memberKey)
        {
            if (string.IsNullOrWhiteSpace(memberKey))
            {
                return string.Empty;
            }

            var separatorIndex = memberKey.LastIndexOf('-');
            return separatorIndex >= 0 && separatorIndex + 1 < memberKey.Length
                ? memberKey.Substring(separatorIndex + 1)
                : memberKey;
        }

        private static LinkageTerminalMember ResolveTargetTerminalMember(
            LinkageSchemeLibraryRouteTemplateMember templateMember,
            IList<LinkageTerminalMember> targetMembers,
            LinkageSchemeLibraryRigidTransform candidateToTargetTransform)
        {
            if (templateMember == null || targetMembers == null || targetMembers.Count == 0)
            {
                return null;
            }

            if (targetMembers.Count == 1)
            {
                return targetMembers[0];
            }

            var templatePositionToken = GetMemberPositionToken(templateMember.MemberKey);
            var exactPositionMatch = targetMembers.FirstOrDefault(member =>
                string.Equals(
                    templatePositionToken,
                    GetMemberPositionToken(member?.MemberKey),
                    StringComparison.OrdinalIgnoreCase));
            if (exactPositionMatch != null)
            {
                return exactPositionMatch;
            }

            var templateCenter = GetCenterPoint(
                templateMember.CenterlinePoints,
                candidateToTargetTransform);
            return targetMembers
                .Where(member => member?.FixedBusbar?.PointList != null)
                .OrderBy(member => DistanceBetweenCenters(
                    templateCenter,
                    GetCenterPoint(member.FixedBusbar.PointList)))
                .FirstOrDefault();
        }

        private static Point3d GetCenterPoint(
            IList<LinkageSchemeLibraryVector> points,
            LinkageSchemeLibraryRigidTransform transform = null)
        {
            var validPoints = (points ?? new List<LinkageSchemeLibraryVector>())
                .Select(point => TransformPoint(transform, point))
                .Where(point => point != null)
                .ToList();
            if (validPoints.Count == 0)
            {
                return null;
            }

            return new Point3d(
                validPoints.Average(point => point.X),
                validPoints.Average(point => point.Y),
                validPoints.Average(point => point.Z));
        }

        private static Point3d GetCenterPoint(IList<Point3d> points)
        {
            var validPoints = (points ?? new List<Point3d>())
                .Where(point => point != null)
                .ToList();
            return validPoints.Count == 0
                ? null
                : new Point3d(
                    validPoints.Average(point => point.X),
                    validPoints.Average(point => point.Y),
                    validPoints.Average(point => point.Z));
        }

        private static double DistanceBetweenCenters(Point3d first, Point3d second)
        {
            return first == null || second == null
                ? double.MaxValue
                : first.DistanceTo(second);
        }

        private static LinkageSchemeLibraryRigidTransform BuildRouteCandidateToTargetTransform(
            LinkageSchemeLibraryRouteTemplateRoute routeTemplateRoute,
            LinkageRouteGroupRequest targetGroupRequest,
            LinkageSchemeLibraryMatchResult matchResult)
        {
            var fallbackTransform = matchResult?.CandidateToTargetTransform;
            var templateMembers = routeTemplateRoute?.OrderedMembers ??
                new List<LinkageSchemeLibraryRouteTemplateMember>();
            var sourceStartMember = templateMembers.FirstOrDefault(member =>
                member != null && member.PositionType == StartPositionType);
            var sourceCabinetMember = templateMembers.LastOrDefault(member =>
                member != null && member.PositionType == CabinetTopPositionType);
            var targetStartMember = ResolveTargetTerminalMember(
                sourceStartMember,
                targetGroupRequest?.StartTerminal?.Members,
                fallbackTransform);
            var targetCabinetMember = ResolveTargetTerminalMember(
                sourceCabinetMember,
                targetGroupRequest?.EndTerminal?.Members,
                fallbackTransform);

            // The profile matcher intentionally fits group anchor points first,
            // because that is the stable signal used for ranking schemes.  A
            // route cannot, however, be converted from that point-only
            // transform when the candidate and current terminal frames differ:
            // the first generated planar part then starts with a thickness
            // direction that is skew to the current fixed terminal.  Rebuild
            // each route from its actual terminal frame (thickness + width +
            // position), while retaining the matcher transform as a safe
            // fallback for legacy templates that do not carry frame vectors.
            var frameTransform = TryBuildTerminalMemberBasisTransform(
                sourceStartMember,
                targetStartMember,
                targetGroupRequest,
                out var terminalBasisDiagnostic);
            if (frameTransform != null)
            {
                return frameTransform;
            }

            frameTransform = TryBuildStartTerminalFrameTransform(
                routeTemplateRoute,
                sourceStartMember,
                targetStartMember,
                sourceCabinetMember,
                targetCabinetMember,
                targetGroupRequest,
                out var frameDiagnostic);
            if (frameTransform != null)
            {
                return frameTransform;
            }

            // Keep the old point-fit transform only when the persisted route
            // lacks enough direction data to build a terminal frame.  The
            // diagnostic is intentionally not surfaced here: the caller will
            // report the precise conversion failure instead of hiding a valid
            // candidate behind a template metadata warning.
            _ = terminalBasisDiagnostic;
            _ = frameDiagnostic;

            var sourceStartCenter = GetCenterPoint(sourceStartMember?.CenterlinePoints);
            var sourceCabinetCenter = GetCenterPoint(sourceCabinetMember?.CenterlinePoints);
            var targetStartCenter = GetCenterPoint(targetStartMember?.FixedBusbar?.PointList);
            var targetCabinetCenter = GetCenterPoint(targetCabinetMember?.FixedBusbar?.PointList);
            var sourceStartDirection = CreateDirection(
                sourceStartMember?.CenterlinePoints);
            var sourceCabinetDirection = CreateDirection(
                sourceCabinetMember?.CenterlinePoints);
            var targetStartDirection = CreateDirection(
                targetStartMember?.FixedBusbar?.PointList);
            var targetCabinetDirection = CreateDirection(
                targetCabinetMember?.FixedBusbar?.PointList);
            if (sourceStartCenter == null || sourceCabinetCenter == null ||
                targetStartCenter == null || targetCabinetCenter == null ||
                sourceStartDirection == null || sourceCabinetDirection == null ||
                targetStartDirection == null || targetCabinetDirection == null)
            {
                return fallbackTransform;
            }

            var sourceSeparation = new Vector3d(sourceStartCenter, sourceCabinetCenter);
            var targetSeparation = new Vector3d(targetStartCenter, targetCabinetCenter);
            var sourceBasisB = Orthogonalize(sourceSeparation, sourceStartDirection);
            var sourceBasisA = sourceStartDirection.Normalized;
            var targetBasisA = targetStartDirection.Normalized;
            if (sourceBasisA.Dot(sourceSeparation) *
                targetBasisA.Dot(targetSeparation) < 0d)
            {
                targetBasisA = Negate(targetBasisA);
            }
            var targetBasisB = Orthogonalize(targetSeparation, targetBasisA);
            if (sourceBasisB == null || targetBasisB == null)
            {
                return fallbackTransform;
            }

            var sourceBasisC = sourceBasisA.Cross(sourceBasisB).Normalized;
            var targetBasisC = targetBasisA.Cross(targetBasisB).Normalized;
            if (sourceBasisC == null || targetBasisC == null ||
                sourceBasisC.Norm <= 1e-8d || targetBasisC.Norm <= 1e-8d)
            {
                return fallbackTransform;
            }

            var transform = CreateBasisTransform(
                sourceBasisA,
                sourceBasisB,
                sourceBasisC,
                targetBasisA,
                targetBasisB,
                targetBasisC,
                sourceStartCenter,
                targetStartCenter);
            return transform ?? fallbackTransform;
        }

        private static LinkageSchemeLibraryRigidTransform
            TryBuildStartTerminalFrameTransform(
            LinkageSchemeLibraryRouteTemplateRoute routeTemplateRoute,
            LinkageSchemeLibraryRouteTemplateMember sourceStartMember,
            LinkageTerminalMember targetStartMember,
            LinkageSchemeLibraryRouteTemplateMember sourceCabinetMember,
            LinkageTerminalMember targetCabinetMember,
            LinkageRouteGroupRequest targetGroupRequest,
            out string diagnostic)
        {
            diagnostic = null;
            if (sourceStartMember == null || targetStartMember?.FixedBusbar == null)
            {
                diagnostic = "missing start terminal member";
                return null;
            }

            var sourceThickness = ToVector(sourceStartMember.StartThicknessDirection);
            var targetThickness = targetStartMember.FixedBusbar.StartBusbarVector ??
                targetGroupRequest?.StartTerminal?.ThicknessDirection;
            var sourceStartOrigin = GetCenterPoint(sourceStartMember.CenterlinePoints);
            var targetStartOrigin = GetCenterPoint(targetStartMember.FixedBusbar.PointList);
            if (sourceStartOrigin == null || targetStartOrigin == null)
            {
                diagnostic = "start terminal frame origin is invalid";
                return null;
            }

            var sourceCabinetOrigin = GetCenterPoint(sourceCabinetMember?.CenterlinePoints);
            var targetCabinetOrigin = GetCenterPoint(targetCabinetMember?.FixedBusbar?.PointList);
            var sourceSeparation = sourceCabinetOrigin == null
                ? null
                : new Vector3d(sourceStartOrigin, sourceCabinetOrigin);
            var targetSeparation = targetCabinetOrigin == null
                ? null
                : new Vector3d(targetStartOrigin, targetCabinetOrigin);
            var sourceBasisA = sourceThickness?.Normalized;
            var targetBasisA = targetThickness?.Normalized;
            var sourceBasisB = sourceSeparation == null
                ? null
                : Orthogonalize(sourceSeparation, sourceBasisA);
            var targetBasisB = targetSeparation == null
                ? null
                : Orthogonalize(targetSeparation, targetBasisA);
            if (sourceBasisA == null || targetBasisA == null ||
                sourceBasisB == null || targetBasisB == null)
            {
                // A legacy route may not have a usable cabinet anchor.  In
                // that case fall back to its persisted width frame so the
                // historical transform remains available.
                if (!TryBuildFrame(
                        sourceThickness,
                        ToVector(sourceStartMember.WidthDirection),
                        out sourceBasisA,
                        out sourceBasisB,
                        out var sourceBasisC) ||
                    !TryBuildFrame(
                        targetThickness,
                        targetStartMember.FixedBusbar.WidthVector,
                        out targetBasisA,
                        out targetBasisB,
                        out var targetBasisC))
                {
                    diagnostic = "start terminal frame vectors are invalid";
                    return null;
                }

                return CreateBasisTransform(
                    sourceBasisA,
                    sourceBasisB,
                    sourceBasisC,
                    targetBasisA,
                    targetBasisB,
                    targetBasisC,
                    sourceStartOrigin,
                    targetStartOrigin);
            }

            var sourceBasisCFromSeparation = sourceBasisA.Cross(sourceBasisB).Normalized;
            var targetBasisCFromSeparation = targetBasisA.Cross(targetBasisB).Normalized;
            if (sourceBasisCFromSeparation == null || targetBasisCFromSeparation == null)
            {
                diagnostic = "start terminal separation frame is invalid";
                return null;
            }

            var frameTransform = CreateBasisTransform(
                sourceBasisA,
                sourceBasisB,
                sourceBasisCFromSeparation,
                targetBasisA,
                targetBasisB,
                targetBasisCFromSeparation,
                sourceStartOrigin,
                targetStartOrigin);
            if (frameTransform == null)
            {
                return null;
            }

            // Refine translation from the recorded start lap center.  The
            // target projection is authoritative for the current fixed start
            // bar; the end projection is only a fallback when the start
            // interface metadata is unavailable.  This keeps the frame
            // rotation that makes the route planar instead of averaging two
            // incompatible terminal offsets.
            var routeMiddleMembers = (routeTemplateRoute?.OrderedMembers ??
                new List<LinkageSchemeLibraryRouteTemplateMember>())
                .Where(member => member != null && member.PositionType == MiddlePositionType)
                .ToList();
            var translationCorrections = new List<Vector3d>();
            if (routeMiddleMembers.Count > 0)
            {
                var sourceStartInterface = ResolveTemplateInterfacePoint(
                    routeTemplateRoute,
                    sourceStartMember.MemberKey,
                    routeMiddleMembers[0].MemberKey,
                    null);
                var transformedStartInterface = TransformPoint(
                    frameTransform,
                    ToVector(sourceStartInterface));
                var targetStartInterface = ProjectPointToPolyline(
                    transformedStartInterface,
                    targetStartMember.FixedBusbar.PointList)?.Point;
                if (transformedStartInterface != null && targetStartInterface != null)
                {
                    translationCorrections.Add(new Vector3d(
                        transformedStartInterface,
                        targetStartInterface));
                }

                if (translationCorrections.Count == 0)
                {
                    var sourceEndInterface = ResolveTemplateInterfacePoint(
                        routeTemplateRoute,
                        routeMiddleMembers[routeMiddleMembers.Count - 1].MemberKey,
                        sourceCabinetMember?.MemberKey,
                        null);
                    var transformedEndInterface = TransformPoint(
                        frameTransform,
                        ToVector(sourceEndInterface));
                    var targetEndInterface = ProjectPointToPolyline(
                        transformedEndInterface,
                        targetCabinetMember?.FixedBusbar?.PointList)?.Point;
                    if (transformedEndInterface != null && targetEndInterface != null)
                    {
                        translationCorrections.Add(new Vector3d(
                            transformedEndInterface,
                            targetEndInterface));
                    }
                }
            }
            if (translationCorrections.Count > 0)
            {
                frameTransform.TranslationX += translationCorrections.Average(vector => vector.X);
                frameTransform.TranslationY += translationCorrections.Average(vector => vector.Y);
                frameTransform.TranslationZ += translationCorrections.Average(vector => vector.Z);
            }

            return frameTransform;
        }

        private static LinkageSchemeLibraryRigidTransform
            TryBuildTerminalMemberBasisTransform(
            LinkageSchemeLibraryRouteTemplateMember sourceStartMember,
            LinkageTerminalMember targetStartMember,
            LinkageRouteGroupRequest targetGroupRequest,
            out string diagnostic)
        {
            diagnostic = null;
            if (sourceStartMember == null || targetStartMember?.FixedBusbar == null)
            {
                diagnostic = "terminal basis members are missing";
                return null;
            }

            var sourceThickness = ToVector(sourceStartMember.StartThicknessDirection);
            var sourceWidth = ToVector(sourceStartMember.WidthDirection);
            var targetThickness = targetStartMember.FixedBusbar.StartBusbarVector ??
                targetGroupRequest?.StartTerminal?.ThicknessDirection;
            var targetWidth = targetStartMember.FixedBusbar.WidthVector;
            if (!TryBuildFrame(
                    sourceThickness,
                    sourceWidth,
                    out var sourceBasisA,
                    out var sourceBasisB,
                    out var sourceBasisC) ||
                !TryBuildFrame(
                    targetThickness,
                    targetWidth,
                    out var targetBasisA,
                    out var targetBasisB,
                    out var targetBasisC))
            {
                diagnostic = "terminal basis vectors are invalid";
                return null;
            }

            var sourceOrigin = GetCenterPoint(sourceStartMember.CenterlinePoints);
            var targetOrigin = GetCenterPoint(targetStartMember.FixedBusbar.PointList);
            if (sourceOrigin == null || targetOrigin == null)
            {
                diagnostic = "terminal basis origins are invalid";
                return null;
            }

            return CreateBasisTransform(
                sourceBasisA,
                sourceBasisB,
                sourceBasisC,
                targetBasisA,
                targetBasisB,
                targetBasisC,
                sourceOrigin,
                targetOrigin);
        }

        private static bool TryBuildFrame(
            Vector3d thicknessDirection,
            Vector3d widthDirection,
            out Vector3d basisA,
            out Vector3d basisB,
            out Vector3d basisC)
        {
            basisA = thicknessDirection?.Normalized;
            basisB = null;
            basisC = null;
            if (basisA == null || basisA.Norm <= 1e-8d ||
                widthDirection == null || widthDirection.Norm <= 1e-8d)
            {
                return false;
            }

            basisB = Orthogonalize(widthDirection, basisA);
            if (basisB == null || basisB.Norm <= 1e-8d)
            {
                return false;
            }

            basisC = basisA.Cross(basisB).Normalized;
            return basisC != null && basisC.Norm > 1e-8d;
        }

        private static Vector3d CreateDirection(IList<Point3d> points)
        {
            if (points == null || points.Count < 2 || points[0] == null ||
                points[points.Count - 1] == null)
            {
                return null;
            }

            var direction = new Vector3d(points[0], points[points.Count - 1]);
            return direction.Norm <= 1e-8d ? null : direction.Normalized;
        }

        private static Vector3d CreateDirection(IList<LinkageSchemeLibraryVector> points)
        {
            if (points == null || points.Count < 2 || points[0] == null ||
                points[points.Count - 1] == null)
            {
                return null;
            }

            var direction = new Vector3d(
                points[0].X,
                points[0].Y,
                points[0].Z);
            var endDirection = new Vector3d(
                points[points.Count - 1].X,
                points[points.Count - 1].Y,
                points[points.Count - 1].Z);
            direction = new Vector3d(
                endDirection.X - direction.X,
                endDirection.Y - direction.Y,
                endDirection.Z - direction.Z);
            return direction.Norm <= 1e-8d ? null : direction.Normalized;
        }

        private static Vector3d Orthogonalize(Vector3d vector, Vector3d axis)
        {
            if (vector == null || axis == null || vector.Norm <= 1e-8d ||
                axis.Norm <= 1e-8d)
            {
                return null;
            }

            var normalizedAxis = axis.Normalized;
            var projection = vector.Dot(normalizedAxis);
            var orthogonal = new Vector3d(
                vector.X - normalizedAxis.X * projection,
                vector.Y - normalizedAxis.Y * projection,
                vector.Z - normalizedAxis.Z * projection);
            return orthogonal.Norm <= 1e-8d ? null : orthogonal.Normalized;
        }

        private static Vector3d Negate(Vector3d vector)
        {
            return vector == null
                ? null
                : new Vector3d(-vector.X, -vector.Y, -vector.Z);
        }

        private static LinkageSchemeLibraryRigidTransform CreateBasisTransform(
            Vector3d sourceBasisA,
            Vector3d sourceBasisB,
            Vector3d sourceBasisC,
            Vector3d targetBasisA,
            Vector3d targetBasisB,
            Vector3d targetBasisC,
            Point3d sourceOrigin,
            Point3d targetOrigin)
        {
            var m11 = targetBasisA.X * sourceBasisA.X +
                targetBasisB.X * sourceBasisB.X + targetBasisC.X * sourceBasisC.X;
            var m12 = targetBasisA.X * sourceBasisA.Y +
                targetBasisB.X * sourceBasisB.Y + targetBasisC.X * sourceBasisC.Y;
            var m13 = targetBasisA.X * sourceBasisA.Z +
                targetBasisB.X * sourceBasisB.Z + targetBasisC.X * sourceBasisC.Z;
            var m21 = targetBasisA.Y * sourceBasisA.X +
                targetBasisB.Y * sourceBasisB.X + targetBasisC.Y * sourceBasisC.X;
            var m22 = targetBasisA.Y * sourceBasisA.Y +
                targetBasisB.Y * sourceBasisB.Y + targetBasisC.Y * sourceBasisC.Y;
            var m23 = targetBasisA.Y * sourceBasisA.Z +
                targetBasisB.Y * sourceBasisB.Z + targetBasisC.Y * sourceBasisC.Z;
            var m31 = targetBasisA.Z * sourceBasisA.X +
                targetBasisB.Z * sourceBasisB.X + targetBasisC.Z * sourceBasisC.X;
            var m32 = targetBasisA.Z * sourceBasisA.Y +
                targetBasisB.Z * sourceBasisB.Y + targetBasisC.Z * sourceBasisC.Y;
            var m33 = targetBasisA.Z * sourceBasisA.Z +
                targetBasisB.Z * sourceBasisB.Z + targetBasisC.Z * sourceBasisC.Z;
            return new LinkageSchemeLibraryRigidTransform
            {
                M11 = m11,
                M12 = m12,
                M13 = m13,
                M21 = m21,
                M22 = m22,
                M23 = m23,
                M31 = m31,
                M32 = m32,
                M33 = m33,
                TranslationX = targetOrigin.X -
                    (m11 * sourceOrigin.X + m12 * sourceOrigin.Y + m13 * sourceOrigin.Z),
                TranslationY = targetOrigin.Y -
                    (m21 * sourceOrigin.X + m22 * sourceOrigin.Y + m23 * sourceOrigin.Z),
                TranslationZ = targetOrigin.Z -
                    (m31 * sourceOrigin.X + m32 * sourceOrigin.Y + m33 * sourceOrigin.Z)
            };
        }

        private static bool TryResolveTargetGroup(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageSchemeLibraryMatchResult matchResult,
            LinkageSchemeLibraryRouteTemplateRoute routeTemplateRoute,
            out LinkageRouteGroupRequest targetGroupRequest,
            out string diagnostic)
        {
            targetGroupRequest = null;
            diagnostic = "方案库路径缺少当前目标组映射。";
            if (routeTemplateRoute == null)
            {
                diagnostic = "方案库路径为空。";
                return false;
            }

            var startMapping = (matchResult.Mappings ?? new List<LinkageSchemeLibraryGroupMapping>())
                .FirstOrDefault(mapping => mapping != null &&
                    mapping.PositionType == StartPositionType &&
                    string.Equals(mapping.CandidateGroupKey, routeTemplateRoute.CandidateStartGroupKey,
                        StringComparison.OrdinalIgnoreCase));
            var cabinetMapping = (matchResult.Mappings ?? new List<LinkageSchemeLibraryGroupMapping>())
                .FirstOrDefault(mapping => mapping != null &&
                    mapping.PositionType == CabinetTopPositionType &&
                    string.Equals(mapping.CandidateGroupKey, routeTemplateRoute.CandidateCabinetTopGroupKey,
                        StringComparison.OrdinalIgnoreCase));
            var targetGroupKey = startMapping?.TargetGroupKey ?? cabinetMapping?.TargetGroupKey;
            targetGroupRequest = routeRequest.Groups?.FirstOrDefault(group => group != null &&
                string.Equals(group.GroupKey, targetGroupKey, StringComparison.OrdinalIgnoreCase));
            if (targetGroupRequest == null)
            {
                diagnostic = "方案库路径的当前目标组不存在：" + (targetGroupKey ?? string.Empty);
                return false;
            }

            return true;
        }

        private static List<Point3d> BuildCurrentTerminalConstrainedCenterline(
            LinkageSchemeLibraryRouteTemplateRoute routeTemplateRoute,
            IList<LinkageSchemeLibraryRouteTemplateMember> middleMembers,
            LinkageRouteGroupRequest targetGroupRequest,
            LinkageSchemeLibraryRigidTransform routeCandidateToTargetTransform)
        {
            var result = new List<Point3d>();
            var startTemplateMember = routeTemplateRoute.OrderedMembers
                ?.FirstOrDefault(member => member != null &&
                    member.PositionType == StartPositionType);
            var endTemplateMember = routeTemplateRoute.OrderedMembers
                ?.LastOrDefault(member => member != null &&
                    member.PositionType == CabinetTopPositionType);
            var startTemplateMemberKey = startTemplateMember?.MemberKey;
            var cabinetTemplateMemberKey = endTemplateMember?.MemberKey;
            var previousTemplateMemberKey = startTemplateMemberKey;
            for (var middleMemberIndex = 0;
                middleMemberIndex < middleMembers.Count;
                middleMemberIndex++)
            {
                var middleMember = middleMembers[middleMemberIndex];
                var transformedMiddlePoints = (middleMember.CenterlinePoints ??
                    new List<LinkageSchemeLibraryVector>()).Select(point =>
                    TransformPoint(routeCandidateToTargetTransform, point))
                    .Where(point => point != null)
                    .ToList();
                if (transformedMiddlePoints.Count < 2)
                {
                    continue;
                }

                var nextTemplateMemberKey = middleMemberIndex + 1 < middleMembers.Count
                    ? middleMembers[middleMemberIndex + 1]?.MemberKey
                    : cabinetTemplateMemberKey;
                var previousConnectionPoint = ResolveTemplateInterfacePoint(
                    routeTemplateRoute,
                    previousTemplateMemberKey,
                    middleMember?.MemberKey,
                    routeCandidateToTargetTransform);
                if (middleMemberIndex == 0)
                {
                    previousConnectionPoint = ResolveTargetTerminalConnectionPoint(
                        startTemplateMember,
                        previousConnectionPoint,
                        targetGroupRequest?.StartTerminal,
                        routeCandidateToTargetTransform,
                        true);
                }
                var nextConnectionPoint = ResolveTemplateInterfacePoint(
                    routeTemplateRoute,
                    middleMember?.MemberKey,
                    nextTemplateMemberKey,
                    routeCandidateToTargetTransform);
                if (middleMemberIndex == middleMembers.Count - 1)
                {
                    nextConnectionPoint = ResolveTargetTerminalConnectionPoint(
                        endTemplateMember,
                        nextConnectionPoint,
                        targetGroupRequest?.EndTerminal,
                        routeCandidateToTargetTransform,
                        false);
                }

                if (previousConnectionPoint == null)
                {
                    previousConnectionPoint = CopyPoint(transformedMiddlePoints[0]);
                }
                if (nextConnectionPoint == null)
                {
                    nextConnectionPoint = CopyPoint(
                        transformedMiddlePoints[transformedMiddlePoints.Count - 1]);
                }

                // The stored middle member is a physical busbar path and its
                // recorded lap center can be in the middle of a segment.  A
                // route seed must therefore be clipped between the two lap
                // centers instead of concatenating the whole member.  This
                // preserves the historical direction/length while making the
                // current terminal positions the authoritative endpoints.
                var clippedMiddlePath = BuildClippedMemberPath(
                    transformedMiddlePoints,
                    previousConnectionPoint,
                    nextConnectionPoint);
                AppendPoints(result, clippedMiddlePath);
                previousTemplateMemberKey = middleMember?.MemberKey;
            }
            return RemoveCoincidentPoints(result);
        }

        private static List<Point3d> BuildHistoricalDirectionCenterline(
            LinkageSchemeLibraryRouteTemplateRoute routeTemplateRoute,
            IList<LinkageSchemeLibraryRouteTemplateMember> middleMembers,
            LinkageSchemeLibraryRigidTransform routeCandidateToTargetTransform)
        {
            var result = new List<Point3d>();
            var startTemplateMember = routeTemplateRoute?.OrderedMembers
                ?.FirstOrDefault(member => member != null &&
                    member.PositionType == StartPositionType);
            var cabinetTemplateMember = routeTemplateRoute?.OrderedMembers
                ?.LastOrDefault(member => member != null &&
                    member.PositionType == CabinetTopPositionType);
            var previousTemplateMemberKey = startTemplateMember?.MemberKey;
            for (var middleMemberIndex = 0;
                middleMemberIndex < (middleMembers?.Count ?? 0);
                middleMemberIndex++)
            {
                var middleMember = middleMembers[middleMemberIndex];
                var transformedPoints = (middleMember?.CenterlinePoints ??
                    new List<LinkageSchemeLibraryVector>())
                    .Select(point => TransformPoint(
                        routeCandidateToTargetTransform,
                        point))
                    .Where(point => point != null)
                    .ToList();
                if (transformedPoints.Count < 2)
                {
                    continue;
                }

                var nextTemplateMemberKey = middleMemberIndex + 1 < middleMembers.Count
                    ? middleMembers[middleMemberIndex + 1]?.MemberKey
                    : cabinetTemplateMember?.MemberKey;
                var previousInterface = ResolveTemplateInterfacePoint(
                    routeTemplateRoute,
                    previousTemplateMemberKey,
                    middleMember?.MemberKey,
                    routeCandidateToTargetTransform);
                var nextInterface = ResolveTemplateInterfacePoint(
                    routeTemplateRoute,
                    middleMember?.MemberKey,
                    nextTemplateMemberKey,
                    routeCandidateToTargetTransform);
                var startProjection = ProjectPointToPolyline(
                    previousInterface,
                    transformedPoints);
                var endProjection = ProjectPointToPolyline(
                    nextInterface,
                    transformedPoints);
                if (startProjection != null && endProjection != null &&
                    startProjection.DistanceAlong > endProjection.DistanceAlong)
                {
                    transformedPoints = transformedPoints.AsEnumerable()
                        .Reverse()
                        .Select(CopyPoint)
                        .ToList();
                }
                AppendPoints(result, transformedPoints);
                previousTemplateMemberKey = middleMember?.MemberKey;
            }
            return RemoveCoincidentPoints(result);
        }

        private static List<Vector3d> BuildHistoricalDirectionSequence(
            LinkageSchemeLibraryRouteTemplateRoute routeTemplateRoute,
            IList<LinkageSchemeLibraryRouteTemplateMember> middleMembers,
            LinkageSchemeLibraryRigidTransform routeCandidateToTargetTransform)
        {
            var result = new List<Vector3d>();
            var startTemplateMember = routeTemplateRoute?.OrderedMembers
                ?.FirstOrDefault(member => member != null &&
                    member.PositionType == StartPositionType);
            var cabinetTemplateMember = routeTemplateRoute?.OrderedMembers
                ?.LastOrDefault(member => member != null &&
                    member.PositionType == CabinetTopPositionType);
            var previousTemplateMemberKey = startTemplateMember?.MemberKey;
            for (var middleMemberIndex = 0;
                middleMemberIndex < (middleMembers?.Count ?? 0);
                middleMemberIndex++)
            {
                var middleMember = middleMembers[middleMemberIndex];
                var transformedPoints = (middleMember?.CenterlinePoints ??
                        new List<LinkageSchemeLibraryVector>())
                    .Select(point => TransformPoint(
                        routeCandidateToTargetTransform,
                        point))
                    .Where(point => point != null)
                    .ToList();
                if (transformedPoints.Count < 2)
                {
                    continue;
                }

                var nextTemplateMemberKey = middleMemberIndex + 1 < middleMembers.Count
                    ? middleMembers[middleMemberIndex + 1]?.MemberKey
                    : cabinetTemplateMember?.MemberKey;
                var previousInterface = ResolveTemplateInterfacePoint(
                    routeTemplateRoute,
                    previousTemplateMemberKey,
                    middleMember?.MemberKey,
                    routeCandidateToTargetTransform);
                var nextInterface = ResolveTemplateInterfacePoint(
                    routeTemplateRoute,
                    middleMember?.MemberKey,
                    nextTemplateMemberKey,
                    routeCandidateToTargetTransform);
                var startProjection = ProjectPointToPolyline(
                    previousInterface,
                    transformedPoints);
                var endProjection = ProjectPointToPolyline(
                    nextInterface,
                    transformedPoints);
                if (startProjection != null && endProjection != null &&
                    startProjection.DistanceAlong > endProjection.DistanceAlong)
                {
                    transformedPoints = transformedPoints.AsEnumerable()
                        .Reverse()
                        .Select(CopyPoint)
                        .ToList();
                }

                // Do not concatenate separate historical member point lists:
                // the gap between two overlapping physical bars is not a path
                // segment and must never become a fake oblique direction.
                foreach (var direction in BuildPathDirections(transformedPoints))
                {
                    if (result.Count == 0 ||
                        result[result.Count - 1].Dot(direction) < 0.999999d)
                    {
                        result.Add(direction);
                    }
                }
                previousTemplateMemberKey = middleMember?.MemberKey;
            }
            return result;
        }

        private static Point3d ResolveTargetTerminalConnectionPoint(
            LinkageSchemeLibraryRouteTemplateMember templateTerminalMember,
            Point3d transformedInterfacePoint,
            LinkageTerminalAssembly targetTerminalAssembly,
            LinkageSchemeLibraryRigidTransform candidateToTargetTransform,
            bool useStartTerminal)
        {
            var targetMember = ResolveTargetTerminalMember(
                templateTerminalMember,
                (targetTerminalAssembly?.Members),
                candidateToTargetTransform);
            var fixedPoints = targetMember?.FixedBusbar?.PointList;
            var projectedPoint = ProjectPointToPolyline(
                transformedInterfacePoint,
                fixedPoints)?.Point;
            if (projectedPoint != null)
            {
                return projectedPoint;
            }

            if (targetTerminalAssembly?.RouteAnchor != null)
            {
                return CopyPoint(targetTerminalAssembly.RouteAnchor);
            }

            if (fixedPoints != null && fixedPoints.Count > 0)
            {
                return CopyPoint(useStartTerminal
                    ? fixedPoints[fixedPoints.Count - 1]
                    : fixedPoints[0]);
            }

            return transformedInterfacePoint == null
                ? null
                : CopyPoint(transformedInterfacePoint);
        }

        private sealed class PolylineProjection
        {
            public Point3d Point { get; set; }
            public int SegmentIndex { get; set; }
            public double DistanceAlong { get; set; }
        }

        private static IList<Point3d> BuildClippedMemberPath(
            IList<Point3d> sourcePoints,
            Point3d startConnectionPoint,
            Point3d endConnectionPoint)
        {
            var normalizedSourcePoints = RemoveCoincidentPoints(sourcePoints);
            if (normalizedSourcePoints.Count < 2)
            {
                return normalizedSourcePoints;
            }

            var orientedPoints = normalizedSourcePoints;
            var startProjection = ProjectPointToPolyline(
                startConnectionPoint,
                orientedPoints);
            var endProjection = ProjectPointToPolyline(
                endConnectionPoint,
                orientedPoints);
            if (startProjection == null || endProjection == null)
            {
                return normalizedSourcePoints;
            }

            if (startProjection.DistanceAlong > endProjection.DistanceAlong)
            {
                orientedPoints = normalizedSourcePoints.AsEnumerable()
                    .Reverse()
                    .Select(CopyPoint)
                    .ToList();
                startProjection = ProjectPointToPolyline(
                    startConnectionPoint,
                    orientedPoints);
                endProjection = ProjectPointToPolyline(
                    endConnectionPoint,
                    orientedPoints);
            }

            var clippedPath = new List<Point3d>();
            // Use the projection on the historical member as the actual
            // route endpoint.  The recorded lap center is normally inside a
            // fixed/member overlap and is not on the centerline itself; if it
            // were emitted as a separate first point, the converter would see
            // a short thickness-direction segment and reject the route before
            // it could resolve terminal stack placement.
            AppendPoints(clippedPath, new[]
            {
                startProjection?.Point ?? startConnectionPoint
            });

            if (startProjection != null && endProjection != null)
            {
                var firstInteriorPointIndex = startProjection.SegmentIndex + 1;
                var lastInteriorPointIndex = endProjection.SegmentIndex;
                for (var pointIndex = firstInteriorPointIndex;
                    pointIndex <= lastInteriorPointIndex &&
                    pointIndex < orientedPoints.Count - 1;
                    pointIndex++)
                {
                    AppendPoints(clippedPath, new[] { orientedPoints[pointIndex] });
                }
                AppendPoints(clippedPath, new[]
                {
                    endProjection.Point
                });
            }
            else
            {
                AppendPoints(clippedPath, new[] { endConnectionPoint });
            }
            return RemoveCoincidentPoints(clippedPath);
        }

        private static PolylineProjection ProjectPointToPolyline(
            Point3d point,
            IList<Point3d> polyline)
        {
            if (point == null || polyline == null || polyline.Count < 2)
            {
                return null;
            }

            PolylineProjection nearestProjection = null;
            var distanceAlong = 0d;
            for (var segmentIndex = 0;
                segmentIndex + 1 < polyline.Count;
                segmentIndex++)
            {
                var segmentStart = polyline[segmentIndex];
                var segmentEnd = polyline[segmentIndex + 1];
                if (segmentStart == null || segmentEnd == null)
                {
                    continue;
                }

                var segmentVector = new Vector3d(segmentStart, segmentEnd);
                var segmentLength = segmentVector.Norm;
                if (segmentLength <= 1e-8d)
                {
                    continue;
                }

                var pointVector = new Vector3d(segmentStart, point);
                var parameter = pointVector.Dot(segmentVector) /
                    (segmentLength * segmentLength);
                parameter = Math.Max(0d, Math.Min(1d, parameter));
                var projection = new Point3d(
                    segmentStart.X + segmentVector.X * parameter,
                    segmentStart.Y + segmentVector.Y * parameter,
                    segmentStart.Z + segmentVector.Z * parameter);
                var distance = projection.DistanceTo(point);
                if (nearestProjection == null || distance <
                    nearestProjection.Point.DistanceTo(point))
                {
                    nearestProjection = new PolylineProjection
                    {
                        Point = projection,
                        SegmentIndex = segmentIndex,
                        DistanceAlong = distanceAlong + segmentLength * parameter
                    };
                }
                distanceAlong += segmentLength;
            }
            return nearestProjection;
        }

        private static Point3d ResolveTemplateInterfacePoint(
            LinkageSchemeLibraryRouteTemplateRoute routeTemplateRoute,
            string firstMemberKey,
            string secondMemberKey,
            LinkageSchemeLibraryRigidTransform transform)
        {
            if (routeTemplateRoute?.OrderedInterfaces == null ||
                string.IsNullOrWhiteSpace(firstMemberKey) ||
                string.IsNullOrWhiteSpace(secondMemberKey))
            {
                return null;
            }

            var routeInterface = routeTemplateRoute.OrderedInterfaces.FirstOrDefault(
                candidateInterface => candidateInterface != null &&
                    ((string.Equals(candidateInterface.FirstMemberKey, firstMemberKey,
                        StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(candidateInterface.SecondMemberKey, secondMemberKey,
                        StringComparison.OrdinalIgnoreCase)) ||
                     (string.Equals(candidateInterface.FirstMemberKey, secondMemberKey,
                        StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(candidateInterface.SecondMemberKey, firstMemberKey,
                        StringComparison.OrdinalIgnoreCase))));
            return routeInterface?.Center == null
                ? null
                : TransformPoint(transform, routeInterface.Center);
        }

        private static void AppendPoints(ICollection<Point3d> target, IEnumerable<Point3d> points)
        {
            foreach (var point in points ?? Enumerable.Empty<Point3d>())
            {
                if (point != null)
                {
                    target.Add(CopyPoint(point));
                }
            }
        }

        private static List<Point3d> RemoveCoincidentPoints(IEnumerable<Point3d> points)
        {
            var result = new List<Point3d>();
            foreach (var point in points ?? Enumerable.Empty<Point3d>())
            {
                if (point == null)
                {
                    continue;
                }
                if (result.Count == 0 || result[result.Count - 1].DistanceTo(point) > 1e-7d)
                {
                    result.Add(CopyPoint(point));
                }
            }
            return result;
        }

        private static void RemoveCoincidentPoints(
            IList<Point3d> points,
            out List<Point3d> normalizedPoints)
        {
            normalizedPoints = RemoveCoincidentPoints(points);
        }

        private static Point3d TransformPoint(LinkageSchemeLibraryRigidTransform transform,
            LinkageSchemeLibraryVector point)
        {
            var transformed = transform?.Transform(point) ?? point;
            return transformed == null ? null : new Point3d(transformed.X, transformed.Y, transformed.Z);
        }

        private static Point3d TransformPoint(
            LinkageSchemeLibraryRigidTransform transform,
            Point3d point)
        {
            if (point == null)
            {
                return null;
            }

            return new Point3d(
                (transform?.M11 ?? 1d) * point.X +
                (transform?.M12 ?? 0d) * point.Y +
                (transform?.M13 ?? 0d) * point.Z +
                (transform?.TranslationX ?? 0d),
                (transform?.M21 ?? 0d) * point.X +
                (transform?.M22 ?? 1d) * point.Y +
                (transform?.M23 ?? 0d) * point.Z +
                (transform?.TranslationY ?? 0d),
                (transform?.M31 ?? 0d) * point.X +
                (transform?.M32 ?? 0d) * point.Y +
                (transform?.M33 ?? 1d) * point.Z +
                (transform?.TranslationZ ?? 0d));
        }

        private static LinkageSchemeLibraryVector TransformVector(
            LinkageSchemeLibraryRigidTransform transform,
            LinkageSchemeLibraryVector vector)
        {
            return transform?.TransformVector(vector) ?? vector;
        }

        private static Vector3d ToVector(LinkageSchemeLibraryVector vector)
        {
            return vector == null ? null : new Vector3d(vector.X, vector.Y, vector.Z);
        }

        private static LinkageSchemeLibraryVector ToVector(Point3d point)
        {
            return point == null
                ? null
                : new LinkageSchemeLibraryVector
                {
                    X = point.X,
                    Y = point.Y,
                    Z = point.Z
                };
        }

        private static Point3d CopyPoint(Point3d point)
        {
            return point == null ? null : new Point3d(point.X, point.Y, point.Z);
        }

        private static LinkageStackedRouteMember CopyStackMember(LinkageStackedRouteMember source)
        {
            return new LinkageStackedRouteMember
            {
                MemberIndex = source.MemberIndex,
                CenterlineOffset = source.CenterlineOffset,
                PlanarParts = source.PlanarParts?.Select(setting => setting.Copy()).ToList() ??
                    new List<PlanarBendBusbarSetting>()
            };
        }

        private static LinkageRouteAccessory CopyAccessory(LinkageRouteAccessory source)
        {
            return new LinkageRouteAccessory
            {
                Kind = source.Kind,
                TerminalSide = source.TerminalSide,
                RelatedGeneratedMemberIndex = source.RelatedGeneratedMemberIndex,
                Setting = source.Setting?.Copy(),
                SupportBlockKey = source.SupportBlockKey
            };
        }

        private static void AddFailure(LinkageRouteSolution solution, string diagnostic)
        {
            solution.Diagnostics.Add(new LinkageRouteFailureDiagnostic
            {
                Stage = LinkageRouteFailureStage.Conversion,
                Message = diagnostic ?? "方案库路径重算失败。"
            });
        }
    }
}
