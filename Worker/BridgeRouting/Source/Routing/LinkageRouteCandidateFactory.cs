using GeometRi;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;

namespace RouteLinkBar.Algorithm.Routing
{
    internal enum LinkageStartPerpendicularLapVariant
    {
        None = 0,
        Full = 1,
        Partial = 2
    }

    internal sealed class LinkageRouteCandidate
    {
        private static long nextCacheIdentity;
        private LinkageRouteGroupRequest groupRequest;
        private IList<Point3d> axisSkeleton;
        private IList<Point3d> manufacturableCenterline;
        private string topologySignature;
        private string identitySignature;

        /// <summary>
        /// 搜索上下文内的候选实例身份。即使两个候选具有相同中心线，
        /// 双始端桥排、拆排或后处理后的实际实体仍可能不同，不得共用
        /// 精确净距缓存。
        /// </summary>
        internal long CacheIdentity { get; } = Interlocked.Increment(
            ref nextCacheIdentity);

        public LinkageRouteGroupRequest GroupRequest
        {
            get => groupRequest;
            set
            {
                groupRequest = value;
                InvalidateSignatureCaches();
            }
        }

        public IList<Point3d> AxisSkeleton
        {
            get => axisSkeleton;
            set
            {
                axisSkeleton = value;
                InvalidateSignatureCaches();
            }
        }

        public IList<Point3d> ManufacturableCenterline
        {
            get => manufacturableCenterline;
            set
            {
                manufacturableCenterline = value;
                InvalidateSignatureCaches();
            }
        }
        public LinkageRouteCenterlineConversionResult PlanarConversion { get; set; }
        internal LinkageRouteCandidate ReverseUSplitCandidate { get; set; }
        public IList<LinkageRouteSolid> ScanSolids { get; } = new List<LinkageRouteSolid>();
        /// <summary>
        /// 最终相间绝缘使用的实际矩形实体。普通叠排仍只需保存物理最外侧
        /// 成员；端头无垫块后每根排的 PointList 已分别移动，不能再用叠排
        /// 外包络代表整相，必须保存全部实际成员及附件的逐段矩形实体。
        /// 每个实体同时携带逻辑组和相序键，始端/柜顶固定排由同一相序键
        /// 排除，同相内部不参与绝缘距离，跨相才执行最小净距计算。
        /// </summary>
        public IList<LinkageRouteSolid> ActualClearanceSolids { get; } =
            new List<LinkageRouteSolid>();
        public bool IsConvertedShape { get; set; }
        public string TopologySignature
        {
            get => topologySignature;
            set
            {
                topologySignature = value;
                InvalidateSignatureCaches();
            }
        }
        public LinkageRouteTraversalDirection TraversalDirection { get; set; }
        public int PhysicalOpposingTerminalSegmentCount { get; set; }
        public double PhysicalReverseTravelLength { get; set; }
        public int PerpendicularLapCount { get; set; }
        public LinkageStartPerpendicularLapVariant StartPerpendicularLapVariant
        {
            get;
            set;
        }
        public int SearchPriority { get; set; } = 1;
        public string IdentitySignature
        {
            get => identitySignature;
            set
            {
                identitySignature = value;
                InvalidateSignatureCaches();
            }
        }
        internal string CachedCandidateSignature { get; set; }
        internal string CachedFinalDiversitySignature { get; set; }
        public int GeneratedBusbarPartCount => PlanarConversion?.CenterlineParts?.Count ??
            int.MaxValue;
        public double MinimumStaticClearance { get; set; } = double.PositiveInfinity;
        internal IList<LinkageRouteClearanceFaceGroup> MinimumClearanceFaceGroups { get; } =
            new List<LinkageRouteClearanceFaceGroup>();
        public int ViolationCount { get; set; }
        public double MaximumViolationDepth { get; set; }
        public Vector3d SuggestedClearanceSeparationDirection { get; set; }
        public LinkageRouteFailureStage FailureStage { get; set; }
        public string Diagnostic { get; set; }
        public bool IsValid => FailureStage == LinkageRouteFailureStage.None &&
            PlanarConversion != null && PlanarConversion.IsValid;

        public LinkageGroupRouteSolution ToGroupSolution()
        {
            var groupSolution = new LinkageGroupRouteSolution
            {
                GroupKey = GroupRequest.GroupKey,
                USplitContactSide = PlanarConversion.USplitContactSide,
                AxisSkeleton = AxisSkeleton.Select(CopyPoint).ToList(),
                ManufacturableCenterline = ManufacturableCenterline.Select(CopyPoint).ToList(),
                PlanarParts = PlanarConversion.CenterlineParts.Select(setting => setting.Copy()).ToList(),
                StackMembers = PlanarConversion.StackMembers.Select(CopyMember).ToList(),
                Accessories = PlanarConversion.Accessories.Select(CopyAccessory).ToList(),
                MinimumClearanceFaceGroups = MinimumClearanceFaceGroups
                    .Select(CopyClearanceFaceGroup)
                    .ToList(),
                ActualCopperLength = PlanarConversion.ActualCopperLength,
                PerpendicularLapCount = PerpendicularLapCount,
                IsConvertedShape = IsConvertedShape,
                TopologySignature = TopologySignature
            };
            foreach (var physicalSectionIndex in PlanarConversion
                .DoubleStartPhysicalBridgeSectionIndices)
            {
                groupSolution.DoubleStartPhysicalBridgeSectionIndices.Add(
                    physicalSectionIndex);
            }
            foreach (var bridgePlacement in PlanarConversion
                .StartStackPlacement?.DoubleStartHorizontalBridgePlacements ??
                Array.Empty<LinkageRouteTerminalBridgePlacement>())
            {
                if (bridgePlacement != null &&
                    bridgePlacement.GeneratedMemberIndex >= 0)
                {
                    groupSolution.ExpectedDoubleStartBridgeMemberIndices.Add(
                        bridgePlacement.GeneratedMemberIndex);
                }
            }

            return groupSolution;
        }

        private static LinkageStackedRouteMember CopyMember(LinkageStackedRouteMember sourceMember)
        {
            return new LinkageStackedRouteMember
            {
                MemberIndex = sourceMember.MemberIndex,
                CenterlineOffset = sourceMember.CenterlineOffset,
                PlanarParts = sourceMember.PlanarParts.Select(setting => setting.Copy()).ToList()
            };
        }

        private static LinkageRouteAccessory CopyAccessory(
            LinkageRouteAccessory sourceAccessory)
        {
            return new LinkageRouteAccessory
            {
                Kind = sourceAccessory.Kind,
                TerminalSide = sourceAccessory.TerminalSide,
                RelatedGeneratedMemberIndex =
                    sourceAccessory.RelatedGeneratedMemberIndex,
                Setting = sourceAccessory.Setting?.Copy(),
                SupportBlockKey = sourceAccessory.SupportBlockKey
            };
        }

        private static Point3d CopyPoint(Point3d point)
        {
            return GeometRiMethod.Copy(point);
        }

        private static LinkageRouteClearanceFaceGroup CopyClearanceFaceGroup(
            LinkageRouteClearanceFaceGroup sourceGroup)
        {
            return sourceGroup == null
                ? null
                : new LinkageRouteClearanceFaceGroup
                {
                    GeneratedGroupKey = sourceGroup.GeneratedGroupKey,
                    GeneratedSegmentIndex = sourceGroup.GeneratedSegmentIndex,
                    StaticGroupKey = sourceGroup.StaticGroupKey,
                    StaticSegmentIndex = sourceGroup.StaticSegmentIndex,
                    Distance = sourceGroup.Distance,
                    GeneratedFaceCenter = sourceGroup.GeneratedFaceCenter == null
                        ? null
                        : CopyPoint(sourceGroup.GeneratedFaceCenter),
                    GeneratedFaceNormal = sourceGroup.GeneratedFaceNormal == null
                        ? null
                        : new Vector3d(sourceGroup.GeneratedFaceNormal.X,
                            sourceGroup.GeneratedFaceNormal.Y,
                            sourceGroup.GeneratedFaceNormal.Z),
                    StaticFaceCenter = sourceGroup.StaticFaceCenter == null
                        ? null
                        : CopyPoint(sourceGroup.StaticFaceCenter),
                    StaticFaceNormal = sourceGroup.StaticFaceNormal == null
                        ? null
                        : new Vector3d(sourceGroup.StaticFaceNormal.X,
                            sourceGroup.StaticFaceNormal.Y,
                            sourceGroup.StaticFaceNormal.Z)
                };
        }

        /// <summary>
        /// 签名相关字段每次变更都递增。与 <see cref="CacheIdentity"/> 组合后
        /// 等价于原“候选签名 + 实例身份”缓存键：签名变化必然换键。
        /// </summary>
        internal long SignatureVersion => Interlocked.Read(ref signatureVersion);

        private long signatureVersion;

        private void InvalidateSignatureCaches()
        {
            CachedCandidateSignature = null;
            CachedFinalDiversitySignature = null;
            Interlocked.Increment(ref signatureVersion);
        }

    }

    internal sealed class LinkageRouteCandidatePool
    {
        internal bool DetourShortcutsEvaluated { get; set; }
        public long EvaluatedSkeletonCount { get; set; }
        public LinkageRouteGroupRequest GroupRequest { get; set; }
        public ISet<LinkageRouteTraversalDirection> TraversalDirections { get; } =
            new HashSet<LinkageRouteTraversalDirection>();
        public IList<LinkageRouteCandidate> ValidCandidates { get; } = new List<LinkageRouteCandidate>();
        public LinkageRouteCandidate BestFailedCandidate { get; set; }
        public IList<string> Diagnostics { get; } = new List<string>();
        public IDictionary<LinkageRouteFailureStage, int> FailureStageCounts { get; } =
            new Dictionary<LinkageRouteFailureStage, int>();
        public IDictionary<LinkageRouteFailureStage, LinkageRouteCandidate>
            RepresentativeFailedCandidates { get; } =
                new Dictionary<LinkageRouteFailureStage, LinkageRouteCandidate>();
        public IDictionary<LinkageRouteFailureStage, IList<string>>
            FailureDiagnosticSamples { get; } =
                new Dictionary<LinkageRouteFailureStage, IList<string>>();
    }

    internal static partial class LinkageRouteCandidateFactory
    {
        private const double CollectiveForbiddenDirectionDifferenceTolerance = 0.1d;

        private const double GeometryTolerance =
            LinkageRouteGeometryTolerances.ExactPointCoincidence;
        private const int MaximumOutputIntersectionAdjustmentCount = 10;

        private sealed class TerminalForbiddenBoundaryDescriptor
        {
            public Point3d BoundaryPoint { get; set; }
            public Vector3d ForbiddenDirection { get; set; }
        }

        private sealed class TerminalFixedThicknessLayerContactGroup
        {
            public double CenterCoordinate { get; set; }
            public IList<LinkageRouteStackTerminalContact> Contacts { get; } =
                new List<LinkageRouteStackTerminalContact>();
        }

        private sealed class StaticClearanceValidationResult
        {
            public bool IsClear { get; set; } = true;
            public double MinimumClearance { get; set; } = double.PositiveInfinity;
            public int ViolationCount { get; set; }
            public double MaximumViolationDepth { get; set; }
            public LinkageRouteSolid DeepestViolationGeneratedSolid { get; set; }
            public LinkageRouteSolid DeepestViolationStaticSolid { get; set; }
            public bool DeepestViolationIsLapEnvelope { get; set; }
            public Vector3d DeepestLapViolationExitDirection { get; set; }
            public double DeepestViolationActualDistance { get; set; } =
                double.PositiveInfinity;
            public IList<LinkageRouteClearanceFaceGroup> MinimumClearanceFaceGroups { get; } =
                new List<LinkageRouteClearanceFaceGroup>();
        }

        public static LinkageRouteCandidate TryRepairAgainstSelectedRoutes(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteCandidate failedRouteCandidate,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            IEnumerable<LinkageRouteCandidate> selectedRouteCandidates)
        {
            if (routeRequest == null || failedRouteCandidate?.GroupRequest == null)
            {
                return null;
            }

            return BuildClearanceDrivenCandidates(
                routeRequest,
                failedRouteCandidate.GroupRequest,
                fixedTerminalAndObstacleSolids,
                selectedRouteCandidates,
                failedRouteCandidate.TraversalDirection,
                CancellationToken.None)
                .FirstOrDefault();
        }

        internal static LinkageRouteCandidate
            TryOptimizeReversedInternalLapConversion(
                LinkageMultiGroupRouteRequest routeRequest,
                LinkageRouteCandidate sourceRouteCandidate,
                IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
                IEnumerable<LinkageRouteCandidate> selectedOtherRouteCandidates,
                CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested ||
                routeRequest == null ||
                sourceRouteCandidate?.GroupRequest == null ||
                sourceRouteCandidate.AxisSkeleton == null ||
                sourceRouteCandidate.AxisSkeleton.Count < 4 ||
                sourceRouteCandidate.PlanarConversion?.IsValid != true)
            {
                return null;
            }

            var dynamicClearanceSolids = (fixedTerminalAndObstacleSolids ??
                    Array.Empty<LinkageRouteSolid>())
                .Concat((selectedOtherRouteCandidates ??
                        Enumerable.Empty<LinkageRouteCandidate>())
                    .Where(selectedRouteCandidate =>
                        selectedRouteCandidate != null)
                    .SelectMany(selectedRouteCandidate =>
                        selectedRouteCandidate.ScanSolids))
                .ToList();
            var localCandidatePool = new LinkageRouteCandidatePool
            {
                GroupRequest = sourceRouteCandidate.GroupRequest
            };
            localCandidatePool.TraversalDirections.Add(
                sourceRouteCandidate.TraversalDirection);
            var evaluatedCenterlineCandidates =
                new Dictionary<string, LinkageRouteCandidate>(
                    StringComparer.Ordinal);
            var conversionContext = CreateConversionContext(
                routeRequest,
                sourceRouteCandidate.GroupRequest,
                sourceRouteCandidate.AxisSkeleton,
                dynamicClearanceSolids);
            foreach (var reversedInternalLapConversion in
                LinkageRouteConversionCandidate.CreateContinuousCandidates(
                        sourceRouteCandidate.AxisSkeleton,
                        conversionContext)
                    .Where(conversionCandidate =>
                        conversionCandidate.IsConverted &&
                        (conversionCandidate.ReversesStartInternalLap ||
                         conversionCandidate.ReversesEndInternalLap)))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                var validatedConversionCandidate = BuildAndAddUniqueCandidate(
                    localCandidatePool,
                    evaluatedCenterlineCandidates,
                    routeRequest,
                    sourceRouteCandidate.GroupRequest,
                    sourceRouteCandidate.AxisSkeleton,
                    reversedInternalLapConversion.Centerline,
                    dynamicClearanceSolids,
                    true,
                    "LOCAL-INTERNAL-LAP-" +
                        reversedInternalLapConversion.TopologySignature,
                    sourceRouteCandidate.TraversalDirection,
                    cancellationToken);
                EvaluateReversedInternalLapClearanceSibling(
                    localCandidatePool,
                    evaluatedCenterlineCandidates,
                    routeRequest,
                    sourceRouteCandidate.GroupRequest,
                    sourceRouteCandidate.AxisSkeleton,
                    reversedInternalLapConversion,
                    validatedConversionCandidate,
                    dynamicClearanceSolids,
                    "LOCAL-INTERNAL-LAP-CLEARANCE-",
                    sourceRouteCandidate.TraversalDirection,
                    cancellationToken);
            }

            return localCandidatePool.ValidCandidates
                .Where(candidate =>
                    candidate?.PlanarConversion?.IsValid == true &&
                    candidate.PlanarConversion.ActualCopperLength + 1e-8 <
                        sourceRouteCandidate.PlanarConversion.ActualCopperLength)
                .OrderBy(candidate =>
                    candidate.PlanarConversion.ActualCopperLength)
                .ThenBy(candidate => candidate.GeneratedBusbarPartCount)
                .ThenBy(GetCandidateSignature, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        internal static IReadOnlyList<LinkageRouteCandidate>
            BuildSolutionLengthOptimizationCandidates(
                LinkageMultiGroupRouteRequest routeRequest,
                LinkageRouteCandidate sourceRouteCandidate,
                IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
                IEnumerable<LinkageRouteCandidate> selectedOtherRouteCandidates,
                CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested ||
                routeRequest?.SearchOptions == null ||
                sourceRouteCandidate?.GroupRequest == null ||
                sourceRouteCandidate.PlanarConversion?.IsValid != true)
            {
                return Array.Empty<LinkageRouteCandidate>();
            }

            var maximumOptimizationCandidateCount = Math.Max(
                1,
                Math.Min(
                    16,
                    routeRequest.SearchOptions
                        .MaximumClearanceRepairCandidatesPerState));
            var selectedOtherRouteCandidateList =
                (selectedOtherRouteCandidates ??
                    Enumerable.Empty<LinkageRouteCandidate>())
                .Where(selectedRouteCandidate => selectedRouteCandidate != null)
                .ToList();
            var solutionOptimizationCandidates = new List<LinkageRouteCandidate>();
            var reversedInternalLapCandidate =
                TryOptimizeReversedInternalLapConversion(
                    routeRequest,
                    sourceRouteCandidate,
                    fixedTerminalAndObstacleSolids,
                    selectedOtherRouteCandidateList,
                    cancellationToken);
            if (reversedInternalLapCandidate != null)
            {
                solutionOptimizationCandidates.Add(reversedInternalLapCandidate);
            }

            var optimizationTraversalDirections = new[]
            {
                sourceRouteCandidate.TraversalDirection,
                sourceRouteCandidate.TraversalDirection ==
                    LinkageRouteTraversalDirection.Forward
                        ? LinkageRouteTraversalDirection.Reverse
                        : LinkageRouteTraversalDirection.Forward
            };
            foreach (var optimizationTraversalDirection in
                optimizationTraversalDirections.Distinct())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Array.Empty<LinkageRouteCandidate>();
                }

                solutionOptimizationCandidates.AddRange(
                    BuildClearanceDrivenCandidatesCore(
                        routeRequest,
                        sourceRouteCandidate.GroupRequest,
                        fixedTerminalAndObstacleSolids,
                        selectedOtherRouteCandidateList,
                        optimizationTraversalDirection,
                        maximumOptimizationCandidateCount,
                        true,
                        8,
                        cancellationToken));
            }

            var sourceRouteLength = sourceRouteCandidate
                .PlanarConversion.ActualCopperLength;
            var orderedOptimizationCandidates = solutionOptimizationCandidates
                .Where(optimizationCandidate =>
                    optimizationCandidate?.PlanarConversion?.IsValid == true &&
                    optimizationCandidate.PlanarConversion.ActualCopperLength +
                        GeometryTolerance < sourceRouteLength)
                .GroupBy(GetCandidateSignature)
                .Select(candidateGroup => candidateGroup
                    .OrderBy(optimizationCandidate => optimizationCandidate
                        .PlanarConversion.ActualCopperLength)
                    .ThenBy(optimizationCandidate =>
                        optimizationCandidate.GeneratedBusbarPartCount)
                    .First())
                .OrderBy(optimizationCandidate => optimizationCandidate
                    .PlanarConversion.ActualCopperLength)
                .ThenBy(optimizationCandidate =>
                    optimizationCandidate.GeneratedBusbarPartCount)
                .ThenBy(GetCandidateSignature, StringComparer.Ordinal)
                .ToList();
            if (orderedOptimizationCandidates.Count <=
                maximumOptimizationCandidateCount)
            {
                return orderedOptimizationCandidates;
            }

            // 首个完整方案之后的快速优化同时保留两端边界：最短候选负责
            // 尽快压缩总长，最长候选保留另一侧临界绕行方向，避免只取最短
            // 一侧导致下一组搭接位置的让位拓扑永远进不了后续组合筛选。
            var boundaryCandidateCountPerSide = Math.Max(
                1,
                Math.Min(2, maximumOptimizationCandidateCount / 2));
            return orderedOptimizationCandidates
                .Take(boundaryCandidateCountPerSide)
                .Concat(orderedOptimizationCandidates
                    .Skip(Math.Max(
                        0,
                        orderedOptimizationCandidates.Count -
                            boundaryCandidateCountPerSide)))
                .GroupBy(GetCandidateSignature)
                .Select(candidateGroup => candidateGroup.First())
                .Take(maximumOptimizationCandidateCount)
                .ToList();
        }

        public static IReadOnlyList<LinkageRouteCandidate> BuildClearanceDrivenCandidates(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            IEnumerable<LinkageRouteCandidate> selectedRouteCandidates,
            CancellationToken cancellationToken)
        {
            return BuildClearanceDrivenCandidates(
                routeRequest,
                routeGroupRequest,
                fixedTerminalAndObstacleSolids,
                selectedRouteCandidates,
                LinkageRouteTraversalDirection.Forward,
                cancellationToken);
        }

        internal static IReadOnlyList<LinkageRouteCandidate> BuildClearanceDrivenCandidates(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            IEnumerable<LinkageRouteCandidate> selectedRouteCandidates,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken)
        {
            return BuildClearanceDrivenCandidatesCore(
                routeRequest,
                routeGroupRequest,
                fixedTerminalAndObstacleSolids,
                selectedRouteCandidates,
                traversalDirection,
                null,
                false,
                8,
                cancellationToken);
        }

        internal static IReadOnlyList<LinkageRouteCandidate>
            BuildCriticalClearanceDrivenCandidates(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            IEnumerable<LinkageRouteCandidate> selectedRouteCandidates,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken)
        {
            var configuredRepairCandidateCount = routeRequest?.SearchOptions?
                .MaximumClearanceRepairCandidatesPerState ?? 1;
            return BuildClearanceDrivenCandidatesCore(
                routeRequest,
                routeGroupRequest,
                fixedTerminalAndObstacleSolids,
                selectedRouteCandidates,
                traversalDirection,
                Math.Max(1, Math.Min(8, configuredRepairCandidateCount)),
                false,
                4,
                cancellationToken);
        }

        internal static LinkageRouteCandidatePool BuildClearanceSeedPool(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken)
        {
            var candidatePool = new LinkageRouteCandidatePool
            {
                GroupRequest = routeGroupRequest
            };
            candidatePool.TraversalDirections.Add(traversalDirection);
            foreach (var clearanceSeedCandidate in BuildRankedClearanceSeedCandidates(
                routeRequest,
                routeGroupRequest,
                fixedTerminalAndObstacleSolids,
                traversalDirection,
                maximumSeedCandidateCount: routeRequest.SearchOptions
                    .FastLayerMaximumSeedCandidateCount,
                maximumSeedCandidateCountPerLayer: routeRequest.SearchOptions
                    .FastLayerMaximumSeedCandidateCountPerSegmentLayer,
                continueAfterFirstSuccessfulLayer: false,
                cancellationToken: cancellationToken))
            {
                candidatePool.ValidCandidates.Add(clearanceSeedCandidate);
            }

            if (candidatePool.ValidCandidates.Count == 0)
            {
                candidatePool.Diagnostics.Add(
                    "绝缘临界首方案层尚未生成有效候选。" );
            }

            return candidatePool;
        }

        internal static LinkageRouteCandidatePool BuildExtendedClearanceSeedPool(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken)
        {
            var candidatePool = new LinkageRouteCandidatePool
            {
                GroupRequest = routeGroupRequest
            };
            candidatePool.TraversalDirections.Add(traversalDirection);
            foreach (var clearanceSeedCandidate in BuildRankedClearanceSeedCandidates(
                routeRequest,
                routeGroupRequest,
                fixedTerminalAndObstacleSolids,
                traversalDirection,
                maximumSeedCandidateCount: routeRequest.SearchOptions
                    .ExtendedLayerMaximumSeedCandidateCount,
                maximumSeedCandidateCountPerLayer: routeRequest.SearchOptions
                    .ExtendedLayerMaximumSeedCandidateCountPerSegmentLayer,
                continueAfterFirstSuccessfulLayer: true,
                cancellationToken: cancellationToken))
            {
                candidatePool.ValidCandidates.Add(clearanceSeedCandidate);
            }

            return candidatePool;
        }

        private static IReadOnlyList<LinkageRouteCandidate>
            BuildRankedClearanceSeedCandidates(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            LinkageRouteTraversalDirection traversalDirection,
            int maximumSeedCandidateCount,
            int maximumSeedCandidateCountPerLayer,
            bool continueAfterFirstSuccessfulLayer,
            CancellationToken cancellationToken)
        {
            var maximumEvaluatedSkeletonsPerLayer =
                continueAfterFirstSuccessfulLayer
                    ? routeRequest.SearchOptions
                        .ExtendedLayerMaximumEvaluatedSkeletonCount
                    : routeRequest.SearchOptions
                        .FastLayerMaximumEvaluatedSkeletonCount;
            var maximumVisitedSkeletonsPerLayer =
                continueAfterFirstSuccessfulLayer
                    ? routeRequest.SearchOptions
                        .ExtendedLayerMaximumVisitedSkeletonCount
                    : routeRequest.SearchOptions
                        .FastLayerMaximumVisitedSkeletonCount;
            var maximumEvaluatedSkeletonsPerTopology =
                continueAfterFirstSuccessfulLayer
                    ? routeRequest.SearchOptions
                        .ExtendedLayerMaximumEvaluatedSkeletonCountPerTopology
                    : routeRequest.SearchOptions
                        .FastLayerMaximumEvaluatedSkeletonCountPerTopology;
            var maximumSegmentCount = continueAfterFirstSuccessfulLayer
                ? routeRequest.SearchOptions.ExtendedLayerMaximumSegmentCount
                : routeRequest.SearchOptions.FastLayerMaximumSegmentCount;
            var repairedCandidatePool = new LinkageRouteCandidatePool
            {
                GroupRequest = routeGroupRequest
            };
            repairedCandidatePool.TraversalDirections.Add(traversalDirection);
            var evaluatedCenterlineCandidates =
                new Dictionary<string, LinkageRouteCandidate>(StringComparer.Ordinal);
            var clearanceSegmentAcceptanceCache = new Dictionary<
                LinkageRouteAxisPathBuilder.SegmentClearanceCacheKey,
                bool>();
            int? firstSuccessfulSegmentCount = null;
            try
            {
            for (var segmentCount = 2;
                segmentCount <= maximumSegmentCount;
                segmentCount++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var visitedSkeletonCount = 0;
                var evaluatedSkeletonCount = 0;
                var validCandidateCountBeforeLayer =
                    repairedCandidatePool.ValidCandidates.Count;
                var evaluatedSkeletonCountByTopology =
                    new Dictionary<string, int>(StringComparer.Ordinal);
                LinkageRouteAxisPathBuilder
                    .VisitClearanceDrivenCandidatesForSegmentCount(
                        routeRequest,
                        routeGroupRequest,
                        fixedTerminalAndObstacleSolids,
                        segmentCount,
                        clearanceAxisSkeleton =>
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                return false;
                            }

                            visitedSkeletonCount++;
                            var axisTopologySignature = GetAxisTopologySignature(
                                clearanceAxisSkeleton);
                            evaluatedSkeletonCountByTopology.TryGetValue(
                                axisTopologySignature,
                                out var topologyEvaluationCount);
                            if (topologyEvaluationCount <
                                    maximumEvaluatedSkeletonsPerTopology &&
                                evaluatedSkeletonCount <
                                    maximumEvaluatedSkeletonsPerLayer)
                            {
                                evaluatedSkeletonCountByTopology[
                                    axisTopologySignature] =
                                    topologyEvaluationCount + 1;
                                evaluatedSkeletonCount++;
                                EvaluateClearanceAxisSkeletonSeed(
                                    repairedCandidatePool,
                                    evaluatedCenterlineCandidates,
                                    routeRequest,
                                    routeGroupRequest,
                                    clearanceAxisSkeleton,
                                    fixedTerminalAndObstacleSolids,
                                    traversalDirection,
                                    cancellationToken);
                            }

                            return repairedCandidatePool.ValidCandidates.Count -
                                    validCandidateCountBeforeLayer <
                                        maximumSeedCandidateCountPerLayer &&
                                repairedCandidatePool.ValidCandidates.Count <
                                    maximumSeedCandidateCount &&
                                evaluatedSkeletonCount <
                                    maximumEvaluatedSkeletonsPerLayer &&
                                visitedSkeletonCount <
                                    maximumVisitedSkeletonsPerLayer;
                        },
                        clearanceSegmentAcceptanceCache,
                        traversalDirection,
                        cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (repairedCandidatePool.ValidCandidates.Count >
                        validCandidateCountBeforeLayer &&
                    !firstSuccessfulSegmentCount.HasValue)
                {
                    firstSuccessfulSegmentCount = segmentCount;
                }

                if (repairedCandidatePool.ValidCandidates.Count >=
                        maximumSeedCandidateCount ||
                    (!continueAfterFirstSuccessfulLayer &&
                     firstSuccessfulSegmentCount.HasValue &&
                     segmentCount >= firstSuccessfulSegmentCount.Value + 1))
                {
                    break;
                }
            }

            }
            catch (OperationCanceledException canceledSeedConversion) when (
                cancellationToken.IsCancellationRequested &&
                canceledSeedConversion.CancellationToken == cancellationToken)
            {
                // Ending this bounded seed enumeration must not terminate its
                // still-live caller. Only candidates added after full validation
                // are retained; the interrupted conversion is never added.
            }
            var uniqueOrderedSeedCandidates = repairedCandidatePool.ValidCandidates
                .GroupBy(GetCandidateSignature)
                .Select(candidateGroup => candidateGroup
                    .OrderBy(candidate => candidate.PlanarConversion.ActualCopperLength)
                    .ThenBy(candidate => candidate.GeneratedBusbarPartCount)
                    .First())
                .OrderBy(candidate => candidate.PlanarConversion.ActualCopperLength)
                .ThenBy(candidate => candidate.GeneratedBusbarPartCount)
                .ToList();
            return SelectDiverseValidCandidates(
                uniqueOrderedSeedCandidates,
                maximumSeedCandidateCount,
                routeRequest.AllowPartialStartPerpendicularLap);
        }

        private static string GetAxisTopologySignature(IList<Point3d> centerline)
        {
            if (centerline == null || centerline.Count < 2)
            {
                return string.Empty;
            }

            return string.Join(string.Empty, Enumerable.Range(0, centerline.Count - 1)
                .Select(segmentIndex => GetDominantAxisIndex(new Vector3d(
                    centerline[segmentIndex],
                    centerline[segmentIndex + 1]).Normalized)));
        }

        private static IEnumerable<IList<Point3d>> SelectEvenlyDistributedSkeletons(
            IEnumerable<IList<Point3d>> sourceSkeletons,
            int maximumSelectionCount)
        {
            var orderedSkeletons = (sourceSkeletons ??
                    Enumerable.Empty<IList<Point3d>>())
                .OrderBy(GetCenterlineLength)
                .ThenBy(GetCenterlineSignature, StringComparer.Ordinal)
                .ToList();
            if (orderedSkeletons.Count <= maximumSelectionCount)
            {
                return orderedSkeletons;
            }

            var selectedSkeletons = new List<IList<Point3d>>();
            var selectedSignatures = new HashSet<string>(StringComparer.Ordinal);
            for (var selectionIndex = 0;
                selectionIndex < Math.Max(1, maximumSelectionCount);
                selectionIndex++)
            {
                var sourceIndex = maximumSelectionCount <= 1
                    ? 0
                    : (int)Math.Round(
                        selectionIndex * (orderedSkeletons.Count - 1d) /
                        (maximumSelectionCount - 1d));
                var selectedSkeleton = orderedSkeletons[sourceIndex];
                if (selectedSignatures.Add(GetCenterlineSignature(selectedSkeleton)))
                {
                    selectedSkeletons.Add(selectedSkeleton);
                }
            }

            return selectedSkeletons;
        }

        private static double GetCenterlineLength(IList<Point3d> centerline)
        {
            return centerline == null
                ? double.PositiveInfinity
                : centerline.Zip(centerline.Skip(1), (firstPoint, secondPoint) =>
                    firstPoint.DistanceTo(secondPoint)).Sum();
        }

        internal static LinkageRouteCandidatePool BuildViolationDirectedSeedPool(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            LinkageRouteCandidatePool sourceCandidatePool,
            CancellationToken cancellationToken)
        {
            var repairedCandidatePool = new LinkageRouteCandidatePool
            {
                GroupRequest = routeGroupRequest
            };
            foreach (var traversalDirection in sourceCandidatePool?.TraversalDirections ??
                Enumerable.Empty<LinkageRouteTraversalDirection>())
            {
                repairedCandidatePool.TraversalDirections.Add(traversalDirection);
            }

            if (sourceCandidatePool == null ||
                !sourceCandidatePool.RepresentativeFailedCandidates.TryGetValue(
                    LinkageRouteFailureStage.Clearance,
                    out var clearanceFailureCandidate) ||
                clearanceFailureCandidate?.AxisSkeleton == null ||
                clearanceFailureCandidate.SuggestedClearanceSeparationDirection == null ||
                clearanceFailureCandidate.MaximumViolationDepth <= GeometryTolerance)
            {
                return repairedCandidatePool;
            }

            var evaluatedCenterlineCandidates =
                new Dictionary<string, LinkageRouteCandidate>(StringComparer.Ordinal);
            var criticalShiftDistance = clearanceFailureCandidate.MaximumViolationDepth;
            foreach (var shiftedAxisSkeleton in CreateViolationDirectedAxisSkeletons(
                clearanceFailureCandidate.AxisSkeleton,
                clearanceFailureCandidate.SuggestedClearanceSeparationDirection,
                criticalShiftDistance))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return repairedCandidatePool;
                }

                var shiftedBaseCandidate = BuildAndAddUniqueCandidate(
                    repairedCandidatePool,
                    evaluatedCenterlineCandidates,
                    routeRequest,
                    routeGroupRequest,
                    shiftedAxisSkeleton,
                    shiftedAxisSkeleton,
                    fixedTerminalAndObstacleSolids,
                    false,
                    "VIOLATION-DIRECTED-XYZ",
                    clearanceFailureCandidate.TraversalDirection,
                    cancellationToken);
                if (shiftedBaseCandidate?.IsValid == true)
                {
                    break;
                }

                var conversionContext = CreateConversionContext(
                    routeRequest,
                    routeGroupRequest,
                    shiftedAxisSkeleton,
                    fixedTerminalAndObstacleSolids);
                foreach (var conversionCandidate in LinkageRouteConversionCandidate
                    .CreateContinuousCandidates(shiftedAxisSkeleton, conversionContext)
                    .Where(candidate => candidate.IsConverted))
                {
                    var shiftedConvertedCandidate = BuildAndAddUniqueCandidate(
                        repairedCandidatePool,
                        evaluatedCenterlineCandidates,
                        routeRequest,
                        routeGroupRequest,
                        shiftedAxisSkeleton,
                        conversionCandidate.Centerline,
                        fixedTerminalAndObstacleSolids,
                        true,
                        "VIOLATION-DIRECTED-" + conversionCandidate.TopologySignature,
                        clearanceFailureCandidate.TraversalDirection,
                        cancellationToken);
                    EvaluateConvertedTerminalReserveSibling(
                        repairedCandidatePool,
                        evaluatedCenterlineCandidates,
                        routeRequest,
                        routeGroupRequest,
                        shiftedAxisSkeleton,
                        shiftedConvertedCandidate,
                        fixedTerminalAndObstacleSolids,
                        "VIOLATION-DIRECTED-TERMINAL-",
                        clearanceFailureCandidate.TraversalDirection,
                        cancellationToken);
                    if (repairedCandidatePool.ValidCandidates.Count > 0 ||
                        cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }

                if (repairedCandidatePool.ValidCandidates.Count > 0)
                {
                    break;
                }
            }

            FinalizeCandidatePool(
                repairedCandidatePool,
                1,
                routeRequest.AllowPartialStartPerpendicularLap,
                cancellationToken);
            return repairedCandidatePool;
        }

        internal static LinkageRouteCandidatePool
            BuildPairViolationDirectedCandidatePool(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteCandidate sourceRouteCandidate,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            IEnumerable<LinkageRouteCandidate> selectedRouteCandidates,
            Vector3d suggestedPairSeparationDirection,
            double pairSeparationDistance,
            CancellationToken cancellationToken)
        {
            var pairRepairCandidatePool = new LinkageRouteCandidatePool
            {
                GroupRequest = sourceRouteCandidate?.GroupRequest
            };
            if (cancellationToken.IsCancellationRequested ||
                routeRequest == null ||
                sourceRouteCandidate?.GroupRequest == null ||
                ((sourceRouteCandidate.AxisSkeleton == null ||
                  sourceRouteCandidate.AxisSkeleton.Count < 4) &&
                 (sourceRouteCandidate.ManufacturableCenterline == null ||
                  sourceRouteCandidate.ManufacturableCenterline.Count < 4)) ||
                suggestedPairSeparationDirection == null ||
                suggestedPairSeparationDirection.Norm <= GeometryTolerance ||
                pairSeparationDistance <= GeometryTolerance)
            {
                return pairRepairCandidatePool;
            }

            try
            {
            var pairRepairClearanceSolids = (fixedTerminalAndObstacleSolids ??
                    Array.Empty<LinkageRouteSolid>())
                .Concat((selectedRouteCandidates ??
                        Enumerable.Empty<LinkageRouteCandidate>())
                    .Where(selectedRouteCandidate =>
                        selectedRouteCandidate != null)
                    .SelectMany(selectedRouteCandidate =>
                        selectedRouteCandidate.ScanSolids))
                .ToList();
            pairRepairCandidatePool.TraversalDirections.Add(
                sourceRouteCandidate.TraversalDirection);
            var evaluatedPairRepairCenterlines =
                new Dictionary<string, LinkageRouteCandidate>(
                    StringComparer.Ordinal);
            var pairRepairSourceSkeletons = new List<
                ViolationDirectedSourceSkeleton>();
            if (sourceRouteCandidate.AxisSkeleton?.Count >= 4)
            {
                pairRepairSourceSkeletons.Add(
                    new ViolationDirectedSourceSkeleton
                    {
                        Points = sourceRouteCandidate.AxisSkeleton,
                        PreserveManufacturableTopology = false
                    });
            }

            if (sourceRouteCandidate.ManufacturableCenterline?.Count >= 4 &&
                !pairRepairSourceSkeletons.Any(pairRepairSourceSkeleton =>
                    string.Equals(
                        GetCenterlineSignature(pairRepairSourceSkeleton.Points),
                        GetCenterlineSignature(
                            sourceRouteCandidate.ManufacturableCenterline),
                        StringComparison.Ordinal)))
            {
                pairRepairSourceSkeletons.Add(
                    new ViolationDirectedSourceSkeleton
                    {
                        Points = sourceRouteCandidate.ManufacturableCenterline,
                        PreserveManufacturableTopology = true
                    });
            }

            foreach (var pairRepairSourceSkeleton in pairRepairSourceSkeletons)
            {
                foreach (var shiftedPairRepairAxisSkeleton in
                    CreateViolationDirectedAxisSkeletons(
                        pairRepairSourceSkeleton.Points,
                        suggestedPairSeparationDirection,
                        pairSeparationDistance))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return pairRepairCandidatePool;
                    }

                    if (pairRepairSourceSkeleton
                        .PreserveManufacturableTopology)
                    {
                        BuildAndAddUniqueCandidate(
                            pairRepairCandidatePool,
                            evaluatedPairRepairCenterlines,
                            routeRequest,
                            sourceRouteCandidate.GroupRequest,
                            sourceRouteCandidate.AxisSkeleton ??
                                shiftedPairRepairAxisSkeleton,
                            shiftedPairRepairAxisSkeleton,
                            pairRepairClearanceSolids,
                            true,
                            "PAIR-VIOLATION-DIRECTED",
                            sourceRouteCandidate.TraversalDirection,
                            cancellationToken);
                    }
                    else
                    {
                        EvaluateClearanceAxisSkeletonSeed(
                            pairRepairCandidatePool,
                            evaluatedPairRepairCenterlines,
                            routeRequest,
                            sourceRouteCandidate.GroupRequest,
                            shiftedPairRepairAxisSkeleton,
                            pairRepairClearanceSolids,
                            sourceRouteCandidate.TraversalDirection,
                            cancellationToken);
                    }
                    if (pairRepairCandidatePool.ValidCandidates.Count >=
                        routeRequest.SearchOptions
                            .MaximumPairRepairCandidateCount)
                    {
                        break;
                    }
                }

                if (pairRepairCandidatePool.ValidCandidates.Count >=
                    routeRequest.SearchOptions.MaximumPairRepairCandidateCount)
                {
                    break;
                }
            }

            FinalizeCandidatePool(
                pairRepairCandidatePool,
                routeRequest.SearchOptions.MaximumPairRepairCandidateCount,
                routeRequest.AllowPartialStartPerpendicularLap,
                cancellationToken);
            return pairRepairCandidatePool;
            }
            catch (OperationCanceledException stoppedPairRepair) when (
                cancellationToken.IsCancellationRequested &&
                stoppedPairRepair.CancellationToken == cancellationToken)
            {
                // This pool owns the combination slice. A completed candidate
                // survives; the interrupted conversion was never added. The
                // caller must still discard canceled cache writes/publication.
                return pairRepairCandidatePool;
            }
        }

        private sealed class ViolationDirectedSourceSkeleton
        {
            public IList<Point3d> Points { get; set; }
            public bool PreserveManufacturableTopology { get; set; }
        }

        private static IEnumerable<IList<Point3d>>
            CreateViolationDirectedAxisSkeletons(
            IList<Point3d> sourceAxisSkeleton,
            Vector3d separationDirection,
            double criticalShiftDistance)
        {
            if (sourceAxisSkeleton == null || sourceAxisSkeleton.Count < 4 ||
                separationDirection == null ||
                separationDirection.Norm <= GeometryTolerance ||
                criticalShiftDistance <= GeometryTolerance)
            {
                yield break;
            }

            var normalizedSeparationDirection = separationDirection.Normalized;
            var separationAxisIndex = GetDominantAxisIndex(
                normalizedSeparationDirection);
            var parallelSegmentIndices = Enumerable.Range(
                    0,
                    sourceAxisSkeleton.Count - 1)
                .Where(segmentIndex =>
                {
                    var segmentDirection = new Vector3d(
                        sourceAxisSkeleton[segmentIndex],
                        sourceAxisSkeleton[segmentIndex + 1]);
                    return segmentDirection.Norm > GeometryTolerance &&
                        GetDominantAxisIndex(segmentDirection.Normalized) ==
                            separationAxisIndex;
                })
                .ToList();
            var generatedSignatures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var firstBoundarySegmentIndex in parallelSegmentIndices)
            {
                foreach (var secondBoundarySegmentIndex in parallelSegmentIndices.Where(
                    segmentIndex => segmentIndex > firstBoundarySegmentIndex))
                {
                    var shiftedAxisSkeleton = sourceAxisSkeleton.Select(CopyPoint).ToList();
                    for (var shiftedPointIndex = firstBoundarySegmentIndex + 1;
                        shiftedPointIndex <= secondBoundarySegmentIndex;
                        shiftedPointIndex++)
                    {
                        shiftedAxisSkeleton[shiftedPointIndex] = TranslatePoint(
                            shiftedAxisSkeleton[shiftedPointIndex],
                            normalizedSeparationDirection,
                            criticalShiftDistance);
                    }

                    var shiftedSignature = GetCenterlineSignature(shiftedAxisSkeleton);
                    if (generatedSignatures.Add(shiftedSignature))
                    {
                        yield return shiftedAxisSkeleton;
                    }
                }
            }
        }

        private static int GetDominantAxisIndex(Vector3d direction)
        {
            var absoluteX = Math.Abs(direction.X);
            var absoluteY = Math.Abs(direction.Y);
            var absoluteZ = Math.Abs(direction.Z);
            if (absoluteX >= absoluteY && absoluteX >= absoluteZ)
            {
                return 0;
            }

            return absoluteY >= absoluteZ ? 1 : 2;
        }

        private static IReadOnlyList<LinkageRouteCandidate>
            BuildClearanceDrivenCandidatesCore(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            IEnumerable<LinkageRouteCandidate> selectedRouteCandidates,
            LinkageRouteTraversalDirection traversalDirection,
            int? maximumRepairCandidateCountOverride,
            bool stopAfterFirstValidCandidate,
            int maximumClearanceSegmentCount,
            CancellationToken cancellationToken)
        {
            if (routeRequest == null || routeGroupRequest == null)
            {
                return new List<LinkageRouteCandidate>();
            }

            var dynamicClearanceSolids = (fixedTerminalAndObstacleSolids ??
                    new List<LinkageRouteSolid>())
                .Concat((selectedRouteCandidates ?? Enumerable.Empty<LinkageRouteCandidate>())
                    .Where(selectedRouteCandidate => selectedRouteCandidate != null)
                    .SelectMany(selectedRouteCandidate => selectedRouteCandidate.ScanSolids))
                .ToList();
            var maximumRepairCandidateCount = Math.Max(
                1,
                maximumRepairCandidateCountOverride ??
                    routeRequest.SearchOptions.MaximumClearanceRepairCandidatesPerState);
            var evaluatedCenterlineCandidates =
                new Dictionary<string, LinkageRouteCandidate>(StringComparer.Ordinal);
            var repairedCandidatePool = new LinkageRouteCandidatePool
            {
                GroupRequest = routeGroupRequest
            };
            repairedCandidatePool.TraversalDirections.Add(traversalDirection);
            var generatedBusbarWidth = routeGroupRequest.GetBusbarWidth(routeRequest);
            var generatedSingleBusbarThickness = routeGroupRequest.GetBusbarThickness(
                routeRequest);
            var generatedStackBendAllowance = LinkageRouteStackGeometry
                .GetStackBendAllowance(
                    generatedSingleBusbarThickness,
                    routeGroupRequest.GetStackCount(routeRequest),
                    routeGroupRequest.StackMiddleSpace,
                    routeRequest.GetEffectiveBendAllowance());
            int? firstSuccessfulSegmentCount = null;
            var clearanceSegmentAcceptanceCache = new Dictionary<
                LinkageRouteAxisPathBuilder.SegmentClearanceCacheKey,
                bool>();
            try
            {
            try
            {
            for (var clearanceSegmentCount = 2;
                clearanceSegmentCount <= Math.Max(
                    2,
                    Math.Min(
                        routeRequest.SearchOptions
                            .ExtendedLayerMaximumSegmentCount,
                        maximumClearanceSegmentCount));
                clearanceSegmentCount++)
            {
                var maximumSeedSkeletonsPerSegmentLayer = routeRequest
                    .SearchOptions.MaximumSeedSkeletonsPerSegmentLayer;
                var evaluatedSkeletonCountInSegmentLayer = 0;
                var validCandidateCountBeforeSegmentLayer =
                    repairedCandidatePool.ValidCandidates.Count;
                LinkageRouteAxisPathBuilder
                    .VisitClearanceDrivenCandidatesForSegmentCount(
                        routeRequest,
                        routeGroupRequest,
                        dynamicClearanceSolids,
                        clearanceSegmentCount,
                        clearanceAxisSkeleton =>
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                return false;
                            }

                            // 每条XYZ骨架生成后立即转换、构造实体并校验；不再
                            // 等待整段数层完成或先按未转换XYZ长度截断。
                            if (stopAfterFirstValidCandidate)
                            {
                                EvaluateClearanceAxisSkeletonSeed(
                                    repairedCandidatePool,
                                    evaluatedCenterlineCandidates,
                                    routeRequest,
                                    routeGroupRequest,
                                    clearanceAxisSkeleton,
                                    dynamicClearanceSolids,
                                    traversalDirection,
                                    cancellationToken);
                                evaluatedSkeletonCountInSegmentLayer++;
                            }
                            else
                            {
                                EvaluateClearanceAxisSkeleton(
                                    repairedCandidatePool,
                                    evaluatedCenterlineCandidates,
                                    routeRequest,
                                    routeGroupRequest,
                                    clearanceAxisSkeleton,
                                    dynamicClearanceSolids,
                                    generatedBusbarWidth,
                                    generatedStackBendAllowance,
                                    traversalDirection,
                                    cancellationToken);
                            }
                            return stopAfterFirstValidCandidate
                                ? repairedCandidatePool.ValidCandidates.Count == 0 &&
                                    evaluatedSkeletonCountInSegmentLayer <
                                        maximumSeedSkeletonsPerSegmentLayer
                                : repairedCandidatePool.ValidCandidates.Count <
                                    maximumRepairCandidateCount * 4;
                        },
                        clearanceSegmentAcceptanceCache,
                        traversalDirection,
                        cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (stopAfterFirstValidCandidate &&
                    repairedCandidatePool.ValidCandidates.Count > 0)
                {
                    break;
                }

                if (repairedCandidatePool.ValidCandidates.Count >
                    validCandidateCountBeforeSegmentLayer &&
                    !firstSuccessfulSegmentCount.HasValue)
                {
                    firstSuccessfulSegmentCount = clearanceSegmentCount;
                }

                // 段数越多优先级越低。首个成功层后再完整搜索一层，为不同障碍侧
                // 的绕行拓扑保留候选；N/M转换仍在每条骨架产生后即时执行。
                if (firstSuccessfulSegmentCount.HasValue &&
                    clearanceSegmentCount >= firstSuccessfulSegmentCount.Value + 1 &&
                    repairedCandidatePool.ValidCandidates.Count >=
                        maximumRepairCandidateCount)
                {
                    break;
                }
            }
            }
            catch (OperationCanceledException stoppedTraversal) when (
                cancellationToken.IsCancellationRequested &&
                stoppedTraversal.CancellationToken == cancellationToken)
            {
                // The traversal owns this slice. Only fully validated candidates
                // already added to the pool survive; the interrupted item does not.
            }
            var uniqueRepairedCandidates = repairedCandidatePool.ValidCandidates
                .Where(candidate => candidate?.IsValid == true)
                .GroupBy(GetCandidateSignature)
                .Select(candidateGroup => candidateGroup
                    .OrderBy(candidate => candidate.PlanarConversion.ActualCopperLength)
                    .ThenBy(candidate => candidate.GeneratedBusbarPartCount)
                    .ThenBy(candidate => candidate.TopologySignature)
                    .First())
                .OrderBy(candidate => candidate.PlanarConversion.ActualCopperLength)
                .ThenBy(candidate => candidate.GeneratedBusbarPartCount)
                .ThenBy(candidate => candidate.TopologySignature)
                .ToList();
            return SelectDiverseValidCandidates(
                uniqueRepairedCandidates,
                maximumRepairCandidateCount,
                routeRequest.AllowPartialStartPerpendicularLap);
            }
            finally
            {
                VirtualClearanceTraversalObserver?.Invoke(repairedCandidatePool,
                    cancellationToken.IsCancellationRequested ? "cancelled" : "completed");
            }
        }

        private static void EvaluateClearanceAxisSkeleton(
            LinkageRouteCandidatePool repairedCandidatePool,
            IDictionary<string, LinkageRouteCandidate> evaluatedCenterlineCandidates,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IList<Point3d> clearanceAxisSkeleton,
            IReadOnlyList<LinkageRouteSolid> dynamicClearanceSolids,
            double generatedBusbarWidth,
            double generatedStackBendAllowance,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken)
        {
            var clearanceBaseCandidate = BuildAndAddUniqueCandidate(
                repairedCandidatePool,
                evaluatedCenterlineCandidates,
                routeRequest,
                routeGroupRequest,
                clearanceAxisSkeleton,
                clearanceAxisSkeleton,
                dynamicClearanceSolids,
                false,
                "XYZ-CLEARANCE",
                traversalDirection,
                cancellationToken);
            var clearanceAxisSkeletonVariants = new List<IList<Point3d>>
            {
                clearanceAxisSkeleton
            };
            var terminalReserveClearanceAxisSkeleton =
                CreateTerminalReserveCriticalAxisSkeleton(
                    routeRequest,
                    routeGroupRequest,
                    clearanceAxisSkeleton,
                    clearanceBaseCandidate?.PlanarConversion);
            if (terminalReserveClearanceAxisSkeleton != null)
            {
                clearanceAxisSkeletonVariants.Add(
                    terminalReserveClearanceAxisSkeleton);
                BuildAndAddUniqueCandidate(
                    repairedCandidatePool,
                    evaluatedCenterlineCandidates,
                    routeRequest,
                    routeGroupRequest,
                    terminalReserveClearanceAxisSkeleton,
                    terminalReserveClearanceAxisSkeleton,
                    dynamicClearanceSolids,
                    false,
                    "XYZ-CLEARANCE-TERMINAL",
                    traversalDirection,
                    cancellationToken);
            }

            foreach (var internalReserveClearanceAxisSkeleton in
                CreateInternalLapReserveCriticalAxisSkeletons(
                    routeRequest,
                    routeGroupRequest,
                    clearanceAxisSkeleton,
                    clearanceBaseCandidate?.PlanarConversion))
            {
                clearanceAxisSkeletonVariants.Add(
                    internalReserveClearanceAxisSkeleton);
                BuildAndAddUniqueCandidate(
                    repairedCandidatePool,
                    evaluatedCenterlineCandidates,
                    routeRequest,
                    routeGroupRequest,
                    internalReserveClearanceAxisSkeleton,
                    internalReserveClearanceAxisSkeleton,
                    dynamicClearanceSolids,
                    false,
                    "XYZ-CLEARANCE-INTERNAL",
                    traversalDirection,
                    cancellationToken);
            }

            foreach (var clearanceAxisSkeletonVariant in
                clearanceAxisSkeletonVariants.Where(
                    skeletonVariant => skeletonVariant.Count >= 3))
            {
                var clearanceConversionContext = CreateConversionContext(
                    routeRequest,
                    routeGroupRequest,
                    clearanceAxisSkeletonVariant,
                    dynamicClearanceSolids);
                foreach (var clearanceConversionCandidate in
                    LinkageRouteConversionCandidate.CreateContinuousCandidates(
                        clearanceAxisSkeletonVariant,
                        clearanceConversionContext))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    if (!clearanceConversionCandidate.IsConverted)
                    {
                        continue;
                    }

                    var clearanceConvertedCandidate = BuildAndAddUniqueCandidate(
                        repairedCandidatePool,
                        evaluatedCenterlineCandidates,
                        routeRequest,
                        routeGroupRequest,
                        clearanceAxisSkeletonVariant,
                        clearanceConversionCandidate.Centerline,
                        dynamicClearanceSolids,
                        true,
                        "CLEARANCE-" +
                        clearanceConversionCandidate.TopologySignature,
                        traversalDirection,
                        cancellationToken);
                    EvaluateReversedInternalLapClearanceSibling(
                        repairedCandidatePool,
                        evaluatedCenterlineCandidates,
                        routeRequest,
                        routeGroupRequest,
                        clearanceAxisSkeletonVariant,
                        clearanceConversionCandidate,
                        clearanceConvertedCandidate,
                        dynamicClearanceSolids,
                        "CLEARANCE-INTERNAL-LAP-",
                        traversalDirection,
                        cancellationToken);
                    EvaluateConvertedTerminalReserveSibling(
                        repairedCandidatePool,
                        evaluatedCenterlineCandidates,
                        routeRequest,
                        routeGroupRequest,
                        clearanceAxisSkeletonVariant,
                        clearanceConvertedCandidate,
                        dynamicClearanceSolids,
                        "CLEARANCE-TERMINAL-",
                        traversalDirection,
                        cancellationToken);
                }
            }
        }

        private static void EvaluateClearanceAxisSkeletonSeed(
            LinkageRouteCandidatePool repairedCandidatePool,
            IDictionary<string, LinkageRouteCandidate> evaluatedCenterlineCandidates,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IList<Point3d> clearanceAxisSkeleton,
            IReadOnlyList<LinkageRouteSolid> dynamicClearanceSolids,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken)
        {
            var clearanceBaseCandidate = BuildAndAddUniqueCandidate(
                repairedCandidatePool,
                evaluatedCenterlineCandidates,
                routeRequest,
                routeGroupRequest,
                clearanceAxisSkeleton,
                clearanceAxisSkeleton,
                dynamicClearanceSolids,
                false,
                "XYZ-CLEARANCE-SEED",
                traversalDirection,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested ||
                clearanceAxisSkeleton.Count < 3)
            {
                return;
            }

            var clearanceConversionContext = CreateConversionContext(
                routeRequest,
                routeGroupRequest,
                clearanceAxisSkeleton,
                dynamicClearanceSolids);
            foreach (var clearanceConversionCandidate in
                LinkageRouteConversionCandidate.CreateContinuousCandidates(
                    clearanceAxisSkeleton,
                    clearanceConversionContext)
                    .Where(candidate => candidate.IsConverted))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var convertedClearanceSeedCandidate = BuildAndAddUniqueCandidate(
                    repairedCandidatePool,
                    evaluatedCenterlineCandidates,
                    routeRequest,
                    routeGroupRequest,
                    clearanceAxisSkeleton,
                    clearanceConversionCandidate.Centerline,
                    dynamicClearanceSolids,
                    true,
                    "CLEARANCE-SEED-" +
                        clearanceConversionCandidate.TopologySignature,
                    traversalDirection,
                    cancellationToken);
                if (convertedClearanceSeedCandidate?.IsValid == true)
                {
                    return;
                }
            }
        }

        public static LinkageRouteCandidatePool BuildPool(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            bool enableContinuousConversion,
            CancellationToken cancellationToken)
        {
            return BuildPool(
                routeRequest,
                routeGroupRequest,
                fixedTerminalAndObstacleSolids,
                enableContinuousConversion,
                LinkageRouteTraversalDirection.Forward,
                cancellationToken);
        }

        /// <summary>
        /// 构建首方案使用的快速候选层。该层保留完整 XYZ 骨架和直接 N/M 连续
        /// 转换，但不提前生成绝缘临界扩展、端部留量兄弟或回折绝缘修复候选。
        /// 昂贵候选由多组搜索在基础层确实为空或组合冲突后按需补充。
        /// </summary>
        internal static LinkageRouteCandidate RebuildProxyCandidateForPhysicalGroup(
            LinkageMultiGroupRouteRequest physicalRequest,
            LinkageRouteGroupRequest physicalGroup,
            LinkageRouteCandidate proxyCandidate,
            IReadOnlyList<LinkageRouteSolid> physicalStaticSolids,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || physicalRequest == null ||
                physicalGroup == null || proxyCandidate?.AxisSkeleton == null ||
                proxyCandidate.AxisSkeleton.Count < 2 || proxyCandidate.ManufacturableCenterline == null ||
                proxyCandidate.ManufacturableCenterline.Count < 2 ||
                !string.Equals(physicalGroup.GroupKey, proxyCandidate.GroupRequest?.GroupKey,
                    StringComparison.OrdinalIgnoreCase)) return null;
            // A live proxy is only a topology proposal, never an accepted physical
            // object. Rebuild actual placements/sections, then run every ordinary
            // candidate gate; do not use output-only validation or mutate the proxy.
            return BuildCandidate(physicalRequest, physicalGroup,
                proxyCandidate.AxisSkeleton.Select(point => point.Copy()).ToList(),
                proxyCandidate.ManufacturableCenterline.Select(point => point.Copy()).ToList(),
                physicalStaticSolids, proxyCandidate.IsConvertedShape,
                "PHYSICAL-PROXY-" + proxyCandidate.TopologySignature,
                proxyCandidate.TraversalDirection, cancellationToken);
        }

        internal static LinkageRouteCandidatePool BuildPhysicalProxySeedPool(
            LinkageMultiGroupRouteRequest physicalRequest,
            LinkageRouteGroupRequest physicalGroup,
            LinkageMultiGroupRouteRequest proxyRequest,
            LinkageRouteGroupRequest proxyGroup,
            IReadOnlyList<LinkageRouteSolid> physicalStaticSolids,
            CancellationToken cancellationToken,
            int prefetchWindow = 1)
        {
            var result = new LinkageRouteCandidatePool { GroupRequest = physicalGroup };
            foreach (var direction in new[] { LinkageRouteTraversalDirection.Forward,
                LinkageRouteTraversalDirection.Reverse })
            {
                if (cancellationToken.IsCancellationRequested) break;
                var physicalPool = BuildFastPool(physicalRequest, physicalGroup, physicalStaticSolids,
                    direction, cancellationToken, proxyRequest, proxyGroup, prefetchWindow);
                result.TraversalDirections.Add(direction);
                result = MergeTraversalPools(physicalRequest, new[] { result, physicalPool });
            }
            FinalizeCandidatePool(result, physicalRequest.SearchOptions.MaximumCandidatesPerGroup,
                physicalRequest.AllowPartialStartPerpendicularLap, cancellationToken);
            return result;
        }

        internal static LinkageRouteCandidatePool BuildFastPool(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken,
            LinkageMultiGroupRouteRequest proxyTopologyRequest = null,
            LinkageRouteGroupRequest proxyTopologyGroup = null,
            int prefetchWindow = 1)
        {
            return BuildFastPoolCore(routeRequest, routeGroupRequest, fixedTerminalAndObstacleSolids,
                traversalDirection, cancellationToken, proxyTopologyRequest, proxyTopologyGroup, 4, true,
                prefetchWindow: prefetchWindow);
        }

        internal static LinkageRouteCandidatePool BuildFirstOriginalSeedPool(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken,
            int prefetchWindow = 1)
        {
            return BuildFastPoolCore(routeRequest, routeGroupRequest, fixedTerminalAndObstacleSolids,
                traversalDirection, cancellationToken, null, null, 1, false,
                prefetchWindow: prefetchWindow);
        }

        internal static LinkageRouteCandidatePool BuildFirstCriticalPlaneSeedPool(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken,
            int prefetchWindow = 1)
        {
            return BuildFastPoolCore(routeRequest, routeGroupRequest, fixedTerminalAndObstacleSolids,
                traversalDirection, cancellationToken, null, null, 1, true,
                prefetchWindow: prefetchWindow);
        }

        internal static LinkageRouteCandidatePool BuildContinuationSeedPool(
            LinkageMultiGroupRouteRequest request, LinkageRouteGroupRequest group,
            IReadOnlyList<LinkageRouteSolid> fixedSolids, bool criticalFamily,
            CancellationToken token, Action<LinkageRouteCandidatePool> checkpoint)
        {
            return BuildFastPoolCore(request, group, fixedSolids,
                LinkageRouteTraversalDirection.Forward, token, null, null,
                request.SearchOptions.MaximumCandidatesPerGroup, criticalFamily, checkpoint);
        }

        private static LinkageRouteCandidatePool BuildFastPoolCore(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken,
            LinkageMultiGroupRouteRequest proxyTopologyRequest,
            LinkageRouteGroupRequest proxyTopologyGroup,
            int requestedSeedCount,
            bool includeCriticalPlanePrepass,
            Action<LinkageRouteCandidatePool> completedCandidate = null,
            int prefetchWindow = 1)
        {
            var candidatePool = new LinkageRouteCandidatePool
            {
                GroupRequest = routeGroupRequest
            };
            candidatePool.TraversalDirections.Add(traversalDirection);
            try
            {
            var evaluatedCenterlineCandidates =
                new Dictionary<string, LinkageRouteCandidate>(StringComparer.Ordinal);
            // 断点续搜的构建者按主搜索名额逐候选暂停，不能让预取绕过名额继续占用核；
            // 该模式保持原串行构建。
            var effectivePrefetchWindow = completedCandidate == null
                ? Math.Max(1, prefetchWindow)
                : 1;
            Func<IList<Point3d>, bool> isSkeletonEvaluated = skeleton =>
                evaluatedCenterlineCandidates.ContainsKey(GetCenterlineSignature(skeleton));
            Func<IList<Point3d>, LinkageRouteCandidate> buildFastBaseCandidate = skeleton =>
                BuildUniqueCandidateCore(routeRequest, routeGroupRequest, skeleton, skeleton,
                    fixedTerminalAndObstacleSolids, false, "XYZ-FAST", traversalDirection,
                    cancellationToken, proxyTopologyGroup);
            var usesSectionSpecificBusbarSettings = routeGroupRequest
                .HasSectionSpecificBusbarSettings(routeRequest) ||
                routeGroupRequest.EndTerminal?.CabinetBridgeAssembly != null;
            var sectionSpecificFastCandidateTarget = Math.Min(
                routeRequest.SearchOptions.MaximumCandidatesPerGroup,
                requestedSeedCount);
            BuildWidthSizedLengthOverlapCandidates(
                candidatePool,
                evaluatedCenterlineCandidates,
                routeRequest,
                routeGroupRequest,
                fixedTerminalAndObstacleSolids,
                traversalDirection,
                cancellationToken);
            completedCandidate?.Invoke(candidatePool);

            if (usesSectionSpecificBusbarSettings &&
                LinkageRouteDoubleStartLapGeometry.TryCreateLengthSeparatedCollectiveFixedBusbar(
                    routeGroupRequest.StartTerminal, out _))
            {
                BuildDoubleStartBridgeCandidates(candidatePool, routeRequest,
                    routeGroupRequest, fixedTerminalAndObstacleSolids,
                    traversalDirection, cancellationToken);
                completedCandidate?.Invoke(candidatePool);
            }

            // 始端与柜顶在排长方向存在一个排宽的公共搭接区时，专用直搭
            // 候选已经是完整、可制造的单排连接。快速首发层应立即把这类
            // 候选交给组合搜索，不能继续同步展开耗时的双始端 I/L 桥，
            // 否则前面数百毫秒得到的合法候选会被后续桥接枚举长期阻塞。
            // 其他桥接及普通拓扑仍由后续候选层继续补充。
            // 宽度重叠候选只是优先层，不能因为已经有有效候选就提前结束快速池。
            // 同一组仍必须继续枚举普通 XYZ 骨架及其 N/M 转换；否则双始端的
            // OVERLAP-WIDTH-PERPENDICULAR-LAP 会把直排和斜排候选整体短路，
            // 绝缘搜索也就无法比较到更安全的直排路径。

            if (!usesSectionSpecificBusbarSettings)
            {
                BuildDoubleStartBridgeCandidates(
                    candidatePool,
                    routeRequest,
                    routeGroupRequest,
                    fixedTerminalAndObstacleSolids,
                    traversalDirection,
                    cancellationToken);
                completedCandidate?.Invoke(candidatePool);
            }
            var fastAxisSkeletons = LinkageRouteAxisPathBuilder.BuildCandidates(
                proxyTopologyRequest ?? routeRequest,
                proxyTopologyGroup ?? routeGroupRequest,
                traversalDirection,
                cancellationToken).Concat(proxyTopologyGroup == null
                    ? LinkageRouteAxisPathBuilder.BuildActualSectionCandidates(
                        routeRequest, routeGroupRequest, traversalDirection, cancellationToken)
                    : Array.Empty<IList<Point3d>>()).ToList();
            if (usesSectionSpecificBusbarSettings && includeCriticalPlanePrepass)
            {
                // First evaluate short raw paths and bounded one-plane critical
                // siblings. A costly N/M conversion on an early failed path must
                // not starve a later, already constructible actual XYZ route.
                // The normal conversion pass below keeps every original path.
                var criticalBaseSkeletons = fastAxisSkeletons.OrderBy(GetCenterlineLength)
                    .ThenBy(points => points.Count)
                    .ToList();
                using (var criticalBasePrefetcher = new OrderedSkeletonCandidatePrefetcher(
                    criticalBaseSkeletons, effectivePrefetchWindow, isSkeletonEvaluated,
                    buildFastBaseCandidate))
                for (var criticalBaseIndex = 0; criticalBaseIndex < criticalBaseSkeletons.Count;
                    criticalBaseIndex++)
                {
                    var criticalBaseSkeleton = criticalBaseSkeletons[criticalBaseIndex];
                    if (cancellationToken.IsCancellationRequested) return candidatePool;
                    candidatePool.EvaluatedSkeletonCount++;
                    var criticalBaseCandidate = BuildAndAddUniqueCandidate(candidatePool,
                        evaluatedCenterlineCandidates, routeRequest, routeGroupRequest,
                        criticalBaseSkeleton, criticalBaseSkeleton, fixedTerminalAndObstacleSolids,
                        false, "XYZ-FAST", traversalDirection, cancellationToken, proxyTopologyGroup,
                        criticalBasePrefetcher.Take(criticalBaseIndex));
                    completedCandidate?.Invoke(candidatePool);
                    if (criticalBaseCandidate?.FailureStage == LinkageRouteFailureStage.Containment ||
                        criticalBaseCandidate?.FailureStage == LinkageRouteFailureStage.PlanarConversion ||
                        criticalBaseCandidate?.FailureStage == LinkageRouteFailureStage.Lap)
                    {
                        // Keep proven whole-plane wall siblings in this raw
                        // layer too; new local planes must not starve them
                        // behind the later, costly conversion layer.
                        foreach (var criticalWallSkeleton in LinkageRouteAxisPathBuilder
                            .BuildExplicitWallAlignedSiblings(routeRequest, routeGroupRequest, criticalBaseSkeleton))
                        {
                            if (cancellationToken.IsCancellationRequested) return candidatePool;
                            BuildAndAddUniqueCandidate(candidatePool, evaluatedCenterlineCandidates,
                                routeRequest, routeGroupRequest, criticalWallSkeleton, criticalWallSkeleton,
                                fixedTerminalAndObstacleSolids, false, "XYZ-FAST-ACTUAL-WALL",
                                traversalDirection, cancellationToken);
                            completedCandidate?.Invoke(candidatePool);
                            if (candidatePool.ValidCandidates.Count >= sectionSpecificFastCandidateTarget)
                            {
                                FinalizeCandidatePool(candidatePool, routeRequest.SearchOptions.MaximumCandidatesPerGroup,
                                    routeRequest.AllowPartialStartPerpendicularLap, cancellationToken);
                                return candidatePool;
                            }
                        }
                    }
                    if (criticalBaseCandidate?.FailureStage == LinkageRouteFailureStage.Lap ||
                        criticalBaseCandidate?.FailureStage == LinkageRouteFailureStage.Clearance ||
                        criticalBaseCandidate?.FailureStage == LinkageRouteFailureStage.Containment)
                    {
                        foreach (var criticalPlaneSkeleton in LinkageRouteAxisPathBuilder.BuildCriticalPlaneAlignedSiblings(
                            routeRequest, routeGroupRequest, criticalBaseSkeleton, fixedTerminalAndObstacleSolids, cancellationToken))
                        {
                            if (cancellationToken.IsCancellationRequested) return candidatePool;
                            BuildAndAddUniqueCandidate(candidatePool, evaluatedCenterlineCandidates,
                                routeRequest, routeGroupRequest, criticalPlaneSkeleton, criticalPlaneSkeleton,
                                fixedTerminalAndObstacleSolids, false, "XYZ-FAST-CRITICAL-PLANE", traversalDirection, cancellationToken);
                            completedCandidate?.Invoke(candidatePool);
                            if (candidatePool.ValidCandidates.Count >= sectionSpecificFastCandidateTarget)
                            {
                                FinalizeCandidatePool(candidatePool, routeRequest.SearchOptions.MaximumCandidatesPerGroup,
                                    routeRequest.AllowPartialStartPerpendicularLap, cancellationToken);
                                return candidatePool;
                            }
                        }
                    }
                    if (candidatePool.ValidCandidates.Count >= sectionSpecificFastCandidateTarget)
                    {
                        FinalizeCandidatePool(candidatePool, routeRequest.SearchOptions.MaximumCandidatesPerGroup,
                            routeRequest.AllowPartialStartPerpendicularLap, cancellationToken);
                        return candidatePool;
                    }
                }
            }
            using (var fastBasePrefetcher = new OrderedSkeletonCandidatePrefetcher(
                fastAxisSkeletons, effectivePrefetchWindow, isSkeletonEvaluated,
                buildFastBaseCandidate))
            for (var fastSkeletonIndex = 0; fastSkeletonIndex < fastAxisSkeletons.Count;
                fastSkeletonIndex++)
            {
                var axisSkeleton = fastAxisSkeletons[fastSkeletonIndex];
                candidatePool.EvaluatedSkeletonCount++;
                if (cancellationToken.IsCancellationRequested)
                {
                    return candidatePool;
                }

                var fastAxisCandidate = BuildAndAddUniqueCandidate(
                    candidatePool,
                    evaluatedCenterlineCandidates,
                    routeRequest,
                    routeGroupRequest,
                    axisSkeleton,
                    axisSkeleton,
                    fixedTerminalAndObstacleSolids,
                    false,
                    "XYZ-FAST",
                    traversalDirection,
                    cancellationToken,
                    proxyTopologyGroup,
                    fastBasePrefetcher.Take(fastSkeletonIndex));
                completedCandidate?.Invoke(candidatePool);
                if (usesSectionSpecificBusbarSettings &&
                    (fastAxisCandidate?.FailureStage == LinkageRouteFailureStage.Containment ||
                     fastAxisCandidate?.FailureStage == LinkageRouteFailureStage.PlanarConversion ||
                     fastAxisCandidate?.FailureStage == LinkageRouteFailureStage.Lap))
                {
                    // A legal wall-side plane can also provide the missing flat
                    // lap/transition span, not just reduce an out-of-box envelope.
                    // Keep the original skeleton; every sibling is separately
                    // materialized with the actual specifications and all gates.
                    foreach (var wallAlignedSkeleton in LinkageRouteAxisPathBuilder
                        .BuildExplicitWallAlignedSiblings(routeRequest, routeGroupRequest, axisSkeleton))
                    {
                        if (cancellationToken.IsCancellationRequested) return candidatePool;
                        BuildAndAddUniqueCandidate(candidatePool, evaluatedCenterlineCandidates,
                            routeRequest, routeGroupRequest, wallAlignedSkeleton, wallAlignedSkeleton,
                            fixedTerminalAndObstacleSolids, false, "XYZ-FAST-ACTUAL-WALL",
                            traversalDirection, cancellationToken);
                        completedCandidate?.Invoke(candidatePool);
                    }
                }
                if (usesSectionSpecificBusbarSettings &&
                    candidatePool.ValidCandidates.Count >=
                        sectionSpecificFastCandidateTarget &&
                    (requestedSeedCount == 1 || candidatePool.ValidCandidates.Any(candidate =>
                        candidate != null && !candidate.IsConvertedShape)))
                {
                    FinalizeCandidatePool(
                        candidatePool,
                        routeRequest.SearchOptions.MaximumCandidatesPerGroup,
                        routeRequest.AllowPartialStartPerpendicularLap,
                        cancellationToken);
                    return candidatePool;
                }
                if (axisSkeleton.Count < 3)
                {
                    continue;
                }

                var conversionContext = CreateConversionContext(
                    routeRequest,
                    routeGroupRequest,
                    axisSkeleton,
                    fixedTerminalAndObstacleSolids);
                foreach (var conversionCandidate in LinkageRouteConversionCandidate
                    .CreateContinuousCandidates(axisSkeleton, conversionContext)
                    .Where(candidate => candidate.IsConverted))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return candidatePool;
                    }

                    BuildAndAddUniqueCandidate(
                        candidatePool,
                        evaluatedCenterlineCandidates,
                        routeRequest,
                        routeGroupRequest,
                        axisSkeleton,
                        conversionCandidate.Centerline,
                        fixedTerminalAndObstacleSolids,
                        true,
                        "FAST-" + conversionCandidate.TopologySignature,
                        traversalDirection,
                        cancellationToken,
                        proxyTopologyGroup);
                    completedCandidate?.Invoke(candidatePool);
                    if (usesSectionSpecificBusbarSettings &&
                        candidatePool.ValidCandidates.Count >=
                            sectionSpecificFastCandidateTarget &&
                        (requestedSeedCount == 1 || candidatePool.ValidCandidates.Any(candidate =>
                            candidate != null && !candidate.IsConvertedShape)))
                    {
                        FinalizeCandidatePool(
                            candidatePool,
                            routeRequest.SearchOptions.MaximumCandidatesPerGroup,
                            routeRequest.AllowPartialStartPerpendicularLap,
                            cancellationToken);
                        return candidatePool;
                    }
                }
            }

            if (usesSectionSpecificBusbarSettings &&
                candidatePool.ValidCandidates.Count <
                    sectionSpecificFastCandidateTarget)
            {
                BuildDoubleStartBridgeCandidates(
                    candidatePool,
                    routeRequest,
                    routeGroupRequest,
                    fixedTerminalAndObstacleSolids,
                    traversalDirection,
                    cancellationToken);
                completedCandidate?.Invoke(candidatePool);
            }

            FinalizeCandidatePool(
                candidatePool,
                routeRequest.SearchOptions.MaximumCandidatesPerGroup,
                routeRequest.AllowPartialStartPerpendicularLap,
                cancellationToken);
            return candidatePool;
            }
            catch (OperationCanceledException canceledConversion) when (
                cancellationToken.IsCancellationRequested &&
                canceledConversion.CancellationToken == cancellationToken)
            {
                // Match the existing between-candidate cancellation behavior.
                // A completed, fully validated seed is not part of the next
                // interrupted conversion. The caller still owns stage cancel
                // and must not publish after a request-level cancellation.
                return candidatePool;
            }
        }

        private static void BuildWidthSizedLengthOverlapCandidates(
            LinkageRouteCandidatePool destinationCandidatePool,
            IDictionary<string, LinkageRouteCandidate> evaluatedCenterlineCandidates,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested ||
                traversalDirection != LinkageRouteTraversalDirection.Forward)
            {
                return;
            }

            foreach (var overlapAxisSkeleton in LinkageRouteAxisPathBuilder
                .BuildWidthSizedLengthOverlapCandidates(
                    routeRequest,
                    routeGroupRequest,
                    cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var directOverlapCandidate = BuildAndAddUniqueCandidate(
                    destinationCandidatePool,
                    evaluatedCenterlineCandidates,
                    routeRequest,
                    routeGroupRequest,
                    overlapAxisSkeleton,
                    overlapAxisSkeleton,
                    fixedTerminalAndObstacleSolids,
                    false,
                    "OVERLAP-WIDTH-PERPENDICULAR-LAP",
                    traversalDirection,
                    cancellationToken);
                MarkWidthSizedLengthOverlapPriority(directOverlapCandidate);
                if (overlapAxisSkeleton.Count < 3)
                {
                    continue;
                }

                var overlapConversionContext = CreateConversionContext(
                    routeRequest,
                    routeGroupRequest,
                    overlapAxisSkeleton,
                    fixedTerminalAndObstacleSolids);
                // 公共区直搭需要跨过终端组件的完整垂直搭接跨度。通用转换
                // 上下文只读取一根代表性固定排宽，会让 N 形斜段过早开始并
                // 切入同组的另一根始端或长柜顶排。这里仅扩大端部保留量，
                // 使一根 N 形排仍完整覆盖同组全部搭接面。
                overlapConversionContext.StartMatingBusbarWidth = Math.Max(
                    overlapConversionContext.StartMatingBusbarWidth,
                    LinkageRouteTerminalPoseFactory
                        .GetCollectivePerpendicularLapSpan(
                            routeGroupRequest.StartTerminal,
                            routeGroupRequest.GetBusbarWidth(routeRequest)));
                overlapConversionContext.EndMatingBusbarWidth = Math.Max(
                    overlapConversionContext.EndMatingBusbarWidth,
                    LinkageRouteTerminalPoseFactory
                        .GetCollectivePerpendicularLapSpan(
                            routeGroupRequest.EndTerminal,
                            routeGroupRequest.GetBusbarWidth(routeRequest)));
                foreach (var overlapConversionCandidate in
                    LinkageRouteConversionCandidate
                        .CreateRestrictedWholePathCandidates(
                            overlapAxisSkeleton,
                            overlapConversionContext,
                            overlapAxisSkeleton[0].DistanceTo(
                                overlapAxisSkeleton[1]),
                            overlapAxisSkeleton[
                                overlapAxisSkeleton.Count - 2].DistanceTo(
                                overlapAxisSkeleton[
                                    overlapAxisSkeleton.Count - 1])))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var convertedOverlapCandidate = BuildAndAddUniqueCandidate(
                        destinationCandidatePool,
                        evaluatedCenterlineCandidates,
                        routeRequest,
                        routeGroupRequest,
                        overlapAxisSkeleton,
                        overlapConversionCandidate.Centerline,
                        fixedTerminalAndObstacleSolids,
                        true,
                        "OVERLAP-WIDTH-PERPENDICULAR-LAP>" +
                            overlapConversionCandidate.TopologySignature,
                        traversalDirection,
                        cancellationToken);
                    MarkWidthSizedLengthOverlapPriority(
                        convertedOverlapCandidate);
                }
            }
        }

        private static void MarkWidthSizedLengthOverlapPriority(
            LinkageRouteCandidate overlapRouteCandidate)
        {
            if (overlapRouteCandidate == null)
            {
                return;
            }

            // -1 明确排在双始端 I 型桥接（0）和普通枚举（1）之前。
            overlapRouteCandidate.SearchPriority = -1;
            overlapRouteCandidate.IdentitySignature =
                "OVERLAP-WIDTH-PERPENDICULAR-LAP";
        }

        private static void BuildDoubleStartBridgeCandidates(
            LinkageRouteCandidatePool destinationCandidatePool,
            LinkageMultiGroupRouteRequest physicalRouteRequest,
            LinkageRouteGroupRequest physicalRouteGroupRequest,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested ||
                physicalRouteRequest?.AllowDoubleStartMiddleLap != true ||
                physicalRouteGroupRequest?.StartTerminal?.Members?.Count < 2 ||
                traversalDirection != LinkageRouteTraversalDirection.Forward)
            {
                return;
            }

            var bridgeBusbarWidth = physicalRouteGroupRequest
                .GetDoubleStartBridgeBusbarWidth(physicalRouteRequest);
            var bridgeSingleBusbarThickness = physicalRouteGroupRequest
                .GetDoubleStartBridgeBusbarThickness(physicalRouteRequest);
            var bridgeStackCount = physicalRouteGroupRequest
                .GetStartLapStackCount(physicalRouteRequest);
            var collectiveStartLapSpan = LinkageRouteTerminalPoseFactory
                .GetCollectivePerpendicularLapSpan(
                    physicalRouteGroupRequest.StartTerminal,
                    bridgeBusbarWidth);
            var bridgeMinimumPoseLength = Math.Max(
                physicalRouteRequest.MinimumSegmentLength,
                collectiveStartLapSpan);
            var bridgeOverlapBoundaryReferencePoint =
                LinkageRouteAxisPathBuilder
                    .GetWidthSizedOverlapBoundaryReferencePoint(
                        physicalRouteGroupRequest,
                        bridgeBusbarWidth,
                        physicalRouteGroupRequest
                            .GetCabinetTopLapBusbarWidth(
                                physicalRouteRequest));
            var hasLengthSeparatedStart = LinkageRouteDoubleStartLapGeometry
                .TryCreateLengthSeparatedCollectiveFixedBusbar(
                    physicalRouteGroupRequest.StartTerminal, out _);
            var preferBentBridge = !hasLengthSeparatedStart &&
                AreTerminalThicknessDirectionsPerpendicular(
                    physicalRouteGroupRequest);
            var collectivePerpendicularStartPoses = LinkageRouteTerminalPoseFactory
                .BuildForRouteTerminalWithOptionalPartialPerpendicularLap(
                    physicalRouteGroupRequest.StartTerminal,
                    physicalRouteGroupRequest.EndTerminal.RouteAnchor,
                    true,
                    bridgeBusbarWidth,
                    Math.Max(
                        physicalRouteRequest.MinimumSegmentLength,
                        bridgeBusbarWidth),
                    bridgeMinimumPoseLength,
                    physicalRouteRequest.MinimumSegmentLength,
                    null,
                    false,
                    false,
                    physicalRouteRequest.AllowPartialStartPerpendicularLap,
                    0d,
                    bridgeOverlapBoundaryReferencePoint,
                requireFixedTerminalEndLap: physicalRouteRequest.RequireFixedTerminalEndLap)
                .Where(terminalPose => terminalPose.IsPerpendicularLap)
                .ToList();
            if (LinkageRouteDoubleStartLapGeometry.TryCreateLengthSeparatedCollectiveFixedBusbar(
                    physicalRouteGroupRequest.StartTerminal, out var lengthSeparatedFixedView))
            {
                var collectiveBridgeTerminal = new LinkageTerminalAssembly
                {
                    RouteAnchor = physicalRouteGroupRequest.StartTerminal.RouteAnchor,
                    ThicknessDirection = physicalRouteGroupRequest.StartTerminal.ThicknessDirection,
                    LengthDirection = lengthSeparatedFixedView.WidthVector.Cross(lengthSeparatedFixedView.StartBusbarVector),
                    Members = new List<LinkageTerminalMember>
                    {
                        new LinkageTerminalMember { MemberKey = "LENGTH_SEPARATED_BRIDGE_VIEW", FixedBusbar = lengthSeparatedFixedView }
                    }
                };
                var widthAxis = lengthSeparatedFixedView.WidthVector.Normalized;
                var viewCenter = lengthSeparatedFixedView.PointList[0];
                var oppositeWidthCoordinate = new Vector3d(viewCenter, physicalRouteGroupRequest.EndTerminal.RouteAnchor).Dot(widthAxis);
                var mirroredOppositeAnchor = TranslateDoubleStartBridgePoint(
                    physicalRouteGroupRequest.EndTerminal.RouteAnchor, widthAxis, -2d * oppositeWidthCoordinate);
                foreach (var bridgeApproachAnchor in new[] {mirroredOppositeAnchor, physicalRouteGroupRequest.EndTerminal.RouteAnchor})
                collectivePerpendicularStartPoses.AddRange(LinkageRouteTerminalPoseFactory.BuildForRouteTerminal(
                    collectiveBridgeTerminal, bridgeApproachAnchor, true,
                    bridgeBusbarWidth, bridgeBusbarWidth,
                    Math.Max(physicalRouteRequest.MinimumSegmentLength, lengthSeparatedFixedView.BusBarWidth),
                    null, false, false, false, 0d,
                    requireFixedTerminalEndLap: physicalRouteRequest.RequireFixedTerminalEndLap)
                    .Where(pose => pose.IsPerpendicularLap));
            }
            if (preferBentBridge)
            {
                var originalCollectivePerpendicularStartPoses =
                    collectivePerpendicularStartPoses.ToList();
                foreach (var originalCollectivePerpendicularStartPose in
                    originalCollectivePerpendicularStartPoses)
                {
                    var originalCollectiveRouteDirection =
                        originalCollectivePerpendicularStartPose
                            .RouteDirection.Normalized;
                    var oppositeCollectiveRouteDirection = new Vector3d(
                        -originalCollectiveRouteDirection.X,
                        -originalCollectiveRouteDirection.Y,
                        -originalCollectiveRouteDirection.Z);
                    LinkageRouteTerminalPose oppositeCollectiveTerminalPose;
                    var originalPoseUsesPartialPerpendicularLap =
                        physicalRouteRequest
                            .AllowPartialStartPerpendicularLap &&
                        originalCollectivePerpendicularStartPose
                            .PerpendicularLapCoverageSpan +
                            GeometryTolerance < collectiveStartLapSpan;
                    if (originalPoseUsesPartialPerpendicularLap)
                    {
                        var mirroredOppositeAnchor =
                            TranslateDoubleStartBridgePoint(
                                originalCollectivePerpendicularStartPose.LapEndpoint,
                                originalCollectiveRouteDirection,
                                -(collectiveStartLapSpan + bridgeBusbarWidth) * 2d);
                        oppositeCollectiveTerminalPose =
                            LinkageRouteTerminalPoseFactory.BuildForRouteTerminal(
                                    physicalRouteGroupRequest.StartTerminal,
                                    mirroredOppositeAnchor,
                                    true,
                                    bridgeBusbarWidth,
                                    Math.Max(
                                        physicalRouteRequest.MinimumSegmentLength,
                                        bridgeBusbarWidth),
                                    physicalRouteRequest.MinimumSegmentLength,
                                    null,
                                    false,
                                    false,
                                    true,
                                    0d,
                                    bridgeOverlapBoundaryReferencePoint,
                requireFixedTerminalEndLap: physicalRouteRequest.RequireFixedTerminalEndLap)
                                .FirstOrDefault(terminalPose =>
                                    terminalPose.IsPerpendicularLap &&
                                    terminalPose.RouteDirection.Normalized.Dot(
                                        oppositeCollectiveRouteDirection) >=
                                        1d - GeometryTolerance);
                    }
                    else
                    {
                        oppositeCollectiveTerminalPose =
                            new LinkageRouteTerminalPose
                            {
                                LapEndpoint = TranslateDoubleStartBridgePoint(
                                    originalCollectivePerpendicularStartPose.LapEndpoint,
                                    originalCollectiveRouteDirection,
                                    collectiveStartLapSpan),
                                RouteDirection = oppositeCollectiveRouteDirection,
                                RequiredTerminalSegmentLength =
                                    originalCollectivePerpendicularStartPose
                                        .RequiredTerminalSegmentLength,
                                PerpendicularLapCoverageSpan =
                                    originalCollectivePerpendicularStartPose
                                        .PerpendicularLapCoverageSpan,
                                IsPerpendicularLap = true
                            };
                    }
                    if (oppositeCollectiveTerminalPose == null)
                    {
                        continue;
                    }

                    var oppositeCollectiveLapEndpoint =
                        oppositeCollectiveTerminalPose.LapEndpoint;
                    if (collectivePerpendicularStartPoses.Any(existingPose =>
                            existingPose.LapEndpoint.DistanceTo(
                                oppositeCollectiveLapEndpoint) <=
                                GeometryTolerance &&
                            existingPose.RouteDirection.Normalized.Dot(
                                oppositeCollectiveRouteDirection) >=
                                1d - GeometryTolerance))
                    {
                        continue;
                    }

                    collectivePerpendicularStartPoses.Add(
                        oppositeCollectiveTerminalPose);
                }
            }

            if (collectivePerpendicularStartPoses.Count == 0)
            {
                return;
            }


            var bridgeStartPlacements = LinkageRouteStackPlacementSolver
                .ResolveTerminalPlacements(
                    physicalRouteGroupRequest.StartTerminal,
                    bridgeStackCount,
                    bridgeSingleBusbarThickness,
                    physicalRouteGroupRequest.StackStartSpace,
                    physicalRouteGroupRequest.StackMiddleSpace,
                    false,
                    true)
                .OrderBy(terminalPlacement => Math.Abs(
                    terminalPlacement.BaseCenterOffset))
                .ThenBy(terminalPlacement => terminalPlacement.BaseCenterOffset)
                .ToList();
            var maximumSpecialCandidateCount = Math.Max(
                8,
                Math.Min(
                    32,
                    physicalRouteRequest.SearchOptions.MaximumCandidatesPerGroup));
            var maximumSpecialCandidateCountPerPose = Math.Max(
                4,
                maximumSpecialCandidateCount /
                    collectivePerpendicularStartPoses.Count);
            var downstreamCandidatePoolsByVirtualTerminalSignature =
                new Dictionary<string, LinkageRouteCandidatePool>(
                    StringComparer.Ordinal);
            var acceptedSpecialCandidateCount = 0;
            foreach (var collectivePerpendicularStartPose in
                collectivePerpendicularStartPoses)
            {
                var acceptedSpecialCandidateCountForPose = 0;
                foreach (var sourceBridgeStartPlacement in bridgeStartPlacements)
                {
                    if (acceptedSpecialCandidateCountForPose >=
                        maximumSpecialCandidateCountPerPose)
                    {
                        break;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var mappedBridgeStartPlacement =
                        MapDoubleStartBridgePlacementToMemberOrder(
                            physicalRouteGroupRequest,
                            sourceBridgeStartPlacement);
                    if (mappedBridgeStartPlacement == null ||
                        mappedBridgeStartPlacement.GeneratedMemberOffsets.Count !=
                            bridgeStackCount)
                    {
                        continue;
                    }

                    var bridgeBaseStartPoint = TranslateDoubleStartBridgePoint(
                        collectivePerpendicularStartPose.LapEndpoint,
                        physicalRouteGroupRequest.StartTerminal
                            .ThicknessDirection.Normalized,
                        mappedBridgeStartPlacement.BaseCenterOffset);
                    var bridgeLengthCandidates = BuildDoubleStartBridgeLengths(
                        physicalRouteRequest,
                        physicalRouteGroupRequest,
                        collectivePerpendicularStartPose,
                        bridgeBaseStartPoint,
                        bridgeBusbarWidth,
                        false);
                    foreach (var bridgeLengthCandidate in bridgeLengthCandidates)
                    {
                        if (acceptedSpecialCandidateCountForPose >=
                            maximumSpecialCandidateCountPerPose)
                        {
                            break;
                        }

                        if (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }

                        var bridgeBaseEndPoint = TranslateDoubleStartBridgePoint(
                            bridgeBaseStartPoint,
                            collectivePerpendicularStartPose.RouteDirection.Normalized,
                            bridgeLengthCandidate);
                        var straightBridgeBaseSetting =
                            CreateDoubleStartBridgeSetting(
                            physicalRouteRequest,
                            physicalRouteGroupRequest,
                            bridgeBaseStartPoint,
                            bridgeBaseEndPoint);
                        var bridgeBaseSettings =
                            BuildDoubleStartBridgeBaseSettings(
                                physicalRouteRequest,
                                physicalRouteGroupRequest,
                                straightBridgeBaseSetting,
                                bridgeBusbarWidth,
                                preferBentBridge,
                                mappedBridgeStartPlacement
                                    .GeneratedMemberOffsets);
                        foreach (var bridgeBaseSetting in bridgeBaseSettings)
                        {
                            if (acceptedSpecialCandidateCountForPose >=
                                maximumSpecialCandidateCountPerPose)
                            {
                                break;
                            }

                            if (cancellationToken.IsCancellationRequested)
                            {
                                return;
                            }

                            var bridgeTerminalPoint = bridgeBaseSetting.PointList[
                                bridgeBaseSetting.PointList.Count - 1];
                            var bridgeTerminalDirection = new Vector3d(
                                bridgeBaseSetting.PointList[
                                    bridgeBaseSetting.PointList.Count - 2],
                                bridgeTerminalPoint);
                            var bridgeTerminalThicknessDirection =
                                bridgeTerminalDirection.Normalized.Cross(
                                    bridgeBaseSetting.WidthVector.Normalized);
                            if (bridgeTerminalThicknessDirection == null ||
                                bridgeTerminalThicknessDirection.Norm <=
                                    GeometryTolerance)
                            {
                                continue;
                            }

                            bridgeTerminalThicknessDirection =
                                bridgeTerminalThicknessDirection.Normalized;
                            var actualBridgeSettings =
                                CreateDoubleStartBridgeStackSettings(
                                    bridgeBaseSetting,
                                    physicalRouteGroupRequest.StartTerminal
                                        .ThicknessDirection.Normalized,
                                    mappedBridgeStartPlacement
                                        .GeneratedMemberOffsets);
                            IReadOnlyList<Point3d>
                                bridgeInternalLapReferencePoints = null;
                            var defaultDownstreamCandidateCount = 0;
                            for (var bridgeDownstreamLapAttemptIndex = 0;
                                bridgeDownstreamLapAttemptIndex < 2;
                                bridgeDownstreamLapAttemptIndex++)
                            {
                                if (bridgeDownstreamLapAttemptIndex > 0)
                                {
                                    // L 桥的默认末端搭接可能先生成出后续候选，
                                    // 但该后续排在与真实 L 桥合并后仍会回穿首腿。
                                    // 只有合并结果全部无效时才补充末腿内部搭接点，
                                    // 避免给已有有效桥接的普通方案增加枚举成本。
                                    if (bridgeBaseSetting.PointList.Count < 3 &&
                                        defaultDownstreamCandidateCount > 0)
                                    {
                                        break;
                                    }

                                    var bridgeInternalLapContinuationLength =
                                        bridgeBusbarWidth / 2d +
                                        LinkageRouteStackGeometry
                                            .GetStackBendAllowance(
                                                physicalRouteGroupRequest
                                                    .GetBusbarThickness(
                                                        physicalRouteRequest),
                                                bridgeStackCount,
                                                physicalRouteGroupRequest
                                                    .StackMiddleSpace,
                                                physicalRouteRequest
                                                    .GetEffectiveBendAllowance());
                                    bridgeInternalLapReferencePoints =
                                        BuildDoubleStartBridgeInternalLapReferencePoints(
                                            bridgeBaseSetting,
                                            physicalRouteGroupRequest.EndTerminal,
                                            bridgeBusbarWidth,
                                            bridgeInternalLapContinuationLength,
                                            physicalRouteRequest.SearchOptions
                                                .DoubleStartBridgeMaximumInternalLapCandidateCount);
                                    if (bridgeInternalLapReferencePoints.Count == 0)
                                    {
                                        break;
                                    }
                                }

                                var virtualBridgeTerminal =
                                    CreateDoubleStartBridgeVirtualTerminal(
                                        bridgeTerminalPoint,
                                        bridgeTerminalDirection,
                                        bridgeTerminalThicknessDirection,
                                        actualBridgeSettings,
                                        bridgeInternalLapReferencePoints);
                                var virtualTerminalSignature =
                                    GetDoubleStartBridgeDownstreamPoolSignature(
                                        virtualBridgeTerminal);
                                if (!downstreamCandidatePoolsByVirtualTerminalSignature
                                    .TryGetValue(
                                        virtualTerminalSignature,
                                        out var downstreamCandidatePool))
                                {
                                    var virtualBridgeRouteGroup =
                                        CreateDoubleStartBridgeVirtualRouteGroup(
                                            physicalRouteGroupRequest,
                                            virtualBridgeTerminal,
                                            bridgeStackCount);
                                    var virtualBridgeRouteRequest =
                                        CreateDoubleStartBridgeVirtualRouteRequest(
                                            physicalRouteRequest,
                                            virtualBridgeRouteGroup);
                                    downstreamCandidatePool = BuildFastPool(
                                        virtualBridgeRouteRequest,
                                        virtualBridgeRouteGroup,
                                        fixedTerminalAndObstacleSolids,
                                        LinkageRouteTraversalDirection.Forward,
                                        cancellationToken);
                                    if (cancellationToken.IsCancellationRequested)
                                    {
                                        return;
                                    }

                                    downstreamCandidatePoolsByVirtualTerminalSignature[
                                        virtualTerminalSignature] =
                                        downstreamCandidatePool;
                                }
                                if (bridgeDownstreamLapAttemptIndex == 0)
                                {
                                    defaultDownstreamCandidateCount =
                                        downstreamCandidatePool.ValidCandidates.Count;
                                }

                                var acceptedMergedBridgeForLapAttempt = false;
                                foreach (var downstreamCandidate in
                                    downstreamCandidatePool.ValidCandidates)
                                {
                                    if (acceptedSpecialCandidateCountForPose >=
                                        maximumSpecialCandidateCountPerPose)
                                    {
                                        break;
                                    }

                                    if (cancellationToken.IsCancellationRequested)
                                    {
                                        return;
                                    }

                                    var mergedBridgeCandidate =
                                        BuildMergedDoubleStartBridgeCandidate(
                                            physicalRouteRequest,
                                            physicalRouteGroupRequest,
                                            fixedTerminalAndObstacleSolids,
                                            bridgeBaseSetting,
                                            actualBridgeSettings,
                                            mappedBridgeStartPlacement,
                                            downstreamCandidate,
                                            preferBentBridge
                                                ? bridgeBaseSetting.PointList.Count >= 3
                                                    ? -10
                                                    : 1
                                                : 0,
                                            cancellationToken);
                                    if (mergedBridgeCandidate == null)
                                    {
                                        continue;
                                    }

                                    AddCandidate(
                                        destinationCandidatePool,
                                        mergedBridgeCandidate);
                                    if (mergedBridgeCandidate.IsValid)
                                    {
                                        acceptedMergedBridgeForLapAttempt = true;
                                        acceptedSpecialCandidateCount++;
                                        acceptedSpecialCandidateCountForPose++;
                                    }

                                    if (acceptedSpecialCandidateCount >=
                                        maximumSpecialCandidateCount)
                                    {
                                        return;
                                    }
                                }

                                if (acceptedMergedBridgeForLapAttempt ||
                                    acceptedSpecialCandidateCountForPose >=
                                        maximumSpecialCandidateCountPerPose)
                                {
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }

        private static IReadOnlyList<double> BuildDoubleStartBridgeLengths(
            LinkageMultiGroupRouteRequest physicalRouteRequest,
            LinkageRouteGroupRequest physicalRouteGroupRequest,
            LinkageRouteTerminalPose collectivePerpendicularStartPose,
            Point3d bridgeBaseStartPoint,
            double generatedBusbarWidth,
            bool reserveBentDownstreamLap)
        {
            var widestStartFixedBusbar = physicalRouteGroupRequest.StartTerminal
                .Members
                .Select(terminalMember =>
                    terminalMember?.FixedBusbar?.BusBarWidth ?? 0d)
                .DefaultIfEmpty(generatedBusbarWidth)
                .Max();
            var requiredStraightDoubleLapLength =
                physicalRouteRequest.AllowPartialStartPerpendicularLap
                    ? Math.Max(
                        generatedBusbarWidth,
                        collectivePerpendicularStartPose
                            .PerpendicularLapCoverageSpan) +
                        generatedBusbarWidth
                    : (LinkageRouteDoubleStartLapGeometry.TryCreateLengthSeparatedCollectiveFixedBusbar(
                            physicalRouteGroupRequest.StartTerminal, out _)
                        ? collectivePerpendicularStartPose.PerpendicularLapCoverageSpan
                        : widestStartFixedBusbar) + generatedBusbarWidth;
            var minimumBridgeLength = Math.Max(
                collectivePerpendicularStartPose.RequiredTerminalSegmentLength,
                Math.Max(
                    physicalRouteRequest.MinimumSegmentLength,
                    requiredStraightDoubleLapLength));
            var normalizedBridgeLengthDirection =
                collectivePerpendicularStartPose.RouteDirection.Normalized;
            var projectedCabinetCoordinates = physicalRouteGroupRequest
                .EndTerminal.Members
                .Where(terminalMember => terminalMember?.FixedBusbar?.PointList !=
                    null)
                .SelectMany(terminalMember => terminalMember.FixedBusbar.PointList)
                .Where(cabinetFixedPoint => cabinetFixedPoint != null)
                .Select(cabinetFixedPoint => new Vector3d(
                        bridgeBaseStartPoint,
                        cabinetFixedPoint)
                    .Dot(normalizedBridgeLengthDirection))
                .ToList();
            if (physicalRouteGroupRequest.EndTerminal.RouteAnchor != null)
            {
                projectedCabinetCoordinates.Add(new Vector3d(
                        bridgeBaseStartPoint,
                        physicalRouteGroupRequest.EndTerminal.RouteAnchor)
                    .Dot(normalizedBridgeLengthDirection));
            }

            // 多根柜顶排的外层成员可能不在组件 RouteAnchor 上。桥接排必须
            // 延伸到该方向最远柜顶实体投影再加半个生成排宽，否则三叠排的
            // 朝柜顶 L 臂会少掉外层间距，后续垂直搭接无法达到满排宽。
            var bridgeDownstreamStackBendAllowance =
                reserveBentDownstreamLap
                    ? LinkageRouteStackGeometry.GetStackBendAllowance(
                        physicalRouteGroupRequest.GetBusbarThickness(
                            physicalRouteRequest),
                        physicalRouteGroupRequest.GetStackCount(
                            physicalRouteRequest),
                        physicalRouteGroupRequest.StackMiddleSpace,
                        physicalRouteRequest.GetEffectiveBendAllowance())
                    : 0d;
            var projectedCabinetDistance =
                projectedCabinetCoordinates.DefaultIfEmpty(0d).Max() +
                generatedBusbarWidth / 2d +
                bridgeDownstreamStackBendAllowance;
            var projectedBridgeLength = Math.Max(
                minimumBridgeLength,
                projectedCabinetDistance);
            var maximumContainmentBridgeLength =
                GetDoubleStartBridgeContainmentLength(
                    physicalRouteRequest,
                    bridgeBaseStartPoint,
                    normalizedBridgeLengthDirection);
            if (maximumContainmentBridgeLength + GeometryTolerance <
                minimumBridgeLength)
            {
                return Array.Empty<double>();
            }

            projectedBridgeLength = Math.Min(
                projectedBridgeLength,
                maximumContainmentBridgeLength);
            var bridgeLengthCandidates = new List<double>
            {
                projectedBridgeLength,
                minimumBridgeLength
            };
            if (projectedBridgeLength - minimumBridgeLength >
                generatedBusbarWidth + GeometryTolerance)
            {
                bridgeLengthCandidates.Add(
                    (projectedBridgeLength + minimumBridgeLength) / 2d);
            }

            return bridgeLengthCandidates
                .Where(bridgeLength =>
                    bridgeLength + GeometryTolerance >= minimumBridgeLength &&
                    bridgeLength <= maximumContainmentBridgeLength +
                        GeometryTolerance)
                .Distinct()
                .ToList();
        }

        private static bool AreTerminalThicknessDirectionsPerpendicular(
            LinkageRouteGroupRequest physicalRouteGroupRequest)
        {
            var physicalStartThicknessDirection = physicalRouteGroupRequest?
                .StartTerminal?.ThicknessDirection;
            var physicalCabinetThicknessDirection = physicalRouteGroupRequest?
                .EndTerminal?.ThicknessDirection;
            return physicalStartThicknessDirection != null &&
                physicalCabinetThicknessDirection != null &&
                physicalStartThicknessDirection.Norm > GeometryTolerance &&
                physicalCabinetThicknessDirection.Norm > GeometryTolerance &&
                Math.Abs(
                    physicalStartThicknessDirection.Normalized.Dot(
                        physicalCabinetThicknessDirection.Normalized)) <=
                    LinkageRouteGeometryTolerances.AssemblyDirection;
        }

        private static IReadOnlyList<PlanarBendBusbarSetting>
            BuildDoubleStartBridgeBaseSettings(
            LinkageMultiGroupRouteRequest physicalRouteRequest,
            LinkageRouteGroupRequest physicalRouteGroupRequest,
            PlanarBendBusbarSetting straightBridgeBaseSetting,
            double generatedBusbarWidth,
            bool preferBentBridge,
            IEnumerable<double> bridgeMemberOffsets)
        {
            // 双始端连接排是固定方向、固定层位的直 I 排；转向必须由其后的
            // 普通路径排承担，连接排本身只能沿排长方向改变两个端点。
            if (preferBentBridge)
            {
                var bentBridgeSettings = BuildDoubleStartBentBridgeSettings(
                    physicalRouteRequest,
                    physicalRouteGroupRequest,
                    straightBridgeBaseSetting,
                    generatedBusbarWidth,
                    bridgeMemberOffsets);
                if (bentBridgeSettings.Count > 0)
                {
                    return bentBridgeSettings
                        .Concat(new[] { straightBridgeBaseSetting })
                        .ToArray();
                }
            }

            return new[] { straightBridgeBaseSetting };
        }

        private static IReadOnlyList<PlanarBendBusbarSetting>
            BuildDoubleStartBentBridgeSettings(
            LinkageMultiGroupRouteRequest physicalRouteRequest,
            LinkageRouteGroupRequest physicalRouteGroupRequest,
            PlanarBendBusbarSetting straightBridgeBaseSetting,
            double generatedBusbarWidth,
            IEnumerable<double> bridgeMemberOffsets)
        {
            if (physicalRouteRequest == null ||
                physicalRouteGroupRequest?.StartTerminal == null ||
                physicalRouteGroupRequest.EndTerminal?.RouteAnchor == null ||
                straightBridgeBaseSetting?.PointList == null ||
                straightBridgeBaseSetting.PointList.Count != 2 ||
                generatedBusbarWidth <= GeometryTolerance ||
                !AreTerminalThicknessDirectionsPerpendicular(
                    physicalRouteGroupRequest))
            {
                return Array.Empty<PlanarBendBusbarSetting>();
            }

            var straightBridgeStartPoint =
                straightBridgeBaseSetting.PointList[0];
            var straightBridgeCornerPoint =
                straightBridgeBaseSetting.PointList[1];
            var straightBridgeDirection = new Vector3d(
                straightBridgeStartPoint,
                straightBridgeCornerPoint);
            var physicalStartThicknessDirection = physicalRouteGroupRequest
                .StartTerminal.ThicknessDirection;
            if (straightBridgeDirection.Norm <= GeometryTolerance ||
                physicalStartThicknessDirection == null ||
                physicalStartThicknessDirection.Norm <= GeometryTolerance)
            {
                return Array.Empty<PlanarBendBusbarSetting>();
            }

            var normalizedStraightBridgeDirection =
                straightBridgeDirection.Normalized;
            var normalizedStartThicknessDirection =
                physicalStartThicknessDirection.Normalized;
            var generatedSingleBusbarThickness = physicalRouteGroupRequest
                .GetBusbarThickness(physicalRouteRequest);
            var maximumBridgeMemberOffsetMagnitude =
                (bridgeMemberOffsets ?? Enumerable.Empty<double>())
                    .Select(Math.Abs)
                    .DefaultIfEmpty(0d)
                    .Max();
            // L 桥属于普通路径候选，转角预留只能由普通叠排包络决定。
            // 端头无垫块若需要移动该桥，必须在完整方案后处理时复核实体。
            var outgoingLegInnerHalfExtent =
                maximumBridgeMemberOffsetMagnitude +
                generatedSingleBusbarThickness / 2d;
            var straightBridgeLength = straightBridgeDirection.Norm;
            var sameGroupStartBoundaryProjection =
                GetDoubleStartTerminalMaximumProjection(
                    physicalRouteGroupRequest.StartTerminal,
                    straightBridgeStartPoint,
                    normalizedStraightBridgeDirection);
            var requiredBridgeCornerProjection = Math.Max(
                straightBridgeLength,
                sameGroupStartBoundaryProjection +
                    outgoingLegInnerHalfExtent);
            var requiredCrossGroupClearance = physicalRouteGroupRequest
                .GetRequiredInsulationClearance(physicalRouteRequest);
            foreach (var otherPhysicalRouteGroupRequest in
                physicalRouteRequest.Groups.Where(routeGroupRequest =>
                    routeGroupRequest != null &&
                    !ReferenceEquals(
                        routeGroupRequest,
                        physicalRouteGroupRequest) &&
                    !string.Equals(
                        routeGroupRequest.GetClearanceGroupKey(),
                        physicalRouteGroupRequest.GetClearanceGroupKey(),
                        StringComparison.OrdinalIgnoreCase)))
            {
                var otherGroupStartBoundaryProjection =
                    GetDoubleStartTerminalMaximumProjection(
                        otherPhysicalRouteGroupRequest.StartTerminal,
                        straightBridgeStartPoint,
                        normalizedStraightBridgeDirection);
                requiredBridgeCornerProjection = Math.Max(
                    requiredBridgeCornerProjection,
                    otherGroupStartBoundaryProjection +
                        outgoingLegInnerHalfExtent +
                        requiredCrossGroupClearance);
            }

            var bentBridgeCornerPoint = TranslateDoubleStartBridgePoint(
                straightBridgeCornerPoint,
                normalizedStraightBridgeDirection,
                Math.Max(
                    0d,
                    requiredBridgeCornerProjection -
                        straightBridgeLength));
            // 双始端桥首段沿两始端的整体排宽跨接。柜顶厚面与始端厚面
            // 垂直时，L 桥必须沿始端排厚方向折弯，才能把末端局部排厚
            // 旋转到柜顶排厚方向；沿始端排长方向折弯不会完成厚面换向，
            // 后续三叠排只能形成重叠搭接区而无法得到合法候选。
            var rawBentLegStraightProjection =
                normalizedStartThicknessDirection.Dot(
                normalizedStraightBridgeDirection);
            var planarBentLegDirection = new Vector3d(
                normalizedStartThicknessDirection.X -
                    normalizedStraightBridgeDirection.X *
                    rawBentLegStraightProjection,
                normalizedStartThicknessDirection.Y -
                    normalizedStraightBridgeDirection.Y *
                    rawBentLegStraightProjection,
                normalizedStartThicknessDirection.Z -
                    normalizedStraightBridgeDirection.Z *
                    rawBentLegStraightProjection);

            if (planarBentLegDirection.Norm <= GeometryTolerance)
            {
                return Array.Empty<PlanarBendBusbarSetting>();
            }

            var normalizedPlanarBentLegDirection =
                planarBentLegDirection.Normalized;
            var oppositePlanarBentLegDirection = new Vector3d(
                -normalizedPlanarBentLegDirection.X,
                -normalizedPlanarBentLegDirection.Y,
                -normalizedPlanarBentLegDirection.Z);
            var cornerToCabinetVector = new Vector3d(
                bentBridgeCornerPoint,
                physicalRouteGroupRequest.EndTerminal.RouteAnchor);
            var orderedBentLegDirections = new[]
                {
                    normalizedPlanarBentLegDirection,
                    oppositePlanarBentLegDirection
                }
                .OrderByDescending(bentLegDirection =>
                    cornerToCabinetVector.Dot(bentLegDirection))
                .ToList();
            var generatedStackBendAllowance = LinkageRouteStackGeometry
                .GetStackBendAllowance(
                    generatedSingleBusbarThickness,
                    physicalRouteGroupRequest.GetStackCount(
                        physicalRouteRequest),
                    physicalRouteGroupRequest.StackMiddleSpace,
                    physicalRouteRequest.GetEffectiveBendAllowance());
            var minimumBentLegLength = Math.Max(
                physicalRouteRequest.MinimumSegmentLength,
                generatedBusbarWidth + generatedStackBendAllowance);
            var bentBridgeSettings = new List<PlanarBendBusbarSetting>();
            for (var orderedBentLegDirectionIndex = 0;
                orderedBentLegDirectionIndex < orderedBentLegDirections.Count;
                orderedBentLegDirectionIndex++)
            {
                var orderedBentLegDirection = orderedBentLegDirections[
                    orderedBentLegDirectionIndex];
                var projectedCabinetLength = cornerToCabinetVector.Dot(
                    orderedBentLegDirection) + generatedBusbarWidth / 2d;
                var preferredBentLegLength = Math.Max(
                    minimumBentLegLength,
                    projectedCabinetLength);
                // L 臂之后还要由一根带折弯的排继续寻路。无论臂指向柜顶
                // 还是背离柜顶，除本臂自身的排宽与叠排折弯留量外都必须
                // 再保留半个排宽：一方面保证后续垂直搭接交点两侧各有
                // 半排宽，另一方面避免后续首段回穿桥接排的第一直段。
                preferredBentLegLength = Math.Max(
                    preferredBentLegLength,
                    minimumBentLegLength + generatedBusbarWidth / 2d);
                var maximumBentLegLength =
                    GetDoubleStartBridgeContainmentLength(
                        physicalRouteRequest,
                        bentBridgeCornerPoint,
                        orderedBentLegDirection);
                if (maximumBentLegLength + GeometryTolerance <
                    minimumBentLegLength)
                {
                    continue;
                }

                preferredBentLegLength = Math.Min(
                    preferredBentLegLength,
                    maximumBentLegLength);
                var bentBridgeEndPoint = TranslateDoubleStartBridgePoint(
                    bentBridgeCornerPoint,
                    orderedBentLegDirection,
                    preferredBentLegLength);
                bentBridgeSettings.Add(
                    CreateDoubleStartBridgeSetting(
                        physicalRouteRequest,
                        physicalRouteGroupRequest,
                        new[]
                        {
                            straightBridgeStartPoint,
                            bentBridgeCornerPoint,
                            bentBridgeEndPoint
                        }));
            }

            return bentBridgeSettings;
        }

        private static double GetDoubleStartTerminalMaximumProjection(
            LinkageTerminalAssembly terminalAssembly,
            Point3d projectionOrigin,
            Vector3d normalizedProjectionDirection)
        {
            if (terminalAssembly?.Members == null ||
                projectionOrigin == null ||
                normalizedProjectionDirection == null ||
                normalizedProjectionDirection.Norm <= GeometryTolerance)
            {
                return double.NegativeInfinity;
            }

            var maximumTerminalProjection = double.NegativeInfinity;
            foreach (var terminalMember in terminalAssembly.Members)
            {
                var fixedBusbarSetting = terminalMember?.FixedBusbar;
                if (fixedBusbarSetting?.PointList == null ||
                    fixedBusbarSetting.PointList.Count == 0)
                {
                    continue;
                }

                var projectedHalfExtent = 0d;
                if (fixedBusbarSetting.WidthVector != null &&
                    fixedBusbarSetting.WidthVector.Norm > GeometryTolerance)
                {
                    projectedHalfExtent += Math.Abs(
                        fixedBusbarSetting.WidthVector.Normalized.Dot(
                            normalizedProjectionDirection)) *
                        fixedBusbarSetting.BusBarWidth / 2d;
                }

                if (fixedBusbarSetting.StartBusbarVector != null &&
                    fixedBusbarSetting.StartBusbarVector.Norm >
                        GeometryTolerance)
                {
                    projectedHalfExtent += Math.Abs(
                        fixedBusbarSetting.StartBusbarVector.Normalized.Dot(
                            normalizedProjectionDirection)) *
                        fixedBusbarSetting.BusBarThickness / 2d;
                }

                foreach (var fixedBusbarPoint in
                    fixedBusbarSetting.PointList.Where(point => point != null))
                {
                    maximumTerminalProjection = Math.Max(
                        maximumTerminalProjection,
                        new Vector3d(
                                projectionOrigin,
                                fixedBusbarPoint)
                            .Dot(normalizedProjectionDirection) +
                            projectedHalfExtent);
                }
            }

            return maximumTerminalProjection;
        }

        private static double GetDoubleStartBridgeContainmentLength(
            LinkageMultiGroupRouteRequest physicalRouteRequest,
            Point3d bridgeBaseStartPoint,
            Vector3d normalizedBridgeLengthDirection)
        {
            if (physicalRouteRequest?.ContainmentMinimum == null ||
                physicalRouteRequest.ContainmentMaximum == null)
            {
                return double.PositiveInfinity;
            }

            if (Math.Abs(normalizedBridgeLengthDirection.X) >= 1d - 1e-6d)
            {
                return normalizedBridgeLengthDirection.X > 0d
                    ? physicalRouteRequest.ContainmentMaximum.X -
                        bridgeBaseStartPoint.X
                    : bridgeBaseStartPoint.X -
                        physicalRouteRequest.ContainmentMinimum.X;
            }

            if (Math.Abs(normalizedBridgeLengthDirection.Y) >= 1d - 1e-6d)
            {
                return normalizedBridgeLengthDirection.Y > 0d
                    ? physicalRouteRequest.ContainmentMaximum.Y -
                        bridgeBaseStartPoint.Y
                    : bridgeBaseStartPoint.Y -
                        physicalRouteRequest.ContainmentMinimum.Y;
            }

            return normalizedBridgeLengthDirection.Z > 0d
                ? physicalRouteRequest.ContainmentMaximum.Z - bridgeBaseStartPoint.Z
                : bridgeBaseStartPoint.Z - physicalRouteRequest.ContainmentMinimum.Z;
        }

        private static LinkageRouteStackTerminalPlacement
            MapDoubleStartBridgePlacementToMemberOrder(
            LinkageRouteGroupRequest physicalRouteGroupRequest,
            LinkageRouteStackTerminalPlacement sourceBridgeStartPlacement)
        {
            if (sourceBridgeStartPlacement?.GeneratedMemberOffsets == null)
            {
                return null;
            }

            var reverseBridgeMemberOrder = physicalRouteGroupRequest.StackReverse;
            var normalizedStartThicknessDirection = physicalRouteGroupRequest
                .StartTerminal.ThicknessDirection.Normalized;
            if (physicalRouteGroupRequest.StackMoveVector != null &&
                physicalRouteGroupRequest.StackMoveVector.Norm > GeometryTolerance &&
                physicalRouteGroupRequest.StackMoveVector.Normalized.Dot(
                    normalizedStartThicknessDirection) < -GeometryTolerance)
            {
                reverseBridgeMemberOrder = !reverseBridgeMemberOrder;
            }

            var bridgeMemberCount = sourceBridgeStartPlacement
                .GeneratedMemberOffsets.Count;
            if ((sourceBridgeStartPlacement
                    .SpacerlessMovedGeneratedMemberIndices ??
                    new List<int>())
                .Any(generatedMemberIndex => generatedMemberIndex < 0 ||
                    generatedMemberIndex >= bridgeMemberCount))
            {
                return null;
            }

            var mappedBridgeMemberOffsets = new double[bridgeMemberCount];
            var physicalToMappedBridgeMemberIndices = new int[bridgeMemberCount];
            for (var mappedBridgeMemberIndex = 0;
                mappedBridgeMemberIndex < bridgeMemberCount;
                mappedBridgeMemberIndex++)
            {
                var physicalBridgeMemberIndex = reverseBridgeMemberOrder
                    ? bridgeMemberCount - 1 - mappedBridgeMemberIndex
                    : mappedBridgeMemberIndex;
                mappedBridgeMemberOffsets[mappedBridgeMemberIndex] =
                    sourceBridgeStartPlacement.GeneratedMemberOffsets[
                        physicalBridgeMemberIndex];
                physicalToMappedBridgeMemberIndices[physicalBridgeMemberIndex] =
                    mappedBridgeMemberIndex;
            }

            return new LinkageRouteStackTerminalPlacement
            {
                Signature = sourceBridgeStartPlacement.Signature,
                BaseCenterOffset = sourceBridgeStartPlacement.BaseCenterOffset,
                GeneratedMemberOffsets = mappedBridgeMemberOffsets,
                SpacerlessMovedGeneratedMemberIndices =
                    (sourceBridgeStartPlacement
                            .SpacerlessMovedGeneratedMemberIndices ??
                            new List<int>())
                    .Select(physicalGeneratedMemberIndex =>
                        physicalToMappedBridgeMemberIndices[
                            physicalGeneratedMemberIndex])
                    .OrderBy(mappedGeneratedMemberIndex =>
                        mappedGeneratedMemberIndex)
                    .ToList(),
                DoubleStartHorizontalBridgeGeneratedMemberIndex =
                    sourceBridgeStartPlacement
                        .DoubleStartHorizontalBridgeGeneratedMemberIndex.HasValue
                        ? (int?)physicalToMappedBridgeMemberIndices[
                            sourceBridgeStartPlacement
                                .DoubleStartHorizontalBridgeGeneratedMemberIndex.Value]
                        : null,
                DoubleStartHorizontalBridgeSideSign =
                    sourceBridgeStartPlacement
                        .DoubleStartHorizontalBridgeSideSign,
                DoubleStartHorizontalBridgeThickness =
                    sourceBridgeStartPlacement
                        .DoubleStartHorizontalBridgeThickness,
                DoubleStartHorizontalBridgePlacements =
                    (sourceBridgeStartPlacement.DoubleStartHorizontalBridgePlacements ??
                        new List<LinkageRouteTerminalBridgePlacement>())
                    .Select(bridgePlacement => new LinkageRouteTerminalBridgePlacement
                    {
                        GeneratedMemberIndex = bridgePlacement.GeneratedMemberIndex,
                        SideSign = bridgePlacement.SideSign,
                        Thickness = bridgePlacement.Thickness
                    }).ToList(),
                Contacts = (sourceBridgeStartPlacement.Contacts ??
                        new List<LinkageRouteStackTerminalContact>())
                    .Select(terminalContact =>
                        new LinkageRouteStackTerminalContact
                        {
                            GeneratedMemberIndex =
                                physicalToMappedBridgeMemberIndices[
                                    terminalContact.GeneratedMemberIndex],
                            FixedMemberIndex = terminalContact.FixedMemberIndex
                        })
                    .ToList()
            };
        }

        private static PlanarBendBusbarSetting CreateDoubleStartBridgeSetting(
            LinkageMultiGroupRouteRequest physicalRouteRequest,
            LinkageRouteGroupRequest physicalRouteGroupRequest,
            Point3d bridgeStartPoint,
            Point3d bridgeEndPoint)
        {
            return CreateDoubleStartBridgeSetting(
                physicalRouteRequest,
                physicalRouteGroupRequest,
                new[]
                {
                    bridgeStartPoint,
                    bridgeEndPoint
                });
        }

        private static PlanarBendBusbarSetting CreateDoubleStartBridgeSetting(
            LinkageMultiGroupRouteRequest physicalRouteRequest,
            LinkageRouteGroupRequest physicalRouteGroupRequest,
            IEnumerable<Point3d> bridgeCenterlinePoints)
        {
            var copiedBridgeCenterlinePoints = (bridgeCenterlinePoints ??
                    Enumerable.Empty<Point3d>())
                .Where(bridgeCenterlinePoint => bridgeCenterlinePoint != null)
                .Select(CopyPoint)
                .ToList();
            if (copiedBridgeCenterlinePoints.Count < 2)
            {
                throw new ArgumentException(
                    "双始端桥接排至少需要两个有效中心线点。",
                    nameof(bridgeCenterlinePoints));
            }

            var normalizedFirstBridgeDirection = new Vector3d(
                copiedBridgeCenterlinePoints[0],
                copiedBridgeCenterlinePoints[1]).Normalized;
            var normalizedBridgeThicknessDirection = physicalRouteGroupRequest
                .StartTerminal.ThicknessDirection.Normalized;
            return new PlanarBendBusbarSetting
            {
                IsCenter = true,
                GroupKey = physicalRouteGroupRequest.GroupKey,
                StartBusbarVector = new Vector3d(
                    normalizedBridgeThicknessDirection.X,
                    normalizedBridgeThicknessDirection.Y,
                    normalizedBridgeThicknessDirection.Z),
                WidthVector = normalizedBridgeThicknessDirection
                    .Cross(normalizedFirstBridgeDirection).Normalized,
                PointList = copiedBridgeCenterlinePoints,
                BarBendingRadius = physicalRouteRequest.BendingRadius,
                BendAllowance = physicalRouteRequest.GetEffectiveBendAllowance(),
                BusBarWidth = physicalRouteGroupRequest.GetBusbarWidth(
                    physicalRouteRequest),
                BusBarThickness = physicalRouteGroupRequest.GetBusbarThickness(
                    physicalRouteRequest),
                StartLapWidth = physicalRouteGroupRequest.GetBusbarWidth(
                    physicalRouteRequest),
                EndLapWidth = physicalRouteGroupRequest.GetBusbarWidth(
                    physicalRouteRequest)
            };
        }

        private static IReadOnlyList<PlanarBendBusbarSetting>
            CreateDoubleStartBridgeStackSettings(
            PlanarBendBusbarSetting bridgeBaseSetting,
            Vector3d bridgeThicknessDirection,
            IEnumerable<double> bridgeMemberOffsets)
        {
            return (bridgeMemberOffsets ?? Enumerable.Empty<double>())
                .Select(bridgeMemberOffset =>
                {
                    var actualBridgeSetting = bridgeBaseSetting.Copy();
                    actualBridgeSetting.PointList =
                        bridgeBaseSetting.PointList.Count >= 3
                            ? bridgeBaseSetting.GetOffsetCenterlinePointList(
                                bridgeMemberOffset)
                            : bridgeBaseSetting.PointList
                                .Select(bridgePoint =>
                                    TranslateDoubleStartBridgePoint(
                                        bridgePoint,
                                        bridgeThicknessDirection,
                                        bridgeMemberOffset))
                                .ToList();
                    return actualBridgeSetting;
                })
                .ToList();
        }

        private static IReadOnlyList<Point3d>
            BuildDoubleStartBridgeInternalLapReferencePoints(
            PlanarBendBusbarSetting bridgeBaseSetting,
            LinkageTerminalAssembly downstreamTerminalAssembly,
            double generatedBusbarWidth,
            double minimumInternalLapContinuationLength,
            int maximumInternalLapCandidateCount)
        {
            if (bridgeBaseSetting?.PointList == null ||
                bridgeBaseSetting.PointList.Count < 2 ||
                generatedBusbarWidth <= GeometryTolerance ||
                minimumInternalLapContinuationLength < 0d)
            {
                return Array.Empty<Point3d>();
            }

            // I 桥只有一段；L 桥后续路径只能搭接在折弯后的末端直腿上。
            // 因此内部满搭接临界点必须沿最后一段计算，不能用整条 L 排
            // 首末点形成的对角向量代替真实排长方向。
            var bridgeReferenceStartPoint = bridgeBaseSetting.PointList[
                bridgeBaseSetting.PointList.Count - 2];
            var bridgeReferenceEndPoint = bridgeBaseSetting.PointList[
                bridgeBaseSetting.PointList.Count - 1];
            var bridgeReferenceDirection = new Vector3d(
                bridgeReferenceStartPoint,
                bridgeReferenceEndPoint);
            if (bridgeReferenceDirection.Norm <= generatedBusbarWidth +
                GeometryTolerance)
            {
                return Array.Empty<Point3d>();
            }

            var normalizedBridgeReferenceDirection =
                bridgeReferenceDirection.Normalized;
            var bridgeReferenceLength = bridgeReferenceDirection.Norm;
            var halfGeneratedBusbarWidth = generatedBusbarWidth / 2d;
            // 内部搭接后的第一条生成腿还要完成满搭接和叠排折弯。
            // 仅从 L 桥转角退半排宽时，外层成员仍会回穿桥的首腿。
            var minimumInternalLapCoordinate = Math.Max(
                halfGeneratedBusbarWidth,
                minimumInternalLapContinuationLength);
            var maximumInternalLapCoordinate =
                bridgeReferenceLength - halfGeneratedBusbarWidth;
            if (maximumInternalLapCoordinate <=
                minimumInternalLapCoordinate + GeometryTolerance)
            {
                return Array.Empty<Point3d>();
            }

            var downstreamProjectionCoordinates = new List<double>();
            if (downstreamTerminalAssembly?.RouteAnchor != null)
            {
                AddUniqueDoubleStartBridgeProjectionCoordinate(
                    downstreamProjectionCoordinates,
                    new Vector3d(
                            bridgeReferenceStartPoint,
                            downstreamTerminalAssembly.RouteAnchor)
                        .Dot(normalizedBridgeReferenceDirection));
            }

            foreach (var downstreamTerminalMember in
                downstreamTerminalAssembly?.Members ??
                    new List<LinkageTerminalMember>())
            {
                foreach (var downstreamFixedPoint in
                    downstreamTerminalMember?.FixedBusbar?.PointList ??
                        new List<Point3d>())
                {
                    if (downstreamFixedPoint == null)
                    {
                        continue;
                    }

                    AddUniqueDoubleStartBridgeProjectionCoordinate(
                        downstreamProjectionCoordinates,
                        new Vector3d(
                                bridgeReferenceStartPoint,
                                downstreamFixedPoint)
                            .Dot(normalizedBridgeReferenceDirection));
                }
            }

            downstreamProjectionCoordinates.Sort();
            var rawInternalLapCoordinates = new List<double>();
            foreach (var downstreamProjectionCoordinate in
                downstreamProjectionCoordinates)
            {
                rawInternalLapCoordinates.Add(
                    downstreamProjectionCoordinate);
                rawInternalLapCoordinates.Add(
                    downstreamProjectionCoordinate -
                        halfGeneratedBusbarWidth);
            }

            for (var downstreamProjectionIndex = 0;
                downstreamProjectionIndex + 1 <
                    downstreamProjectionCoordinates.Count;
                downstreamProjectionIndex++)
            {
                var adjacentProjectionMiddleCoordinate =
                    (downstreamProjectionCoordinates[
                         downstreamProjectionIndex] +
                     downstreamProjectionCoordinates[
                         downstreamProjectionIndex + 1]) / 2d;
                rawInternalLapCoordinates.Add(
                    adjacentProjectionMiddleCoordinate);
                rawInternalLapCoordinates.Add(
                    adjacentProjectionMiddleCoordinate -
                        halfGeneratedBusbarWidth);
            }

            var boundedInternalLapCoordinates = new List<double>();
            foreach (var rawInternalLapCoordinate in rawInternalLapCoordinates)
            {
                AddDoubleStartBridgeInternalLapCoordinate(
                    boundedInternalLapCoordinates,
                    rawInternalLapCoordinate,
                    minimumInternalLapCoordinate,
                    maximumInternalLapCoordinate);
            }

            var downstreamRouteAnchorProjectionCoordinate =
                downstreamTerminalAssembly?.RouteAnchor == null
                    ? maximumInternalLapCoordinate
                    : new Vector3d(
                            bridgeReferenceStartPoint,
                            downstreamTerminalAssembly.RouteAnchor)
                        .Dot(normalizedBridgeReferenceDirection);
            return boundedInternalLapCoordinates
                .OrderBy(internalLapCoordinate => Math.Abs(
                    internalLapCoordinate -
                    downstreamRouteAnchorProjectionCoordinate))
                .ThenByDescending(internalLapCoordinate =>
                    internalLapCoordinate)
                .Take(Math.Max(1, maximumInternalLapCandidateCount))
                .Select(internalLapCoordinate =>
                    TranslateDoubleStartBridgePoint(
                        bridgeReferenceStartPoint,
                        normalizedBridgeReferenceDirection,
                        internalLapCoordinate))
                .ToList();
        }

        private static void AddUniqueDoubleStartBridgeProjectionCoordinate(
            ICollection<double> destinationProjectionCoordinates,
            double projectionCoordinate)
        {
            if (double.IsNaN(projectionCoordinate) ||
                double.IsInfinity(projectionCoordinate) ||
                destinationProjectionCoordinates.Any(existingCoordinate =>
                    Math.Abs(existingCoordinate - projectionCoordinate) <=
                        GeometryTolerance))
            {
                return;
            }

            destinationProjectionCoordinates.Add(projectionCoordinate);
        }

        private static void AddDoubleStartBridgeInternalLapCoordinate(
            ICollection<double> destinationInternalLapCoordinates,
            double candidateInternalLapCoordinate,
            double minimumInternalLapCoordinate,
            double maximumInternalLapCoordinate)
        {
            if (double.IsNaN(candidateInternalLapCoordinate) ||
                double.IsInfinity(candidateInternalLapCoordinate))
            {
                return;
            }

            var boundedInternalLapCoordinate = Math.Max(
                minimumInternalLapCoordinate,
                Math.Min(
                    maximumInternalLapCoordinate,
                    candidateInternalLapCoordinate));
            // 桥末端退半排宽是默认姿态，内部兜底不能重复送入该位置。
            if (Math.Abs(
                    boundedInternalLapCoordinate -
                    maximumInternalLapCoordinate) <= GeometryTolerance ||
                destinationInternalLapCoordinates.Any(existingCoordinate =>
                    Math.Abs(
                        existingCoordinate -
                        boundedInternalLapCoordinate) <= GeometryTolerance))
            {
                return;
            }

            destinationInternalLapCoordinates.Add(
                boundedInternalLapCoordinate);
        }

        private static LinkageTerminalAssembly
            CreateDoubleStartBridgeVirtualTerminal(
            Point3d bridgeBaseEndPoint,
            Vector3d bridgeLengthDirection,
            Vector3d bridgeThicknessDirection,
            IReadOnlyList<PlanarBendBusbarSetting> actualBridgeSettings,
            IReadOnlyList<Point3d> preferredPerpendicularLapReferencePoints = null)
        {
            var normalizedBridgeLengthDirection = bridgeLengthDirection.Normalized;
            return new LinkageTerminalAssembly
            {
                RouteAnchor = CopyPoint(bridgeBaseEndPoint),
                LengthDirection = new Vector3d(
                    normalizedBridgeLengthDirection.X,
                    normalizedBridgeLengthDirection.Y,
                    normalizedBridgeLengthDirection.Z),
                ThicknessDirection = new Vector3d(
                    bridgeThicknessDirection.X,
                    bridgeThicknessDirection.Y,
                    bridgeThicknessDirection.Z),
                PreferredPerpendicularLapReferencePoints =
                    preferredPerpendicularLapReferencePoints?.Where(
                        referencePoint => referencePoint != null)
                        .Select(CopyPoint)
                        .ToList(),
                Members = actualBridgeSettings.Select(
                    (actualBridgeSetting, bridgeMemberIndex) =>
                    {
                        var virtualBridgeFixedSetting =
                            actualBridgeSetting.Copy();
                        virtualBridgeFixedSetting.PointList =
                            actualBridgeSetting.PointList
                                .Skip(Math.Max(
                                    0,
                                    actualBridgeSetting.PointList.Count - 2))
                                .Select(CopyPoint)
                                .ToList();
                        virtualBridgeFixedSetting.StartBusbarVector =
                            new Vector3d(
                                bridgeThicknessDirection.X,
                                bridgeThicknessDirection.Y,
                                bridgeThicknessDirection.Z);
                        virtualBridgeFixedSetting.WidthVector =
                            bridgeThicknessDirection.Normalized.Cross(
                                normalizedBridgeLengthDirection).Normalized;
                        return new LinkageTerminalMember
                        {
                            MemberKey = "DOUBLE_START_BRIDGE_" +
                                (bridgeMemberIndex + 1),
                            FixedBusbar = virtualBridgeFixedSetting,
                            ForbiddenHorizontalExitDirection = new Vector3d(
                                -normalizedBridgeLengthDirection.X,
                                -normalizedBridgeLengthDirection.Y,
                                -normalizedBridgeLengthDirection.Z)
                        };
                    })
                    .ToList()
            };
        }

        private static LinkageRouteGroupRequest
            CreateDoubleStartBridgeVirtualRouteGroup(
            LinkageRouteGroupRequest physicalRouteGroupRequest,
            LinkageTerminalAssembly virtualBridgeTerminal,
            int bridgeStackCount)
        {
            virtualBridgeTerminal.IsMaterializedRoutingBridge = true;
            return new LinkageRouteGroupRequest
            {
                GroupKey = physicalRouteGroupRequest.GroupKey,
                Phase = physicalRouteGroupRequest.Phase,
                DisplayName = physicalRouteGroupRequest.DisplayName,
                StartTerminal = virtualBridgeTerminal,
                EndTerminal = physicalRouteGroupRequest.EndTerminal,
                BusbarWidth = physicalRouteGroupRequest.BusbarWidth,
                BusbarThickness = physicalRouteGroupRequest.BusbarThickness,
                StackCount = bridgeStackCount,
                // 虚拟始端已经是实体化后的双始端连接排；从该排继续向
                // 柜顶生成的第一段属于中间排，因此仍使用中间排规格。
                StartLapBusbarWidth = physicalRouteGroupRequest.BusbarWidth,
                StartLapBusbarThickness = physicalRouteGroupRequest.BusbarThickness,
                StartLapStackCount = bridgeStackCount,
                CabinetTopLapBusbarWidth =
                    physicalRouteGroupRequest.CabinetTopLapBusbarWidth,
                CabinetTopLapBusbarThickness =
                    physicalRouteGroupRequest.CabinetTopLapBusbarThickness,
                CabinetTopLapStackCount =
                    physicalRouteGroupRequest.CabinetTopLapStackCount,
                StackStartSpace = 0d,
                StackMiddleSpace = physicalRouteGroupRequest.StackMiddleSpace,
                StackEndSpace = physicalRouteGroupRequest.StackEndSpace,
                StackMoveVector = physicalRouteGroupRequest.StackMoveVector == null
                    ? null
                    : new Vector3d(
                        physicalRouteGroupRequest.StackMoveVector.X,
                        physicalRouteGroupRequest.StackMoveVector.Y,
                        physicalRouteGroupRequest.StackMoveVector.Z),
                StackReverse = physicalRouteGroupRequest.StackReverse
            };
        }

        private static LinkageMultiGroupRouteRequest
            CreateDoubleStartBridgeVirtualRouteRequest(
            LinkageMultiGroupRouteRequest physicalRouteRequest,
            LinkageRouteGroupRequest virtualBridgeRouteGroup)
        {
            var virtualBridgeSearchOptions = physicalRouteRequest.SearchOptions
                .Copy();
            virtualBridgeSearchOptions.MaximumCandidatesPerGroup = Math.Max(
                physicalRouteRequest.SearchOptions
                    .DoubleStartBridgeMinimumCandidateCount,
                Math.Min(
                    physicalRouteRequest.SearchOptions
                        .DoubleStartBridgeMaximumCandidateCount,
                    physicalRouteRequest.SearchOptions
                        .MaximumCandidatesPerGroup));
            return new LinkageMultiGroupRouteRequest
            {
                Groups = new List<LinkageRouteGroupRequest>
                {
                    virtualBridgeRouteGroup
                },
                Obstacles = physicalRouteRequest.Obstacles,
                DefaultBusbarWidth = physicalRouteRequest.DefaultBusbarWidth,
                DefaultBusbarThickness = physicalRouteRequest.DefaultBusbarThickness,
                DefaultStackCount = physicalRouteRequest.DefaultStackCount,
                MinimumSegmentLength = physicalRouteRequest.MinimumSegmentLength,
                BendAllowance = physicalRouteRequest.BendAllowance,
                AllowBendGeometricLimit = physicalRouteRequest.AllowBendGeometricLimit,
                AllowConstrainedBendAllowanceReduction = physicalRouteRequest.AllowConstrainedBendAllowanceReduction,
                BendingRadius = physicalRouteRequest.BendingRadius,
                // 虚拟始端只是把已经建模的 I 型桥接排交给第二阶段寻路。
                // 绝缘阈值必须保持用户输入值，不能再按成员数或排厚增减。
                InsulationClearance = physicalRouteRequest.InsulationClearance,
                LapInsulationClearance = physicalRouteRequest.LapInsulationClearance,
                AllowUShapeRoutes = physicalRouteRequest.AllowUShapeRoutes,
                AllowDoubleStartMiddleLap = false,
                RequireFixedTerminalEndLap = physicalRouteRequest.RequireFixedTerminalEndLap,
                // 该虚拟固定端来自已经实体化的双始端 I/L 桥，不属于物理单始端；
                // 必须保持桥内整段可搭接范围，不能套用单始端居中规则。
                RequireSingleStartMiddleLap = false,
                EnableSingleStartConnectionBar = false,
                // 物理始端的短搭接已在桥排实体化前完成；桥排作为第二阶段
                // 虚拟固定端时必须继续执行普通垂直满搭接。
                AllowPartialStartPerpendicularLap = false,
                // 虚拟桥接阶段仍属于普通路径搜索；无垫块不得进入两阶段
                // 候选生成，也不能改变桥接成员的一一匹配。
                UseSpacerlessTerminalLap = false,
                MinimizeSupportBlocks = false,
                UseSpacerlessStartTerminalLapOverride = false,
                UseSpacerlessEndTerminalLapOverride = false,
                RequireAxisAlignedRoutes =
                    physicalRouteRequest.RequireAxisAlignedRoutes,
                UseExplicitContainmentBox =
                    physicalRouteRequest.UseExplicitContainmentBox,
                ContainmentMinimum = physicalRouteRequest.ContainmentMinimum,
                ContainmentMaximum = physicalRouteRequest.ContainmentMaximum,
                SearchOptions = virtualBridgeSearchOptions
            };
        }

        private static LinkageRouteCandidate BuildMergedDoubleStartBridgeCandidate(
            LinkageMultiGroupRouteRequest physicalRouteRequest,
            LinkageRouteGroupRequest physicalRouteGroupRequest,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            PlanarBendBusbarSetting bridgeBaseSetting,
            IReadOnlyList<PlanarBendBusbarSetting> actualBridgeSettings,
            LinkageRouteStackTerminalPlacement mappedBridgeStartPlacement,
            LinkageRouteCandidate downstreamCandidate,
            int bridgeSearchPriority,
            CancellationToken cancellationToken)
        {
            if (downstreamCandidate?.PlanarConversion?.IsValid != true ||
                !TryResolveDoubleStartBridgeMemberMatching(
                    downstreamCandidate.PlanarConversion.StartStackPlacement,
                    actualBridgeSettings.Count,
                    downstreamCandidate.PlanarConversion.StackMembers.Count,
                    out var bridgeMemberIndexByDownstreamMemberIndex))
            {
                return null;
            }

            var downstreamMemberIndexByBridgeMemberIndex =
                new int[actualBridgeSettings.Count];
            for (var downstreamMemberIndex = 0;
                downstreamMemberIndex <
                    bridgeMemberIndexByDownstreamMemberIndex.Length;
                downstreamMemberIndex++)
            {
                downstreamMemberIndexByBridgeMemberIndex[
                    bridgeMemberIndexByDownstreamMemberIndex[
                        downstreamMemberIndex]] = downstreamMemberIndex;
            }

            var adjustedBridgeBaseSetting = bridgeBaseSetting.Copy();
            var adjustedActualBridgeSettings = actualBridgeSettings
                .Select(actualBridgeSetting => actualBridgeSetting.Copy())
                .ToList();
            var actualBentBridgeTerminalTrimDistances = new List<double>();
            for (var bridgeMemberIndex = 0;
                bridgeMemberIndex < adjustedActualBridgeSettings.Count;
                bridgeMemberIndex++)
            {
                var downstreamMemberIndex =
                    downstreamMemberIndexByBridgeMemberIndex[bridgeMemberIndex];
                var downstreamFirstPlanarPart = downstreamCandidate
                    .PlanarConversion.StackMembers[downstreamMemberIndex]
                    .PlanarParts.FirstOrDefault();
                if (!TryTrimBentBridgeTerminalToPerpendicularLapFootprint(
                        adjustedActualBridgeSettings[bridgeMemberIndex],
                        downstreamFirstPlanarPart,
                        physicalRouteGroupRequest.GetBusbarWidth(
                            physicalRouteRequest),
                        out var actualMemberUsesPerpendicularLap,
                        out var actualMemberTerminalTrimDistance))
                {
                    return null;
                }

                if (actualMemberUsesPerpendicularLap &&
                    actualMemberTerminalTrimDistance > GeometryTolerance)
                {
                    actualBentBridgeTerminalTrimDistances.Add(
                        actualMemberTerminalTrimDistance);
                }
            }

            var downstreamFirstCenterlinePart = downstreamCandidate
                .PlanarConversion.CenterlineParts.FirstOrDefault();
            var baseBridgeLapResolved =
                TryTrimBentBridgeTerminalToPerpendicularLapFootprint(
                    adjustedBridgeBaseSetting,
                    downstreamFirstCenterlinePart,
                    physicalRouteGroupRequest.GetBusbarWidth(
                        physicalRouteRequest),
                    out var baseBridgeUsesPerpendicularLap,
                    out _);
            if ((!baseBridgeLapResolved ||
                    !baseBridgeUsesPerpendicularLap) &&
                actualBentBridgeTerminalTrimDistances.Count > 0)
            {
                TrimBentBridgeTerminalByDistance(
                    adjustedBridgeBaseSetting,
                    actualBentBridgeTerminalTrimDistances.Average());
            }

            var mergedPlanarConversion =
                new LinkageRouteCenterlineConversionResult();
            var mergedBridgeCenterlinePart = adjustedBridgeBaseSetting.Copy();
            {
                mergedBridgeCenterlinePart.PhysicalSectionIndex = 0;
                mergedBridgeCenterlinePart.SpecificationSectionIndex = 0;
            }
            mergedPlanarConversion.CenterlineParts.Add(
                mergedBridgeCenterlinePart);
            // 双始端 I/L 桥不是附件，而是合并结果的第一段实际铜排。
            // 显式保留其分段语义，后处理才能使用独立的连接排规格，避免
            // 因为它恰好位于首段而被误当成普通始端搭接排。
            if (bridgeBaseSetting.PointList.Count < 3)
            {
                mergedPlanarConversion.DoubleStartPhysicalBridgeSectionIndices.Add(0);
            }
            var downstreamPartPosition = 0;
            foreach (var downstreamCenterlinePart in downstreamCandidate
                .PlanarConversion.CenterlineParts)
            {
                var mergedDownstreamPart = downstreamCenterlinePart.Copy();
                {
                    mergedDownstreamPart.PhysicalSectionIndex =
                        downstreamPartPosition + 1;
                    mergedDownstreamPart.SpecificationSectionIndex =
                        downstreamPartPosition + 1;
                }
                mergedPlanarConversion.CenterlineParts.Add(mergedDownstreamPart);
                downstreamPartPosition++;
            }

            for (var bridgeMemberIndex = 0;
                bridgeMemberIndex < actualBridgeSettings.Count;
                bridgeMemberIndex++)
            {
                var downstreamMemberIndex =
                    downstreamMemberIndexByBridgeMemberIndex[bridgeMemberIndex];
                var downstreamStackMember = downstreamCandidate.PlanarConversion
                    .StackMembers[downstreamMemberIndex];
                var mergedStackMember = new LinkageStackedRouteMember
                {
                    MemberIndex = bridgeMemberIndex,
                    CenterlineOffset = mappedBridgeStartPlacement
                        .GeneratedMemberOffsets[bridgeMemberIndex]
                };
                var mergedBridgeMemberPart =
                    adjustedActualBridgeSettings[bridgeMemberIndex].Copy();
                {
                    mergedBridgeMemberPart.PhysicalSectionIndex = 0;
                    mergedBridgeMemberPart.SpecificationSectionIndex = 0;
                }
                mergedStackMember.PlanarParts.Add(mergedBridgeMemberPart);
                var downstreamMemberPartPosition = 0;
                foreach (var downstreamMemberPart in downstreamStackMember.PlanarParts)
                {
                    var mergedDownstreamMemberPart = downstreamMemberPart.Copy();
                    {
                        mergedDownstreamMemberPart.PhysicalSectionIndex =
                            downstreamMemberPartPosition + 1;
                        mergedDownstreamMemberPart.SpecificationSectionIndex =
                            downstreamMemberPartPosition + 1;
                    }
                    mergedStackMember.PlanarParts.Add(mergedDownstreamMemberPart);
                    downstreamMemberPartPosition++;
                }

                mergedPlanarConversion.StackMembers.Add(mergedStackMember);
                // 合并后的首段连接排和原下游成员都属于始端叠排，必须保留
                // 始端成员索引，输出阶段才能从第二段开始做连接排外侧的
                // 厚向逐层排列；否则后处理会误以为没有可联动成员。
                mergedPlanarConversion.StartTerminalMemberIndices.Add(
                    bridgeMemberIndex);
                if (downstreamCandidate.PlanarConversion
                        .EndTerminalMemberIndices.Contains(downstreamMemberIndex))
                {
                    mergedPlanarConversion.EndTerminalMemberIndices.Add(
                        bridgeMemberIndex);
                }
            }

            mergedPlanarConversion.StartStackPlacement =
                CopyDoubleStartBridgePlacement(mappedBridgeStartPlacement);
            mergedPlanarConversion.EndStackPlacement =
                RemapDoubleStartBridgeEndPlacement(
                    downstreamCandidate.PlanarConversion.EndStackPlacement,
                    bridgeMemberIndexByDownstreamMemberIndex);
            if (mergedPlanarConversion.EndStackPlacement == null)
            {
                return null;
            }

            mergedPlanarConversion.ActualCopperLength = mergedPlanarConversion
                .StackMembers
                .SelectMany(stackMember => stackMember.PlanarParts)
                .Sum(planarPart => planarPart.GetDevelopedLength());
            mergedPlanarConversion.IsValid =
                mergedPlanarConversion.CenterlineParts.Count >= 2 &&
                mergedPlanarConversion.StackMembers.Count ==
                    actualBridgeSettings.Count;
            var mergedAxisSkeleton = CreateDoubleStartBridgeDirectionTrace(
                adjustedBridgeBaseSetting.PointList,
                downstreamCandidate.AxisSkeleton);
            var mergedManufacturableCenterline =
                CreateDoubleStartBridgeDirectionTrace(
                    adjustedBridgeBaseSetting.PointList,
                    downstreamCandidate.ManufacturableCenterline);
            var bridgeTopologyName = adjustedBridgeBaseSetting.PointList.Count >= 3
                ? "DOUBLE-START-L-BRIDGE"
                : "DOUBLE-START-I-BRIDGE";
            var bridgeIdentitySignature = string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3}",
                bridgeTopologyName,
                mappedBridgeStartPlacement.Signature ?? string.Empty,
                GetCenterlineSignature(adjustedBridgeBaseSetting.PointList),
                GetCandidateSignature(downstreamCandidate));
            return BuildPreparedCandidate(
                physicalRouteRequest,
                physicalRouteGroupRequest,
                mergedAxisSkeleton,
                mergedManufacturableCenterline,
                mergedPlanarConversion,
                fixedTerminalAndObstacleSolids,
                bridgeTopologyName + ">" +
                    downstreamCandidate.TopologySignature,
                bridgeIdentitySignature,
                bridgeSearchPriority,
                cancellationToken);
        }

        private static bool
            TryTrimBentBridgeTerminalToPerpendicularLapFootprint(
            PlanarBendBusbarSetting bentBridgeSetting,
            PlanarBendBusbarSetting downstreamFirstPlanarPart,
            double requiredBusbarWidth,
            out bool usesPerpendicularLap,
            out double terminalTrimDistance)
        {
            usesPerpendicularLap = false;
            terminalTrimDistance = 0d;
            if (bentBridgeSetting?.PointList == null ||
                bentBridgeSetting.PointList.Count < 2 ||
                downstreamFirstPlanarPart?.PointList == null ||
                downstreamFirstPlanarPart.PointList.Count < 2)
            {
                return false;
            }

            var bentBridgeTerminalSegment = CreateBoundarySegment(
                bentBridgeSetting,
                false);
            var downstreamStartSegment = CreateBoundarySegment(
                downstreamFirstPlanarPart,
                true);
            usesPerpendicularLap = AreSegmentsPerpendicular(
                bentBridgeTerminalSegment,
                downstreamStartSegment);
            if (!usesPerpendicularLap ||
                bentBridgeSetting.PointList.Count < 3)
            {
                return true;
            }

            if (!LinkageRouteTerminalLapRegionValidator
                    .TryCreateActualLapFootprint(
                        bentBridgeSetting,
                        false,
                        downstreamFirstPlanarPart,
                        true,
                        requiredBusbarWidth,
                        out var actualPerpendicularLapFootprint))
            {
                return false;
            }

            var bentBridgeTerminalPointIndex =
                bentBridgeSetting.PointList.Count - 1;
            var bentBridgeTerminalSegmentStart = bentBridgeSetting.PointList[
                bentBridgeTerminalPointIndex - 1];
            var bentBridgeTerminalSegmentEnd = bentBridgeSetting.PointList[
                bentBridgeTerminalPointIndex];
            var bentBridgeTerminalDirection = new Vector3d(
                bentBridgeTerminalSegmentStart,
                bentBridgeTerminalSegmentEnd);
            if (bentBridgeTerminalDirection.Norm <= GeometryTolerance)
            {
                return false;
            }

            bentBridgeTerminalDirection =
                bentBridgeTerminalDirection.Normalized;
            var originalBentBridgeTerminalSegmentLength =
                bentBridgeTerminalSegmentStart.DistanceTo(
                    bentBridgeTerminalSegmentEnd);
            var actualLapOuterBoundaryCoordinate =
                actualPerpendicularLapFootprint.Corners
                    .Select(actualLapCorner => new Vector3d(
                            bentBridgeTerminalSegmentStart,
                            actualLapCorner)
                        .Dot(bentBridgeTerminalDirection))
                    .Max();
            if (actualLapOuterBoundaryCoordinate <= GeometryTolerance ||
                actualLapOuterBoundaryCoordinate >
                    originalBentBridgeTerminalSegmentLength +
                    GeometryTolerance)
            {
                return false;
            }

            terminalTrimDistance = Math.Max(
                0d,
                originalBentBridgeTerminalSegmentLength -
                    actualLapOuterBoundaryCoordinate);
            if (terminalTrimDistance <= GeometryTolerance)
            {
                terminalTrimDistance = 0d;
                return true;
            }

            bentBridgeSetting.PointList[bentBridgeTerminalPointIndex] =
                TranslateDoubleStartBridgePoint(
                    bentBridgeTerminalSegmentStart,
                    bentBridgeTerminalDirection,
                    actualLapOuterBoundaryCoordinate);
            return true;
        }

        private static void TrimBentBridgeTerminalByDistance(
            PlanarBendBusbarSetting bentBridgeSetting,
            double terminalTrimDistance)
        {
            if (bentBridgeSetting?.PointList == null ||
                bentBridgeSetting.PointList.Count < 3 ||
                terminalTrimDistance <= GeometryTolerance)
            {
                return;
            }

            var bentBridgeTerminalPointIndex =
                bentBridgeSetting.PointList.Count - 1;
            var bentBridgeTerminalSegmentStart = bentBridgeSetting.PointList[
                bentBridgeTerminalPointIndex - 1];
            var bentBridgeTerminalSegmentEnd = bentBridgeSetting.PointList[
                bentBridgeTerminalPointIndex];
            var bentBridgeTerminalDirection = new Vector3d(
                bentBridgeTerminalSegmentStart,
                bentBridgeTerminalSegmentEnd);
            if (bentBridgeTerminalDirection.Norm <=
                terminalTrimDistance + GeometryTolerance)
            {
                return;
            }

            bentBridgeSetting.PointList[bentBridgeTerminalPointIndex] =
                TranslateDoubleStartBridgePoint(
                    bentBridgeTerminalSegmentStart,
                    bentBridgeTerminalDirection.Normalized,
                    bentBridgeTerminalDirection.Norm -
                        terminalTrimDistance);
        }

        private static bool TryResolveDoubleStartBridgeMemberMatching(
            LinkageRouteStackTerminalPlacement downstreamStartPlacement,
            int bridgeMemberCount,
            int downstreamMemberCount,
            out int[] bridgeMemberIndexByDownstreamMemberIndex)
        {
            bridgeMemberIndexByDownstreamMemberIndex = null;
            if (downstreamStartPlacement?.Contacts == null ||
                bridgeMemberCount <= 0 ||
                downstreamMemberCount != bridgeMemberCount)
            {
                return false;
            }

            var bridgeOptionsByDownstreamMemberIndex = Enumerable
                .Range(0, downstreamMemberCount)
                .ToDictionary(
                    downstreamMemberIndex => downstreamMemberIndex,
                    downstreamMemberIndex => downstreamStartPlacement.Contacts
                        .Where(terminalContact =>
                            terminalContact.GeneratedMemberIndex ==
                                downstreamMemberIndex &&
                            terminalContact.FixedMemberIndex >= 0 &&
                            terminalContact.FixedMemberIndex < bridgeMemberCount)
                        .Select(terminalContact =>
                            terminalContact.FixedMemberIndex)
                        .Distinct()
                        .OrderBy(bridgeMemberIndex => bridgeMemberIndex)
                        .ToList());
            if (bridgeOptionsByDownstreamMemberIndex.Any(
                    memberOptions => memberOptions.Value.Count == 0))
            {
                return false;
            }

            var orderedDownstreamMemberIndices = bridgeOptionsByDownstreamMemberIndex
                .OrderBy(memberOptions => memberOptions.Value.Count)
                .ThenBy(memberOptions => memberOptions.Key)
                .Select(memberOptions => memberOptions.Key)
                .ToList();
            var resolvedBridgeMemberIndices = Enumerable
                .Repeat(-1, downstreamMemberCount)
                .ToArray();
            var usedBridgeMemberIndices = new bool[bridgeMemberCount];
            if (!TryAssignDoubleStartBridgeMember(
                    0,
                    orderedDownstreamMemberIndices,
                    bridgeOptionsByDownstreamMemberIndex,
                    resolvedBridgeMemberIndices,
                    usedBridgeMemberIndices))
            {
                return false;
            }

            bridgeMemberIndexByDownstreamMemberIndex =
                resolvedBridgeMemberIndices;
            return true;
        }

        private static bool TryAssignDoubleStartBridgeMember(
            int assignmentIndex,
            IReadOnlyList<int> orderedDownstreamMemberIndices,
            IReadOnlyDictionary<int, List<int>>
                bridgeOptionsByDownstreamMemberIndex,
            int[] bridgeMemberIndexByDownstreamMemberIndex,
            bool[] usedBridgeMemberIndices)
        {
            if (assignmentIndex >= orderedDownstreamMemberIndices.Count)
            {
                return true;
            }

            var downstreamMemberIndex =
                orderedDownstreamMemberIndices[assignmentIndex];
            foreach (var bridgeMemberIndex in
                bridgeOptionsByDownstreamMemberIndex[downstreamMemberIndex])
            {
                if (usedBridgeMemberIndices[bridgeMemberIndex])
                {
                    continue;
                }

                usedBridgeMemberIndices[bridgeMemberIndex] = true;
                bridgeMemberIndexByDownstreamMemberIndex[
                    downstreamMemberIndex] = bridgeMemberIndex;
                if (TryAssignDoubleStartBridgeMember(
                        assignmentIndex + 1,
                        orderedDownstreamMemberIndices,
                        bridgeOptionsByDownstreamMemberIndex,
                        bridgeMemberIndexByDownstreamMemberIndex,
                        usedBridgeMemberIndices))
                {
                    return true;
                }

                bridgeMemberIndexByDownstreamMemberIndex[
                    downstreamMemberIndex] = -1;
                usedBridgeMemberIndices[bridgeMemberIndex] = false;
            }

            return false;
        }

        private static LinkageRouteStackTerminalPlacement
            RemapDoubleStartBridgeEndPlacement(
            LinkageRouteStackTerminalPlacement downstreamEndPlacement,
            IReadOnlyList<int> bridgeMemberIndexByDownstreamMemberIndex)
        {
            if (downstreamEndPlacement?.GeneratedMemberOffsets == null ||
                bridgeMemberIndexByDownstreamMemberIndex == null ||
                downstreamEndPlacement.GeneratedMemberOffsets.Count !=
                    bridgeMemberIndexByDownstreamMemberIndex.Count ||
                (downstreamEndPlacement
                        .SpacerlessMovedGeneratedMemberIndices ??
                        new List<int>())
                    .Any(generatedMemberIndex => generatedMemberIndex < 0 ||
                        generatedMemberIndex >=
                            downstreamEndPlacement.GeneratedMemberOffsets.Count))
            {
                return null;
            }

            var remappedEndMemberOffsets = new double[
                bridgeMemberIndexByDownstreamMemberIndex.Count];
            for (var downstreamMemberIndex = 0;
                downstreamMemberIndex <
                    bridgeMemberIndexByDownstreamMemberIndex.Count;
                downstreamMemberIndex++)
            {
                remappedEndMemberOffsets[
                    bridgeMemberIndexByDownstreamMemberIndex[
                        downstreamMemberIndex]] = downstreamEndPlacement
                            .GeneratedMemberOffsets[downstreamMemberIndex];
            }

            return new LinkageRouteStackTerminalPlacement
            {
                Signature = downstreamEndPlacement.Signature,
                BaseCenterOffset = downstreamEndPlacement.BaseCenterOffset,
                GeneratedMemberOffsets = remappedEndMemberOffsets,
                SpacerlessMovedGeneratedMemberIndices =
                    (downstreamEndPlacement
                            .SpacerlessMovedGeneratedMemberIndices ??
                            new List<int>())
                    .Select(downstreamGeneratedMemberIndex =>
                        bridgeMemberIndexByDownstreamMemberIndex[
                            downstreamGeneratedMemberIndex])
                    .OrderBy(bridgeGeneratedMemberIndex =>
                        bridgeGeneratedMemberIndex)
                    .ToList(),
                DoubleStartHorizontalBridgeGeneratedMemberIndex =
                    downstreamEndPlacement
                        .DoubleStartHorizontalBridgeGeneratedMemberIndex.HasValue
                        ? (int?)bridgeMemberIndexByDownstreamMemberIndex[
                            downstreamEndPlacement
                                .DoubleStartHorizontalBridgeGeneratedMemberIndex.Value]
                        : null,
                DoubleStartHorizontalBridgeSideSign =
                    downstreamEndPlacement
                        .DoubleStartHorizontalBridgeSideSign,
                DoubleStartHorizontalBridgeThickness =
                    downstreamEndPlacement
                        .DoubleStartHorizontalBridgeThickness,
                DoubleStartHorizontalBridgePlacements =
                    (downstreamEndPlacement.DoubleStartHorizontalBridgePlacements ??
                        new List<LinkageRouteTerminalBridgePlacement>())
                    .Select(bridgePlacement => new LinkageRouteTerminalBridgePlacement
                    {
                        GeneratedMemberIndex = bridgePlacement.GeneratedMemberIndex,
                        SideSign = bridgePlacement.SideSign,
                        Thickness = bridgePlacement.Thickness
                    }).ToList(),
                Contacts = (downstreamEndPlacement.Contacts ??
                        new List<LinkageRouteStackTerminalContact>())
                    .Select(terminalContact =>
                        new LinkageRouteStackTerminalContact
                        {
                            GeneratedMemberIndex =
                                bridgeMemberIndexByDownstreamMemberIndex[
                                    terminalContact.GeneratedMemberIndex],
                            FixedMemberIndex = terminalContact.FixedMemberIndex
                        })
                    .ToList()
            };
        }

        private static LinkageRouteStackTerminalPlacement
            CopyDoubleStartBridgePlacement(
            LinkageRouteStackTerminalPlacement sourcePlacement)
        {
            return sourcePlacement == null
                ? null
                : new LinkageRouteStackTerminalPlacement
                {
                    Signature = sourcePlacement.Signature,
                    BaseCenterOffset = sourcePlacement.BaseCenterOffset,
                    GeneratedMemberOffsets = sourcePlacement
                        .GeneratedMemberOffsets.ToList(),
                    SpacerlessMovedGeneratedMemberIndices = (sourcePlacement
                            .SpacerlessMovedGeneratedMemberIndices ??
                            new List<int>())
                        .ToList(),
                    // I/L 桥已经合并为方案的第一个真实路径分段，不能继续保留
                    // “横向桥附件”标记，否则附件生成阶段会在同一位置再创建一根
                    // 连接排，造成实体重叠并把本来有效的候选错误淘汰。
                    DoubleStartHorizontalBridgeGeneratedMemberIndex = null,
                    DoubleStartHorizontalBridgeSideSign = 0,
                    DoubleStartHorizontalBridgeThickness = 0d,
                    Contacts = (sourcePlacement.Contacts ??
                            new List<LinkageRouteStackTerminalContact>())
                        .Select(terminalContact =>
                            new LinkageRouteStackTerminalContact
                            {
                                GeneratedMemberIndex =
                                    terminalContact.GeneratedMemberIndex,
                                FixedMemberIndex = terminalContact.FixedMemberIndex
                            })
                        .ToList()
                };
        }

        private static IList<Point3d> CreateDoubleStartBridgeDirectionTrace(
            IEnumerable<Point3d> bridgeCenterline,
            IEnumerable<Point3d> downstreamCenterline)
        {
            var bridgeDirectionTrace = (bridgeCenterline ??
                    Enumerable.Empty<Point3d>())
                .Where(point => point != null)
                .Select(CopyPoint)
                .ToList();
            var downstreamCenterlinePoints = (downstreamCenterline ??
                    Enumerable.Empty<Point3d>())
                .Where(point => point != null)
                .ToList();
            if (bridgeDirectionTrace.Count < 2 ||
                downstreamCenterlinePoints.Count < 2)
            {
                return bridgeDirectionTrace;
            }

            var currentTracePoint = bridgeDirectionTrace[
                bridgeDirectionTrace.Count - 1];
            for (var downstreamPointIndex = 0;
                downstreamPointIndex < downstreamCenterlinePoints.Count - 1;
                downstreamPointIndex++)
            {
                var downstreamSegmentVector = new Vector3d(
                    downstreamCenterlinePoints[downstreamPointIndex],
                    downstreamCenterlinePoints[downstreamPointIndex + 1]);
                if (downstreamSegmentVector.Norm <= GeometryTolerance)
                {
                    continue;
                }

                currentTracePoint = new Point3d(
                    currentTracePoint.X + downstreamSegmentVector.X,
                    currentTracePoint.Y + downstreamSegmentVector.Y,
                    currentTracePoint.Z + downstreamSegmentVector.Z);
                bridgeDirectionTrace.Add(CopyPoint(currentTracePoint));
            }

            return bridgeDirectionTrace;
        }

        private static Point3d TranslateDoubleStartBridgePoint(
            Point3d sourcePoint,
            Vector3d direction,
            double distance)
        {
            return GeometRiMethod.TranslateAlongDirection(
                sourcePoint,
                direction,
                distance);
        }

        internal static LinkageRouteCandidatePool BuildPool(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            bool enableContinuousConversion,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken,
            Action<LinkageRouteCandidatePool> completedCandidate = null)
        {
            var candidatePool = new LinkageRouteCandidatePool { GroupRequest = routeGroupRequest };
            candidatePool.TraversalDirections.Add(traversalDirection);
            var configuredMaximumCandidateCount = Math.Max(
                1, routeRequest.SearchOptions.MaximumCandidatesPerGroup);
            var validCandidateCollectionTarget = configuredMaximumCandidateCount;
            var evaluatedCenterlineCandidates =
                new Dictionary<string, LinkageRouteCandidate>(StringComparer.Ordinal);
            foreach (var axisSkeleton in LinkageRouteAxisPathBuilder.BuildCandidates(
                routeRequest,
                routeGroupRequest,
                traversalDirection,
                cancellationToken))
            {
                candidatePool.EvaluatedSkeletonCount++;
                if (cancellationToken.IsCancellationRequested)
                {
                    return candidatePool;
                }
                BuildAndAddUniqueCandidate(
                    candidatePool,
                    evaluatedCenterlineCandidates,
                    routeRequest,
                    routeGroupRequest,
                    axisSkeleton,
                    axisSkeleton,
                    fixedTerminalAndObstacleSolids,
                    false,
                    "XYZ",
                    traversalDirection,
                    cancellationToken);
                completedCandidate?.Invoke(candidatePool);

                if (axisSkeleton.Count < 3)
                {
                    continue;
                }

                var conversionContext = CreateConversionContext(
                    routeRequest,
                    routeGroupRequest,
                    axisSkeleton,
                    fixedTerminalAndObstacleSolids);
                foreach (var conversionCandidate in LinkageRouteConversionCandidate
                    .CreateContinuousCandidates(axisSkeleton, conversionContext)
                    .Where(candidate => candidate.IsConverted))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return candidatePool;
                    }

                    var convertedRouteCandidate = BuildAndAddUniqueCandidate(
                        candidatePool,
                        evaluatedCenterlineCandidates,
                        routeRequest,
                        routeGroupRequest,
                        axisSkeleton,
                        conversionCandidate.Centerline,
                        fixedTerminalAndObstacleSolids,
                        true,
                        conversionCandidate.TopologySignature,
                        traversalDirection,
                        cancellationToken);
                    completedCandidate?.Invoke(candidatePool);
                    EvaluateReversedInternalLapClearanceSibling(
                        candidatePool,
                        evaluatedCenterlineCandidates,
                        routeRequest,
                        routeGroupRequest,
                        axisSkeleton,
                        conversionCandidate,
                        convertedRouteCandidate,
                        fixedTerminalAndObstacleSolids,
                        "INTERNAL-LAP-",
                        traversalDirection,
                        cancellationToken);
                    completedCandidate?.Invoke(candidatePool);
                    EvaluateConvertedTerminalReserveSibling(
                        candidatePool,
                        evaluatedCenterlineCandidates,
                        routeRequest,
                        routeGroupRequest,
                        axisSkeleton,
                        convertedRouteCandidate,
                        fixedTerminalAndObstacleSolids,
                        "TERMINAL-",
                        traversalDirection,
                        cancellationToken);
                    completedCandidate?.Invoke(candidatePool);
                }

            }
            var hasStaticClearanceFailure =
                candidatePool.FailureStageCounts.ContainsKey(
                    LinkageRouteFailureStage.Clearance);
            // A valid single-group route can still conflict with every route
            // of another group. Keep the existing bounded extension stage
            // available when ordinary seeds exist; do not discard those seeds.
            var shouldSearchExtendedCandidates = enableContinuousConversion &&
                !hasStaticClearanceFailure;
            if (shouldSearchExtendedCandidates)
            {
                var evaluatedExtendedSkeletonCount = 0;
                var extendedSkeletonEvaluationTarget = Math.Max(
                    128,
                    validCandidateCollectionTarget * 2);
                foreach (var extendedAxisSkeleton in LinkageRouteAxisPathBuilder.BuildExtendedCandidates(
                    routeRequest,
                    routeGroupRequest,
                    traversalDirection,
                    cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return candidatePool;
                    }

                    evaluatedExtendedSkeletonCount++;
                    candidatePool.EvaluatedSkeletonCount++;
                    var extendedBaseCandidate = BuildAndAddUniqueCandidate(
                        candidatePool,
                        evaluatedCenterlineCandidates,
                        routeRequest,
                        routeGroupRequest,
                        extendedAxisSkeleton,
                        extendedAxisSkeleton,
                        fixedTerminalAndObstacleSolids,
                        false,
                        "XYZ-EXT",
                        traversalDirection,
                        cancellationToken);
                    completedCandidate?.Invoke(candidatePool);

                    var extendedAxisSkeletonVariants = new List<IList<Point3d>>
                    {
                        extendedAxisSkeleton
                    };
                    var terminalReserveCriticalAxisSkeleton =
                        CreateTerminalReserveCriticalAxisSkeleton(
                            routeRequest,
                            routeGroupRequest,
                            extendedAxisSkeleton,
                            extendedBaseCandidate?.PlanarConversion);
                    if (terminalReserveCriticalAxisSkeleton != null)
                    {
                        extendedAxisSkeletonVariants.Add(terminalReserveCriticalAxisSkeleton);
                        BuildAndAddUniqueCandidate(
                            candidatePool,
                            evaluatedCenterlineCandidates,
                            routeRequest,
                            routeGroupRequest,
                            terminalReserveCriticalAxisSkeleton,
                            terminalReserveCriticalAxisSkeleton,
                            fixedTerminalAndObstacleSolids,
                            false,
                            "XYZ-EXT",
                            traversalDirection,
                            cancellationToken);
                        completedCandidate?.Invoke(candidatePool);
                    }

                    foreach (var extendedAxisSkeletonVariant in extendedAxisSkeletonVariants
                        .Where(skeletonVariant => skeletonVariant.Count >= 3))
                    {
                        var extendedConversionContext = CreateConversionContext(
                            routeRequest,
                            routeGroupRequest,
                            extendedAxisSkeletonVariant,
                            fixedTerminalAndObstacleSolids);
                        foreach (var extendedConversionCandidate in
                            LinkageRouteConversionCandidate
                                .CreateContinuousCandidates(
                                    extendedAxisSkeletonVariant,
                                    extendedConversionContext)
                                .Where(conversionCandidate => conversionCandidate.IsConverted))
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                return candidatePool;
                            }

                            var extendedConvertedRouteCandidate =
                                BuildAndAddUniqueCandidate(
                                candidatePool,
                                evaluatedCenterlineCandidates,
                                routeRequest,
                                routeGroupRequest,
                                extendedAxisSkeletonVariant,
                                extendedConversionCandidate.Centerline,
                                fixedTerminalAndObstacleSolids,
                                true,
                                "EXT-" + extendedConversionCandidate.TopologySignature,
                                traversalDirection,
                                cancellationToken);
                                completedCandidate?.Invoke(candidatePool);
                            EvaluateReversedInternalLapClearanceSibling(
                                candidatePool,
                                evaluatedCenterlineCandidates,
                                routeRequest,
                                routeGroupRequest,
                                extendedAxisSkeletonVariant,
                                extendedConversionCandidate,
                                extendedConvertedRouteCandidate,
                                fixedTerminalAndObstacleSolids,
                                "EXT-INTERNAL-LAP-",
                                traversalDirection,
                                cancellationToken);
                            completedCandidate?.Invoke(candidatePool);
                            EvaluateConvertedTerminalReserveSibling(
                                candidatePool,
                                evaluatedCenterlineCandidates,
                                routeRequest,
                                routeGroupRequest,
                                extendedAxisSkeletonVariant,
                                extendedConvertedRouteCandidate,
                                fixedTerminalAndObstacleSolids,
                                "EXT-TERMINAL-",
                                traversalDirection,
                                cancellationToken);
                            completedCandidate?.Invoke(candidatePool);
                        }
                    }

                    if (candidatePool.ValidCandidates.Count >= validCandidateCollectionTarget &&
                        evaluatedExtendedSkeletonCount >= extendedSkeletonEvaluationTarget)
                    {
                        break;
                    }
                }
            }

            if (enableContinuousConversion && hasStaticClearanceFailure)
            {
                var knownClearanceCandidateSignatures = new HashSet<string>(
                    candidatePool.ValidCandidates.Select(GetCandidateSignature),
                    StringComparer.Ordinal);
                foreach (var clearanceDrivenCandidate in BuildClearanceDrivenCandidates(
                    routeRequest,
                    routeGroupRequest,
                    fixedTerminalAndObstacleSolids,
                    Enumerable.Empty<LinkageRouteCandidate>(),
                    traversalDirection,
                    cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return candidatePool;
                    }

                    if (knownClearanceCandidateSignatures.Add(
                        GetCandidateSignature(clearanceDrivenCandidate)))
                    {
                        candidatePool.ValidCandidates.Add(clearanceDrivenCandidate);
                        completedCandidate?.Invoke(candidatePool);
                    }
                }
            }

            // 快速池之外的完整搜索也必须保留垂直双始端的普通 L 形实体
            // 候选；它不是水平连接排附件，不能只在快速阶段短暂存在。
            if (routeGroupRequest?.StartTerminal?.Members?.Count >= 2 &&
                (AreTerminalThicknessDirectionsPerpendicular(routeGroupRequest) ||
                 LinkageRouteDoubleStartLapGeometry.TryCreateLengthSeparatedCollectiveFixedBusbar(
                     routeGroupRequest.StartTerminal, out _)))
            {
                BuildDoubleStartBridgeCandidates(
                    candidatePool,
                    routeRequest,
                    routeGroupRequest,
                    fixedTerminalAndObstacleSolids,
                    traversalDirection,
                    cancellationToken);
                completedCandidate?.Invoke(candidatePool);
            }

            FinalizeCandidatePool(
                candidatePool,
                configuredMaximumCandidateCount,
                routeRequest.AllowPartialStartPerpendicularLap,
                cancellationToken);
            return candidatePool;
        }

        private static void FinalizeCandidatePool(
            LinkageRouteCandidatePool candidatePool,
            int maximumCandidateCount,
            bool preserveStartPerpendicularLapVariants,
            CancellationToken cancellationToken)
        {
            // Cancellation stops new work; it must not erase already completed
            // candidates. The request owner remains responsible for publication.
            if (cancellationToken.IsCancellationRequested) return;
            var uniqueOrderedCandidates = candidatePool.ValidCandidates
                .Where(candidate =>
                {
                    return !cancellationToken.IsCancellationRequested;
                })
                .GroupBy(GetCandidateSignature)
                .Select(candidateGroup => candidateGroup
                    .OrderBy(candidate => candidate.PlanarConversion.ActualCopperLength)
                    .ThenBy(candidate => candidate.GeneratedBusbarPartCount)
                    .ThenBy(candidate => candidate.SearchPriority)
                .First())
                .OrderBy(candidate => candidate.PlanarConversion.ActualCopperLength)
                .ThenBy(candidate => candidate.GeneratedBusbarPartCount)
                .ThenBy(candidate => candidate.SearchPriority)
                .ToList();
            var orderedCandidates = SelectDiverseValidCandidates(
                uniqueOrderedCandidates,
                Math.Max(1, maximumCandidateCount),
                preserveStartPerpendicularLapVariants);
            if (cancellationToken.IsCancellationRequested) return;
            candidatePool.ValidCandidates.Clear();
            foreach (var orderedCandidate in orderedCandidates)
            {
                candidatePool.ValidCandidates.Add(orderedCandidate);
            }
            if (candidatePool.ValidCandidates.Count == 0)
            {
                candidatePool.Diagnostics.Add(candidatePool.BestFailedCandidate?.Diagnostic ??
                    "逻辑组没有通过路径、搭接、绝缘和转换校验的候选。" );
            }
        }

        internal static LinkageRouteCandidatePool MergeTraversalPools(
            LinkageMultiGroupRouteRequest routeRequest,
            IEnumerable<LinkageRouteCandidatePool> traversalCandidatePools,
            bool preserveCompletedCandidates = false)
        {
            var sourceCandidatePools = (traversalCandidatePools ??
                    Enumerable.Empty<LinkageRouteCandidatePool>())
                .Where(candidatePool => candidatePool != null)
                .ToList();
            var mergedCandidatePool = new LinkageRouteCandidatePool
            {
                GroupRequest = sourceCandidatePools
                    .Select(candidatePool => candidatePool.GroupRequest)
                    .FirstOrDefault(groupRequest => groupRequest != null)
            };
            foreach (var sourceCandidatePool in sourceCandidatePools)
            {
                foreach (var traversalDirection in
                    sourceCandidatePool.TraversalDirections)
                {
                    mergedCandidatePool.TraversalDirections.Add(traversalDirection);
                }

                foreach (var diagnosticMessage in sourceCandidatePool.Diagnostics)
                {
                    mergedCandidatePool.Diagnostics.Add(diagnosticMessage);
                }

                foreach (var failureStageCount in sourceCandidatePool.FailureStageCounts)
                {
                    mergedCandidatePool.FailureStageCounts.TryGetValue(
                        failureStageCount.Key,
                        out var existingFailureCount);
                    mergedCandidatePool.FailureStageCounts[failureStageCount.Key] =
                        existingFailureCount + failureStageCount.Value;
                }

                foreach (var representativeFailurePair in
                    sourceCandidatePool.RepresentativeFailedCandidates)
                {
                    if (!mergedCandidatePool.RepresentativeFailedCandidates.ContainsKey(
                            representativeFailurePair.Key))
                    {
                        mergedCandidatePool.RepresentativeFailedCandidates[
                            representativeFailurePair.Key] =
                            representativeFailurePair.Value;
                    }
                }

                foreach (var failureDiagnosticSamplePair in
                    sourceCandidatePool.FailureDiagnosticSamples)
                {
                    if (!mergedCandidatePool.FailureDiagnosticSamples.TryGetValue(
                            failureDiagnosticSamplePair.Key,
                            out var mergedFailureDiagnosticSamples))
                    {
                        mergedFailureDiagnosticSamples = new List<string>();
                        mergedCandidatePool.FailureDiagnosticSamples[
                            failureDiagnosticSamplePair.Key] =
                            mergedFailureDiagnosticSamples;
                    }

                    foreach (var failureDiagnosticSample in
                        failureDiagnosticSamplePair.Value)
                    {
                        if (mergedFailureDiagnosticSamples.Count >= 8)
                        {
                            break;
                        }

                        if (!mergedFailureDiagnosticSamples.Contains(
                                failureDiagnosticSample))
                        {
                            mergedFailureDiagnosticSamples.Add(
                                failureDiagnosticSample);
                        }
                    }
                }

                if (sourceCandidatePool.BestFailedCandidate != null &&
                    IsBetterFailureCandidate(
                        sourceCandidatePool.BestFailedCandidate,
                        mergedCandidatePool.BestFailedCandidate))
                {
                    mergedCandidatePool.BestFailedCandidate =
                        sourceCandidatePool.BestFailedCandidate;
                }
            }

            var maximumCandidatesPerTraversalDirection = Math.Max(
                1,
                routeRequest.SearchOptions.MaximumCandidatesPerGroup);
            var selectedTraversalCandidates = sourceCandidatePools
                .SelectMany(candidatePool => candidatePool.ValidCandidates)
                .Where(routeCandidate => routeCandidate?.PlanarConversion?.IsValid == true)
                .GroupBy(routeCandidate => routeCandidate.TraversalDirection)
                .SelectMany(directionCandidateGroup =>
                {
                    var distinctCompletedCandidates = directionCandidateGroup
                        .GroupBy(GetCandidateSignature)
                        .Select(signatureGroup => signatureGroup
                            .OrderBy(routeCandidate =>
                                routeCandidate.PlanarConversion.ActualCopperLength)
                            .ThenBy(routeCandidate => routeCandidate.GeneratedBusbarPartCount)
                            .First());
                    // Each finite source is already bounded. Supplementing it must
                    // not remove earlier completed candidates needed by new partners.
                    return preserveCompletedCandidates ? distinctCompletedCandidates :
                        SelectDiverseValidCandidates(distinctCompletedCandidates,
                            maximumCandidatesPerTraversalDirection,
                            routeRequest.AllowPartialStartPerpendicularLap);
                })
                .GroupBy(GetCandidateSignature)
                .Select(signatureGroup => signatureGroup
                    .OrderBy(routeCandidate =>
                        routeCandidate.PlanarConversion.ActualCopperLength)
                    .ThenBy(routeCandidate => routeCandidate.GeneratedBusbarPartCount)
                    .First())
                .OrderBy(routeCandidate =>
                    routeCandidate.PlanarConversion.ActualCopperLength)
                .ThenBy(routeCandidate => routeCandidate.GeneratedBusbarPartCount)
                .ThenBy(GetCandidateSignature, StringComparer.Ordinal)
                .ToList();
            foreach (var selectedTraversalCandidate in selectedTraversalCandidates)
            {
                mergedCandidatePool.ValidCandidates.Add(selectedTraversalCandidate);
            }

            return mergedCandidatePool;
        }

        public static IReadOnlyList<LinkageRouteSolid> BuildFixedAndObstacleSolids(
            LinkageMultiGroupRouteRequest routeRequest)
        {
            return TryBuildFixedAndObstacleSolids(
                routeRequest,
                out var staticSolids,
                out _)
                ? staticSolids
                : Array.Empty<LinkageRouteSolid>();
        }

        public static bool TryBuildFixedAndObstacleSolids(
            LinkageMultiGroupRouteRequest routeRequest,
            out IReadOnlyList<LinkageRouteSolid> fixedAndObstacleSolids,
            out string failureDiagnostic)
        {
            fixedAndObstacleSolids = Array.Empty<LinkageRouteSolid>();
            failureDiagnostic = null;
            if (routeRequest?.Groups == null)
            {
                failureDiagnostic = "静态实体构建缺少完整的多组路径请求。";
                return false;
            }

            var staticSolids = new List<LinkageRouteSolid>();
            var explicitContainmentSolid = routeRequest.UseExplicitContainmentBox
                ? LinkageRouteSolidGeometry.CreateAxisAlignedBoxSolid(
                    null, routeRequest.ContainmentMinimum, routeRequest.ContainmentMaximum)
                : null;
            foreach (var routeGroupRequest in routeRequest.Groups)
            {
                if (!TryAddTerminalSolids(
                    staticSolids,
                    routeGroupRequest.GroupKey,
                    routeGroupRequest.GetClearanceGroupKey(),
                    routeGroupRequest.StartTerminal,
                    explicitContainmentSolid,
                    "始端",
                    out failureDiagnostic) ||
                    !TryAddTerminalSolids(
                    staticSolids,
                    routeGroupRequest.GroupKey,
                    routeGroupRequest.GetClearanceGroupKey(),
                    routeGroupRequest.EndTerminal,
                    explicitContainmentSolid,
                    "柜顶",
                    out failureDiagnostic))
                {
                    return false;
                }
            }

            // 新柜顶桥在寻路前已经由固定几何唯一确定。将完整实际桥/补块也用于
            // 其它相的静态避让，不能等组合后才发现候选穿入已知连接排。
            foreach (var cabinetGroup in routeRequest.Groups)
            foreach (var cabinetTerminal in new[] { cabinetGroup.StartTerminal, cabinetGroup.EndTerminal })
            foreach (var cabinetAccessory in cabinetTerminal?.CabinetBridgeAssembly?.Accessories ??
                new List<LinkageRouteAccessory>())
            {
                var cabinetSetting = cabinetAccessory.Setting;
                var plannedCabinetSolid = LinkageRouteSolidGeometry.CreateSegmentSolid(cabinetGroup.GroupKey,
                    cabinetSetting.PointList[0], cabinetSetting.PointList[1], cabinetSetting.WidthVector,
                    cabinetSetting.StartBusbarVector, cabinetSetting.BusBarWidth, cabinetSetting.BusBarThickness,
                    clearanceGroupKey: cabinetGroup.GetClearanceGroupKey(),
                    solidRole: LinkageRouteSolidRole.ConductiveBusbar,
                    solidSourceKey: cabinetAccessory.SupportBlockKey);
                plannedCabinetSolid.IsPreparedCabinetAccessory = true;
                staticSolids.Add(plannedCabinetSolid);
            }

            if (!TryAddCollectiveTerminalForbiddenRegion(
                    staticSolids,
                    routeRequest,
                    true,
                    out failureDiagnostic) ||
                !TryAddCollectiveTerminalForbiddenRegion(
                    staticSolids,
                    routeRequest,
                    false,
                    out failureDiagnostic))
            {
                return false;
            }

            var obstacleIndex = 0;
            foreach (var routeObstacle in routeRequest.Obstacles ?? new List<LinkageRouteObstacle>())
            {
                if (routeObstacle?.Minimum == null || routeObstacle.Maximum == null)
                {
                    continue;
                }

                staticSolids.Add(LinkageRouteSolidGeometry.CreateAxisAlignedBoxSolid(
                    routeObstacle.GroupKey,
                    routeObstacle.Minimum,
                    routeObstacle.Maximum,
                    obstacleIndex++,
                    routeRequest.ResolveClearanceGroupKey(
                        routeObstacle.GroupKey),
                    LinkageRouteSolidRole.InputObstacle,
                    routeObstacle.ObstacleKey));
            }

            fixedAndObstacleSolids = staticSolids;
            return true;
        }

        private static LinkageRouteCandidate BuildCandidate(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IList<Point3d> axisSkeleton,
            IList<Point3d> manufacturableCenterline,
            IReadOnlyList<LinkageRouteSolid> staticSolids,
            bool isConvertedShape,
            string topologySignature,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            try
            {
                return BuildCandidateCore(
                    routeRequest,
                    routeGroupRequest,
                    axisSkeleton,
                    manufacturableCenterline,
                    staticSolids,
                    isConvertedShape,
                    topologySignature,
                    traversalDirection,
                    cancellationToken);
            }
            catch (ArgumentException exception)
            {
                return Fail(
                    CreateCandidateShell(
                        routeGroupRequest,
                        axisSkeleton,
                        manufacturableCenterline,
                        isConvertedShape,
                        topologySignature,
                        traversalDirection),
                    LinkageRouteFailureStage.Conversion,
                    "候选几何无效：" + exception.Message,
                    1d);
            }
            catch (InvalidOperationException exception)
            {
                return Fail(
                    CreateCandidateShell(
                        routeGroupRequest,
                        axisSkeleton,
                        manufacturableCenterline,
                        isConvertedShape,
                        topologySignature,
                        traversalDirection),
                    LinkageRouteFailureStage.Conversion,
                    "候选转换失败：" + exception.Message,
                    1d);
            }
        }

        /// <summary>
        /// 历史双始端层位只负责提出中心线。发布前必须用当前横向连接排规则
        /// 重建同一候选，使连接排、逐排实体、包围盒和静态净距重新参与终检。
        /// </summary>
        internal static LinkageRouteCandidate
            RebuildLegacyTopologyProposalWithPhysicalBridge(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteCandidate legacyTopologyProposal,
            IReadOnlyList<LinkageRouteSolid> staticSolids,
            CancellationToken cancellationToken)
        {
            if (routeRequest == null ||
                legacyTopologyProposal?.GroupRequest == null ||
                legacyTopologyProposal.AxisSkeleton == null ||
                legacyTopologyProposal.ManufacturableCenterline == null)
            {
                return null;
            }

            var physicalBridgeRequest = routeRequest
                .CreatePhysicalDoubleStartBridgeValidationRequest();
            return BuildCandidate(
                physicalBridgeRequest,
                legacyTopologyProposal.GroupRequest,
                legacyTopologyProposal.AxisSkeleton
                    .Select(CopyPoint)
                    .ToList(),
                legacyTopologyProposal.ManufacturableCenterline
                    .Select(CopyPoint)
                    .ToList(),
                staticSolids,
                legacyTopologyProposal.IsConvertedShape,
                legacyTopologyProposal.TopologySignature +
                    "|PHYSICAL-DOUBLE-START-BRIDGE",
                legacyTopologyProposal.TraversalDirection,
                cancellationToken);
        }

        /// <summary>
        /// Rebuild one scheme-library direction proposal through the same
        /// candidate pipeline used by the ordinary route search.  The library
        /// is allowed to provide only a direction/topology proposal; all
        /// physical centerline conversion, lap placement, static restrictions,
        /// stack materialization and terminal output processing must be owned by
        /// this factory before the result can be previewed.
        /// </summary>
        internal static LinkageRouteCandidate
            BuildSchemeLibraryCandidateFromCenterline(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IList<Point3d> centerline,
            CancellationToken cancellationToken)
        {
            if (routeRequest == null || routeGroupRequest == null ||
                centerline == null || centerline.Count < 2 ||
                cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            if (!TryBuildFixedAndObstacleSolids(
                    routeRequest,
                    out var fixedAndObstacleSolids,
                    out _))
            {
                return null;
            }

            var ordinaryCandidate = BuildCandidate(
                routeRequest,
                routeGroupRequest,
                centerline.Select(CopyPoint).ToList(),
                centerline.Select(CopyPoint).ToList(),
                fixedAndObstacleSolids,
                false,
                "SCHEME_LIBRARY_DERIVED",
                LinkageRouteTraversalDirection.Forward,
                cancellationToken);
            if (ordinaryCandidate?.IsValid != true)
            {
                return ordinaryCandidate;
            }

            var outputCandidate = CreateTerminalOutputPostProcessedCandidate(
                routeRequest,
                ordinaryCandidate,
                fixedAndObstacleSolids,
                cancellationToken);
            return outputCandidate ?? ordinaryCandidate;
        }

        /// <summary>
        /// Build the complete ordinary candidate pool for one current group.
        /// Scheme-library matching must select from this same pool instead of
        /// validating only the first skeleton; the pool includes continuous,
        /// extended and clearance-directed repairs used by the normal search.
        /// </summary>
        internal static LinkageRouteCandidatePool
            BuildSchemeLibraryOrdinaryCandidatePool(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            CancellationToken cancellationToken)
        {
            if (routeRequest == null || routeGroupRequest == null ||
                cancellationToken.IsCancellationRequested ||
                !TryBuildFixedAndObstacleSolids(
                    routeRequest,
                    out var fixedAndObstacleSolids,
                    out _))
            {
                return new LinkageRouteCandidatePool
                {
                    GroupRequest = routeGroupRequest
                };
            }

            var candidatePool = new LinkageRouteCandidatePool
            {
                GroupRequest = routeGroupRequest
            };
            candidatePool.TraversalDirections.Add(
                LinkageRouteTraversalDirection.Forward);
            var evaluatedCenterlineSignatures = new HashSet<string>(
                StringComparer.Ordinal);
            var candidateTarget = Math.Max(
                10,
                routeRequest.SearchOptions.MaximumCandidatesPerGroup * 2);
            foreach (var axisSkeleton in LinkageRouteAxisPathBuilder.BuildCandidates(
                routeRequest,
                routeGroupRequest,
                LinkageRouteTraversalDirection.Forward,
                cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return candidatePool;
                }

                var centerlines = new List<IList<Point3d>>
                {
                    axisSkeleton
                };
                if (axisSkeleton.Count >= 3)
                {
                    var conversionContext = CreateConversionContext(
                        routeRequest,
                        routeGroupRequest,
                        axisSkeleton,
                        fixedAndObstacleSolids);
                    centerlines.AddRange(
                        LinkageRouteConversionCandidate
                            .CreateContinuousCandidates(
                                axisSkeleton,
                                conversionContext)
                            .Where(candidate => candidate.IsConverted)
                            .Select(candidate => candidate.Centerline));
                }

                foreach (var centerline in centerlines)
                {
                    var signature = string.Join(";", (centerline ??
                        new List<Point3d>()).Select(point => string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "{0:0.###},{1:0.###},{2:0.###}",
                            point.X,
                            point.Y,
                            point.Z)));
                    if (!evaluatedCenterlineSignatures.Add(signature))
                    {
                        continue;
                    }
                    var candidate = BuildCandidate(
                        routeRequest,
                        routeGroupRequest,
                        axisSkeleton,
                        centerline,
                        fixedAndObstacleSolids,
                        !ReferenceEquals(axisSkeleton, centerline),
                        "SCHEME-LIBRARY-ORDINARY",
                        LinkageRouteTraversalDirection.Forward,
                        cancellationToken);
                    if (candidate?.IsValid == true)
                    {
                        candidatePool.ValidCandidates.Add(candidate);
                    }
                    if (candidatePool.ValidCandidates.Count >= candidateTarget)
                    {
                        return candidatePool;
                    }
                }
            }
            return candidatePool;
        }

        internal static LinkageRouteCandidate
            FinalizeSchemeLibraryOrdinaryCandidate(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteCandidate ordinaryCandidate,
            CancellationToken cancellationToken)
        {
            if (routeRequest == null || ordinaryCandidate == null ||
                cancellationToken.IsCancellationRequested ||
                !TryBuildFixedAndObstacleSolids(
                    routeRequest,
                    out var fixedAndObstacleSolids,
                    out _))
            {
                return null;
            }

            return CreateTerminalOutputPostProcessedCandidate(
                routeRequest,
                ordinaryCandidate,
                fixedAndObstacleSolids,
                cancellationToken);
        }

        private static LinkageRouteCandidate BuildCandidateCore(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IList<Point3d> axisSkeleton,
            IList<Point3d> manufacturableCenterline,
            IReadOnlyList<LinkageRouteSolid> staticSolids,
            bool isConvertedShape,
            string topologySignature,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var pathSearchRouteRequest = routeRequest.CreatePathSearchRequest();
            var centeredManufacturableCenterline =
                CreateSingleStartCenteredHorizontalLapCenterline(
                    pathSearchRouteRequest,
                    routeGroupRequest,
                    manufacturableCenterline);

            var routeCandidate = CreateCandidateShell(
                routeGroupRequest,
                axisSkeleton,
                centeredManufacturableCenterline,
                isConvertedShape,
                topologySignature,
                traversalDirection);

            if (!ValidateMinimumSegments(
                    centeredManufacturableCenterline,
                    pathSearchRouteRequest.MinimumSegmentLength))
            {
                return Fail(routeCandidate, LinkageRouteFailureStage.MinimumSegment,
                    "中心线存在小于 MinimumSegmentLength 的实际线段。", 1d);
            }

            var planarConversion = LinkageRouteCenterlineConverter.ConvertForUContactSideCancelable(
                centeredManufacturableCenterline,
                pathSearchRouteRequest,
                routeGroupRequest, false, 1, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            routeCandidate.PlanarConversion = planarConversion;
            var primaryCandidate = !planarConversion.IsValid
                ? Fail(routeCandidate, LinkageRouteFailureStage.PlanarConversion,
                    string.Join("；", planarConversion.Diagnostics) +
                    FormatPlanarPartDiagnostic(planarConversion.CenterlineParts), 1d)
                : ValidatePreparedCandidateCore(routeCandidate, pathSearchRouteRequest,
                    routeGroupRequest, staticSolids, cancellationToken);
            // Static-clearance evaluation returns null on cooperative cancel.
            // A canceled evaluation is not a failed candidate to dereference
            // while constructing the reverse contact-side alternative.
            cancellationToken.ThrowIfCancellationRequested();
            if (!pathSearchRouteRequest.AllowUShapeRoutes && planarConversion.USplitWasAttempted &&
                !cancellationToken.IsCancellationRequested)
            {
                var reverseCandidate = CreateCandidateShell(routeGroupRequest, axisSkeleton,
                    centeredManufacturableCenterline, isConvertedShape, topologySignature, traversalDirection);
                reverseCandidate.IdentitySignature = (primaryCandidate.IdentitySignature ?? string.Empty) + "|U-CONTACT-REVERSE";
                reverseCandidate.PlanarConversion = LinkageRouteCenterlineConverter.ConvertForUContactSideCancelable(
                    centeredManufacturableCenterline, pathSearchRouteRequest, routeGroupRequest, false, -1, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (reverseCandidate.PlanarConversion.IsValid)
                {
                    reverseCandidate = ValidatePreparedCandidateCore(reverseCandidate, pathSearchRouteRequest,
                        routeGroupRequest, staticSolids, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reverseCandidate.IsValid)
                    {
                        if (!primaryCandidate.IsValid) return reverseCandidate;
                        primaryCandidate.ReverseUSplitCandidate = reverseCandidate;
                    }
                }
            }
            return primaryCandidate.IsValid ? primaryCandidate :
                TryRepairCriticalLapForLinkedTrial(primaryCandidate, pathSearchRouteRequest,
                    staticSolids, cancellationToken) ?? primaryCandidate;
        }

        private static LinkageRouteCandidate BuildPreparedCandidate(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IList<Point3d> axisSkeleton,
            IList<Point3d> manufacturableCenterline,
            LinkageRouteCenterlineConversionResult preparedPlanarConversion,
            IReadOnlyList<LinkageRouteSolid> staticSolids,
            string topologySignature,
            string identitySignature,
            int searchPriority,
            CancellationToken cancellationToken)
        {
            var preparedRouteCandidate = CreateCandidateShell(
                routeGroupRequest,
                axisSkeleton,
                manufacturableCenterline,
                true,
                topologySignature,
                LinkageRouteTraversalDirection.Forward);
            preparedRouteCandidate.SearchPriority = searchPriority;
            preparedRouteCandidate.IdentitySignature = identitySignature;
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            try
            {
                var pathSearchRouteRequest = routeRequest
                    .CreatePathSearchRequest();
                if (!ValidateMinimumSegments(
                        manufacturableCenterline,
                        pathSearchRouteRequest.MinimumSegmentLength))
                {
                    return Fail(
                        preparedRouteCandidate,
                        LinkageRouteFailureStage.MinimumSegment,
                        "双始端 I/L 型桥接逻辑中心线存在小于 MinimumSegmentLength 的实际线段。",
                        1d);
                }

                preparedRouteCandidate.PlanarConversion = preparedPlanarConversion;
                if (preparedPlanarConversion?.IsValid != true)
                {
                    return Fail(
                        preparedRouteCandidate,
                        LinkageRouteFailureStage.PlanarConversion,
                        "双始端 I/L 型桥接方案缺少有效的两阶段拆排结果。",
                        1d);
                }

                return ValidatePreparedCandidateCore(
                    preparedRouteCandidate,
                    pathSearchRouteRequest,
                    routeGroupRequest,
                    staticSolids,
                    cancellationToken);
            }
            catch (ArgumentException exception)
            {
                return Fail(
                    preparedRouteCandidate,
                    LinkageRouteFailureStage.Conversion,
                    "双始端 I/L 型桥接候选几何无效：" + exception.Message,
                    1d);
            }
            catch (InvalidOperationException exception)
            {
                return Fail(
                    preparedRouteCandidate,
                    LinkageRouteFailureStage.Conversion,
                    "双始端 I/L 型桥接候选转换失败：" + exception.Message,
                    1d);
            }
        }

        /// <summary>
        /// 完整普通方案通过候选与组合校验后，在深拷贝上执行始端垂直
        /// 短搭接和端头无垫块输出后处理。后处理只重建用于输出和测量的
        /// 实体。垂直短搭接和分段规格不以输出几何复核否决普通方案；
        /// 无垫块发生真实重叠时仍局部保留原垫块。后处理结果不得写回搜索池。
        /// </summary>
        internal static LinkageRouteCandidate
            CreateTerminalOutputPostProcessedCandidate(
            LinkageMultiGroupRouteRequest outputRouteRequest,
            LinkageRouteCandidate ordinaryRouteCandidate,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            CancellationToken cancellationToken)
        {
            var requestedOutputCandidate =
                CreateTerminalOutputPostProcessedCandidateCore(
                    outputRouteRequest,
                    ordinaryRouteCandidate,
                    fixedTerminalAndObstacleSolids,
                    cancellationToken);
            var hasSpacerlessPositiveVolumeIntersection =
                IsSpacerlessPositiveVolumeIntersectionFailure(
                    requestedOutputCandidate);
            if (outputRouteRequest == null || requestedOutputCandidate == null)
            {
                return requestedOutputCandidate;
            }

            if (!hasSpacerlessPositiveVolumeIntersection &&
                requestedOutputCandidate.IsValid)
            {
                return requestedOutputCandidate;
            }

            var outputGroupRequest = ResolveOutputGroupRequest(
                outputRouteRequest,
                ordinaryRouteCandidate.GroupRequest);
            if (outputGroupRequest?.HasSectionSpecificBusbarSettings(
                    outputRouteRequest) == true &&
                requestedOutputCandidate.IsValid != true)
            {
                // 通用骨架只负责提供中心线路径。真实分段规格实体化失败时
                // 不能回退成通用中间排规输出，否则会把有效路径伪装成排规
                // 错误的成功方案；由调用方继续尝试同组其他普通骨架。
                return requestedOutputCandidate;
            }

            if (!hasSpacerlessPositiveVolumeIntersection)
            {
                // 端部缩短、层位偏移或附件重建属于已成功普通方案的输出
                // 优化。优化本身失败时回滚到普通实体继续输出，不能把普通
                // 候选改判失败；最终是否可发布仍只看回滚实体的绝缘距离。
                var rolledBackOutputCandidate =
                    CreateMultiStartConnectionPlateAlignedOutputCandidate(
                        outputRouteRequest,
                        ordinaryRouteCandidate,
                        fixedTerminalAndObstacleSolids,
                        cancellationToken);
                if (rolledBackOutputCandidate?.IsValid != true)
                {
                    return ordinaryRouteCandidate;
                }

                var outputFailureDiagnostic = string.Join(
                    "；",
                    new[] { requestedOutputCandidate.Diagnostic }
                        .Concat(requestedOutputCandidate.PlanarConversion
                            ?.Diagnostics ?? new List<string>())
                        .Where(diagnostic =>
                            !string.IsNullOrWhiteSpace(diagnostic))
                        .Distinct(StringComparer.Ordinal));
                rolledBackOutputCandidate.PlanarConversion.Diagnostics.Add(
                    "终端输出偏移或垫块处理未完成，已回滚到普通实体并继续" +
                    "最终绝缘距离复核" +
                    (string.IsNullOrWhiteSpace(outputFailureDiagnostic)
                        ? "。"
                        : "：" + outputFailureDiagnostic));
                return rolledBackOutputCandidate;
            }

            if (!outputRouteRequest.ShouldUseSpacerlessTerminalLap(true) &&
                !outputRouteRequest.ShouldUseSpacerlessTerminalLap(false))
            {
                return requestedOutputCandidate;
            }

            // 无垫块后处理只允许删除不会引起实际铜排正体积重叠的垫块。
            // 若某个终端压紧失败，重新使用同一个普通成功方案，仅让该终端
            // 保留普通垫块；不能把后处理碰撞升级为整套方案失败。
            var fallbackCandidates = new List<KeyValuePair<int,
                LinkageRouteCandidate>>();
            var usesSpacerlessStartTerminal = outputRouteRequest
                .ShouldUseSpacerlessTerminalLap(true);
            var usesSpacerlessEndTerminal = outputRouteRequest
                .ShouldUseSpacerlessTerminalLap(false);
            if (usesSpacerlessStartTerminal)
            {
                AddSpacerlessTerminalFallbackCandidate(
                    fallbackCandidates,
                    outputRouteRequest,
                    ordinaryRouteCandidate,
                    fixedTerminalAndObstacleSolids,
                    cancellationToken,
                    true,
                    false);
            }

            if (usesSpacerlessEndTerminal)
            {
                AddSpacerlessTerminalFallbackCandidate(
                    fallbackCandidates,
                    outputRouteRequest,
                    ordinaryRouteCandidate,
                    fixedTerminalAndObstacleSolids,
                    cancellationToken,
                    false,
                    true);
            }

            if (usesSpacerlessStartTerminal && usesSpacerlessEndTerminal)
            {
                AddSpacerlessTerminalFallbackCandidate(
                    fallbackCandidates,
                    outputRouteRequest,
                    ordinaryRouteCandidate,
                    fixedTerminalAndObstacleSolids,
                    cancellationToken,
                    true,
                    true);
            }

            var selectedFallbackCandidate = fallbackCandidates
                .Where(fallbackCandidate =>
                    fallbackCandidate.Value?.IsValid == true &&
                    !IsSpacerlessPositiveVolumeIntersectionFailure(
                        fallbackCandidate.Value))
                .OrderBy(fallbackCandidate => fallbackCandidate.Key)
                .ThenBy(fallbackCandidate => fallbackCandidate.Value
                    .PlanarConversion.Accessories.Count(routeAccessory =>
                        routeAccessory?.Kind ==
                            LinkageRouteAccessoryKind.SupportBlock))
                .ThenBy(fallbackCandidate => fallbackCandidate.Value
                    .PlanarConversion.ActualCopperLength)
                .Select(fallbackCandidate => fallbackCandidate.Value)
                .FirstOrDefault();
            if (selectedFallbackCandidate == null)
            {
                return requestedOutputCandidate;
            }

            selectedFallbackCandidate.PlanarConversion.Diagnostics.Add(
                "无垫块压紧会使同相叠排发生实体重叠，已回退冲突终端的" +
                "无垫块压紧，并仅在联动调整后仍有实际缝隙的位置保留普通垫块；" +
                "原成功方案仍正常输出。" );
            return selectedFallbackCandidate;
        }

        internal static LinkageRouteCandidate
            CreateMultiStartConnectionPlateAlignedOutputCandidate(
            LinkageMultiGroupRouteRequest outputRouteRequest,
            LinkageRouteCandidate ordinaryRouteCandidate,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested ||
                outputRouteRequest == null ||
                ordinaryRouteCandidate?.GroupRequest == null ||
                ordinaryRouteCandidate.PlanarConversion?.IsValid != true)
            {
                return null;
            }

            var outputGroupRequest = ResolveOutputGroupRequest(
                outputRouteRequest,
                ordinaryRouteCandidate.GroupRequest);

            var outputRouteCandidate = CreateCandidateShell(
                outputGroupRequest,
                ordinaryRouteCandidate.AxisSkeleton,
                ordinaryRouteCandidate.ManufacturableCenterline,
                ordinaryRouteCandidate.IsConvertedShape,
                ordinaryRouteCandidate.TopologySignature,
                ordinaryRouteCandidate.TraversalDirection);
            outputRouteCandidate.SearchPriority =
                ordinaryRouteCandidate.SearchPriority;
            outputRouteCandidate.IdentitySignature = string.IsNullOrWhiteSpace(
                    ordinaryRouteCandidate.IdentitySignature)
                ? "MULTI-START-CONNECTION-PLATE-OUTPUT-ALIGNED"
                : ordinaryRouteCandidate.IdentitySignature +
                  "|MULTI-START-CONNECTION-PLATE-OUTPUT-ALIGNED";

            outputRouteCandidate.PlanarConversion =
                LinkageRouteCenterlineConverter
                    .CreateMultiStartConnectionPlateOutputAlignmentResult(
                        ordinaryRouteCandidate.PlanarConversion,
                        outputGroupRequest.StartTerminal);
            if (outputRouteCandidate.PlanarConversion?.IsValid != true)
            {
                return Fail(
                    outputRouteCandidate,
                    LinkageRouteFailureStage.PlanarConversion,
                    string.Join(
                        "；",
                        outputRouteCandidate.PlanarConversion?.Diagnostics ??
                            new List<string>
                            {
                                "多始端连接排输出层位修正没有生成有效叠排。"
                            }),
                    1d);
            }

            return ValidatePreparedCandidateCore(
                outputRouteCandidate,
                outputRouteRequest,
                outputGroupRequest,
                fixedTerminalAndObstacleSolids,
                cancellationToken,
                enforceCandidateValidation: false,
                inspectPostProcessedPositiveVolumeIntersection: true);
        }

        private static bool IsSpacerlessPositiveVolumeIntersectionFailure(
            LinkageRouteCandidate failedOutputCandidate)
        {
            var failureDiagnostic = string.Join(
                "；",
                new[]
                    {
                        failedOutputCandidate?.Diagnostic
                    }
                    .Concat(failedOutputCandidate?.PlanarConversion?.Diagnostics ??
                        new List<string>())
                    .Where(diagnostic => !string.IsNullOrWhiteSpace(diagnostic)));
            return !string.IsNullOrWhiteSpace(failureDiagnostic) &&
                (failureDiagnostic.IndexOf(
                     "正体积自相交",
                     StringComparison.Ordinal) >= 0 ||
                 failureDiagnostic.IndexOf(
                     "正体积相交",
                     StringComparison.Ordinal) >= 0 ||
                 failureDiagnostic.IndexOf(
                     "正重叠",
                     StringComparison.Ordinal) >= 0);
        }

        private static void AddSpacerlessTerminalFallbackCandidate(
            ICollection<KeyValuePair<int, LinkageRouteCandidate>>
                fallbackCandidates,
            LinkageMultiGroupRouteRequest outputRouteRequest,
            LinkageRouteCandidate ordinaryRouteCandidate,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            CancellationToken cancellationToken,
            bool retainStartTerminalSpacers,
            bool retainEndTerminalSpacers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var fallbackRequest = outputRouteRequest
                .CreateSpacerlessTerminalFallbackRequest(
                    retainStartTerminalSpacers,
                    retainEndTerminalSpacers);
            var fallbackCandidate =
                CreateTerminalOutputPostProcessedCandidateCore(
                    fallbackRequest,
                    ordinaryRouteCandidate,
                    fixedTerminalAndObstacleSolids,
                    cancellationToken);
            fallbackCandidates.Add(new KeyValuePair<int, LinkageRouteCandidate>(
                (retainStartTerminalSpacers ? 1 : 0) +
                (retainEndTerminalSpacers ? 1 : 0),
                fallbackCandidate));
        }

        private static LinkageRouteCandidate
            CreateTerminalOutputPostProcessedCandidateCore(
            LinkageMultiGroupRouteRequest outputRouteRequest,
            LinkageRouteCandidate ordinaryRouteCandidate,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested ||
                outputRouteRequest == null ||
                ordinaryRouteCandidate?.GroupRequest == null ||
                ordinaryRouteCandidate.PlanarConversion?.IsValid != true)
            {
                return null;
            }

            var outputGroupRequest = ResolveOutputGroupRequest(
                outputRouteRequest,
                ordinaryRouteCandidate.GroupRequest);

            var outputRouteCandidate = CreateCandidateShell(
                outputGroupRequest,
                ordinaryRouteCandidate.AxisSkeleton,
                ordinaryRouteCandidate.ManufacturableCenterline,
                ordinaryRouteCandidate.IsConvertedShape,
                ordinaryRouteCandidate.TopologySignature,
                ordinaryRouteCandidate.TraversalDirection);
            outputRouteCandidate.SearchPriority =
                ordinaryRouteCandidate.SearchPriority;
            outputRouteCandidate.IdentitySignature = string.IsNullOrWhiteSpace(
                    ordinaryRouteCandidate.IdentitySignature)
                ? "TERMINAL-OUTPUT-POSTPROCESSED"
                : ordinaryRouteCandidate.IdentitySignature +
                    "|TERMINAL-OUTPUT-POSTPROCESSED";

            try
            {
                outputRouteCandidate.PlanarConversion =
                    LinkageRouteCenterlineConverter
                        .CreateTerminalOutputPostProcessingResult(
                            ordinaryRouteCandidate.PlanarConversion,
                            ordinaryRouteCandidate.ManufacturableCenterline,
                            outputRouteRequest,
                            outputGroupRequest);
                if (outputRouteCandidate.PlanarConversion?.IsValid != true)
                {
                    return Fail(
                        outputRouteCandidate,
                        LinkageRouteFailureStage.PlanarConversion,
                        string.Join(
                            "；",
                            outputRouteCandidate.PlanarConversion?.Diagnostics ??
                                new List<string>
                                {
                                    "终端输出后处理没有生成有效叠排。"
                                }),
                        1d);
                }

                return ValidatePreparedCandidateCore(
                    outputRouteCandidate,
                    outputRouteRequest,
                    outputGroupRequest,
                    fixedTerminalAndObstacleSolids,
                    cancellationToken,
                    enforceCandidateValidation: false,
                    inspectPostProcessedPositiveVolumeIntersection: true);
            }
            catch (ArgumentException exception)
            {
                return Fail(
                    outputRouteCandidate,
                    LinkageRouteFailureStage.Conversion,
                    "终端输出后处理几何无效：" + exception.Message,
                    1d);
            }
            catch (InvalidOperationException exception)
            {
                return Fail(
                    outputRouteCandidate,
                    LinkageRouteFailureStage.Conversion,
                    "终端输出后处理转换失败：" + exception.Message,
                    1d);
            }
        }

        private static LinkageRouteGroupRequest ResolveOutputGroupRequest(
            LinkageMultiGroupRouteRequest outputRouteRequest,
            LinkageRouteGroupRequest sourceGroupRequest)
        {
            if (outputRouteRequest?.Groups == null || sourceGroupRequest == null)
            {
                return sourceGroupRequest;
            }

            return outputRouteRequest.Groups.FirstOrDefault(routeGroupRequest =>
                       string.Equals(
                           routeGroupRequest?.GroupKey,
                           sourceGroupRequest.GroupKey,
                           StringComparison.OrdinalIgnoreCase)) ??
                sourceGroupRequest;
        }

        private static LinkageRouteCandidate ValidatePreparedCandidateCore(
            LinkageRouteCandidate routeCandidate,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<LinkageRouteSolid> staticSolids,
            CancellationToken cancellationToken,
            bool enforceCandidateValidation = true,
            bool inspectPostProcessedPositiveVolumeIntersection = false,
            bool isTerminalHoleShiftTrial = false)
        {
            var planarConversion = routeCandidate.PlanarConversion;
            var isSupportReductionTrial = planarConversion.SupportReductionGeometryAdjusted;
            if (routeGroupRequest.HasSectionSpecificBusbarSettings(routeRequest) &&
                !planarConversion.UsesSectionSpecificBusbarGeometry &&
                !LinkageRouteCenterlineConverter
                    .TryApplySectionSpecificBusbarGeometry(
                        planarConversion,
                        routeRequest,
                        routeGroupRequest,
                        out var sectionSpecificGeometryFailureReason))
            {
                return Fail(
                    routeCandidate,
                    LinkageRouteFailureStage.PlanarConversion,
                    sectionSpecificGeometryFailureReason,
                    1d);
            }

            var usesSpacerlessTerminalLap =
                routeRequest.ShouldUseSpacerlessTerminalLap(true) ||
                routeRequest.ShouldUseSpacerlessTerminalLap(false);
            var usesActualMemberGeometry = usesSpacerlessTerminalLap ||
                planarConversion.UsesSectionSpecificBusbarGeometry ||
                planarConversion.TerminalHoleLapGeometryAdjusted ||
                inspectPostProcessedPositiveVolumeIntersection || isSupportReductionTrial;

            // 先使用设置余量做保形补齐，真实实体受限才逐根尝试减小。
            // 任何试验仍需通过实体/固定排/包围盒等门禁；后续完整校验不省略。
            if (enforceCandidateValidation && !isTerminalHoleShiftTrial && !isSupportReductionTrial &&
                !LinkageRouteSectionSpecificationPostProcessor.TryRepairRoundedLapInterfacesCancelable(
                    planarConversion, routeRequest, routeGroupRequest,
                    () => IsRoundedTrialGeometryValid(routeCandidate, routeRequest, staticSolids, cancellationToken),
                    out var roundedLapRepairFailure, cancellationToken) &&
                !LinkageRouteSectionSpecificationPostProcessor.TryValidateRoundedLapInterfaces(
                    planarConversion, routeRequest, routeGroupRequest, out _))
                return Fail(routeCandidate, LinkageRouteFailureStage.Lap, roundedLapRepairFailure, 1d);
            // 真实圆弧搭接本身有效、但局部调整不能消除盒/干涉等限制时，
            // 继续由下方原有完整实体门禁给出准确失败类别，不把所有约束误报为搭接错误。

            // 默认同规格候选在这里首次生成端部附件。异规格候选已经在
            // 转换阶段按“真实分段规格→端部附件→内部补差垫块”完成，
            // 不能再清空重建，否则 2↔3 搭接的内部补差垫块会被删除。
            if (enforceCandidateValidation && !isTerminalHoleShiftTrial && !isSupportReductionTrial &&
                !planarConversion.UsesSectionSpecificBusbarGeometry &&
                !LinkageRouteTerminalAccessoryBuilder.TryBuild(
                    planarConversion,
                    routeRequest,
                    routeGroupRequest,
                    out var terminalAccessoryFailureReason))
            {
                return Fail(
                    routeCandidate,
                    LinkageRouteFailureStage.PlanarConversion,
                    terminalAccessoryFailureReason,
                    1d);
            }

            if (enforceCandidateValidation &&
                !routeRequest.UseLegacyDoubleStartHorizontalTopologyProposal &&
                !LinkageRouteTerminalAccessoryBuilder.TryValidateLengthSeparatedStartContacts(
                    planarConversion, routeGroupRequest, out var lengthSeparatedStartFailure))
                return Fail(routeCandidate, LinkageRouteFailureStage.Lap, lengthSeparatedStartFailure, 1d);

            if (inspectPostProcessedPositiveVolumeIntersection && !isTerminalHoleShiftTrial && !isSupportReductionTrial)
            {
                ApplyOutputPositiveVolumeRegressionAdjustments(
                    routeCandidate,
                    routeRequest,
                    staticSolids);
            }

            if (enforceCandidateValidation && !isTerminalHoleShiftTrial && !isSupportReductionTrial &&
                !LinkageRouteTerminalHoleLapValidator.TryRepair(routeRequest, routeGroupRequest, planarConversion,
                    () => ValidatePreparedCandidateCore(new LinkageRouteCandidate {
                        GroupRequest = routeGroupRequest, PlanarConversion = planarConversion,
                        AxisSkeleton = routeCandidate.AxisSkeleton,
                        ManufacturableCenterline = routeCandidate.ManufacturableCenterline,
                        IsConvertedShape = routeCandidate.IsConvertedShape,
                        TraversalDirection = routeCandidate.TraversalDirection
                    }, routeRequest, routeGroupRequest, staticSolids, cancellationToken,
                        // 净距校验在协作取消时返回 null；该试移未通过完整校验。
                        enforceCandidateValidation: true, isTerminalHoleShiftTrial: true)?.IsValid == true,
                    out var holeRepairFailure, cancellationToken))
                return Fail(routeCandidate, LinkageRouteFailureStage.Lap, holeRepairFailure, 1d);

            usesActualMemberGeometry |= planarConversion.TerminalHoleLapGeometryAdjusted;

            if (enforceCandidateValidation && !LinkageRouteSectionSpecificationPostProcessor.TryReconcileAdjustedRoundedLapAllowances(
                planarConversion, routeRequest, routeGroupRequest, out var actualRoundedInterfaceFailure))
                return Fail(routeCandidate, LinkageRouteFailureStage.Lap, actualRoundedInterfaceFailure, 1d);

            if (enforceCandidateValidation &&
                routeRequest.RequireAxisAlignedRoutes &&
                !TryValidateAxisAlignedConversion(
                    planarConversion,
                    out var axisAlignmentFailureReason))
            {
                return Fail(
                    routeCandidate,
                    LinkageRouteFailureStage.Conversion,
                    axisAlignmentFailureReason,
                    1d);
            }

            if (enforceCandidateValidation &&
                !TryValidateNoAcuteAngles(
                    planarConversion,
                    out var acuteAngleFailureReason,
                    out var acuteAngleViolationDepth))
            {
                return Fail(
                    routeCandidate,
                    LinkageRouteFailureStage.Conversion,
                    acuteAngleFailureReason,
                    acuteAngleViolationDepth);
            }

            if (enforceCandidateValidation &&
                !TryValidateStackMemberMinimumSegments(
                    planarConversion.StackMembers,
                    routeRequest.MinimumSegmentLength,
                    out var stackMemberMinimumSegmentFailureReason))
            {
                return Fail(
                    routeCandidate,
                    LinkageRouteFailureStage.MinimumSegment,
                    stackMemberMinimumSegmentFailureReason,
                    1d);
            }

            if (enforceCandidateValidation &&
                !usesSpacerlessTerminalLap &&
                !TryValidateNoSelfIntersections(
                    planarConversion,
                    out var selfIntersectionFailureReason))
            {
                return Fail(
                    routeCandidate,
                    LinkageRouteFailureStage.Conversion,
                    selfIntersectionFailureReason,
                    1d);
            }

            if (enforceCandidateValidation &&
                !LinkageRouteBendParity.IsAllowed(
                    planarConversion.CenterlineParts,
                    routeGroupRequest.StartTerminal.ThicknessDirection,
                    routeGroupRequest.StartTerminal.LengthDirection,
                    routeGroupRequest.EndTerminal.ThicknessDirection,
                    out var bendParityFailureReason))
            {
                return Fail(routeCandidate, LinkageRouteFailureStage.BendParity,
                    bendParityFailureReason, 1d);
            }

            if (!BuildScanSolids(
                    routeCandidate,
                    routeRequest,
                    out var solidSelfIntersectionFailureReason) &&
                enforceCandidateValidation)
            {
                return Fail(
                    routeCandidate,
                    LinkageRouteFailureStage.Conversion,
                    solidSelfIntersectionFailureReason,
                    1d);
            }

            if (enforceCandidateValidation &&
                routeCandidate.ScanSolids.Count == 0)
            {
                return Fail(routeCandidate, LinkageRouteFailureStage.Conversion,
                    "中心线无法构造成扫描长方体。", 1d);
            }

            if (enforceCandidateValidation &&
                !usesSpacerlessTerminalLap &&
                !IsInsideContainment(routeCandidate.ScanSolids, routeRequest))
            {
                return Fail(routeCandidate, LinkageRouteFailureStage.Containment,
                    "路径扫描实体超出允许包围盒。", 1d);
            }

            List<LinkageRouteStraightSegmentScanReference>
                actualStraightSegmentScanReferences = null;
            if (usesActualMemberGeometry)
            {
                actualStraightSegmentScanReferences =
                    new List<LinkageRouteStraightSegmentScanReference>();
            }
            var actualGeneratedMemberSolids = BuildActualGeneratedMemberSolids(
                routeCandidate,
                routeRequest,
                actualStraightSegmentScanReferences);
            var actualGeneratedClearanceSolids = usesActualMemberGeometry
                ? actualGeneratedMemberSolids
                : BuildActualGeneratedClearanceSolids(
                    routeCandidate,
                    routeRequest,
                    actualGeneratedMemberSolids);
            foreach (var actualGeneratedClearanceSolid in
                actualGeneratedClearanceSolids)
            {
                routeCandidate.ActualClearanceSolids.Add(
                    actualGeneratedClearanceSolid);
            }

            foreach (var lapClearanceEnvelope in
                BuildLapClearanceEnvelopeSolids(
                    routeCandidate,
                    routeRequest,
                    routeGroupRequest))
            {
                routeCandidate.ActualClearanceSolids.Add(
                    lapClearanceEnvelope);
            }

            if (routeCandidate.ActualClearanceSolids.Count == 0)
            {
                return Fail(
                    routeCandidate,
                    LinkageRouteFailureStage.Conversion,
                    "最终叠排成员的实际点集合无法构造成有向长方体。",
                    1d);
            }

            if (enforceCandidateValidation &&
                 usesActualMemberGeometry &&
                !TryValidateNoActualMemberCenterlineSelfIntersections(
                    planarConversion,
                    out var actualCenterlineSelfIntersectionFailureReason))
            {
                return Fail(
                    routeCandidate,
                    LinkageRouteFailureStage.Conversion,
                    actualCenterlineSelfIntersectionFailureReason,
                    1d);
            }

            if ((enforceCandidateValidation ||
                 inspectPostProcessedPositiveVolumeIntersection) &&
                usesActualMemberGeometry &&
                !TryValidateNoPositiveVolumeSelfIntersections(
                    actualStraightSegmentScanReferences,
                    out var actualSolidSelfIntersectionFailureReason,
                    compareAcrossScanSequences: true))
            {
                if (enforceCandidateValidation)
                {
                    return Fail(
                        routeCandidate,
                        LinkageRouteFailureStage.Conversion,
                        actualSolidSelfIntersectionFailureReason,
                        1d);
                }

                // 最终输出实体已经先执行多始端连接排层位回归调整。
                // 调整后仍有正体积干涉时只能记录输出错误，不能反向淘汰
                // 已经通过普通搜索的方案；后续继续按实际实体复算绝缘距离。
                planarConversion.Diagnostics.Add(
                    "最终输出连续 10 轮联动回归调整后仍存在正体积自相交，" +
                    "方案已保留并标记错误：" +
                    actualSolidSelfIntersectionFailureReason);
            }

            var actualGeneratedMemberSweepSolids =
                BuildActualGeneratedMemberSweepSolids(
                    routeCandidate,
                    routeRequest,
                    actualGeneratedMemberSolids);
            if (usesActualMemberGeometry)
            {
                // 端头压紧以及双始端横向桥排会形成相对旧基准中心线的
                // 非对称厚向偏移。后续修复、包围盒和诊断必须使用移动后的
                // 实际成员/附件实体，不能继续沿用旧对称叠排包络。
                routeCandidate.ScanSolids.Clear();
                foreach (var actualGeneratedMemberSweepSolid in
                    actualGeneratedMemberSweepSolids)
                {
                    routeCandidate.ScanSolids.Add(
                        actualGeneratedMemberSweepSolid);
                }

            }
            if (enforceCandidateValidation &&
                routeRequest.UseExplicitContainmentBox &&
                !IsInsideContainment(
                    actualGeneratedMemberSweepSolids,
                    routeRequest,
                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
            {
                return Fail(
                    routeCandidate,
                    LinkageRouteFailureStage.Containment,
                    "叠排各层实际扫描实体或折弯扫掠实体超出允许包围盒。",
                    1d);
            }

            if ((enforceCandidateValidation ||
                 inspectPostProcessedPositiveVolumeIntersection) &&
                !TryValidateNoStaticPositiveVolumeIntersections(
                    actualGeneratedMemberSolids,
                    staticSolids,
                    out var staticIntersectionFailureReason))
            {
                if (enforceCandidateValidation)
                {
                    return Fail(
                        routeCandidate,
                        LinkageRouteFailureStage.Clearance,
                        staticIntersectionFailureReason,
                        1d);
                }

                planarConversion.Diagnostics.Add(
                    "最终输出连续 10 轮联动回归调整后仍与固定排或障碍物" +
                    "存在正体积干涉，方案已保留、标记错误并继续复算绝缘距离：" +
                    staticIntersectionFailureReason);
            }

            LinkageRouteSolid deepestViolationCandidateSolid = null;
            LinkageRouteSolid deepestViolationStaticSolid = null;
            var deepestViolationActualDistance = double.PositiveInfinity;
            var requiredCandidateInsulationClearance = routeGroupRequest
                .GetRequiredInsulationClearance(routeRequest);
            var staticClearanceValidationResult = EvaluateStaticClearance(
                routeCandidate.ActualClearanceSolids,
                staticSolids,
                requiredCandidateInsulationClearance,
                cancellationToken);
            if (staticClearanceValidationResult == null)
            {
                return null;
            }

            routeCandidate.MinimumStaticClearance =
                staticClearanceValidationResult.MinimumClearance;
            routeCandidate.ViolationCount =
                staticClearanceValidationResult.ViolationCount;
            routeCandidate.MaximumViolationDepth =
                staticClearanceValidationResult.MaximumViolationDepth;
            routeCandidate.MinimumClearanceFaceGroups.Clear();
            foreach (var clearanceFaceGroup in
                staticClearanceValidationResult.MinimumClearanceFaceGroups)
            {
                routeCandidate.MinimumClearanceFaceGroups.Add(clearanceFaceGroup);
            }
            deepestViolationCandidateSolid = staticClearanceValidationResult
                .DeepestViolationGeneratedSolid;
            deepestViolationStaticSolid = staticClearanceValidationResult
                .DeepestViolationStaticSolid;
            deepestViolationActualDistance = staticClearanceValidationResult
                .DeepestViolationActualDistance;

            if (enforceCandidateValidation &&
                !staticClearanceValidationResult.IsClear)
            {
                routeCandidate.SuggestedClearanceSeparationDirection =
                    staticClearanceValidationResult.DeepestViolationIsLapEnvelope
                    ? staticClearanceValidationResult.DeepestLapViolationExitDirection
                    : GetSuggestedAxisSeparationDirection(
                        deepestViolationCandidateSolid,
                        deepestViolationStaticSolid);
                if (staticClearanceValidationResult.DeepestViolationIsLapEnvelope)
                {
                    return Fail(routeCandidate, LinkageRouteFailureStage.Clearance,
                        string.Format(CultureInfo.InvariantCulture,
                            "搭接区域绝缘违规：候选段={0}，固定排段={1}，组={2}，搭接面要求={3:F6} mm，" +
                            "单侧盒轴向退让={4:F6} mm，方向={5}；通用原铜排最小净距={6:F6}，要求={7:F6}。",
                            deepestViolationCandidateSolid?.SegmentIndex ?? -1,
                            deepestViolationStaticSolid?.SegmentIndex ?? -1,
                            deepestViolationStaticSolid?.GroupKey ?? "<未命名固定排>",
                            routeRequest.LapInsulationClearance, routeCandidate.MaximumViolationDepth,
                            FormatAxisDirection(routeCandidate.SuggestedClearanceSeparationDirection),
                            staticClearanceValidationResult.MinimumClearance, requiredCandidateInsulationClearance),
                        routeCandidate.MaximumViolationDepth);
                }
                var clearanceDiagnosticPrefix = staticClearanceValidationResult
                    .DeepestViolationIsLapEnvelope
                    ? "搭接面绝缘距离不足："
                    : "路径与其他相位固定排的三维绝缘距离不足：";
                return Fail(routeCandidate, LinkageRouteFailureStage.Clearance,
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        clearanceDiagnosticPrefix +
                        "候选段={0}，冲突段={1}，冲突组={2}，实际距离={3:F6}，" +
                        "要求距离={4:F6}，搭接面要求={6:F6}，建议分离方向={5}。",
                        deepestViolationCandidateSolid?.SegmentIndex ?? -1,
                        deepestViolationStaticSolid?.SegmentIndex ?? -1,
                        deepestViolationStaticSolid?.GroupKey ?? "<未命名固定排>",
                        deepestViolationActualDistance,
                        requiredCandidateInsulationClearance,
                        FormatAxisDirection(
                            routeCandidate
                                .SuggestedClearanceSeparationDirection),
                        staticClearanceValidationResult
                            .DeepestViolationIsLapEnvelope
                            ? routeRequest.LapInsulationClearance
                            : 0d),
                    routeCandidate.MaximumViolationDepth);
            }

            if (enforceCandidateValidation &&
                !ValidateTerminalLapCoverage(
                    routeCandidate,
                    routeRequest,
                    routeGroupRequest,
                    out var terminalLapFailureReason))
            {
                return Fail(routeCandidate, LinkageRouteFailureStage.Lap,
                    terminalLapFailureReason, 1d);
            }

            if (enforceCandidateValidation &&
                !LinkageRouteTerminalHoleLapValidator.TryValidate(routeRequest, routeGroupRequest, planarConversion, out var holeFinalFailure))
                return Fail(routeCandidate, LinkageRouteFailureStage.Lap, holeFinalFailure, 1d);

            if (enforceCandidateValidation &&
                !LinkageRouteFixedTerminalEndLapValidator.TryValidateConversion(
                    routeRequest, routeGroupRequest, routeCandidate.PlanarConversion, out var fixedEndLapFailure))
                return Fail(routeCandidate, LinkageRouteFailureStage.Lap, fixedEndLapFailure, 1d);

            if (enforceCandidateValidation &&
                !ValidateSingleStartLapPosition(
                    routeCandidate,
                    routeRequest,
                    routeGroupRequest,
                    out var singleStartLapFailureReason))
            {
                return Fail(
                    routeCandidate,
                    LinkageRouteFailureStage.Lap,
                    singleStartLapFailureReason,
                    1d);
            }

            // 候选已经使用每个物理段的真实排宽、排厚和叠排数量；折弯
            // 留量必须在同一次候选终检中通过，不能等发布后再修补。
            if (enforceCandidateValidation &&
                !ValidatePerpendicularLapBendReserves(
                    routeCandidate,
                    routeRequest,
                    routeGroupRequest,
                    out var perpendicularLapReserveFailureReason))
            {
                return Fail(routeCandidate, LinkageRouteFailureStage.Lap,
                    perpendicularLapReserveFailureReason, 1d);
            }

            if (enforceCandidateValidation &&
                !ValidateActualTerminalLapRegionSeparation(
                    routeCandidate,
                    routeRequest,
                    routeGroupRequest,
                    out var terminalLapRegionFailureReason))
            {
                return Fail(routeCandidate, LinkageRouteFailureStage.Lap,
                    terminalLapRegionFailureReason, 1d);
            }

            routeCandidate.PerpendicularLapCount = CountPerpendicularLapInterfaces(
                routeCandidate,
                routeGroupRequest,
                routeGroupRequest.GetBusbarWidth(routeRequest),
                routeRequest.AllowPartialStartPerpendicularLap);
            routeCandidate.StartPerpendicularLapVariant =
                ClassifyStartPerpendicularLapVariant(
                    routeCandidate,
                    routeRequest,
                    routeGroupRequest);

            // 保形补齐及逐排回退可能改变点集和展开长度，不能让旧缓存进入排序/剪枝。
            planarConversion.ActualCopperLength = planarConversion.StackMembers.SelectMany(member => member.PlanarParts)
                .Sum(part => part.GetDevelopedLength()) + LinkageRouteCopperLength.GetBridgeLength(planarConversion.Accessories);

            if (routeGroupRequest.EndTerminal?.CabinetBridgeAssembly != null)
            {
                // 柜顶桥已是实际实体。先检查最终始端层位，再把候选交给组合，
                // 避免大量“组合可行、输出后相交”的种子占住有限候选名额。
                if (enforceCandidateValidation && planarConversion.Accessories.Any(accessory =>
                    accessory?.Kind == LinkageRouteAccessoryKind.DoubleStartHorizontalBridge))
                    return CreateMultiStartConnectionPlateAlignedOutputCandidate(
                        routeRequest, routeCandidate, staticSolids, cancellationToken);

                var physicalCandidate = new LinkageRouteSolution();
                physicalCandidate.GroupSolutions.Add(new LinkageGroupRouteSolution {
                    GroupKey = routeGroupRequest.GroupKey,
                    StackMembers = planarConversion.StackMembers.ToList(),
                    Accessories = planarConversion.Accessories.ToList()
                });
                if (!Algorithms.LinkageMultiGroupRouteEngine.TryValidatePhysicalSolutionStructure(
                    physicalCandidate, out var bridgeCandidateFailure,
                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
                    return Fail(routeCandidate, LinkageRouteFailureStage.Conversion,
                        bridgeCandidateFailure, 1d);
            }

            return routeCandidate;
        }

        private static bool IsRoundedTrialGeometryValid(LinkageRouteCandidate candidate,
            LinkageMultiGroupRouteRequest request, IReadOnlyList<LinkageRouteSolid> staticSolids,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested ||
                !TryValidateNoAcuteAngles(candidate.PlanarConversion, out _, out _) ||
                !TryValidateStackMemberMinimumSegments(candidate.PlanarConversion.StackMembers, request.MinimumSegmentLength, out _) ||
                !TryValidateNoActualMemberCenterlineSelfIntersections(candidate.PlanarConversion, out _)) return false;
            var references = new List<LinkageRouteStraightSegmentScanReference>();
            var actualSolids = BuildActualGeneratedMemberSolids(candidate, request, references);
            if (!TryValidateNoPositiveVolumeSelfIntersections(references, out _, compareAcrossScanSequences: true) ||
                !TryValidateNoStaticPositiveVolumeIntersections(actualSolids, staticSolids, out _)) return false;
            var sweep = BuildActualGeneratedMemberSweepSolids(candidate, request, actualSolids);
            if (request.UseExplicitContainmentBox && !IsInsideContainment(sweep, request,
                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison)) return false;
            var clearanceSolids = actualSolids.Concat(BuildLapClearanceEnvelopeSolids(candidate, request, candidate.GroupRequest)).ToList();
            var clearance = EvaluateStaticClearance(clearanceSolids, staticSolids,
                candidate.GroupRequest.GetRequiredInsulationClearance(request), cancellationToken);
            return clearance?.IsClear == true;
        }

        private static void ApplyOutputPositiveVolumeRegressionAdjustments(
            LinkageRouteCandidate routeCandidate,
            LinkageMultiGroupRouteRequest routeRequest,
            IReadOnlyList<LinkageRouteSolid> staticSolids)
        {
            for (var adjustmentIndex = 0;
                adjustmentIndex < MaximumOutputIntersectionAdjustmentCount;
                adjustmentIndex++)
            {
                var straightSegmentScanReferences =
                    new List<LinkageRouteStraightSegmentScanReference>();
                var actualGeneratedMemberSolids =
                    BuildActualGeneratedMemberSolids(
                        routeCandidate,
                        routeRequest,
                        straightSegmentScanReferences);
                var hasNoSelfIntersection =
                    TryValidateNoPositiveVolumeSelfIntersections(
                        straightSegmentScanReferences,
                        out _,
                        compareAcrossScanSequences: true);
                var hasNoStaticIntersection =
                    TryValidateNoStaticPositiveVolumeIntersections(
                        actualGeneratedMemberSolids,
                        staticSolids,
                        out _);
                if (hasNoSelfIntersection && hasNoStaticIntersection)
                {
                    return;
                }

                // 每轮都以刚刚实体复查得到的当前层位为输入：先处理已经
                // 合并进主路径的连接排，再处理附件式连接排及其外侧叠排。
                // 调整只修改最终输出深拷贝，不写回普通搜索候选。
                if (!LinkageRouteSectionSpecificationPostProcessor
                        .TryAlignPhysicalBridgeFollowingLapEndpoints(
                            routeCandidate.PlanarConversion,
                            routeCandidate.GroupRequest?.StartTerminal,
                            out var physicalBridgeAdjustmentFailureReason) &&
                    !string.IsNullOrWhiteSpace(
                        physicalBridgeAdjustmentFailureReason) &&
                    !routeCandidate.PlanarConversion.Diagnostics.Contains(
                        physicalBridgeAdjustmentFailureReason))
                {
                    routeCandidate.PlanarConversion.Diagnostics.Add(
                        physicalBridgeAdjustmentFailureReason);
                }

                LinkageRouteTerminalAccessoryBuilder
                    .ApplyOutputTerminalBridgeLayerAlignment(
                        routeCandidate.PlanarConversion);
            }
        }

        private static bool TryValidateAxisAlignedConversion(
            LinkageRouteCenterlineConversionResult planarConversion,
            out string failureReason)
        {
            if (!TryValidateAxisAlignedPlanarParts(
                    planarConversion?.CenterlineParts,
                    out failureReason))
            {
                return false;
            }

            var actualStackMemberPlanarParts = (planarConversion?.StackMembers ??
                    new List<LinkageStackedRouteMember>())
                .Where(stackMember => stackMember?.PlanarParts != null)
                .SelectMany(stackMember => stackMember.PlanarParts);
            if (TryValidateAxisAlignedPlanarParts(
                    actualStackMemberPlanarParts,
                    out var actualStackMemberFailureReason))
            {
                var accessoryPlanarParts = (planarConversion?.Accessories ??
                        new List<LinkageRouteAccessory>())
                    .Where(routeAccessory => routeAccessory?.Setting != null)
                    .Select(routeAccessory => routeAccessory.Setting);
                if (TryValidateAxisAlignedPlanarParts(
                        accessoryPlanarParts,
                        out var accessoryFailureReason))
                {
                    return true;
                }

                failureReason = "端部附件不满足 XYZ 轴向限制：" +
                    accessoryFailureReason;
                return false;
            }

            failureReason = "叠排实际层位不满足 XYZ 轴向限制：" +
                actualStackMemberFailureReason;
            return false;
        }

        private static bool TryValidateAxisAlignedPlanarParts(
            IEnumerable<PlanarBendBusbarSetting> planarParts,
            out string failureReason)
        {
            const double axisAlignmentTolerance = 1e-6d;
            failureReason = null;
            var planarPartIndex = 0;
            foreach (var planarPart in planarParts ??
                Enumerable.Empty<PlanarBendBusbarSetting>())
            {
                planarPartIndex++;
                if (planarPart?.PointList == null ||
                    planarPart.PointList.Count < 2)
                {
                    failureReason = string.Format(
                        CultureInfo.InvariantCulture,
                        "仅允许 XYZ 轴向路径时，第 {0} 根生成排缺少有效中心线。",
                        planarPartIndex);
                    return false;
                }

                for (var segmentIndex = 0;
                    segmentIndex < planarPart.PointList.Count - 1;
                    segmentIndex++)
                {
                    var segmentDirection = new Vector3d(
                        planarPart.PointList[segmentIndex],
                        planarPart.PointList[segmentIndex + 1]);
                    var normalizedSegmentDirection =
                        segmentDirection.Norm <= GeometryTolerance
                            ? null
                            : segmentDirection.Normalized;
                    var significantAxisComponentCount =
                        normalizedSegmentDirection == null
                            ? 0
                            : new[]
                                {
                                    Math.Abs(normalizedSegmentDirection.X),
                                    Math.Abs(normalizedSegmentDirection.Y),
                                    Math.Abs(normalizedSegmentDirection.Z)
                                }
                                .Count(axisComponent =>
                                    axisComponent > axisAlignmentTolerance);
                    if (significantAxisComponentCount == 1)
                    {
                        continue;
                    }

                    failureReason = string.Format(
                        CultureInfo.InvariantCulture,
                        "仅允许 XYZ 轴向路径，第 {0} 根生成排第 {1} 段为斜向或零长度线段。",
                        planarPartIndex,
                        segmentIndex + 1);
                    return false;
                }
            }

            return true;
        }

        private static StaticClearanceValidationResult EvaluateStaticClearance(
            IEnumerable<LinkageRouteSolid> generatedSolids,
            IEnumerable<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            double requiredInsulationClearance,
            CancellationToken cancellationToken)
        {
            var validationResult = new StaticClearanceValidationResult();
            const double minimumFaceGroupTolerance = 1e-6d;
            foreach (var generatedSolid in generatedSolids ??
                Enumerable.Empty<LinkageRouteSolid>())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                foreach (var fixedTerminalOrObstacleSolid in
                    fixedTerminalAndObstacleSolids ??
                    Enumerable.Empty<LinkageRouteSolid>())
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return null;
                    }

                    if (fixedTerminalOrObstacleSolid == null ||
                        fixedTerminalOrObstacleSolid.IsForbiddenTerminalRegion ||
                        fixedTerminalOrObstacleSolid.SolidRole ==
                            LinkageRouteSolidRole.InputObstacle)
                    {
                        continue;
                    }

                    var clearanceEvaluation = LinkageRouteClearanceEvaluator
                        .EvaluatePairWithBroadPhase(
                            generatedSolid,
                            fixedTerminalOrObstacleSolid,
                            requiredInsulationClearance,
                            LinkageRouteGeometryTolerances.FinalAcceptanceComparison);
                    if (clearanceEvaluation.IsSameGroupExcluded)
                    {
                        continue;
                    }

                    if (!double.IsInfinity(clearanceEvaluation.ActualDistance) &&
                        !clearanceEvaluation.IsLapEnvelopePair)
                    {
                        var previousMinimumClearance =
                            validationResult.MinimumClearance;
                        validationResult.MinimumClearance = Math.Min(
                            previousMinimumClearance,
                            clearanceEvaluation.ActualDistance);
                        if (!clearanceEvaluation.IsExactDistance)
                        {
                            continue;
                        }

                        if (clearanceEvaluation.ActualDistance +
                                minimumFaceGroupTolerance <
                            previousMinimumClearance)
                        {
                            validationResult.MinimumClearanceFaceGroups.Clear();
                        }

                        if (Math.Abs(clearanceEvaluation.ActualDistance -
                                validationResult.MinimumClearance) <=
                            minimumFaceGroupTolerance &&
                            !validationResult.MinimumClearanceFaceGroups.Any(
                                existingFaceGroup =>
                                    existingFaceGroup.GeneratedSegmentIndex ==
                                        generatedSolid.SegmentIndex &&
                                    existingFaceGroup.StaticSegmentIndex ==
                                        fixedTerminalOrObstacleSolid.SegmentIndex &&
                                    string.Equals(
                                        existingFaceGroup.StaticGroupKey,
                                        fixedTerminalOrObstacleSolid.GroupKey,
                                        StringComparison.OrdinalIgnoreCase)))
                        {
                            validationResult.MinimumClearanceFaceGroups.Add(
                                CreateClearanceFaceGroup(
                                    generatedSolid,
                                    fixedTerminalOrObstacleSolid,
                                    clearanceEvaluation.ActualDistance));
                        }
                    }
                    if (clearanceEvaluation.IsClear)
                    {
                        continue;
                    }

                    validationResult.IsClear = false;
                    validationResult.ViolationCount++;
                    Vector3d staticLapExitDirection = null;
                    var effectiveViolationDepth =
                        clearanceEvaluation.IsLapEnvelopePair
                            ? LinkageRouteSolidDistance.GetOverlapAxisExitDistance(generatedSolid,
                                fixedTerminalOrObstacleSolid, out staticLapExitDirection)
                            : clearanceEvaluation.ViolationDepth;
                    if (effectiveViolationDepth <=
                            validationResult.MaximumViolationDepth &&
                        validationResult.DeepestViolationGeneratedSolid != null)
                    {
                        continue;
                    }

                    validationResult.MaximumViolationDepth =
                        effectiveViolationDepth;
                    validationResult.DeepestViolationGeneratedSolid =
                        generatedSolid;
                    validationResult.DeepestViolationStaticSolid =
                        fixedTerminalOrObstacleSolid;
                    validationResult.DeepestViolationIsLapEnvelope =
                        clearanceEvaluation.IsLapEnvelopePair;
                    validationResult.DeepestLapViolationExitDirection = staticLapExitDirection;
                    validationResult.DeepestViolationActualDistance =
                        clearanceEvaluation.ActualDistance;
                }
            }

            return validationResult;
        }

        internal static LinkageRouteClearanceFaceGroup CreateClearanceFaceGroup(
            LinkageRouteSolid generatedSolid,
            LinkageRouteSolid staticSolid,
            double distance)
        {
            var centerDirection = new Vector3d(
                generatedSolid.Center,
                staticSolid.Center);
            if (centerDirection.Norm <= GeometryTolerance)
            {
                centerDirection = new Vector3d(1d, 0d, 0d);
            }

            var generatedNormal = SelectSolidFaceNormal(
                generatedSolid,
                centerDirection.Normalized);
            var staticNormal = SelectSolidFaceNormal(
                staticSolid,
                new Vector3d(
                    -centerDirection.X,
                    -centerDirection.Y,
                    -centerDirection.Z).Normalized);
            return new LinkageRouteClearanceFaceGroup
            {
                GeneratedGroupKey = generatedSolid.GroupKey,
                GeneratedSegmentIndex = generatedSolid.SegmentIndex,
                StaticGroupKey = staticSolid.GroupKey,
                StaticSegmentIndex = staticSolid.SegmentIndex,
                Distance = distance,
                GeneratedFaceNormal = generatedNormal,
                GeneratedFaceCenter = OffsetSolidFaceCenter(
                    generatedSolid,
                    generatedNormal),
                StaticFaceNormal = staticNormal,
                StaticFaceCenter = OffsetSolidFaceCenter(
                    staticSolid,
                    staticNormal)
            };
        }

        private static Vector3d SelectSolidFaceNormal(
            LinkageRouteSolid routeSolid,
            Vector3d towardDirection)
        {
            var candidates = new[]
            {
                routeSolid.LengthDirection.Normalized,
                new Vector3d(-routeSolid.LengthDirection.X,
                    -routeSolid.LengthDirection.Y,
                    -routeSolid.LengthDirection.Z).Normalized,
                routeSolid.WidthDirection.Normalized,
                new Vector3d(-routeSolid.WidthDirection.X,
                    -routeSolid.WidthDirection.Y,
                    -routeSolid.WidthDirection.Z).Normalized,
                routeSolid.ThicknessDirection.Normalized,
                new Vector3d(-routeSolid.ThicknessDirection.X,
                    -routeSolid.ThicknessDirection.Y,
                    -routeSolid.ThicknessDirection.Z).Normalized
            };
            return candidates
                .OrderByDescending(candidate => candidate.Dot(towardDirection))
                .First();
        }

        private static Point3d OffsetSolidFaceCenter(
            LinkageRouteSolid routeSolid,
            Vector3d faceNormal)
        {
            var halfExtent = Math.Abs(faceNormal.Dot(
                routeSolid.LengthDirection.Normalized)) * routeSolid.HalfLength +
                Math.Abs(faceNormal.Dot(routeSolid.WidthDirection.Normalized)) *
                    routeSolid.HalfWidth +
                Math.Abs(faceNormal.Dot(routeSolid.ThicknessDirection.Normalized)) *
                    routeSolid.HalfThickness;
            return new Point3d(
                routeSolid.Center.X + faceNormal.X * halfExtent,
                routeSolid.Center.Y + faceNormal.Y * halfExtent,
                routeSolid.Center.Z + faceNormal.Z * halfExtent);
        }

        private static IReadOnlyList<LinkageRouteSolid>
            BuildActualGeneratedMemberSolids(
                LinkageRouteCandidate routeCandidate,
                LinkageMultiGroupRouteRequest routeRequest,
                ICollection<LinkageRouteStraightSegmentScanReference>
                    straightSegmentScanReferences = null)
        {
            return BuildActualGeneratedMemberSolidsCore(
                routeCandidate,
                false,
                straightSegmentScanReferences);
        }

        private static IReadOnlyList<LinkageRouteSolid>
            BuildActualGeneratedOuterMemberSolids(
                LinkageRouteCandidate routeCandidate,
                LinkageMultiGroupRouteRequest routeRequest)
        {
            return BuildActualGeneratedMemberSolidsCore(
                routeCandidate,
                true);
        }

        private static IReadOnlyList<LinkageRouteSolid>
            BuildActualGeneratedClearanceSolids(
                LinkageRouteCandidate routeCandidate,
                LinkageMultiGroupRouteRequest routeRequest,
                IReadOnlyList<LinkageRouteSolid> actualGeneratedMemberSolids)
        {
            var usesSpacerlessTerminalLap =
                routeRequest.ShouldUseSpacerlessTerminalLap(true) ||
                routeRequest.ShouldUseSpacerlessTerminalLap(false);
            var usesSectionSpecificBusbarSettings = routeCandidate
                ?.GroupRequest?.HasSectionSpecificBusbarSettings(routeRequest) ==
                true;
            return (usesSpacerlessTerminalLap ||
                    usesSectionSpecificBusbarSettings)
                ? actualGeneratedMemberSolids
                : BuildActualGeneratedOuterMemberSolids(
                    routeCandidate,
                    routeRequest);
        }

        /// <summary>
        /// 为每个真实搭接接口建立单侧绝缘膨胀盒。该盒只与其他绝缘组的
        /// 原始铜排实体做正体积干涉判断，绝不作为普通铜排再次参与距离
        /// 膨胀，也不与另一个搭接绝缘盒重复计算。
        /// </summary>
        internal static IReadOnlyList<LinkageRouteSolid>
            BuildLapClearanceEnvelopeSolids(
                LinkageRouteCandidate routeCandidate,
                LinkageMultiGroupRouteRequest routeRequest,
                LinkageRouteGroupRequest routeGroupRequest,
                double acceptanceTolerance = GeometryTolerance)
        {
            var lapClearanceEnvelopesByInterfaceKey =
                new Dictionary<string, LinkageRouteSolid>(StringComparer.Ordinal);
            var lapInsulationClearance = routeRequest?.LapInsulationClearance ?? 0d;
            if (routeCandidate?.PlanarConversion?.StackMembers == null ||
                lapInsulationClearance <= GeometryTolerance)
            {
                return lapClearanceEnvelopesByInterfaceKey.Values.ToList();
            }

            var orderedStackMembers = routeCandidate.PlanarConversion.StackMembers
                .Where(stackMember => stackMember?.PlanarParts != null)
                .OrderBy(stackMember => stackMember.MemberIndex)
                .ToList();
            if (orderedStackMembers.Count == 0 &&
                routeCandidate.PlanarConversion.CenterlineParts != null &&
                routeCandidate.PlanarConversion.CenterlineParts.Count > 0)
            {
                orderedStackMembers.Add(new LinkageStackedRouteMember
                {
                    MemberIndex = 0,
                    PlanarParts = routeCandidate.PlanarConversion.CenterlineParts
                });
            }
            IReadOnlyList<LinkageStackedRouteMember> selectedStackMembers =
                orderedStackMembers.Count <= 2
                ? orderedStackMembers
                : new[] { orderedStackMembers.First(), orderedStackMembers.Last() };

            // 与真实端部搭接验收使用相同要求。固定排可比生成排更宽，
            // 不能用Max(两排宽)重新拒绝已合法的接口并静默漏掉绝缘盒。
            // 每组仅派生一次，不能在成员对循环内重复求几何。
            var startEnvelopeRequiredLapLength = routeGroupRequest.GetStartTerminalRequiredLapLength(routeRequest);
            var endEnvelopeRequiredLapLength = routeGroupRequest.GetCabinetTopLapBusbarWidth(routeRequest);

            foreach (var stackedRouteMember in selectedStackMembers)
            {
                var planarParts = stackedRouteMember.PlanarParts;
                for (var planarPartIndex = 0;
                    planarPartIndex < planarParts.Count;
                    planarPartIndex++)
                {
                    var currentPlanarPart = planarParts[planarPartIndex];
                    if (planarPartIndex == 0)
                    {
                        foreach (var terminalMember in routeGroupRequest.StartTerminal?.Members ??
                            new List<LinkageTerminalMember>())
                        {
                            AddLapClearanceEnvelopeForPhysicalInterface(
                                lapClearanceEnvelopesByInterfaceKey,
                                currentPlanarPart,
                                true,
                                terminalMember?.FixedBusbar,
                                false,
                                routeGroupRequest,
                                lapInsulationClearance,
                                "START", acceptanceTolerance,
                                startEnvelopeRequiredLapLength);
                        }
                    }

                    if (planarPartIndex < planarParts.Count - 1)
                    {
                        AddLapClearanceEnvelopeForPhysicalInterface(
                            lapClearanceEnvelopesByInterfaceKey,
                            currentPlanarPart,
                            false,
                            planarParts[planarPartIndex + 1],
                            true,
                            routeGroupRequest,
                            lapInsulationClearance,
                            "INTERNAL-" + planarPartIndex, acceptanceTolerance);
                    }

                    if (planarPartIndex == planarParts.Count - 1)
                    {
                        foreach (var terminalMember in routeGroupRequest.EndTerminal?.Members ??
                            new List<LinkageTerminalMember>())
                        {
                            AddLapClearanceEnvelopeForPhysicalInterface(
                                lapClearanceEnvelopesByInterfaceKey,
                                currentPlanarPart,
                                false,
                                terminalMember?.FixedBusbar,
                                true,
                                routeGroupRequest,
                                lapInsulationClearance,
                                "END", acceptanceTolerance, endEnvelopeRequiredLapLength);
                        }
                    }
                }

                var routeAccessories = routeCandidate.PlanarConversion.Accessories ??
                    new List<LinkageRouteAccessory>();
                for (var routeAccessoryIndex = 0;
                    routeAccessoryIndex < routeAccessories.Count;
                    routeAccessoryIndex++)
                {
                    var routeAccessory = routeAccessories[routeAccessoryIndex];
                    if (routeAccessory?.Setting == null ||
                        routeAccessory.RelatedGeneratedMemberIndex !=
                            stackedRouteMember.MemberIndex ||
                        planarParts.Count == 0)
                    {
                        continue;
                    }
                    if (routeRequest.EnableSingleStartConnectionBar &&
                        routeGroupRequest.StartTerminal?.Members?.Count == 1 &&
                        routeAccessory.Kind == LinkageRouteAccessoryKind.DoubleStartHorizontalBridge)
                        continue; // 下方按真实夹持侧及两个接口统一生成，不能合并正负两侧。

                    var accessoryTarget = routeAccessory.TerminalSide ==
                        LinkageRouteTerminalSide.Start
                        ? planarParts.First()
                        : planarParts.Last();
                    var accessoryTargetUsesStart = routeAccessory.TerminalSide ==
                        LinkageRouteTerminalSide.Start;
                    AddLapClearanceEnvelopeForPhysicalInterface(
                        lapClearanceEnvelopesByInterfaceKey,
                        accessoryTarget,
                        accessoryTargetUsesStart,
                        routeAccessory.Setting,
                        true,
                        routeGroupRequest,
                        lapInsulationClearance,
                        "ACCESSORY-" + routeAccessory.TerminalSide, acceptanceTolerance,
                        accessoryTargetUsesStart ? startEnvelopeRequiredLapLength : endEnvelopeRequiredLapLength);
                }
            }

            if (routeRequest.EnableSingleStartConnectionBar &&
                routeGroupRequest.StartTerminal?.Members?.Count == 1)
            {
                // 新结构有两处真实接口：生成排→连接排、连接排→固定排。
                // 中间夹持层不一定是首末叠排，不能沿用外层抽取来漏掉该接口。
                foreach (var connectionAccessory in routeCandidate.PlanarConversion.Accessories.Where(accessory =>
                    accessory?.Kind == LinkageRouteAccessoryKind.DoubleStartHorizontalBridge &&
                    accessory.TerminalSide == LinkageRouteTerminalSide.Start && accessory.Setting != null))
                {
                    var fixedStartSetting = routeGroupRequest.StartTerminal.Members[0].FixedBusbar;
                    var connectionSide = Math.Sign(new Vector3d(fixedStartSetting.PointList[0],
                        connectionAccessory.Setting.PointList[0]).Dot(routeGroupRequest.StartTerminal.ThicknessDirection.Normalized));
                    var relatedStartSetting = orderedStackMembers.FirstOrDefault(member =>
                        member.MemberIndex == connectionAccessory.RelatedGeneratedMemberIndex)?.PlanarParts.FirstOrDefault();
                    AddLapClearanceEnvelopeForPhysicalInterface(lapClearanceEnvelopesByInterfaceKey,
                        relatedStartSetting, true, connectionAccessory.Setting, true,
                        routeGroupRequest, lapInsulationClearance, "SINGLE-START-CONNECTION-GENERATED-" + connectionSide,
                        acceptanceTolerance, startEnvelopeRequiredLapLength);
                    AddLapClearanceEnvelopeForPhysicalInterface(lapClearanceEnvelopesByInterfaceKey,
                        connectionAccessory.Setting, true, fixedStartSetting, true,
                        routeGroupRequest, lapInsulationClearance, "SINGLE-START-CONNECTION-FIXED-" + connectionSide,
                        acceptanceTolerance, startEnvelopeRequiredLapLength);
                }
            }
            foreach (var terminal in new[] { routeGroupRequest.StartTerminal, routeGroupRequest.EndTerminal })
            {
                var cabinet = terminal?.CabinetBridgeAssembly;
                if (cabinet == null) continue;
                var side = ReferenceEquals(terminal, routeGroupRequest.StartTerminal) ? LinkageRouteTerminalSide.Start : LinkageRouteTerminalSide.End;
                var additions = routeCandidate.PlanarConversion.Accessories.Where(a => a.TerminalSide == side && a.Setting != null).ToList();
                foreach (var addition in additions)
                foreach (var fixedMember in cabinet.OriginalTerminal.Members)
                    AddLapClearanceEnvelopeForPhysicalInterface(lapClearanceEnvelopesByInterfaceKey,
                        addition.Setting, true, fixedMember.FixedBusbar, true, routeGroupRequest,
                        lapInsulationClearance, "CABINET-FIXED", acceptanceTolerance, cabinet.LapWidth);
                for (var i = 0; i < additions.Count; i++)
                for (var j = i+1; j < additions.Count; j++)
                    AddLapClearanceEnvelopeForPhysicalInterface(lapClearanceEnvelopesByInterfaceKey,
                        additions[i].Setting, true, additions[j].Setting, true, routeGroupRequest,
                        lapInsulationClearance, "CABINET-ACCESSORY", acceptanceTolerance, cabinet.LapWidth);
            }
            return lapClearanceEnvelopesByInterfaceKey.Values.ToList();
        }

        // 历史匹配可能具有不同始端前缀、共享末段。绝缘接口来自真实接触图，
        // 不是唯一实体列表的相邻元素。普通候选仍调用原串联构造器。
        internal static IReadOnlyList<LinkageRouteSolid> BuildHistoricalLapClearanceEnvelopeSolids(
            LinkageRouteCandidate candidate, LinkageMultiGroupRouteRequest request,
            LinkageRouteGroupRequest groupRequest, double acceptanceTolerance)
        {
            var envelopes = new Dictionary<string, LinkageRouteSolid>(StringComparer.Ordinal);
            if (request.LapInsulationClearance <= GeometryTolerance) return envelopes.Values.ToList();
            var generated = candidate.PlanarConversion.StackMembers.SelectMany(member => member.PlanarParts)
                .Concat(candidate.PlanarConversion.Accessories.Select(accessory => accessory.Setting)).ToList();
            var fixedSettings = groupRequest.StartTerminal.Members.Concat(groupRequest.EndTerminal.Members)
                .Select(member => member.FixedBusbar).ToList();
            var historicalBlocks = new HashSet<PlanarBendBusbarSetting>(candidate.PlanarConversion.Accessories
                .Where(LinkageSchemeLibraryRouteRebuilder.IsHistoricalInterfaceSupportBlock).Select(accessory => accessory.Setting));
            for (var firstIndex = 0; firstIndex < generated.Count; firstIndex++)
            {
                for (var fixedIndex = 0; fixedIndex < fixedSettings.Count; fixedIndex++)
                    AddHistoricalLapClearanceEnvelope(envelopes, generated[firstIndex], true,
                        fixedSettings[fixedIndex], false, historicalBlocks, groupRequest, request.LapInsulationClearance,
                        "HISTORICAL-F-" + firstIndex + "-" + fixedIndex, acceptanceTolerance);
                for (var secondIndex = firstIndex + 1; secondIndex < generated.Count; secondIndex++)
                    AddHistoricalLapClearanceEnvelope(envelopes, generated[firstIndex], false,
                        generated[secondIndex], true, historicalBlocks, groupRequest, request.LapInsulationClearance,
                        "HISTORICAL-I-" + firstIndex + "-" + secondIndex, acceptanceTolerance);
            }
            return envelopes.Values.ToList();
        }

        private static IReadOnlyList<LinkageRouteSolid>
            BuildActualGeneratedMemberSolidsCore(
                LinkageRouteCandidate routeCandidate,
                bool includeOnlyOuterStackMembers,
                ICollection<LinkageRouteStraightSegmentScanReference>
                    straightSegmentScanReferences = null)
        {
            var actualGeneratedMemberSolids = new List<LinkageRouteSolid>();
            var actualGeneratedSolidIndex = 0;
            var allStackedRouteMembers = (routeCandidate?.PlanarConversion
                    ?.StackMembers ?? new List<LinkageStackedRouteMember>())
                .Where(stackedRouteMember => stackedRouteMember != null)
                .OrderBy(stackedRouteMember => stackedRouteMember.MemberIndex)
                .ToList();
            IReadOnlyList<LinkageStackedRouteMember> selectedStackedRouteMembers =
                allStackedRouteMembers;
            if (includeOnlyOuterStackMembers &&
                allStackedRouteMembers.Count > 2)
            {
                selectedStackedRouteMembers = new[]
                {
                    allStackedRouteMembers[0],
                    allStackedRouteMembers[allStackedRouteMembers.Count - 1]
                };
            }
            for (var selectedStackMemberIndex = 0;
                selectedStackMemberIndex < selectedStackedRouteMembers.Count;
                selectedStackMemberIndex++)
            {
                var stackedRouteMember = selectedStackedRouteMembers[
                    selectedStackMemberIndex];
                var actualGeneratedPlanarParts = stackedRouteMember
                    ?.PlanarParts ?? new List<PlanarBendBusbarSetting>();
                for (var actualGeneratedPlanarPartIndex = 0;
                    actualGeneratedPlanarPartIndex <
                        actualGeneratedPlanarParts.Count;
                    actualGeneratedPlanarPartIndex++)
                {
                    var actualGeneratedPlanarPart = actualGeneratedPlanarParts[
                        actualGeneratedPlanarPartIndex];
                    if (actualGeneratedPlanarPart?.PointList == null ||
                        actualGeneratedPlanarPart.PointList.Count < 2 ||
                        actualGeneratedPlanarPart.WidthVector == null ||
                        actualGeneratedPlanarPart.WidthVector.Norm <= GeometryTolerance)
                    {
                        continue;
                    }

                    var actualGeneratedWidthDirection = actualGeneratedPlanarPart
                        .WidthVector.Normalized;
                    for (var actualGeneratedSegmentIndex = 0;
                        actualGeneratedSegmentIndex <
                            actualGeneratedPlanarPart.PointList.Count - 1;
                        actualGeneratedSegmentIndex++)
                    {
                        var actualGeneratedSegmentStart = actualGeneratedPlanarPart
                            .PointList[actualGeneratedSegmentIndex];
                        var actualGeneratedSegmentEnd = actualGeneratedPlanarPart
                            .PointList[actualGeneratedSegmentIndex + 1];
                        var actualGeneratedSegmentDirection = new Vector3d(
                            actualGeneratedSegmentStart,
                            actualGeneratedSegmentEnd).Normalized;
                        var actualGeneratedThicknessDirection =
                            actualGeneratedSegmentDirection
                                .Cross(actualGeneratedWidthDirection)
                                .Normalized;
                        var actualGeneratedSegmentSolid =
                            LinkageRouteSolidGeometry.CreateSegmentSolid(
                                routeCandidate.GroupRequest.GroupKey,
                                actualGeneratedSegmentStart,
                                actualGeneratedSegmentEnd,
                                actualGeneratedWidthDirection,
                                actualGeneratedThicknessDirection,
                                actualGeneratedPlanarPart.BusBarWidth,
                                actualGeneratedPlanarPart.BusBarThickness,
                                actualGeneratedSolidIndex++,
                                routeCandidate.GroupRequest
                                    .GetClearanceGroupKey());
                        actualGeneratedMemberSolids.Add(
                            actualGeneratedSegmentSolid);
                        straightSegmentScanReferences?.Add(
                            new LinkageRouteStraightSegmentScanReference
                            {
                                ScanSolid = actualGeneratedSegmentSolid,
                                ScanSequenceIndex = selectedStackMemberIndex + 1,
                                SourceLabel = string.Format(
                                    CultureInfo.InvariantCulture,
                                    "叠排第 {0} 层第 {1} 根排",
                                    stackedRouteMember.MemberIndex + 1,
                                    actualGeneratedPlanarPartIndex + 1),
                                PlanarPartIndex =
                                    actualGeneratedPlanarPartIndex,
                                SegmentIndexWithinPlanarPart =
                                    actualGeneratedSegmentIndex,
                                SegmentCountWithinPlanarPart =
                                    actualGeneratedPlanarPart.PointList.Count - 1
                            });
                    }
                }
            }

            var routeAccessories = routeCandidate?.PlanarConversion
                ?.Accessories ?? new List<LinkageRouteAccessory>();
            for (var routeAccessoryIndex = 0;
                routeAccessoryIndex < routeAccessories.Count;
                routeAccessoryIndex++)
            {
                var routeAccessory = routeAccessories[routeAccessoryIndex];
                var accessorySetting = routeAccessory?.Setting;
                if (accessorySetting?.PointList == null ||
                    accessorySetting.PointList.Count < 2 ||
                    accessorySetting.WidthVector == null ||
                    accessorySetting.WidthVector.Norm <= GeometryTolerance)
                {
                    continue;
                }

                var accessoryWidthDirection = accessorySetting.WidthVector.Normalized;
                for (var accessorySegmentIndex = 0;
                    accessorySegmentIndex < accessorySetting.PointList.Count - 1;
                    accessorySegmentIndex++)
                {
                    var accessorySegmentStart = accessorySetting.PointList[
                        accessorySegmentIndex];
                    var accessorySegmentEnd = accessorySetting.PointList[
                        accessorySegmentIndex + 1];
                    var accessoryLengthDirection = new Vector3d(
                        accessorySegmentStart,
                        accessorySegmentEnd).Normalized;
                    var accessoryThicknessDirection = accessoryLengthDirection
                        .Cross(accessoryWidthDirection).Normalized;
                    var accessorySegmentSolid =
                        LinkageRouteSolidGeometry.CreateSegmentSolid(
                            routeCandidate.GroupRequest.GroupKey,
                            accessorySegmentStart,
                            accessorySegmentEnd,
                            accessoryWidthDirection,
                            accessoryThicknessDirection,
                            accessorySetting.BusBarWidth,
                            accessorySetting.BusBarThickness,
                            actualGeneratedSolidIndex++,
                            routeCandidate.GroupRequest
                                .GetClearanceGroupKey());
                    actualGeneratedMemberSolids.Add(accessorySegmentSolid);
                    straightSegmentScanReferences?.Add(
                        new LinkageRouteStraightSegmentScanReference
                        {
                            ScanSolid = accessorySegmentSolid,
                            ScanSequenceIndex = -routeAccessoryIndex - 1,
                            SourceLabel = string.Format(
                                CultureInfo.InvariantCulture,
                                "{0}附件第 {1} 根排",
                                routeAccessory.Kind,
                                routeAccessoryIndex + 1),
                            PlanarPartIndex = 0,
                            SegmentIndexWithinPlanarPart =
                                accessorySegmentIndex,
                            SegmentCountWithinPlanarPart =
                                accessorySetting.PointList.Count - 1
                        });
                }
            }

            return actualGeneratedMemberSolids;
        }

        private static void AddLapClearanceEnvelopeForPhysicalInterface(
            IDictionary<string, LinkageRouteSolid> destination,
            PlanarBendBusbarSetting targetSetting,
            bool preferredTargetStart,
            PlanarBendBusbarSetting neighborSetting,
            bool preferredNeighborStart,
            LinkageRouteGroupRequest groupRequest,
            double clearance,
            string interfaceKey,
            double acceptanceTolerance = GeometryTolerance,
            double requiredLapLength = 0d)
        {
            if (targetSetting == null || neighborSetting == null)
            {
                return;
            }
            // 历史逐排点列可以从柜顶指向始端。优先使用普通路径的
            // 约定端段；不存在实际接触时才枚举两端，且每个物理接口
            // 只选第一个真实接触面，不能把绝缘实体加入铜排集合。
            foreach (var targetStart in new[]
                { preferredTargetStart, !preferredTargetStart })
            foreach (var neighborStart in new[]
                { preferredNeighborStart, !preferredNeighborStart })
            {
                if (!LinkageRouteTerminalLapRegionValidator
                    .TryCreateActualLapFootprint(targetSetting,
                        targetStart, neighborSetting, neighborStart,
                        requiredLapLength > GeometryTolerance ? requiredLapLength : Math.Max(targetSetting.BusBarWidth,
                            neighborSetting.BusBarWidth), out _, tolerance: acceptanceTolerance))
                {
                    continue;
                }
                AddLapClearanceEnvelope(destination, targetSetting,
                    targetStart, neighborSetting, neighborStart,
                    groupRequest, clearance, interfaceKey, acceptanceTolerance, requiredLapLength);
                // 两根实际铜排各自背离接触面的外表面都必须留足净空。
                // 固定排或相邻排可能在接头最外层，不能仅保护生成排的
                // 表面，否则另一根排的厚度会吞掉一部分35mm外部净空。
                // 沿用同一个已验收搭接矩形，不能交换主从后重新执行
                // 非对称的满宽判据而把异宽固定排的外侧再次静默漏掉。
                AddLapClearanceEnvelope(destination, targetSetting,
                    targetStart, neighborSetting, neighborStart,
                    groupRequest, clearance, interfaceKey, acceptanceTolerance, requiredLapLength, true);
                return;
            }
        }

        private static void AddLapClearanceEnvelope(
            IDictionary<string, LinkageRouteSolid> destination,
            PlanarBendBusbarSetting targetSetting,
            bool targetUseStart,
            PlanarBendBusbarSetting neighborSetting,
            bool neighborUseStart,
            LinkageRouteGroupRequest routeGroupRequest,
            double lapInsulationClearance,
            string interfaceKey,
            double acceptanceTolerance = GeometryTolerance,
            double requiredLapLength = 0d,
            bool protectNeighborOutsideSurface = false)
        {
            if (destination == null ||
                targetSetting == null || neighborSetting == null ||
                routeGroupRequest == null)
            {
                return;
            }

            if (!LinkageRouteTerminalLapRegionValidator.TryCreateActualLapFootprint(
                    targetSetting,
                    targetUseStart,
                    neighborSetting,
                    neighborUseStart,
                    requiredLapLength > GeometryTolerance ? requiredLapLength : Math.Max(targetSetting.BusBarWidth, neighborSetting.BusBarWidth),
                    out var lapFootprint, tolerance: acceptanceTolerance))
            {
                return;
            }

            var targetBoundarySolid = CreateBoundarySolidForLapEnvelope(
                targetSetting,
                targetUseStart,
                routeGroupRequest);
            var neighborBoundarySolid = CreateBoundarySolidForLapEnvelope(
                neighborSetting,
                neighborUseStart,
                routeGroupRequest);
            if (targetBoundarySolid == null || neighborBoundarySolid == null ||
                lapFootprint?.Corners == null || lapFootprint.Corners.Count < 4)
            {
                return;
            }

            var outwardNormal = lapFootprint.PlaneNormal?.Normalized;
            if (outwardNormal == null || outwardNormal.Norm <= GeometryTolerance)
            {
                return;
            }

            var targetToNeighbor = new Vector3d(
                targetBoundarySolid.Center,
                neighborBoundarySolid.Center);
            if (targetToNeighbor.Dot(outwardNormal) > 0d)
            {
                outwardNormal = new Vector3d(
                    -outwardNormal.X,
                    -outwardNormal.Y,
                    -outwardNormal.Z);
            }
            if (protectNeighborOutsideSurface) outwardNormal *= -1d;

            var lengthStart = lapFootprint.Corners[0];
            var lengthEnd = lapFootprint.Corners[2];
            var widthStart = lapFootprint.Corners[1];
            var lapLength = lengthStart.DistanceTo(lengthEnd);
            var lapWidth = lengthStart.DistanceTo(widthStart);
            if (lapLength <= GeometryTolerance || lapWidth <= GeometryTolerance)
            {
                return;
            }

            // Corners[0] -> Corners[2] is a rectangle boundary, not its centerline.
            // Center the full footprint width before constructing the one-sided envelope.
            var lapCenterlineStart = LinkageRouteSolidGeometry.Translate(
                lengthStart, lapFootprint.WidthDirection, lapWidth / 2d);
            var lapCenterlineEnd = LinkageRouteSolidGeometry.Translate(
                lengthEnd, lapFootprint.WidthDirection, lapWidth / 2d);
            var outsideBoundarySolid = protectNeighborOutsideSurface ? neighborBoundarySolid : targetBoundarySolid;
            var outsideHalfThickness = outsideBoundarySolid.HalfThickness *
                Math.Abs(outsideBoundarySolid.ThicknessDirection.Dot(outwardNormal));
            var lapCenter = new Point3d(
                (lapCenterlineStart.X + lapCenterlineEnd.X) / 2d,
                (lapCenterlineStart.Y + lapCenterlineEnd.Y) / 2d,
                (lapCenterlineStart.Z + lapCenterlineEnd.Z) / 2d);
            // footprint位于原target中心面；邻排外侧须先投影到邻排中心面，
            // 再加邻排真实半厚和完整净空，不能从接触面或target面起算。
            var outsideCenterPlaneOffset = new Vector3d(lapCenter, outsideBoundarySolid.Center).Dot(outwardNormal);
            var deduplicationKey = string.Join(
                "|",
                interfaceKey ?? string.Empty,
                FormatPointForLapKey(lapCenter, lapFootprint.LengthDirection),
                FormatPointForLapKey(lapCenter, lapFootprint.WidthDirection),
                lapLength.ToString("F5", CultureInfo.InvariantCulture),
                lapWidth.ToString("F5", CultureInfo.InvariantCulture),
                FormatDirectionForLapKey(lapFootprint.LengthDirection),
                FormatDirectionForLapKey(lapFootprint.WidthDirection),
                FormatOutwardNormalForLapKey(outwardNormal));

            var clearanceEnvelope = LinkageRouteSolidGeometry.CreateSegmentSolid(
                routeGroupRequest.GroupKey,
                lapCenterlineStart,
                lapCenterlineEnd,
                lapFootprint.WidthDirection,
                outwardNormal,
                lapWidth,
                lapInsulationClearance,
                -1,
                routeGroupRequest.GetClearanceGroupKey(),
                LinkageRouteSolidRole.LapClearanceEnvelope,
                "LAP-CLEARANCE-" + deduplicationKey);
            clearanceEnvelope.Center = LinkageRouteSolidGeometry.Translate(
                clearanceEnvelope.Center,
                outwardNormal,
                outsideCenterPlaneOffset + outsideHalfThickness + lapInsulationClearance / 2d);
            if (destination.TryGetValue(deduplicationKey, out var existingEnvelope))
            {
                var existingOutwardProjection = new Vector3d(
                    new Point3d(0d, 0d, 0d),
                    existingEnvelope.Center).Dot(outwardNormal);
                var candidateOutwardProjection = new Vector3d(
                    new Point3d(0d, 0d, 0d),
                    clearanceEnvelope.Center).Dot(outwardNormal);
                if (candidateOutwardProjection <= existingOutwardProjection + GeometryTolerance)
                {
                    return;
                }
            }

            destination[deduplicationKey] = clearanceEnvelope;
            LinkedTrialLapInterfaces.Add(clearanceEnvelope, new LinkedTrialLapInterface
            {
                Target = targetSetting, Neighbor = neighborSetting,
                TargetStart = targetUseStart, NeighborStart = neighborUseStart
            });
        }

        private static LinkageRouteSolid CreateBoundarySolidForLapEnvelope(
            PlanarBendBusbarSetting busbarSetting,
            bool useStart,
            LinkageRouteGroupRequest routeGroupRequest)
        {
            if (busbarSetting?.PointList == null ||
                busbarSetting.PointList.Count < 2 ||
                busbarSetting.WidthVector == null ||
                busbarSetting.WidthVector.Norm <= GeometryTolerance)
            {
                return null;
            }

            var segmentIndex = useStart ? 0 : busbarSetting.PointList.Count - 2;
            var segmentStart = busbarSetting.PointList[segmentIndex];
            var segmentEnd = busbarSetting.PointList[segmentIndex + 1];
            var segmentDirection = new Vector3d(segmentStart, segmentEnd);
            if (segmentDirection.Norm <= GeometryTolerance)
            {
                return null;
            }

            var thicknessDirection = segmentDirection.Normalized
                .Cross(busbarSetting.WidthVector.Normalized);
            if (thicknessDirection.Norm <= GeometryTolerance)
            {
                return null;
            }

            return LinkageRouteSolidGeometry.CreateSegmentSolid(
                routeGroupRequest.GroupKey,
                segmentStart,
                segmentEnd,
                busbarSetting.WidthVector,
                thicknessDirection,
                busbarSetting.BusBarWidth,
                busbarSetting.BusBarThickness,
                -1,
                routeGroupRequest.GetClearanceGroupKey());
        }

        private static string FormatPointForLapKey(Point3d point)
        {
            return point == null
                ? "null"
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:F5},{1:F5},{2:F5}",
                    point.X,
                    point.Y,
                    point.Z);
        }

        private static string FormatPointForLapKey(
            Point3d point,
            Vector3d axis)
        {
            return point == null || axis == null
                ? "null"
                : point.ToVector.Dot(axis.Normalized).ToString(
                    "F5",
                    CultureInfo.InvariantCulture);
        }

        private static string FormatDirectionForLapKey(Vector3d vector)
        {
            if (vector == null || vector.Norm <= GeometryTolerance)
            {
                return "null";
            }

            var normalizedVector = vector.Normalized;
            var sign = normalizedVector.X < -GeometryTolerance ||
                (Math.Abs(normalizedVector.X) <= GeometryTolerance &&
                 (normalizedVector.Y < -GeometryTolerance ||
                  (Math.Abs(normalizedVector.Y) <= GeometryTolerance &&
                   normalizedVector.Z < 0d)))
                ? -1d
                : 1d;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:F5},{1:F5},{2:F5}",
                normalizedVector.X * sign,
                normalizedVector.Y * sign,
                normalizedVector.Z * sign);
        }

        // 两张相反外侧面是不同绝缘区域，不能沿用无向轴的正负归一化。
        // 只清除格式化负零；同一朝向的叠排描述仍由原逻辑取最外侧并去重。
        private static string FormatOutwardNormalForLapKey(Vector3d outwardNormal)
        {
            var signedNormal = outwardNormal.Normalized;
            return string.Format(CultureInfo.InvariantCulture, "{0:F5},{1:F5},{2:F5}",
                Math.Abs(signedNormal.X) <= GeometryTolerance ? 0d : signedNormal.X,
                Math.Abs(signedNormal.Y) <= GeometryTolerance ? 0d : signedNormal.Y,
                Math.Abs(signedNormal.Z) <= GeometryTolerance ? 0d : signedNormal.Z);
        }

        private static string FormatPointForLapKey(Vector3d vector)
        {
            return vector == null
                ? "null"
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:F5},{1:F5},{2:F5}",
                    vector.X,
                    vector.Y,
                    vector.Z);
        }

        private static IReadOnlyList<LinkageRouteSolid>
            BuildActualGeneratedMemberSweepSolids(
                LinkageRouteCandidate routeCandidate,
                LinkageMultiGroupRouteRequest routeRequest,
                IEnumerable<LinkageRouteSolid> actualGeneratedMemberSolids)
        {
            var actualGeneratedMemberSweepSolids =
                (actualGeneratedMemberSolids ??
                    Enumerable.Empty<LinkageRouteSolid>())
                .ToList();
            var actualGeneratedSweepSolidIndex =
                actualGeneratedMemberSweepSolids.Count;
            var actualStackedRouteMembers = routeCandidate?.PlanarConversion
                    ?.StackMembers ?? new List<LinkageStackedRouteMember>();
            foreach (var stackedRouteMember in actualStackedRouteMembers)
            {
                foreach (var actualGeneratedPlanarPart in stackedRouteMember
                    ?.PlanarParts ?? new List<PlanarBendBusbarSetting>())
                {
                    if (actualGeneratedPlanarPart?.PointList == null ||
                        actualGeneratedPlanarPart.PointList.Count < 3 ||
                        actualGeneratedPlanarPart.WidthVector == null ||
                        actualGeneratedPlanarPart.WidthVector.Norm <= GeometryTolerance)
                    {
                        continue;
                    }

                    var actualGeneratedWidthDirection = actualGeneratedPlanarPart
                        .WidthVector.Normalized;
                    for (var actualGeneratedBendPointIndex = 1;
                        actualGeneratedBendPointIndex <
                            actualGeneratedPlanarPart.PointList.Count - 1;
                        actualGeneratedBendPointIndex++)
                    {
                        var actualGeneratedIncomingDirection = new Vector3d(
                            actualGeneratedPlanarPart.PointList[
                                actualGeneratedBendPointIndex - 1],
                            actualGeneratedPlanarPart.PointList[
                                actualGeneratedBendPointIndex]).Normalized;
                        var actualGeneratedOutgoingDirection = new Vector3d(
                            actualGeneratedPlanarPart.PointList[
                                actualGeneratedBendPointIndex],
                            actualGeneratedPlanarPart.PointList[
                                actualGeneratedBendPointIndex + 1]).Normalized;
                        var actualGeneratedBendSweepHalfSpan =
                            actualGeneratedPlanarPart.BusBarThickness / 2d +
                            Math.Max(actualGeneratedPlanarPart.BarBendingRadius,
                                actualGeneratedPlanarPart.BendAllowance);
                        var actualGeneratedBendSweepSolid =
                            LinkageRouteSolidGeometry.CreateBendSweepSolid(
                                routeCandidate.GroupRequest.GroupKey,
                                actualGeneratedPlanarPart.PointList[
                                    actualGeneratedBendPointIndex],
                                actualGeneratedIncomingDirection,
                                actualGeneratedOutgoingDirection,
                                actualGeneratedWidthDirection,
                                actualGeneratedBendSweepHalfSpan,
                                actualGeneratedPlanarPart.BusBarWidth,
                                actualGeneratedSweepSolidIndex++,
                                routeCandidate.GroupRequest
                                    .GetClearanceGroupKey());
                        actualGeneratedMemberSweepSolids.Add(
                            actualGeneratedBendSweepSolid);
                    }
                }
            }

            return actualGeneratedMemberSweepSolids;
        }

        private static bool TryValidateNoStaticPositiveVolumeIntersections(
            IEnumerable<LinkageRouteSolid> actualGeneratedMemberSolids,
            IEnumerable<LinkageRouteSolid> staticSolids,
            out string failureReason)
        {
            failureReason = null;
            foreach (var actualGeneratedMemberSolid in actualGeneratedMemberSolids ??
                Enumerable.Empty<LinkageRouteSolid>())
            {
                foreach (var staticSolid in staticSolids ??
                    Enumerable.Empty<LinkageRouteSolid>())
                {
                    if (actualGeneratedMemberSolid == null ||
                        staticSolid == null)
                    {
                        continue;
                    }

                    var isSameGroupStaticSolid =
                        !string.IsNullOrWhiteSpace(
                            actualGeneratedMemberSolid.GroupKey) &&
                        !string.IsNullOrWhiteSpace(staticSolid.GroupKey) &&
                        string.Equals(
                            actualGeneratedMemberSolid.GroupKey,
                            staticSolid.GroupKey,
                            StringComparison.OrdinalIgnoreCase);
                    // 静态避让视图和本组实际输出是同一柜顶附件，不能重复算成
                    // 两个互相穿透的实体。本组全部生成排/桥/垫块仍经过实际自交全检。
                    if (isSameGroupStaticSolid && staticSolid.IsPreparedCabinetAccessory)
                        continue;
                    var isSameClearanceGroupStaticSolid =
                        !string.IsNullOrWhiteSpace(
                            actualGeneratedMemberSolid.ClearanceGroupKey) &&
                        !string.IsNullOrWhiteSpace(
                            staticSolid.ClearanceGroupKey) &&
                        string.Equals(
                            actualGeneratedMemberSolid.ClearanceGroupKey,
                            staticSolid.ClearanceGroupKey,
                            StringComparison.OrdinalIgnoreCase);
                    var isInputObstacle = staticSolid.SolidRole ==
                        LinkageRouteSolidRole.InputObstacle;
                    if ((!staticSolid.IsForbiddenTerminalRegion &&
                         !isInputObstacle &&
                         !isSameGroupStaticSolid &&
                         !isSameClearanceGroupStaticSolid) ||
                        !LinkageRouteSolidDistance.HasPositiveVolumeIntersection(
                            actualGeneratedMemberSolid,
                            staticSolid,
                            LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
                    {
                        continue;
                    }

                    failureReason = staticSolid.IsForbiddenTerminalRegion
                        ? "路径进入始端或柜顶不可伸出侧整体禁区。"
                        : isInputObstacle
                            ? "路径与输入障碍物发生正体积相交：" +
                              (staticSolid.SolidSourceKey ?? "<未命名障碍物>") + "。"
                            : (isSameGroupStaticSolid
                            ? "路径与同组始端或柜顶固定排发生正体积相交；"
                            : "路径与同相其他逻辑组固定排发生正体积相交；") +
                            "规定搭接只允许厚面零体积接触。生成实体=" +
                            FormatRouteSolidDiagnostic(
                                actualGeneratedMemberSolid) +
                            "；固定实体=" +
                            FormatRouteSolidDiagnostic(staticSolid);
                    return false;
                }
            }

            return true;
        }

        private static string FormatRouteSolidDiagnostic(
            LinkageRouteSolid routeSolid)
        {
            if (routeSolid?.Center == null)
            {
                return "<无>";
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "中心({0:F3},{1:F3},{2:F3})，半长宽厚=({3:F3},{4:F3},{5:F3})，" +
                "长向={6}，宽向={7}，厚向={8}，段={9}",
                routeSolid.Center.X,
                routeSolid.Center.Y,
                routeSolid.Center.Z,
                routeSolid.HalfLength,
                routeSolid.HalfWidth,
                routeSolid.HalfThickness,
                FormatAxisDirection(routeSolid.LengthDirection),
                FormatAxisDirection(routeSolid.WidthDirection),
                FormatAxisDirection(routeSolid.ThicknessDirection),
                routeSolid.SegmentIndex);
        }

        private static string FormatPlanarPartDiagnostic(
            IEnumerable<PlanarBendBusbarSetting> planarParts)
        {
            var planarPartList = (planarParts ??
                    Enumerable.Empty<PlanarBendBusbarSetting>())
                .Where(planarPart => planarPart?.PointList != null)
                .ToList();
            if (planarPartList.Count == 0)
            {
                return string.Empty;
            }

            return "；实际拆排=" + string.Join("/", planarPartList.Select(
                (planarPart, planarPartIndex) =>
                    "P" + (planarPartIndex + 1) + "[" +
                    string.Join("→", planarPart.PointList.Select(point => string.Format(
                        CultureInfo.InvariantCulture,
                        "({0:F3},{1:F3},{2:F3})",
                        point.X,
                        point.Y,
                        point.Z))) + "]"));
        }

        private static bool TryValidateNoAcuteAngles(
            LinkageRouteCenterlineConversionResult planarConversion,
            out string acuteAngleFailureReason,
            out double acuteAngleViolationDepth)
        {
            acuteAngleFailureReason = null;
            acuteAngleViolationDepth = 0d;
            for (var centerlinePartIndex = 0;
                centerlinePartIndex < planarConversion.CenterlineParts.Count;
                centerlinePartIndex++)
            {
                if (TryValidateNoAcuteAngles(
                        planarConversion.CenterlineParts[centerlinePartIndex],
                        "中心基准第 " + (centerlinePartIndex + 1) + " 根生成排",
                        out acuteAngleFailureReason,
                        out acuteAngleViolationDepth))
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        private static bool TryValidateNoSelfIntersections(
            LinkageRouteCenterlineConversionResult planarConversion,
            out string selfIntersectionFailureReason)
        {
            selfIntersectionFailureReason = null;
            for (var planarPartIndex = 0;
                planarPartIndex < planarConversion.CenterlineParts.Count;
                planarPartIndex++)
            {
                var planarBusbarSetting = planarConversion.CenterlineParts[planarPartIndex];
                if (LinkageRouteBendGeometry.HasNoSelfIntersection(
                        planarBusbarSetting.PointList,
                        out var firstIntersectingSegmentIndex,
                        out var secondIntersectingSegmentIndex))
                {
                    continue;
                }

                selfIntersectionFailureReason = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "第 {0} 根生成排形成回路：第 {1} 段与第 {2} 段相交。",
                    planarPartIndex + 1,
                    firstIntersectingSegmentIndex + 1,
                    secondIntersectingSegmentIndex + 1);
                return false;
            }

            return true;
        }

        private static bool TryValidateNoActualMemberCenterlineSelfIntersections(
            LinkageRouteCenterlineConversionResult planarConversion,
            out string selfIntersectionFailureReason)
        {
            selfIntersectionFailureReason = null;
            foreach (var actualStackMember in planarConversion?.StackMembers ??
                new List<LinkageStackedRouteMember>())
            {
                var actualMemberPlanarParts = actualStackMember?.PlanarParts ??
                    new List<PlanarBendBusbarSetting>();
                for (var actualMemberPlanarPartIndex = 0;
                    actualMemberPlanarPartIndex < actualMemberPlanarParts.Count;
                    actualMemberPlanarPartIndex++)
                {
                    var actualMemberPlanarPart = actualMemberPlanarParts[
                        actualMemberPlanarPartIndex];
                    if (actualMemberPlanarPart?.PointList == null ||
                        LinkageRouteBendGeometry.HasNoSelfIntersection(
                            actualMemberPlanarPart.PointList,
                            out var firstActualIntersectingSegmentIndex,
                            out var secondActualIntersectingSegmentIndex))
                    {
                        continue;
                    }

                    selfIntersectionFailureReason = string.Format(
                        CultureInfo.InvariantCulture,
                        "端头压紧后叠排第 {0} 层第 {1} 根排形成回路：" +
                        "第 {2} 段与第 {3} 段相交。",
                        actualStackMember.MemberIndex + 1,
                        actualMemberPlanarPartIndex + 1,
                        firstActualIntersectingSegmentIndex + 1,
                        secondActualIntersectingSegmentIndex + 1);
                    return false;
                }
            }

            var actualRouteAccessories = planarConversion?.Accessories ??
                new List<LinkageRouteAccessory>();
            for (var actualRouteAccessoryIndex = 0;
                actualRouteAccessoryIndex < actualRouteAccessories.Count;
                actualRouteAccessoryIndex++)
            {
                var actualRouteAccessorySetting = actualRouteAccessories[
                    actualRouteAccessoryIndex]?.Setting;
                if (actualRouteAccessorySetting?.PointList == null ||
                    LinkageRouteBendGeometry.HasNoSelfIntersection(
                        actualRouteAccessorySetting.PointList,
                        out var firstAccessoryIntersectingSegmentIndex,
                        out var secondAccessoryIntersectingSegmentIndex))
                {
                    continue;
                }

                selfIntersectionFailureReason = string.Format(
                    CultureInfo.InvariantCulture,
                    "端头压紧后第 {0} 个附件形成回路：第 {1} 段与第 {2} 段相交。",
                    actualRouteAccessoryIndex + 1,
                    firstAccessoryIntersectingSegmentIndex + 1,
                    secondAccessoryIntersectingSegmentIndex + 1);
                return false;
            }

            return true;
        }

        private static bool TryValidateNoAcuteAngles(
            PlanarBendBusbarSetting planarBusbarSetting,
            string busbarLabel,
            out string acuteAngleFailureReason,
            out double acuteAngleViolationDepth)
        {
            acuteAngleFailureReason = null;
            acuteAngleViolationDepth = 0d;
            if (LinkageRouteBendGeometry.HasNoAcuteAngle(
                    planarBusbarSetting?.PointList,
                    out var acuteCornerPointIndex,
                    out var acuteCornerAngleDegrees))
            {
                return true;
            }

            acuteAngleViolationDepth = Math.Max(
                0d,
                LinkageRouteBendGeometry.MinimumAllowedCornerAngleDegrees -
                    acuteCornerAngleDegrees);
            acuteAngleFailureReason = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0}的第 {1} 个折弯为锐角（{2:F3}°），必须不小于 {3:F2}°。",
                busbarLabel,
                acuteCornerPointIndex,
                acuteCornerAngleDegrees,
                LinkageRouteBendGeometry.MinimumAllowedCornerAngleDegrees);
            return false;
        }

        private static int CountPerpendicularLapInterfaces(
            LinkageRouteCandidate routeCandidate,
            LinkageRouteGroupRequest routeGroupRequest,
            double requiredBusbarWidth,
            bool allowPartialStartPerpendicularLap)
        {
            var perpendicularLapInterfaceCount = 0;
            foreach (var stackedRouteMember in routeCandidate.PlanarConversion.StackMembers)
            {
                if (stackedRouteMember.PlanarParts.Count == 0)
                {
                    continue;
                }

                var firstGeneratedSetting = stackedRouteMember.PlanarParts[0];
                foreach (var startTerminalMember in routeGroupRequest.StartTerminal.Members)
                {
                    perpendicularLapInterfaceCount += CountPerpendicularLapPair(
                        startTerminalMember?.FixedBusbar,
                        true,
                        firstGeneratedSetting,
                        true,
                        requiredBusbarWidth,
                        false,
                        allowPartialStartPerpendicularLap);
                }

                for (var planarPartIndex = 0;
                    planarPartIndex < stackedRouteMember.PlanarParts.Count - 1;
                    planarPartIndex++)
                {
                    perpendicularLapInterfaceCount += CountPerpendicularLapPair(
                        stackedRouteMember.PlanarParts[planarPartIndex],
                        false,
                        stackedRouteMember.PlanarParts[planarPartIndex + 1],
                        true,
                        requiredBusbarWidth);
                }

                var lastGeneratedSetting = stackedRouteMember.PlanarParts[
                    stackedRouteMember.PlanarParts.Count - 1];
                foreach (var endTerminalMember in routeGroupRequest.EndTerminal.Members)
                {
                    perpendicularLapInterfaceCount += CountPerpendicularLapPair(
                        lastGeneratedSetting,
                        false,
                        endTerminalMember?.FixedBusbar,
                        false,
                        requiredBusbarWidth);
                }
            }

            return perpendicularLapInterfaceCount;
        }

        private static int CountPerpendicularLapPair(
            PlanarBendBusbarSetting firstBusbarSetting,
            bool firstUseStart,
            PlanarBendBusbarSetting secondBusbarSetting,
            bool secondUseStart,
            double requiredBusbarWidth,
            bool allowFirstOneSidedPerpendicularLap = false,
            bool allowSecondOneSidedPerpendicularLap = false)
        {
            var firstLapSegment = CreateBoundarySegment(firstBusbarSetting, firstUseStart);
            var secondLapSegment = CreateBoundarySegment(secondBusbarSetting, secondUseStart);
            if (firstLapSegment == null || secondLapSegment == null)
            {
                return 0;
            }

            var lapEvaluation = allowFirstOneSidedPerpendicularLap ||
                allowSecondOneSidedPerpendicularLap
                    ? EvaluateAcceptedOneSidedLap(
                            firstLapSegment,
                            secondLapSegment,
                            requiredBusbarWidth,
                            allowFirstOneSidedPerpendicularLap,
                            allowSecondOneSidedPerpendicularLap)
                    : EvaluateAcceptedLap(
                        firstLapSegment,
                        secondLapSegment,
                        requiredBusbarWidth);
            return lapEvaluation.IsValid && lapEvaluation.IsPerpendicular ? 1 : 0;
        }

        private static LinkageRouteCandidate CreateCandidateShell(
            LinkageRouteGroupRequest routeGroupRequest,
            IEnumerable<Point3d> axisSkeleton,
            IEnumerable<Point3d> manufacturableCenterline,
            bool isConvertedShape,
            string topologySignature,
            LinkageRouteTraversalDirection traversalDirection)
        {
            var copiedAxisSkeleton = (axisSkeleton ?? Enumerable.Empty<Point3d>())
                .Where(point => point != null)
                .Select(CopyPoint)
                .ToList();
            var copiedManufacturableCenterline = (manufacturableCenterline ??
                    Enumerable.Empty<Point3d>())
                .Where(point => point != null)
                .Select(CopyPoint)
                .ToList();
            EvaluatePhysicalDirection(
                routeGroupRequest,
                copiedManufacturableCenterline,
                out var physicalOpposingTerminalSegmentCount,
                out var physicalReverseTravelLength);
            return new LinkageRouteCandidate
            {
                GroupRequest = routeGroupRequest,
                AxisSkeleton = copiedAxisSkeleton,
                ManufacturableCenterline = copiedManufacturableCenterline,
                IsConvertedShape = isConvertedShape,
                TopologySignature = topologySignature,
                TraversalDirection = traversalDirection,
                PhysicalOpposingTerminalSegmentCount =
                    physicalOpposingTerminalSegmentCount,
                PhysicalReverseTravelLength = physicalReverseTravelLength
            };
        }

        private static void EvaluatePhysicalDirection(
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<Point3d> physicalStartToCabinetCenterline,
            out int physicalOpposingTerminalSegmentCount,
            out double physicalReverseTravelLength)
        {
            physicalOpposingTerminalSegmentCount = 0;
            physicalReverseTravelLength = 0d;
            var physicalStartAnchor = routeGroupRequest?.StartTerminal?.RouteAnchor;
            var physicalCabinetAnchor = routeGroupRequest?.EndTerminal?.RouteAnchor;
            if (physicalStartAnchor == null || physicalCabinetAnchor == null ||
                physicalStartToCabinetCenterline == null ||
                physicalStartToCabinetCenterline.Count < 2)
            {
                return;
            }

            var physicalConnectionDeltaX =
                physicalCabinetAnchor.X - physicalStartAnchor.X;
            var physicalConnectionDeltaY =
                physicalCabinetAnchor.Y - physicalStartAnchor.Y;
            var physicalConnectionDeltaZ =
                physicalCabinetAnchor.Z - physicalStartAnchor.Z;
            for (var centerlineSegmentIndex = 0;
                centerlineSegmentIndex < physicalStartToCabinetCenterline.Count - 1;
                centerlineSegmentIndex++)
            {
                var currentCenterlinePoint =
                    physicalStartToCabinetCenterline[centerlineSegmentIndex];
                var nextCenterlinePoint =
                    physicalStartToCabinetCenterline[centerlineSegmentIndex + 1];
                var segmentDeltaX = nextCenterlinePoint.X - currentCenterlinePoint.X;
                var segmentDeltaY = nextCenterlinePoint.Y - currentCenterlinePoint.Y;
                var segmentDeltaZ = nextCenterlinePoint.Z - currentCenterlinePoint.Z;
                var reverseTravelForCurrentSegment = 0d;
                if (IsOpposingPhysicalDirectionComponent(
                        segmentDeltaX,
                        physicalConnectionDeltaX))
                {
                    reverseTravelForCurrentSegment += Math.Abs(segmentDeltaX);
                }

                if (IsOpposingPhysicalDirectionComponent(
                        segmentDeltaY,
                        physicalConnectionDeltaY))
                {
                    reverseTravelForCurrentSegment += Math.Abs(segmentDeltaY);
                }

                if (IsOpposingPhysicalDirectionComponent(
                        segmentDeltaZ,
                        physicalConnectionDeltaZ))
                {
                    reverseTravelForCurrentSegment += Math.Abs(segmentDeltaZ);
                }

                physicalReverseTravelLength += reverseTravelForCurrentSegment;
                var isTerminalSegment = centerlineSegmentIndex == 0 ||
                    centerlineSegmentIndex ==
                        physicalStartToCabinetCenterline.Count - 2;
                if (isTerminalSegment &&
                    reverseTravelForCurrentSegment > GeometryTolerance)
                {
                    physicalOpposingTerminalSegmentCount++;
                }
            }
        }

        private static bool IsOpposingPhysicalDirectionComponent(
            double centerlineSegmentDelta,
            double physicalConnectionDelta)
        {
            return Math.Abs(centerlineSegmentDelta) > GeometryTolerance &&
                Math.Abs(physicalConnectionDelta) > GeometryTolerance &&
                centerlineSegmentDelta * physicalConnectionDelta < 0d;
        }

        private static bool BuildScanSolids(
            LinkageRouteCandidate routeCandidate,
            LinkageMultiGroupRouteRequest routeRequest,
            out string solidSelfIntersectionFailureReason)
        {
            solidSelfIntersectionFailureReason = null;
            var routeGroupRequest = routeCandidate.GroupRequest;
            var busbarWidth = routeGroupRequest.GetBusbarWidth(routeRequest);
            var busbarThickness = routeGroupRequest.GetBusbarThickness(routeRequest);
            var stackEnvelopeThickness = LinkageRouteStackGeometry.GetStackEnvelopeThickness(
                busbarThickness,
                routeGroupRequest.GetStackCount(routeRequest),
                routeGroupRequest.StackMiddleSpace);
            var stackBendAllowance = LinkageRouteStackGeometry.GetStackBendAllowance(
                busbarThickness,
                routeGroupRequest.GetStackCount(routeRequest),
                routeGroupRequest.StackMiddleSpace,
                routeRequest.GetEffectiveBendAllowance());
            var scanSolidIndex = 0;
            var straightSegmentScanReferences =
                new List<LinkageRouteStraightSegmentScanReference>();
            for (var planarCenterlinePartIndex = 0;
                planarCenterlinePartIndex <
                    routeCandidate.PlanarConversion.CenterlineParts.Count;
                planarCenterlinePartIndex++)
            {
                var planarCenterlinePart = routeCandidate.PlanarConversion
                    .CenterlineParts[planarCenterlinePartIndex];
                var normalizedWidthDirection = planarCenterlinePart.WidthVector.Normalized;
                var planarPartSegmentCount = planarCenterlinePart.PointList.Count - 1;
                for (var partSegmentIndex = 0;
                    partSegmentIndex < planarCenterlinePart.PointList.Count - 1;
                    partSegmentIndex++)
                {
                    var segmentStart = planarCenterlinePart.PointList[partSegmentIndex];
                    var segmentEnd = planarCenterlinePart.PointList[partSegmentIndex + 1];
                    var segmentDirection = new Vector3d(segmentStart, segmentEnd).Normalized;
                    var localThicknessDirection = segmentDirection
                        .Cross(normalizedWidthDirection).Normalized;
                    var straightSegmentScanSolid = LinkageRouteSolidGeometry.CreateSegmentSolid(
                        routeGroupRequest.GroupKey,
                        segmentStart,
                        segmentEnd,
                        normalizedWidthDirection,
                        localThicknessDirection,
                        busbarWidth,
                        stackEnvelopeThickness,
                        scanSolidIndex++,
                        routeGroupRequest.GetClearanceGroupKey());
                    routeCandidate.ScanSolids.Add(straightSegmentScanSolid);
                    straightSegmentScanReferences.Add(
                        new LinkageRouteStraightSegmentScanReference
                        {
                            ScanSolid = straightSegmentScanSolid,
                            ScanSequenceIndex = 0,
                            PlanarPartIndex = planarCenterlinePartIndex,
                            SegmentIndexWithinPlanarPart = partSegmentIndex,
                            SegmentCountWithinPlanarPart = planarPartSegmentCount
                        });

                    if (partSegmentIndex >= planarCenterlinePart.PointList.Count - 2)
                    {
                        continue;
                    }

                    var outgoingDirection = new Vector3d(
                        planarCenterlinePart.PointList[partSegmentIndex + 1],
                        planarCenterlinePart.PointList[partSegmentIndex + 2]).Normalized;
                    routeCandidate.ScanSolids.Add(LinkageRouteSolidGeometry.CreateBendSweepSolid(
                        routeGroupRequest.GroupKey,
                        planarCenterlinePart.PointList[partSegmentIndex + 1],
                        segmentDirection,
                        outgoingDirection,
                        normalizedWidthDirection,
                        stackBendAllowance,
                        busbarWidth,
                        scanSolidIndex++,
                        routeGroupRequest.GetClearanceGroupKey()));
                }
            }

            if (routeRequest.ShouldUseSpacerlessTerminalLap(true) ||
                routeRequest.ShouldUseSpacerlessTerminalLap(false))
            {
                // 无垫块压紧及双始端桥排会使端段偏离旧对称包络；实际成员
                // 展开完成后再用真实直段复核，避免这里误拒或漏检。
                return true;
            }

            return TryValidateNoPositiveVolumeSelfIntersections(
                straightSegmentScanReferences,
                out solidSelfIntersectionFailureReason);
        }

        private static bool TryValidateNoPositiveVolumeSelfIntersections(
            IList<LinkageRouteStraightSegmentScanReference> straightSegmentScanReferences,
            out string solidSelfIntersectionFailureReason,
            bool compareAcrossScanSequences = true)
        {
            solidSelfIntersectionFailureReason = null;
            for (var firstScanReferenceIndex = 0;
                firstScanReferenceIndex < straightSegmentScanReferences.Count;
                firstScanReferenceIndex++)
            {
                var firstStraightSegmentReference =
                    straightSegmentScanReferences[firstScanReferenceIndex];
                for (var secondScanReferenceIndex = firstScanReferenceIndex + 1;
                    secondScanReferenceIndex < straightSegmentScanReferences.Count;
                    secondScanReferenceIndex++)
                {
                    var secondStraightSegmentReference =
                        straightSegmentScanReferences[secondScanReferenceIndex];
                    if (!compareAcrossScanSequences &&
                        firstStraightSegmentReference.ScanSequenceIndex !=
                            secondStraightSegmentReference.ScanSequenceIndex)
                    {
                        // 实际叠排每层及每个桥排附件都是独立铜排。这里验证的
                        // 是单根铜排自身非相邻段穿插；不同铜排之间的层位连续
                        // 关系由叠排展开和无垫块端部层位校验负责。
                        continue;
                    }

                    if (AreExpectedConnectedSegments(
                            firstStraightSegmentReference,
                            secondStraightSegmentReference))
                    {
                        continue;
                    }

                    if (!LinkageRouteSolidDistance.HasPositiveVolumeIntersection(
                            firstStraightSegmentReference.ScanSolid,
                            secondStraightSegmentReference.ScanSolid,
                            LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
                    {
                        continue;
                    }

                    solidSelfIntersectionFailureReason = string.Format(
                        CultureInfo.InvariantCulture,
                        "同相生成排扫描长方体发生正体积自相交：{0}第 {1} 段" +
                        "（中心={4}，半长宽厚={5}）与{2}第 {3} 段" +
                        "（中心={6}，半长宽厚={7}）重叠。",
                        firstStraightSegmentReference.SourceLabel ??
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "第 {0} 根排",
                                firstStraightSegmentReference.PlanarPartIndex + 1),
                        firstStraightSegmentReference.SegmentIndexWithinPlanarPart + 1,
                        secondStraightSegmentReference.SourceLabel ??
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "第 {0} 根排",
                                secondStraightSegmentReference.PlanarPartIndex + 1),
                        secondStraightSegmentReference.SegmentIndexWithinPlanarPart + 1,
                        FormatPoint(firstStraightSegmentReference.ScanSolid.Center),
                        FormatSolidHalfDimensions(
                            firstStraightSegmentReference.ScanSolid),
                        FormatPoint(secondStraightSegmentReference.ScanSolid.Center),
                        FormatSolidHalfDimensions(
                            secondStraightSegmentReference.ScanSolid));
                    return false;
                }
            }

            return true;
        }

        private static bool AreExpectedConnectedSegments(
            LinkageRouteStraightSegmentScanReference firstStraightSegmentReference,
            LinkageRouteStraightSegmentScanReference secondStraightSegmentReference)
        {
            if (firstStraightSegmentReference.ScanSequenceIndex !=
                secondStraightSegmentReference.ScanSequenceIndex)
            {
                return false;
            }

            if (firstStraightSegmentReference.PlanarPartIndex ==
                secondStraightSegmentReference.PlanarPartIndex)
            {
                return Math.Abs(
                    firstStraightSegmentReference.SegmentIndexWithinPlanarPart -
                    secondStraightSegmentReference.SegmentIndexWithinPlanarPart) == 1;
            }

            return secondStraightSegmentReference.PlanarPartIndex ==
                    firstStraightSegmentReference.PlanarPartIndex + 1 &&
                firstStraightSegmentReference.SegmentIndexWithinPlanarPart ==
                    firstStraightSegmentReference.SegmentCountWithinPlanarPart - 1 &&
                secondStraightSegmentReference.SegmentIndexWithinPlanarPart == 0;
        }

        internal static bool ValidateSingleMemberFinalTerminalLapCoverage(LinkageRouteCenterlineConversionResult conversion,
            LinkageMultiGroupRouteRequest request, LinkageRouteGroupRequest group, out string failureReason)
        {
            failureReason = null;
            if (conversion.StackMembers.Count != 1 || group.StartTerminal.Members.Count != 1 || group.EndTerminal.Members.Count != 1)
                return true; // 多层关系仍由现有实体接触/层位终检处理，不猜恢复协议未携带的夹层信息。
            var startPlacement = conversion.StartStackPlacement;
            var endPlacement = conversion.EndStackPlacement;
            try
            {
                // 单固定/单生成身份唯一；只是补足内部检查参数，覆盖仍从实际实体重新测量。
                conversion.StartStackPlacement = new LinkageRouteStackTerminalPlacement {
                    Contacts = new List<LinkageRouteStackTerminalContact> { new LinkageRouteStackTerminalContact { FixedMemberIndex=0,GeneratedMemberIndex=0 } } };
                conversion.EndStackPlacement = new LinkageRouteStackTerminalPlacement {
                    Contacts = new List<LinkageRouteStackTerminalContact> { new LinkageRouteStackTerminalContact { FixedMemberIndex=0,GeneratedMemberIndex=0 } } };
                return ValidateTerminalLapCoverage(new LinkageRouteCandidate { PlanarConversion=conversion },request,group,out failureReason);
            }
            finally { conversion.StartStackPlacement=startPlacement;conversion.EndStackPlacement=endPlacement; }
        }

        private static bool ValidateTerminalLapCoverage(
            LinkageRouteCandidate routeCandidate,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            out string failureReason)
        {
            failureReason = null;
            if (routeGroupRequest.StartTerminal.Members.Count == 0 ||
                routeGroupRequest.EndTerminal.Members.Count == 0)
            {
                failureReason = "始端或柜顶没有固定排成员。";
                return false;
            }

            var startLapBusbarWidth = routeGroupRequest
                .GetStartTerminalRequiredLapLength(routeRequest);
            var cabinetTopLapBusbarWidth = routeGroupRequest
                .GetCabinetTopLapBusbarWidth(routeRequest);
            if (!HasFullCoverageCoreWithAccessories(
                    routeGroupRequest.StartTerminal,
                    routeCandidate.PlanarConversion.StackMembers,
                    routeCandidate.PlanarConversion.StartStackPlacement,
                    true,
                    startLapBusbarWidth,
                    routeRequest.ShouldUseSpacerlessTerminalLap(true)
                        ? 0d
                        : routeGroupRequest.StackStartSpace,
                    "始端",
                    routeRequest.AllowDoubleStartMiddleLap &&
                        routeGroupRequest.StartTerminal.Members.Count >= 2,
                    routeRequest.AllowPartialStartPerpendicularLap,
                    routeCandidate.PlanarConversion.Accessories,
                    out failureReason,
                    routeRequest.EnableSingleStartConnectionBar))
            {
                return false;
            }

            return HasFullCoverageWithAccessories(
                    routeGroupRequest.EndTerminal,
                    routeCandidate.PlanarConversion.StackMembers,
                    routeCandidate.PlanarConversion.EndStackPlacement,
                    false,
                    cabinetTopLapBusbarWidth,
                    routeRequest.ShouldUseSpacerlessTerminalLap(false)
                        ? 0d
                        : routeGroupRequest.StackEndSpace,
                    "柜顶",
                    routeCandidate.PlanarConversion.Accessories,
                    out failureReason);
        }

        private static LinkageStartPerpendicularLapVariant
            ClassifyStartPerpendicularLapVariant(
            LinkageRouteCandidate routeCandidate,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest)
        {
            // 关闭短搭接时不做第二次覆盖校验，保持原候选路径和计算开销。
            if (routeRequest?.AllowPartialStartPerpendicularLap != true ||
                !HasDeclaredStartPerpendicularLapContact(
                    routeCandidate,
                    routeGroupRequest))
            {
                return LinkageStartPerpendicularLapVariant.None;
            }

            var startLapBusbarWidth = routeGroupRequest
                .GetStartLapBusbarWidth(routeRequest);
            var satisfiesOrdinaryFullLap = HasFullCoverageCoreWithAccessories(
                routeGroupRequest.StartTerminal,
                routeCandidate.PlanarConversion.StackMembers,
                routeCandidate.PlanarConversion.StartStackPlacement,
                true,
                startLapBusbarWidth,
                routeRequest.ShouldUseSpacerlessTerminalLap(true)
                    ? 0d
                    : routeGroupRequest.StackStartSpace,
                "始端",
                routeRequest.AllowDoubleStartMiddleLap &&
                        routeGroupRequest.StartTerminal.Members.Count >= 2,
                false,
                routeCandidate.PlanarConversion.Accessories,
                out _);
            return satisfiesOrdinaryFullLap
                ? LinkageStartPerpendicularLapVariant.Full
                : LinkageStartPerpendicularLapVariant.Partial;
        }

        private static bool HasDeclaredStartPerpendicularLapContact(
            LinkageRouteCandidate routeCandidate,
            LinkageRouteGroupRequest routeGroupRequest)
        {
            var generatedMembers = routeCandidate?.PlanarConversion
                ?.StackMembers;
            var fixedMembers = routeGroupRequest?.StartTerminal?.Members;
            var startTerminalContacts = routeCandidate?.PlanarConversion
                ?.StartStackPlacement?.Contacts;
            if (generatedMembers == null || fixedMembers == null ||
                startTerminalContacts == null)
            {
                return false;
            }

            foreach (var startTerminalContact in startTerminalContacts)
            {
                if (startTerminalContact.FixedMemberIndex < 0 ||
                    startTerminalContact.FixedMemberIndex >= fixedMembers.Count ||
                    startTerminalContact.GeneratedMemberIndex < 0 ||
                    startTerminalContact.GeneratedMemberIndex >=
                        generatedMembers.Count)
                {
                    continue;
                }

                var fixedStartSegment = CreateBoundarySegment(
                    fixedMembers[startTerminalContact.FixedMemberIndex]
                        ?.FixedBusbar,
                    true);
                var generatedStartSegment = CreateBoundarySegment(
                    generatedMembers[startTerminalContact.GeneratedMemberIndex]
                        ?.PlanarParts?.FirstOrDefault(),
                    true);
                if (fixedStartSegment != null &&
                    generatedStartSegment != null &&
                    AreSegmentsPerpendicular(
                        fixedStartSegment,
                        generatedStartSegment))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ValidateSingleStartLapPosition(
            LinkageRouteCandidate routeCandidate,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            out string failureReason)
        {
            failureReason = null;
            var singleMemberStartTerminal = routeGroupRequest?.StartTerminal;
            if (singleMemberStartTerminal?.Members?.Count != 1)
            {
                return true;
            }

            var requireHorizontalWidthCenter =
                routeRequest?.RequireSingleStartMiddleLap == true;
            var validatePerpendicularAllowedEndpoint =
                singleMemberStartTerminal.PreferredPerpendicularLapReferencePoints ==
                    null ||
                !singleMemberStartTerminal
                    .PreferredPerpendicularLapReferencePoints
                    .Any(referencePoint => referencePoint != null);
            if (!requireHorizontalWidthCenter &&
                !validatePerpendicularAllowedEndpoint)
            {
                return true;
            }

            var generatedBusbarWidth = routeGroupRequest.GetBusbarWidth(
                routeRequest);
            if (!LinkageRouteTerminalPoseFactory.TryResolveSingleMemberLapBoundary(
                    singleMemberStartTerminal,
                    routeGroupRequest.EndTerminal?.RouteAnchor,
                    generatedBusbarWidth,
                    out var allowedStartBoundaryPoint,
                    out var inwardStartLengthDirection,
                    out var fixedStartWidthDirection))
            {
                failureReason = "单始端搭接无法解析允许搭接端头、排长或排宽方向。";
                return false;
            }

            var normalizedInwardStartLengthDirection =
                inwardStartLengthDirection.Normalized;
            var normalizedFixedStartWidthDirection =
                fixedStartWidthDirection.Normalized;
            const double singleStartLapPositionTolerance =
                LinkageRouteGeometryTolerances.FinalAcceptanceComparison;
            foreach (var stackedRouteMember in routeCandidate?.PlanarConversion
                ?.StackMembers ?? new List<LinkageStackedRouteMember>())
            {
                var generatedStartSetting = stackedRouteMember?.PlanarParts
                    ?.FirstOrDefault();
                var generatedStartBoundarySegment = CreateBoundarySegment(
                    generatedStartSetting,
                    true);
                if (generatedStartBoundarySegment == null)
                {
                    failureReason = "单始端搭接缺少有效的生成排始端段。";
                    return false;
                }

                var generatedStartDirection = new Vector3d(
                    generatedStartBoundarySegment.Start,
                    generatedStartBoundarySegment.End);
                if (generatedStartDirection.Norm <= GeometryTolerance)
                {
                    failureReason = "单始端搭接的生成排始端段长度为零。";
                    return false;
                }

                var normalizedGeneratedStartDirection =
                    generatedStartDirection.Normalized;
                var allowedBoundaryToGeneratedEndpoint = new Vector3d(
                    allowedStartBoundaryPoint,
                    generatedStartBoundarySegment.Start);
                if (Math.Abs(normalizedGeneratedStartDirection.Dot(
                        normalizedInwardStartLengthDirection)) >= 1d - 1e-6d)
                {
                    if (!requireHorizontalWidthCenter)
                    {
                        continue;
                    }

                    var generatedWidthCenterOffset =
                        allowedBoundaryToGeneratedEndpoint.Dot(
                            normalizedFixedStartWidthDirection);
                    if (Math.Abs(generatedWidthCenterOffset) <=
                        singleStartLapPositionTolerance)
                    {
                        continue;
                    }

                    failureReason = string.Format(
                        CultureInfo.InvariantCulture,
                        "单始端平行搭接偏离始端排宽中心 {0:F3} mm。",
                        generatedWidthCenterOffset);
                    return false;
                }

                if (Math.Abs(normalizedGeneratedStartDirection.Dot(
                        normalizedFixedStartWidthDirection)) >= 1d - 1e-6d)
                {
                    if (!validatePerpendicularAllowedEndpoint)
                    {
                        continue;
                    }

                    var actualGeneratedBusbarWidth =
                        generatedStartBoundarySegment.BusbarWidth >
                            GeometryTolerance
                            ? generatedStartBoundarySegment.BusbarWidth
                            : generatedBusbarWidth;
                    var requiredEndpointContactCenterCoordinate =
                        actualGeneratedBusbarWidth / 2d;
                    var actualEndpointContactCenterCoordinate =
                        allowedBoundaryToGeneratedEndpoint.Dot(
                            normalizedInwardStartLengthDirection);
                    var generatedEndpointPositionOffset =
                        actualEndpointContactCenterCoordinate -
                        requiredEndpointContactCenterCoordinate;
                    if (Math.Abs(generatedEndpointPositionOffset) <=
                        singleStartLapPositionTolerance)
                    {
                        continue;
                    }

                    failureReason = string.Format(
                        CultureInfo.InvariantCulture,
                        "单始端垂直搭接偏离允许搭接端头 {0:F3} mm。",
                        generatedEndpointPositionOffset);
                    return false;
                }

                failureReason =
                    "单始端搭接只接受沿始端排长或排宽方向的实际端段。";
                return false;
            }

            return true;
        }

        private static bool ValidatePerpendicularLapBendReserves(
            LinkageRouteCandidate routeCandidate,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            out string failureReason)
        {
            failureReason = null;
            var generatedBusbarWidth = routeGroupRequest.GetBusbarWidth(routeRequest);
            var usesSectionSpecificBusbarSettings = routeGroupRequest
                .HasSectionSpecificBusbarSettings(routeRequest);
            // 基准中心线已按叠排包络预留最不利层位；这里逐根检查实际铜排，
            // 只能再次要求单排折弯余量，不能把叠排包络重复加到每一层。
            var actualMemberBendAllowance = routeRequest.GetEffectiveBendAllowance();

            foreach (var stackedRouteMember in routeCandidate.PlanarConversion.StackMembers)
            {
                for (var generatedPartIndex = 0;
                    generatedPartIndex < stackedRouteMember.PlanarParts.Count;
                    generatedPartIndex++)
                {
                    var generatedPlanarPart = stackedRouteMember.PlanarParts[generatedPartIndex];
                    var perpendicularStartLapNeighborWidth =
                        GetPerpendicularStartLapNeighborWidth(
                        stackedRouteMember.PlanarParts,
                        generatedPartIndex,
                        routeGroupRequest.StartTerminal,
                        generatedBusbarWidth,
                        routeRequest.AllowPartialStartPerpendicularLap);
                    var perpendicularEndLapNeighborWidth =
                        GetPerpendicularEndLapNeighborWidth(
                            stackedRouteMember.PlanarParts,
                        generatedPartIndex,
                            routeGroupRequest.EndTerminal,
                            generatedBusbarWidth);
                    var requiredCurrentSectionBendAllowance =
                        usesSectionSpecificBusbarSettings
                            ? GetSectionStackBendAllowance(
                                routeRequest,
                                routeGroupRequest,
                                generatedPartIndex,
                                stackedRouteMember.PlanarParts.Count)
                            : actualMemberBendAllowance;
                    if (usesSectionSpecificBusbarSettings &&
                        generatedPartIndex > 0 &&
                        ArePlanarPartBoundarySegmentsPerpendicular(
                            stackedRouteMember.PlanarParts[
                                generatedPartIndex - 1],
                            false,
                            generatedPlanarPart,
                            true))
                    {
                        perpendicularStartLapNeighborWidth = GetSectionBusbarWidth(
                            routeRequest,
                            routeGroupRequest,
                            generatedPartIndex - 1,
                            stackedRouteMember.PlanarParts.Count);
                    }
                    else if (usesSectionSpecificBusbarSettings &&
                        generatedPartIndex == 0 &&
                        !routeRequest.AllowPartialStartPerpendicularLap &&
                        IsPerpendicularTerminalTravel(
                            new Vector3d(
                                generatedPlanarPart.PointList[0],
                                generatedPlanarPart.PointList[1]),
                            routeGroupRequest.StartTerminal))
                    {
                        perpendicularStartLapNeighborWidth =
                            GetMaximumTerminalMemberBusbarWidth(
                                routeGroupRequest.StartTerminal,
                                generatedBusbarWidth);
                    }
                    if (usesSectionSpecificBusbarSettings &&
                        generatedPartIndex + 1 <
                            stackedRouteMember.PlanarParts.Count &&
                        ArePlanarPartBoundarySegmentsPerpendicular(
                            generatedPlanarPart,
                            false,
                            stackedRouteMember.PlanarParts[
                                generatedPartIndex + 1],
                            true))
                    {
                        perpendicularEndLapNeighborWidth = GetSectionBusbarWidth(
                            routeRequest,
                            routeGroupRequest,
                            generatedPartIndex + 1,
                            stackedRouteMember.PlanarParts.Count);
                    }
                    else if (usesSectionSpecificBusbarSettings &&
                        generatedPartIndex ==
                            stackedRouteMember.PlanarParts.Count - 1 &&
                        IsPerpendicularTerminalTravel(
                            new Vector3d(
                                generatedPlanarPart.PointList[
                                    generatedPlanarPart.PointList.Count - 2],
                                generatedPlanarPart.PointList[
                                    generatedPlanarPart.PointList.Count - 1]),
                            routeGroupRequest.EndTerminal))
                    {
                        perpendicularEndLapNeighborWidth =
                            GetMaximumTerminalMemberBusbarWidth(
                                routeGroupRequest.EndTerminal,
                                generatedBusbarWidth);
                    }

                    if (generatedPlanarPart.PointList.Count == 2)
                    {
                        var straightGeneratedPartLength = generatedPlanarPart.PointList[0]
                            .DistanceTo(generatedPlanarPart.PointList[1]);
                        var requiredStraightDoubleLapSegmentLength =
                            perpendicularStartLapNeighborWidth.GetValueOrDefault() +
                            perpendicularEndLapNeighborWidth.GetValueOrDefault();
                        if (!perpendicularStartLapNeighborWidth.HasValue ||
                            !perpendicularEndLapNeighborWidth.HasValue ||
                            straightGeneratedPartLength + 1e-6 >=
                            requiredStraightDoubleLapSegmentLength)
                        {
                            continue;
                        }

                        // 短直 I 排不能在候选阶段直接淘汰。它保持刚性 I
                        // 形，后续由端部联动、相邻搭接段和最终垫块处理；
                        // 若最终实体仍无法闭合，再由最终实体门禁拒绝。
                        continue;
                    }

                    if (perpendicularStartLapNeighborWidth.HasValue)
                    {
                        var requiredStartBoundarySegmentLength =
                            perpendicularStartLapNeighborWidth.Value +
                            LinkageRouteRoundedLapGeometry.GetActualMemberReserve(
                                routeRequest, generatedPlanarPart, true);
                        var generatedStartBoundarySegmentLength = generatedPlanarPart.PointList[0]
                            .DistanceTo(generatedPlanarPart.PointList[1]);
                        if (generatedStartBoundarySegmentLength +
                            LinkageRouteGeometryTolerances.FinalAcceptanceComparison <
                            requiredStartBoundarySegmentLength)
                        {
                            failureReason = string.Format(
                                System.Globalization.CultureInfo.InvariantCulture,
                                "叠排成员 {0} 的第 {1} 根非 I 排始端为垂直搭接，实际端部线段 {2:F3}，至少需要所搭接铜排的排宽加单排折弯余量 {3:F3}。",
                                stackedRouteMember.MemberIndex + 1,
                                generatedPartIndex + 1,
                                generatedStartBoundarySegmentLength,
                                requiredStartBoundarySegmentLength);
                            return false;
                        }
                    }

                    if (perpendicularEndLapNeighborWidth.HasValue)
                    {
                        var requiredEndBoundarySegmentLength =
                            perpendicularEndLapNeighborWidth.Value +
                            LinkageRouteRoundedLapGeometry.GetActualMemberReserve(
                                routeRequest, generatedPlanarPart, false);
                        var generatedEndBoundarySegmentIndex =
                            generatedPlanarPart.PointList.Count - 2;
                        var generatedEndBoundarySegmentLength = generatedPlanarPart.PointList[
                                generatedEndBoundarySegmentIndex]
                            .DistanceTo(generatedPlanarPart.PointList[
                                generatedEndBoundarySegmentIndex + 1]);
                        if (generatedEndBoundarySegmentLength +
                            LinkageRouteGeometryTolerances.FinalAcceptanceComparison <
                            requiredEndBoundarySegmentLength)
                        {
                            failureReason = string.Format(
                                System.Globalization.CultureInfo.InvariantCulture,
                                "叠排成员 {0} 的第 {1} 根非 I 排末端为垂直搭接，实际端部线段 {2:F3}，至少需要所搭接铜排的排宽加单排折弯余量 {3:F3}。",
                                stackedRouteMember.MemberIndex + 1,
                                generatedPartIndex + 1,
                                generatedEndBoundarySegmentLength,
                                requiredEndBoundarySegmentLength);
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        private static string FormatSolidHalfDimensions(
            LinkageRouteSolid routeSolid)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "({0:F3},{1:F3},{2:F3})",
                routeSolid?.HalfLength ?? double.NaN,
                routeSolid?.HalfWidth ?? double.NaN,
                routeSolid?.HalfThickness ?? double.NaN);
        }

        private static string FormatPoint(Point3d point)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "({0:F3},{1:F3},{2:F3})",
                point?.X ?? double.NaN,
                point?.Y ?? double.NaN,
                point?.Z ?? double.NaN);
        }

        private static double GetMaximumTerminalMemberBusbarWidth(
            LinkageTerminalAssembly terminalAssembly,
            double fallbackBusbarWidth)
        {
            return (terminalAssembly?.Members ??
                    new List<LinkageTerminalMember>())
                .Where(terminalMember =>
                    terminalMember?.FixedBusbar?.BusBarWidth >
                        GeometryTolerance)
                .Select(terminalMember =>
                    terminalMember.FixedBusbar.BusBarWidth)
                .DefaultIfEmpty(fallbackBusbarWidth)
                .Max();
        }

        private static bool ArePlanarPartBoundarySegmentsPerpendicular(
            PlanarBendBusbarSetting firstSetting,
            bool useFirstStartBoundary,
            PlanarBendBusbarSetting secondSetting,
            bool useSecondStartBoundary)
        {
            if (firstSetting?.PointList == null ||
                firstSetting.PointList.Count < 2 ||
                secondSetting?.PointList == null ||
                secondSetting.PointList.Count < 2)
            {
                return false;
            }

            var firstSegmentIndex = useFirstStartBoundary
                ? 0
                : firstSetting.PointList.Count - 2;
            var secondSegmentIndex = useSecondStartBoundary
                ? 0
                : secondSetting.PointList.Count - 2;
            var firstSegmentDirection = new Vector3d(
                firstSetting.PointList[firstSegmentIndex],
                firstSetting.PointList[firstSegmentIndex + 1]);
            var secondSegmentDirection = new Vector3d(
                secondSetting.PointList[secondSegmentIndex],
                secondSetting.PointList[secondSegmentIndex + 1]);
            return firstSegmentDirection.Norm > GeometryTolerance &&
                secondSegmentDirection.Norm > GeometryTolerance &&
                Math.Abs(firstSegmentDirection.Normalized.Dot(
                    secondSegmentDirection.Normalized)) <= GeometryTolerance;
        }

        private static IList<Point3d> CreateTerminalReserveCriticalAxisSkeleton(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IList<Point3d> extendedAxisSkeleton,
            LinkageRouteCenterlineConversionResult planarConversion)
        {
            if (planarConversion?.CenterlineParts == null ||
                planarConversion.CenterlineParts.Count == 0)
            {
                return null;
            }

            var generatedBusbarWidth = routeGroupRequest.GetBusbarWidth(routeRequest);
            var generatedSingleBusbarThickness = routeGroupRequest
                .GetBusbarThickness(routeRequest);
            var generatedStackCount = routeGroupRequest.GetStackCount(
                routeRequest);
            var generatedStackBendAllowance = LinkageRouteStackGeometry
                .GetStackBendAllowance(
                    generatedSingleBusbarThickness,
                    generatedStackCount,
                    routeGroupRequest.StackMiddleSpace,
                    routeRequest.GetEffectiveBendAllowance());
            var generatedPlanarParts = planarConversion.CenterlineParts;
            var firstGeneratedPlanarPart = generatedPlanarParts[0];
            var lastGeneratedPlanarPart = generatedPlanarParts[
                generatedPlanarParts.Count - 1];
            var startAxisTravelDirection = new Vector3d(
                extendedAxisSkeleton[0],
                extendedAxisSkeleton[1]);
            var endAxisTravelDirection = new Vector3d(
                extendedAxisSkeleton[extendedAxisSkeleton.Count - 2],
                extendedAxisSkeleton[extendedAxisSkeleton.Count - 1]);
            var startPerpendicularLapCoverageLength =
                routeRequest.AllowPartialStartPerpendicularLap
                    ? GetRequiredPartialStartPerpendicularLapCoverageLength(
                        firstGeneratedPlanarPart,
                        routeGroupRequest.StartTerminal.Members
                            .Select(startTerminalMember =>
                                startTerminalMember?.FixedBusbar),
                        generatedBusbarWidth)
                    : GetCollectivePerpendicularLapSpan(
                        routeGroupRequest.StartTerminal,
                        generatedBusbarWidth);
            var startTerminalRequiredActualLength =
                IsPerpendicularTerminalTravel(
                    startAxisTravelDirection,
                    routeGroupRequest.StartTerminal)
                    ? Math.Max(
                        routeRequest.MinimumSegmentLength,
                        startPerpendicularLapCoverageLength +
                            generatedStackBendAllowance)
                    : 0d;
            var endTerminalRequiredActualLength =
                IsPerpendicularTerminalTravel(
                    endAxisTravelDirection,
                    routeGroupRequest.EndTerminal)
                    ? Math.Max(
                        routeRequest.MinimumSegmentLength,
                        GetCollectivePerpendicularLapSpan(
                            routeGroupRequest.EndTerminal,
                            generatedBusbarWidth) +
                            generatedStackBendAllowance)
                    : 0d;
            var startTerminalActualLength = GetPlanarTerminalSegmentLength(
                firstGeneratedPlanarPart,
                true);
            var endTerminalActualLength = GetPlanarTerminalSegmentLength(
                lastGeneratedPlanarPart,
                false);
            startTerminalActualLength = GetShortestStackTerminalSegmentLength(
                planarConversion,
                true,
                startTerminalActualLength);
            endTerminalActualLength = GetShortestStackTerminalSegmentLength(
                planarConversion,
                false,
                endTerminalActualLength);
            var terminalPlaneExtensionCandidate =
                LinkageRouteAxisPathBuilder.CreateTerminalPlaneExtensionCandidate(
                    extendedAxisSkeleton,
                    Math.Max(
                        0d,
                        startTerminalRequiredActualLength - startTerminalActualLength),
                    Math.Max(
                        0d,
                        endTerminalRequiredActualLength - endTerminalActualLength),
                    routeRequest.MinimumSegmentLength);
            if (terminalPlaneExtensionCandidate != null)
            {
                return terminalPlaneExtensionCandidate;
            }

            var firstPartPerpendicularStartLapNeighborWidth =
                GetPerpendicularStartLapNeighborWidth(
                generatedPlanarParts,
                0,
                routeGroupRequest.StartTerminal,
                generatedBusbarWidth,
                routeRequest.AllowPartialStartPerpendicularLap);
            var firstPartPerpendicularEndLapNeighborWidth =
                GetPerpendicularEndLapNeighborWidth(
                generatedPlanarParts,
                0,
                routeGroupRequest.EndTerminal,
                generatedBusbarWidth);
            var lastPartPerpendicularStartLapNeighborWidth =
                GetPerpendicularStartLapNeighborWidth(
                generatedPlanarParts,
                generatedPlanarParts.Count - 1,
                routeGroupRequest.StartTerminal,
                generatedBusbarWidth,
                routeRequest.AllowPartialStartPerpendicularLap);
            var lastPartPerpendicularEndLapNeighborWidth =
                GetPerpendicularEndLapNeighborWidth(
                generatedPlanarParts,
                generatedPlanarParts.Count - 1,
                routeGroupRequest.EndTerminal,
                generatedBusbarWidth);

            double? startTerminalCriticalSegmentLength = null;
            if (firstPartPerpendicularStartLapNeighborWidth.HasValue)
            {
                if (firstGeneratedPlanarPart.PointList.Count >= 3)
                {
                    startTerminalCriticalSegmentLength = Math.Max(
                        routeRequest.MinimumSegmentLength,
                        firstPartPerpendicularStartLapNeighborWidth.Value +
                        generatedStackBendAllowance);
                }
                else if (generatedPlanarParts.Count > 1 &&
                    firstPartPerpendicularEndLapNeighborWidth.HasValue)
                {
                    startTerminalCriticalSegmentLength = Math.Max(
                        routeRequest.MinimumSegmentLength,
                        firstPartPerpendicularStartLapNeighborWidth.Value +
                        firstPartPerpendicularEndLapNeighborWidth.Value / 2d);
                }
            }

            double? endTerminalCriticalSegmentLength = null;
            if (lastPartPerpendicularEndLapNeighborWidth.HasValue)
            {
                if (lastGeneratedPlanarPart.PointList.Count >= 3)
                {
                    endTerminalCriticalSegmentLength = Math.Max(
                        routeRequest.MinimumSegmentLength,
                        lastPartPerpendicularEndLapNeighborWidth.Value +
                        generatedStackBendAllowance);
                }
                else if (generatedPlanarParts.Count > 1 &&
                    lastPartPerpendicularStartLapNeighborWidth.HasValue)
                {
                    endTerminalCriticalSegmentLength = Math.Max(
                        routeRequest.MinimumSegmentLength,
                        lastPartPerpendicularEndLapNeighborWidth.Value +
                        lastPartPerpendicularStartLapNeighborWidth.Value / 2d);
                }
            }

            return LinkageRouteAxisPathBuilder.CreateTerminalPlaneCriticalCandidate(
                extendedAxisSkeleton,
                startTerminalCriticalSegmentLength,
                endTerminalCriticalSegmentLength,
                routeRequest.MinimumSegmentLength);
        }

        private static void EvaluateReversedInternalLapClearanceSibling(
            LinkageRouteCandidatePool candidatePool,
            IDictionary<string, LinkageRouteCandidate> evaluatedCenterlineCandidates,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IList<Point3d> sourceAxisSkeleton,
            LinkageRouteConversionCandidate conversionCandidate,
            LinkageRouteCandidate failedConvertedRouteCandidate,
            IReadOnlyList<LinkageRouteSolid> clearanceSolids,
            string topologyPrefix,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested ||
                conversionCandidate == null ||
                failedConvertedRouteCandidate == null ||
                failedConvertedRouteCandidate.FailureStage !=
                    LinkageRouteFailureStage.Clearance ||
                failedConvertedRouteCandidate.MaximumViolationDepth <=
                    GeometryTolerance ||
                failedConvertedRouteCandidate
                    .SuggestedClearanceSeparationDirection == null ||
                failedConvertedRouteCandidate
                    .SuggestedClearanceSeparationDirection.Norm <=
                    GeometryTolerance)
            {
                return;
            }

            var clearanceCriticalShiftDistance =
                failedConvertedRouteCandidate.MaximumViolationDepth;
            IList<Point3d> shiftedInternalLapCenterline = null;
            if (conversionCandidate.ReversesStartInternalLap)
            {
                TryCreateStartInternalLapShiftedCenterline(
                    sourceAxisSkeleton,
                    conversionCandidate,
                    failedConvertedRouteCandidate
                        .SuggestedClearanceSeparationDirection,
                    clearanceCriticalShiftDistance,
                    out shiftedInternalLapCenterline);
            }
            else if (conversionCandidate.ReversesEndInternalLap)
            {
                TryCreateEndInternalLapShiftedCenterline(
                    sourceAxisSkeleton,
                    conversionCandidate,
                    failedConvertedRouteCandidate
                        .SuggestedClearanceSeparationDirection,
                    clearanceCriticalShiftDistance,
                    out shiftedInternalLapCenterline);
            }

            if (shiftedInternalLapCenterline == null)
            {
                return;
            }

            // 只根据最深绝缘违规做一次精确临界延长，不递归、不做固定步长。
            // 新候选仍完整经过最小段长、拆排、搭接、锐角、自交和绝缘校验。
            BuildAndAddUniqueCandidate(
                candidatePool,
                evaluatedCenterlineCandidates,
                routeRequest,
                routeGroupRequest,
                sourceAxisSkeleton,
                shiftedInternalLapCenterline,
                clearanceSolids,
                true,
                topologyPrefix + conversionCandidate.TopologySignature,
                traversalDirection,
                cancellationToken);
        }

        private static bool TryCreateStartInternalLapShiftedCenterline(
            IList<Point3d> sourceAxisSkeleton,
            LinkageRouteConversionCandidate conversionCandidate,
            Vector3d clearanceSeparationDirection,
            double clearanceCriticalShiftDistance,
            out IList<Point3d> shiftedCenterline)
        {
            shiftedCenterline = null;
            var conversionStartPointIndex =
                conversionCandidate.ConversionStartPointIndex;
            if (sourceAxisSkeleton == null ||
                conversionCandidate.Centerline == null ||
                conversionStartPointIndex <= 0 ||
                clearanceSeparationDirection == null ||
                clearanceSeparationDirection.Norm <= GeometryTolerance ||
                clearanceCriticalShiftDistance <= GeometryTolerance)
            {
                return false;
            }

            var normalizedClearanceSeparationDirection =
                clearanceSeparationDirection.Normalized;
            var adjustablePrefixSegmentIndex = -1;
            for (var sourceSegmentIndex = conversionStartPointIndex - 1;
                sourceSegmentIndex >= 0;
                sourceSegmentIndex--)
            {
                var sourceSegmentDirection = new Vector3d(
                    sourceAxisSkeleton[sourceSegmentIndex],
                    sourceAxisSkeleton[sourceSegmentIndex + 1]);
                if (sourceSegmentDirection.Norm > GeometryTolerance &&
                    sourceSegmentDirection.Normalized.Cross(
                        normalizedClearanceSeparationDirection).Norm <=
                        GeometryTolerance &&
                    sourceSegmentDirection.Normalized.Dot(
                        normalizedClearanceSeparationDirection) > 0d)
                {
                    adjustablePrefixSegmentIndex = sourceSegmentIndex;
                    break;
                }
            }

            if (adjustablePrefixSegmentIndex < 0)
            {
                return false;
            }

            shiftedCenterline = conversionCandidate.Centerline
                .Select(CopyPoint)
                .ToList();
            var convertedStartAnchorPointIndex =
                conversionCandidate.ConvertedStartAnchorPointIndex;
            var firstShiftedPointIndex = adjustablePrefixSegmentIndex + 1;
            var lastShiftedPointIndex = convertedStartAnchorPointIndex + 1;
            if (convertedStartAnchorPointIndex < 0 ||
                lastShiftedPointIndex >= shiftedCenterline.Count)
            {
                shiftedCenterline = null;
                return false;
            }

            for (var shiftedPointIndex = firstShiftedPointIndex;
                shiftedPointIndex <= lastShiftedPointIndex;
                shiftedPointIndex++)
            {
                shiftedCenterline[shiftedPointIndex] = TranslatePoint(
                    shiftedCenterline[shiftedPointIndex],
                    normalizedClearanceSeparationDirection,
                    clearanceCriticalShiftDistance);
            }

            return true;
        }

        private static bool TryCreateEndInternalLapShiftedCenterline(
            IList<Point3d> sourceAxisSkeleton,
            LinkageRouteConversionCandidate conversionCandidate,
            Vector3d clearanceSeparationDirection,
            double clearanceCriticalShiftDistance,
            out IList<Point3d> shiftedCenterline)
        {
            shiftedCenterline = null;
            var conversionEndPointIndex =
                conversionCandidate.ConversionEndPointIndex;
            if (sourceAxisSkeleton == null ||
                conversionCandidate.Centerline == null ||
                conversionEndPointIndex < 0 ||
                conversionEndPointIndex >= sourceAxisSkeleton.Count - 1 ||
                clearanceSeparationDirection == null ||
                clearanceSeparationDirection.Norm <= GeometryTolerance ||
                clearanceCriticalShiftDistance <= GeometryTolerance)
            {
                return false;
            }

            var normalizedClearanceSeparationDirection =
                clearanceSeparationDirection.Normalized;
            var adjustableSuffixSegmentIndex = -1;
            for (var sourceSegmentIndex = conversionEndPointIndex;
                sourceSegmentIndex < sourceAxisSkeleton.Count - 1;
                sourceSegmentIndex++)
            {
                var sourceSegmentDirection = new Vector3d(
                    sourceAxisSkeleton[sourceSegmentIndex],
                    sourceAxisSkeleton[sourceSegmentIndex + 1]);
                if (sourceSegmentDirection.Norm > GeometryTolerance &&
                    sourceSegmentDirection.Normalized.Cross(
                        normalizedClearanceSeparationDirection).Norm <=
                        GeometryTolerance &&
                    sourceSegmentDirection.Normalized.Dot(
                        normalizedClearanceSeparationDirection) < 0d)
                {
                    adjustableSuffixSegmentIndex = sourceSegmentIndex;
                    break;
                }
            }

            if (adjustableSuffixSegmentIndex < 0)
            {
                return false;
            }

            shiftedCenterline = conversionCandidate.Centerline
                .Select(CopyPoint)
                .ToList();
            var convertedEndAnchorIndex =
                conversionCandidate.ConvertedEndAnchorPointIndex;
            var firstShiftedPointIndex = convertedEndAnchorIndex - 1;
            var lastShiftedPointIndex = convertedEndAnchorIndex +
                adjustableSuffixSegmentIndex - conversionEndPointIndex;
            if (firstShiftedPointIndex < 0 ||
                lastShiftedPointIndex >= shiftedCenterline.Count)
            {
                shiftedCenterline = null;
                return false;
            }

            for (var shiftedPointIndex = firstShiftedPointIndex;
                shiftedPointIndex <= lastShiftedPointIndex;
                shiftedPointIndex++)
            {
                shiftedCenterline[shiftedPointIndex] = TranslatePoint(
                    shiftedCenterline[shiftedPointIndex],
                    normalizedClearanceSeparationDirection,
                    clearanceCriticalShiftDistance);
            }

            return true;
        }

        private static Point3d TranslatePoint(
            Point3d sourcePoint,
            Vector3d normalizedDirection,
            double distance)
        {
            return GeometRiMethod.Translate(
                sourcePoint,
                normalizedDirection,
                distance);
        }

        private static void EvaluateConvertedTerminalReserveSibling(
            LinkageRouteCandidatePool candidatePool,
            IDictionary<string, LinkageRouteCandidate> evaluatedCenterlineCandidates,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IList<Point3d> sourceAxisSkeleton,
            LinkageRouteCandidate convertedRouteCandidate,
            IReadOnlyList<LinkageRouteSolid> clearanceSolids,
            string topologyPrefix,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested ||
                convertedRouteCandidate?.PlanarConversion?.IsValid != true)
            {
                return;
            }

            var convertedCenterline = convertedRouteCandidate.ManufacturableCenterline;
            if (convertedCenterline == null || convertedCenterline.Count < 3)
            {
                return;
            }

            var generatedBusbarWidth = routeGroupRequest.GetBusbarWidth(routeRequest);
            var generatedSingleBusbarThickness = routeGroupRequest
                .GetBusbarThickness(routeRequest);
            var generatedStackCount = routeGroupRequest.GetStackCount(
                routeRequest);
            var generatedStackBendAllowance = LinkageRouteStackGeometry
                .GetStackBendAllowance(
                    generatedSingleBusbarThickness,
                    generatedStackCount,
                    routeGroupRequest.StackMiddleSpace,
                    routeRequest.GetEffectiveBendAllowance());
            var convertedStartTravelDirection = new Vector3d(
                convertedCenterline[0],
                convertedCenterline[1]);
            var convertedEndTravelDirection = new Vector3d(
                convertedCenterline[convertedCenterline.Count - 2],
                convertedCenterline[convertedCenterline.Count - 1]);
            var requiredConvertedStartSegmentLength = IsPerpendicularTerminalTravel(
                    convertedStartTravelDirection,
                    routeGroupRequest.StartTerminal)
                ? Math.Max(
                    routeRequest.MinimumSegmentLength,
                    GetCollectivePerpendicularLapSpan(
                        routeGroupRequest.StartTerminal,
                        generatedBusbarWidth) +
                        generatedStackBendAllowance)
                : 0d;
            var requiredConvertedEndSegmentLength = IsPerpendicularTerminalTravel(
                    convertedEndTravelDirection,
                    routeGroupRequest.EndTerminal)
                ? Math.Max(
                    routeRequest.MinimumSegmentLength,
                    GetCollectivePerpendicularLapSpan(
                        routeGroupRequest.EndTerminal,
                        generatedBusbarWidth) +
                        generatedStackBendAllowance)
                : 0d;
            var shortestConvertedStartSegmentLength =
                GetShortestStackTerminalSegmentLength(
                    convertedRouteCandidate.PlanarConversion,
                    true,
                    convertedCenterline[0].DistanceTo(convertedCenterline[1]));
            var shortestConvertedEndSegmentLength =
                GetShortestStackTerminalSegmentLength(
                    convertedRouteCandidate.PlanarConversion,
                    false,
                    convertedCenterline[convertedCenterline.Count - 2].DistanceTo(
                        convertedCenterline[convertedCenterline.Count - 1]));
            var convertedStartAdditionalLength = Math.Max(
                0d,
                requiredConvertedStartSegmentLength -
                    shortestConvertedStartSegmentLength);
            var convertedEndAdditionalLength = Math.Max(
                0d,
                requiredConvertedEndSegmentLength -
                    shortestConvertedEndSegmentLength);
            if (convertedStartAdditionalLength <= GeometryTolerance &&
                convertedEndAdditionalLength <= GeometryTolerance)
            {
                return;
            }

            var terminalReserveConvertedCenterline = convertedCenterline
                .Select(CopyPoint)
                .ToList();
            var startAdjacentPointIndex = 1;
            var endAdjacentPointIndex = terminalReserveConvertedCenterline.Count - 2;
            if (startAdjacentPointIndex == endAdjacentPointIndex &&
                convertedStartAdditionalLength > GeometryTolerance &&
                convertedEndAdditionalLength > GeometryTolerance)
            {
                return;
            }

            if (convertedStartAdditionalLength > GeometryTolerance)
            {
                ExtendConvertedTerminalSegment(
                    terminalReserveConvertedCenterline,
                    convertedStartAdditionalLength,
                    true);
            }

            if (convertedEndAdditionalLength > GeometryTolerance)
            {
                ExtendConvertedTerminalSegment(
                    terminalReserveConvertedCenterline,
                    convertedEndAdditionalLength,
                    false);
            }

            BuildAndAddUniqueCandidate(
                candidatePool,
                evaluatedCenterlineCandidates,
                routeRequest,
                routeGroupRequest,
                sourceAxisSkeleton,
                terminalReserveConvertedCenterline,
                clearanceSolids,
                true,
                topologyPrefix + convertedRouteCandidate.TopologySignature,
                traversalDirection,
                cancellationToken);
        }

        private static void ExtendConvertedTerminalSegment(
            IList<Point3d> convertedCenterline,
            double terminalAdditionalLength,
            bool extendStartTerminal)
        {
            var terminalPointIndex = extendStartTerminal
                ? 0
                : convertedCenterline.Count - 1;
            var adjacentPointIndex = extendStartTerminal
                ? 1
                : convertedCenterline.Count - 2;
            var terminalPoint = convertedCenterline[terminalPointIndex];
            var adjacentPoint = convertedCenterline[adjacentPointIndex];
            var terminalToAdjacentDirection = new Vector3d(
                terminalPoint,
                adjacentPoint);
            if (terminalToAdjacentDirection.Norm <= GeometryTolerance)
            {
                return;
            }

            var normalizedTerminalToAdjacentDirection =
                terminalToAdjacentDirection.Normalized;
            var extendedTerminalSegmentLength = terminalToAdjacentDirection.Norm +
                terminalAdditionalLength;
            convertedCenterline[adjacentPointIndex] = new Point3d(
                terminalPoint.X + normalizedTerminalToAdjacentDirection.X *
                    extendedTerminalSegmentLength,
                terminalPoint.Y + normalizedTerminalToAdjacentDirection.Y *
                    extendedTerminalSegmentLength,
                terminalPoint.Z + normalizedTerminalToAdjacentDirection.Z *
                    extendedTerminalSegmentLength);
        }

        private static bool IsPerpendicularTerminalTravel(
            Vector3d terminalTravelDirection,
            LinkageTerminalAssembly terminalAssembly)
        {
            return terminalTravelDirection != null &&
                terminalTravelDirection.Norm > 1e-8d &&
                terminalAssembly?.LengthDirection != null &&
                terminalAssembly.LengthDirection.Norm > 1e-8d &&
                Math.Abs(terminalTravelDirection.Normalized.Dot(
                    terminalAssembly.LengthDirection.Normalized)) <= 1e-6d;
        }

        private static double GetPlanarTerminalSegmentLength(
            PlanarBendBusbarSetting planarSetting,
            bool readStartSegment)
        {
            if (planarSetting?.PointList == null || planarSetting.PointList.Count < 2)
            {
                return 0d;
            }

            var firstTerminalPointIndex = readStartSegment
                ? 0
                : planarSetting.PointList.Count - 2;
            return planarSetting.PointList[firstTerminalPointIndex].DistanceTo(
                planarSetting.PointList[firstTerminalPointIndex + 1]);
        }

        private static double GetShortestStackTerminalSegmentLength(
            LinkageRouteCenterlineConversionResult planarConversion,
            bool readStartSegment,
            double fallbackTerminalSegmentLength)
        {
            if (planarConversion?.StackMembers == null ||
                planarConversion.StackMembers.Count == 0)
            {
                return fallbackTerminalSegmentLength;
            }

            var actualStackTerminalSegmentLengths = planarConversion.StackMembers
                .Where(stackedRouteMember =>
                    stackedRouteMember?.PlanarParts != null &&
                    stackedRouteMember.PlanarParts.Count > 0)
                .Select(stackedRouteMember => readStartSegment
                    ? stackedRouteMember.PlanarParts[0]
                    : stackedRouteMember.PlanarParts[
                        stackedRouteMember.PlanarParts.Count - 1])
                .Select(terminalPlanarPart => GetPlanarTerminalSegmentLength(
                    terminalPlanarPart,
                    readStartSegment))
                .Where(terminalSegmentLength =>
                    terminalSegmentLength > GeometryTolerance)
                .ToList();
            return actualStackTerminalSegmentLengths.Count > 0
                ? actualStackTerminalSegmentLengths.Min()
                : fallbackTerminalSegmentLength;
        }

        private static IReadOnlyList<IList<Point3d>>
            CreateInternalLapReserveCriticalAxisSkeletons(
                LinkageMultiGroupRouteRequest routeRequest,
                LinkageRouteGroupRequest routeGroupRequest,
                IList<Point3d> sourceAxisSkeleton,
                LinkageRouteCenterlineConversionResult planarConversion)
        {
            var reserveCriticalAxisSkeletons = new List<IList<Point3d>>();
            if (routeRequest == null || routeGroupRequest == null ||
                sourceAxisSkeleton == null || sourceAxisSkeleton.Count < 3 ||
                planarConversion?.CenterlineParts == null ||
                planarConversion.CenterlineParts.Count < 2)
            {
                return reserveCriticalAxisSkeletons;
            }

            var generatedBusbarWidth = routeGroupRequest.GetBusbarWidth(
                routeRequest);
            var generatedSingleBusbarThickness = routeGroupRequest
                .GetBusbarThickness(routeRequest);
            var generatedStackCount = routeGroupRequest.GetStackCount(
                routeRequest);
            var generatedStackBendAllowance = LinkageRouteStackGeometry
                .GetStackBendAllowance(
                    generatedSingleBusbarThickness,
                    generatedStackCount,
                    routeGroupRequest.StackMiddleSpace,
                    routeRequest.GetEffectiveBendAllowance());
            var requiredBendBoundaryLength = generatedBusbarWidth +
                generatedStackBendAllowance;
            var planarPartCount = planarConversion.CenterlineParts.Count;
            for (var planarInterfaceIndex = 0;
                planarInterfaceIndex < planarPartCount - 1;
                planarInterfaceIndex++)
            {
                var precedingPlanarPart = planarConversion.CenterlineParts[
                    planarInterfaceIndex];
                var followingPlanarPart = planarConversion.CenterlineParts[
                    planarInterfaceIndex + 1];
                var precedingLapSegment = CreateBoundarySegment(
                    precedingPlanarPart,
                    false);
                var followingLapSegment = CreateBoundarySegment(
                    followingPlanarPart,
                    true);
                if (precedingLapSegment == null || followingLapSegment == null)
                {
                    continue;
                }

                var interfaceLapEvaluation = LinkageRouteLapValidator.Evaluate(
                    precedingLapSegment,
                    followingLapSegment,
                    generatedBusbarWidth);
                if (!interfaceLapEvaluation.IsValid ||
                    !interfaceLapEvaluation.IsPerpendicular ||
                    interfaceLapEvaluation.ProjectedIntersection == null)
                {
                    continue;
                }

                var transitionAxisPointIndex = 1;
                var transitionAxisPointDistance = sourceAxisSkeleton[1]
                    .DistanceTo(interfaceLapEvaluation.ProjectedIntersection);
                for (var axisPointIndex = 2;
                    axisPointIndex < sourceAxisSkeleton.Count - 1;
                    axisPointIndex++)
                {
                    var currentAxisPointDistance = sourceAxisSkeleton[axisPointIndex]
                        .DistanceTo(interfaceLapEvaluation.ProjectedIntersection);
                    if (currentAxisPointDistance >= transitionAxisPointDistance)
                    {
                        continue;
                    }

                    transitionAxisPointIndex = axisPointIndex;
                    transitionAxisPointDistance = currentAxisPointDistance;
                }
                if (sourceAxisSkeleton[transitionAxisPointIndex].DistanceTo(
                        interfaceLapEvaluation.ProjectedIntersection) >
                    generatedBusbarWidth + GeometryTolerance)
                {
                    continue;
                }

                if (precedingPlanarPart.PointList.Count >= 3)
                {
                    var precedingBoundarySegmentLength =
                        precedingPlanarPart.PointList[
                            precedingPlanarPart.PointList.Count - 2].DistanceTo(
                            precedingPlanarPart.PointList[
                                precedingPlanarPart.PointList.Count - 1]);
                    var precedingBoundaryLengthDeficit =
                        requiredBendBoundaryLength -
                        precedingBoundarySegmentLength;
                    if (precedingBoundaryLengthDeficit > GeometryTolerance)
                    {
                        var precedingBoundaryDirection = new Vector3d(
                            precedingPlanarPart.PointList[
                                precedingPlanarPart.PointList.Count - 2],
                            precedingPlanarPart.PointList[
                                precedingPlanarPart.PointList.Count - 1]).Normalized;
                        reserveCriticalAxisSkeletons.Add(ShiftAxisSkeletonRange(
                            sourceAxisSkeleton,
                            transitionAxisPointIndex,
                            sourceAxisSkeleton.Count - 1,
                            precedingBoundaryDirection,
                            precedingBoundaryLengthDeficit));
                    }
                }

                if (followingPlanarPart.PointList.Count >= 3)
                {
                    var followingBoundarySegmentLength =
                        followingPlanarPart.PointList[0].DistanceTo(
                            followingPlanarPart.PointList[1]);
                    var followingBoundaryLengthDeficit =
                        requiredBendBoundaryLength -
                        followingBoundarySegmentLength;
                    if (followingBoundaryLengthDeficit > GeometryTolerance)
                    {
                        var followingBoundaryDirection = new Vector3d(
                            followingPlanarPart.PointList[0],
                            followingPlanarPart.PointList[1]).Normalized;
                        reserveCriticalAxisSkeletons.Add(ShiftAxisSkeletonRange(
                            sourceAxisSkeleton,
                            0,
                            transitionAxisPointIndex,
                            followingBoundaryDirection,
                            -followingBoundaryLengthDeficit));
                    }
                }
            }

            return reserveCriticalAxisSkeletons
                .GroupBy(GetCenterlineSignature, StringComparer.Ordinal)
                .Select(axisSkeletonGroup => axisSkeletonGroup.First())
                .ToList();
        }

        private static double GetSectionBusbarWidth(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            int planarPartIndex,
            int planarPartCount)
        {
            if (planarPartCount == 1 ||
                planarPartIndex == planarPartCount - 1)
            {
                return routeGroupRequest.GetCabinetTopLapBusbarWidth(
                    routeRequest);
            }

            return planarPartIndex == 0
                ? routeGroupRequest.GetStartLapBusbarWidth(routeRequest)
                : routeGroupRequest.GetBusbarWidth(routeRequest);
        }

        private static double GetSectionStackBendAllowance(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            int planarPartIndex,
            int planarPartCount)
        {
            if (planarPartCount == 1 ||
                planarPartIndex == planarPartCount - 1)
            {
                return routeGroupRequest
                    .GetCabinetTopLapSectionStackBendAllowance(routeRequest);
            }

            return planarPartIndex == 0
                ? routeGroupRequest
                    .GetStartLapSectionStackBendAllowance(routeRequest)
                : routeGroupRequest.GetMiddleSectionStackBendAllowance(
                    routeRequest);
        }

        private static IList<Point3d> ShiftAxisSkeletonRange(
            IList<Point3d> sourceAxisSkeleton,
            int firstShiftedPointIndex,
            int lastShiftedPointIndex,
            Vector3d shiftDirection,
            double shiftDistance)
        {
            var shiftedAxisSkeleton = sourceAxisSkeleton
                .Select(CopyPoint)
                .ToList();
            var normalizedShiftDirection = shiftDirection.Normalized;
            for (var shiftedPointIndex = firstShiftedPointIndex;
                shiftedPointIndex <= lastShiftedPointIndex;
                shiftedPointIndex++)
            {
                var sourcePoint = shiftedAxisSkeleton[shiftedPointIndex];
                shiftedAxisSkeleton[shiftedPointIndex] = new Point3d(
                    sourcePoint.X + normalizedShiftDirection.X * shiftDistance,
                    sourcePoint.Y + normalizedShiftDirection.Y * shiftDistance,
                    sourcePoint.Z + normalizedShiftDirection.Z * shiftDistance);
            }

            return shiftedAxisSkeleton;
        }

        private static double? GetPerpendicularStartLapNeighborWidth(
            IList<PlanarBendBusbarSetting> generatedPlanarParts,
            int generatedPartIndex,
            LinkageTerminalAssembly startTerminalAssembly,
            double requiredBusbarWidth,
            bool allowPartialStartPerpendicularLap)
        {
            if (generatedPartIndex > 0)
            {
                var precedingGeneratedPart = generatedPlanarParts[generatedPartIndex - 1];
                return CountPerpendicularLapPair(
                    precedingGeneratedPart,
                    false,
                    generatedPlanarParts[generatedPartIndex],
                    true,
                    requiredBusbarWidth) > 0
                        ? precedingGeneratedPart.BusBarWidth
                        : (double?)null;
            }

            var validStartTerminalNeighborSettings = startTerminalAssembly.Members
                .Select(startTerminalMember => startTerminalMember?.FixedBusbar)
                .Where(startFixedBusbarSetting => startFixedBusbarSetting != null &&
                    CountPerpendicularLapPair(
                        startFixedBusbarSetting,
                        true,
                        generatedPlanarParts[generatedPartIndex],
                        true,
                        requiredBusbarWidth,
                        false,
                        allowPartialStartPerpendicularLap) > 0)
                .ToList();
            return validStartTerminalNeighborSettings.Count == 0
                ? (double?)null
                : allowPartialStartPerpendicularLap
                    ? GetRequiredPartialStartPerpendicularLapCoverageLength(
                        generatedPlanarParts[generatedPartIndex],
                        validStartTerminalNeighborSettings,
                        requiredBusbarWidth)
                : GetCollectivePerpendicularLapSpan(
                    startTerminalAssembly,
                    validStartTerminalNeighborSettings.Max(
                        validSetting => validSetting.BusBarWidth));
        }

        private static double? GetPerpendicularEndLapNeighborWidth(
            IList<PlanarBendBusbarSetting> generatedPlanarParts,
            int generatedPartIndex,
            LinkageTerminalAssembly endTerminalAssembly,
            double requiredBusbarWidth)
        {
            if (generatedPartIndex + 1 < generatedPlanarParts.Count)
            {
                var followingGeneratedPart = generatedPlanarParts[generatedPartIndex + 1];
                return CountPerpendicularLapPair(
                    generatedPlanarParts[generatedPartIndex],
                    false,
                    followingGeneratedPart,
                    true,
                    requiredBusbarWidth) > 0
                        ? followingGeneratedPart.BusBarWidth
                        : (double?)null;
            }

            var validEndTerminalNeighborSettings = endTerminalAssembly.Members
                .Select(endTerminalMember => endTerminalMember?.FixedBusbar)
                .Where(endFixedBusbarSetting => endFixedBusbarSetting != null &&
                    CountPerpendicularLapPair(
                        generatedPlanarParts[generatedPartIndex],
                        false,
                        endFixedBusbarSetting,
                        false,
                        requiredBusbarWidth) > 0)
                .ToList();
            return validEndTerminalNeighborSettings.Count == 0
                ? (double?)null
                : GetCollectivePerpendicularLapSpan(
                    endTerminalAssembly,
                    validEndTerminalNeighborSettings.Max(
                        validSetting => validSetting.BusBarWidth));
        }

        private static double
            GetRequiredPartialStartPerpendicularLapCoverageLength(
            PlanarBendBusbarSetting generatedStartSetting,
            IEnumerable<PlanarBendBusbarSetting> fixedStartSettings,
            double requiredBusbarWidth)
        {
            var generatedStartSegment = CreateBoundarySegment(
                generatedStartSetting,
                true);
            if (generatedStartSegment == null)
            {
                return requiredBusbarWidth;
            }

            var generatedStartDirection = new Vector3d(
                generatedStartSegment.Start,
                generatedStartSegment.End);
            if (generatedStartDirection.Norm <= GeometryTolerance)
            {
                return requiredBusbarWidth;
            }

            var normalizedGeneratedStartDirection =
                generatedStartDirection.Normalized;
            var generatedStartBusbarWidth =
                generatedStartSegment.BusbarWidth > GeometryTolerance
                    ? generatedStartSegment.BusbarWidth
                    : requiredBusbarWidth;
            var maximumRequiredLapEndCoordinate = 0d;
            var resolvedPerpendicularStartContactCount = 0;
            foreach (var fixedStartSetting in fixedStartSettings ??
                Enumerable.Empty<PlanarBendBusbarSetting>())
            {
                var fixedStartSegment = CreateBoundarySegment(
                    fixedStartSetting,
                    true);
                if (fixedStartSegment == null)
                {
                    continue;
                }

                if (!AreSegmentsPerpendicular(
                        fixedStartSegment,
                        generatedStartSegment))
                {
                    continue;
                }

                var fixedCenterCoordinateAlongGeneratedDirection = new Vector3d(
                        generatedStartSegment.Start,
                        fixedStartSegment.Start)
                    .Dot(normalizedGeneratedStartDirection);
                var fixedStartBusbarWidth =
                    fixedStartSegment.BusbarWidth > GeometryTolerance
                        ? fixedStartSegment.BusbarWidth
                        : requiredBusbarWidth;
                var fixedStartHalfWidth = fixedStartBusbarWidth / 2d;
                var fixedStartMinimumCoordinate =
                    fixedCenterCoordinateAlongGeneratedDirection -
                    fixedStartHalfWidth;
                var fixedStartMaximumCoordinate =
                    fixedCenterCoordinateAlongGeneratedDirection +
                    fixedStartHalfWidth;
                var requiredMemberOverlapWidth = Math.Min(
                    generatedStartBusbarWidth,
                    fixedStartBusbarWidth);
                var memberOverlapStartCoordinate = Math.Max(
                    0d,
                    fixedStartMinimumCoordinate);
                var requiredMemberLapEndCoordinate =
                    memberOverlapStartCoordinate +
                    requiredMemberOverlapWidth;
                if (requiredMemberLapEndCoordinate >
                    fixedStartMaximumCoordinate + GeometryTolerance)
                {
                    continue;
                }

                maximumRequiredLapEndCoordinate = Math.Max(
                    maximumRequiredLapEndCoordinate,
                    requiredMemberLapEndCoordinate);
                resolvedPerpendicularStartContactCount++;
            }

            return resolvedPerpendicularStartContactCount > 0
                ? Math.Max(0d, maximumRequiredLapEndCoordinate)
                : requiredBusbarWidth;
        }

        private static bool ValidateActualTerminalLapRegionSeparation(
            LinkageRouteCandidate routeCandidate,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            out string failureReason)
        {
            failureReason = null;
            var requiredBusbarWidth = routeGroupRequest.GetBusbarWidth(routeRequest);
            var requiredStartLapBusbarWidth = routeGroupRequest
                .GetStartTerminalRequiredLapLength(routeRequest);
            var requiredCabinetTopLapBusbarWidth = routeGroupRequest
                .GetCabinetTopLapBusbarWidth(routeRequest);
            var startStackContacts = routeCandidate.PlanarConversion
                .StartStackPlacement?.Contacts ??
                new List<LinkageRouteStackTerminalContact>();
            var endStackContacts = routeCandidate.PlanarConversion
                .EndStackPlacement?.Contacts ??
                new List<LinkageRouteStackTerminalContact>();
            foreach (var stackedRouteMember in routeCandidate.PlanarConversion.StackMembers)
            {
                for (var partIndex = 0;
                    partIndex < stackedRouteMember.PlanarParts.Count;
                    partIndex++)
                {
                    var currentPart = stackedRouteMember.PlanarParts[partIndex];
                    var startLapFootprints = new List<LinkageRouteLapFootprint>();
                    var endLapFootprints = new List<LinkageRouteLapFootprint>();
                    if (partIndex == 0)
                    {
                        var generatedMemberStartContacts = startStackContacts.Where(
                            stackContact => stackContact.GeneratedMemberIndex ==
                                stackedRouteMember.MemberIndex)
                            .ToList();
                        if (!TryAddTerminalLapFootprintsWithAccessories(
                                startLapFootprints,
                                currentPart,
                                true,
                                routeGroupRequest.StartTerminal,
                                generatedMemberStartContacts,
                                true,
                                requiredStartLapBusbarWidth,
                                "始端",
                                routeRequest.AllowDoubleStartMiddleLap &&
                                    routeGroupRequest.StartTerminal.Members.Count >= 2,
                                routeRequest.AllowPartialStartPerpendicularLap,
                                routeCandidate.PlanarConversion.Accessories,
                                out failureReason,
                                routeRequest.EnableSingleStartConnectionBar))
                        {
                            return false;
                        }
                    }
                    else if (!TryAddRequiredLapFootprint(
                        startLapFootprints,
                        currentPart,
                        true,
                        stackedRouteMember.PlanarParts[partIndex - 1],
                        false,
                        requiredBusbarWidth,
                        "前一根实际铜排",
                        out failureReason))
                    {
                        return false;
                    }

                    if (partIndex == stackedRouteMember.PlanarParts.Count - 1)
                    {
                        var generatedMemberEndContacts = endStackContacts.Where(
                            stackContact => stackContact.GeneratedMemberIndex ==
                                stackedRouteMember.MemberIndex)
                            .ToList();
                        if (!TryAddTerminalLapFootprintsWithAccessories(
                                endLapFootprints,
                                currentPart,
                                false,
                                routeGroupRequest.EndTerminal,
                                generatedMemberEndContacts,
                                false,
                                requiredCabinetTopLapBusbarWidth,
                                "柜顶",
                                false,
                                false,
                                routeCandidate.PlanarConversion.Accessories,
                                out failureReason))
                        {
                            return false;
                        }
                    }
                    else if (!TryAddRequiredLapFootprint(
                        endLapFootprints,
                        currentPart,
                        false,
                        stackedRouteMember.PlanarParts[partIndex + 1],
                        true,
                        requiredBusbarWidth,
                        "后一根实际铜排",
                        out failureReason))
                    {
                        return false;
                    }

                    // 大缝隙只贴合一侧时，未出现在 Contacts 中的叠排成员并没有
                    // 该侧搭接面。只有同一根实际铜排两端都存在真实搭接面时，
                    // 才有“两端搭接区域不得正面积重叠”的检查对象。
                    if (startLapFootprints.Count > 0 && endLapFootprints.Count > 0 &&
                        !LinkageRouteTerminalLapRegionValidator.HaveNoPositiveAreaOverlap(
                            startLapFootprints,
                            endLapFootprints,
                            out var actualLapRegionFailureReason))
                    {
                        failureReason = string.Format(
                            "叠排成员 {0} 的第 {1} 根实际铜排首末搭接区域校验失败：{2}",
                            stackedRouteMember.MemberIndex + 1,
                            partIndex + 1,
                            actualLapRegionFailureReason);
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool TryAddTerminalLapFootprints(
            ICollection<LinkageRouteLapFootprint> destinationFootprints,
            PlanarBendBusbarSetting generatedSetting,
            bool generatedUseStart,
            LinkageTerminalAssembly terminalAssembly,
            IEnumerable<LinkageRouteStackTerminalContact> terminalContacts,
            bool fixedUseStart,
            double requiredBusbarWidth,
            string terminalLabel,
            bool allowCollectiveParallelLap,
            out string failureReason)
        {
            return TryAddTerminalLapFootprintsWithAccessories(
                destinationFootprints,
                generatedSetting,
                generatedUseStart,
                terminalAssembly,
                terminalContacts,
                fixedUseStart,
                requiredBusbarWidth,
                terminalLabel,
                allowCollectiveParallelLap,
                false,
                Enumerable.Empty<LinkageRouteAccessory>(),
                out failureReason);
        }

        private static bool TryAddTerminalLapFootprintsWithAccessories(
            ICollection<LinkageRouteLapFootprint> destinationFootprints,
            PlanarBendBusbarSetting generatedSetting,
            bool generatedUseStart,
            LinkageTerminalAssembly terminalAssembly,
            IEnumerable<LinkageRouteStackTerminalContact> terminalContacts,
            bool fixedUseStart,
            double requiredBusbarWidth,
            string terminalLabel,
            bool allowCollectiveParallelLap,
            bool allowPartialPerpendicularLap,
            IEnumerable<LinkageRouteAccessory> routeAccessories,
            out string failureReason,
            bool enableSingleStartConnectionBar = false)
        {
            failureReason = null;
            var generatedMemberTerminalContacts = (terminalContacts ??
                    Enumerable.Empty<LinkageRouteStackTerminalContact>())
                .ToList();
            if (generatedMemberTerminalContacts.Count == 0)
            {
                return true;
            }

            if (terminalAssembly?.Members == null)
            {
                failureReason = terminalLabel + "已声明存在搭接，但固定端成员为空。";
                return false;
            }

            var generatedBoundarySegment = CreateBoundarySegment(
                generatedSetting,
                generatedUseStart);
            if (generatedBoundarySegment == null)
            {
                failureReason = terminalLabel +
                    "已声明存在搭接，但生成排端段无效。";
                return false;
            }

            foreach (var generatedMemberTerminalContact in
                generatedMemberTerminalContacts)
            {
                if (generatedMemberTerminalContact.FixedMemberIndex < 0 ||
                    generatedMemberTerminalContact.FixedMemberIndex >=
                        terminalAssembly.Members.Count)
                {
                    failureReason = string.Format(
                        "{0}已声明存在搭接，但固定排索引 {1} 超出范围。",
                        terminalLabel,
                        generatedMemberTerminalContact.FixedMemberIndex + 1);
                    return false;
                }
            }

            var fixedThicknessLayerContactGroups =
                GroupTerminalContactsByFixedThicknessLayer(
                    terminalAssembly,
                    generatedMemberTerminalContacts);
            if (fixedThicknessLayerContactGroups.Count == 0)
            {
                failureReason = terminalLabel +
                    "已声明存在搭接，但无法解析固定排厚度层。";
                return false;
            }

            foreach (var fixedThicknessLayerContactGroup in
                fixedThicknessLayerContactGroups)
            {
                var bridgeFixedMemberIndices =
                    fixedThicknessLayerContactGroup.Contacts
                        .Select(terminalContact =>
                            terminalContact.FixedMemberIndex)
                        .Distinct()
                        .ToList();
                var bridgeGeneratedMemberIndex =
                    fixedThicknessLayerContactGroup.Contacts[0]
                        .GeneratedMemberIndex;
                var doubleStartBridgeAccessory = (routeAccessories ??
                        Enumerable.Empty<LinkageRouteAccessory>())
                    .FirstOrDefault(routeAccessory =>
                        routeAccessory?.Kind ==
                            LinkageRouteAccessoryKind
                                .DoubleStartHorizontalBridge &&
                        routeAccessory.TerminalSide ==
                            (generatedUseStart
                                ? LinkageRouteTerminalSide.Start
                                : LinkageRouteTerminalSide.End) &&
                        routeAccessory.RelatedGeneratedMemberIndex ==
                            bridgeGeneratedMemberIndex);
                if (doubleStartBridgeAccessory?.Setting != null &&
                    (bridgeFixedMemberIndices.Count >= 2 ||
                     (enableSingleStartConnectionBar && generatedUseStart &&
                      terminalAssembly.Members.Count == 1 &&
                      HasDoubleStartHorizontalBridgeCoverage(
                          generatedSetting, generatedUseStart,
                          doubleStartBridgeAccessory.Setting, terminalAssembly,
                          bridgeFixedMemberIndices, requiredBusbarWidth, false))) &&
                    TryCreateAcceptedLapFootprint(
                            generatedSetting,
                            generatedUseStart,
                            doubleStartBridgeAccessory.Setting,
                            true,
                            requiredBusbarWidth,
                            out var bridgeLapFootprint))
                {
                    destinationFootprints.Add(bridgeLapFootprint);
                    continue;
                }

                var successfulLayerFootprints =
                    new List<LinkageRouteLapFootprint>();
                var layerFailureDiagnostics = new List<string>();
                var layerContainsPerpendicularLap = false;
                var layerContainsParallelLap = false;
                var layerFixedMemberIndices = fixedThicknessLayerContactGroup.Contacts
                    .Select(terminalContact => terminalContact.FixedMemberIndex)
                    .Distinct()
                    .ToList();
                if (allowCollectiveParallelLap &&
                    layerFixedMemberIndices.Count >= 2 &&
                    LinkageRouteDoubleStartLapGeometry
                        .TryCreateCollectiveFixedBusbar(
                            terminalAssembly,
                            out var collectiveFixedBusbar))
                {
                    var collectiveFixedBoundarySegment = CreateBoundarySegment(
                        collectiveFixedBusbar,
                        fixedUseStart);
                    if (collectiveFixedBoundarySegment != null &&
                        !AreSegmentsPerpendicular(
                            collectiveFixedBoundarySegment,
                            generatedBoundarySegment))
                    {
                        layerContainsParallelLap = true;
                        if (TryCreateAcceptedLapFootprint(
                                generatedSetting,
                                generatedUseStart,
                                collectiveFixedBusbar,
                                fixedUseStart,
                                requiredBusbarWidth,
                                out var collectiveLapFootprint))
                        {
                            destinationFootprints.Add(collectiveLapFootprint);
                            continue;
                        }

                        layerFailureDiagnostics.Add(
                            "双始端连续整体范围无法建立实际搭接矩形");
                    }
                }

                foreach (var terminalContact in
                    fixedThicknessLayerContactGroup.Contacts)
                {
                    var fixedMemberIndex = terminalContact.FixedMemberIndex;
                    var fixedTerminalMember =
                        terminalAssembly.Members[fixedMemberIndex];
                    var fixedSetting = fixedTerminalMember == null
                        ? null
                        : fixedTerminalMember.FixedBusbar;
                    var fixedBoundarySegment = CreateBoundarySegment(
                        fixedSetting,
                        fixedUseStart);
                    if (fixedBoundarySegment == null)
                    {
                        layerFailureDiagnostics.Add(string.Format(
                            "固定排 {0} 端段无效",
                            fixedMemberIndex + 1));
                        continue;
                    }

                    var isPerpendicularLap = AreSegmentsPerpendicular(
                        fixedBoundarySegment,
                        generatedBoundarySegment);
                    layerContainsPerpendicularLap |= isPerpendicularLap;
                    layerContainsParallelLap |= !isPerpendicularLap;
                    if (TryCreateAcceptedLapFootprint(
                            generatedSetting,
                            generatedUseStart,
                            fixedSetting,
                            fixedUseStart,
                            requiredBusbarWidth,
                            out var actualLapFootprint,
                            allowPartialPerpendicularLap &&
                                isPerpendicularLap,
                            false))
                    {
                        successfulLayerFootprints.Add(actualLapFootprint);
                    }
                    else
                    {
                        layerFailureDiagnostics.Add(string.Format(
                            "固定排 {0} 无法建立实际搭接矩形",
                            fixedMemberIndex + 1));
                    }
                }

                var layerRequiresEveryFixedMember =
                    layerContainsPerpendicularLap;
                var layerFootprintsAreValid =
                    !layerContainsPerpendicularLap || !layerContainsParallelLap;
                layerFootprintsAreValid &= layerRequiresEveryFixedMember
                    ? successfulLayerFootprints.Count ==
                        fixedThicknessLayerContactGroup.Contacts.Count
                    : successfulLayerFootprints.Count > 0;
                if (!layerFootprintsAreValid)
                {
                    failureReason = string.Format(
                        "{0}已声明存在搭接，但固定层 {1:F3} 未满足{2}：{3}。",
                        terminalLabel,
                        fixedThicknessLayerContactGroup.CenterCoordinate,
                        layerRequiresEveryFixedMember
                            ? allowPartialPerpendicularLap
                                ? "垂直搭接全部成员且远侧至少一个生成排排宽"
                                : "垂直搭接全部成员满搭接"
                            : "水平搭接任意单成员满搭接",
                        string.Join("；", layerFailureDiagnostics.Take(3)));
                    return false;
                }

                foreach (var successfulLayerFootprint in
                    successfulLayerFootprints)
                {
                    destinationFootprints.Add(successfulLayerFootprint);
                }
            }

            return true;
        }

        private static bool TryAddRequiredLapFootprint(
            ICollection<LinkageRouteLapFootprint> destinationFootprints,
            PlanarBendBusbarSetting targetSetting,
            bool targetUseStart,
            PlanarBendBusbarSetting neighborSetting,
            bool neighborUseStart,
            double requiredBusbarWidth,
            string neighborLabel,
            out string failureReason)
        {
            failureReason = null;
            if (TryCreateAcceptedLapFootprint(
                    targetSetting,
                    targetUseStart,
                    neighborSetting,
                    neighborUseStart,
                    requiredBusbarWidth,
                    out var actualLapFootprint))
            {
                destinationFootprints.Add(actualLapFootprint);
                return true;
            }

            failureReason = neighborLabel + "已声明存在搭接，但无法建立实际搭接矩形。";
            return false;
        }

        // 只供候选/实际端部验收使用；基础求交器的默认容差不变。
        private static bool TryCreateAcceptedLapFootprint(
            PlanarBendBusbarSetting targetSetting, bool targetUseStart,
            PlanarBendBusbarSetting neighborSetting, bool neighborUseStart,
            double requiredWidth, out LinkageRouteLapFootprint footprint,
            bool allowTargetOneSided = false, bool allowNeighborOneSided = false)
        {
            return LinkageRouteTerminalLapRegionValidator.TryCreateActualLapFootprint(
                targetSetting, targetUseStart, neighborSetting, neighborUseStart,
                requiredWidth, out footprint, allowTargetOneSided, allowNeighborOneSided,
                LinkageRouteGeometryTolerances.FinalAcceptanceComparison);
        }

        private static LinkageRouteLapEvaluation EvaluateAcceptedLap(
            LinkageRouteLapSegment firstSegment,
            LinkageRouteLapSegment secondSegment,
            double requiredWidth)
        {
            return LinkageRouteLapValidator.Evaluate(firstSegment, secondSegment,
                requiredWidth,
                ((firstSegment?.SingleBusbarThickness ?? 0d) +
                    (secondSegment?.SingleBusbarThickness ?? 0d)) / 2d,
                LinkageRouteGeometryTolerances.FinalAcceptanceComparison);
        }

        private static LinkageRouteLapEvaluation EvaluateAcceptedOneSidedLap(
            LinkageRouteLapSegment firstSegment,
            LinkageRouteLapSegment secondSegment,
            double requiredWidth,
            bool allowFirstOneSided,
            bool allowSecondOneSided)
        {
            return LinkageRouteLapValidator.EvaluateAllowingOneSidedPerpendicularLap(
                firstSegment, secondSegment, requiredWidth,
                ((firstSegment?.SingleBusbarThickness ?? 0d) +
                    (secondSegment?.SingleBusbarThickness ?? 0d)) / 2d,
                allowFirstOneSided, allowSecondOneSided,
                LinkageRouteGeometryTolerances.FinalAcceptanceComparison);
        }

        private static bool HasFullCoverage(
            LinkageTerminalAssembly terminalAssembly,
            IList<LinkageStackedRouteMember> generatedMembers,
            LinkageRouteStackTerminalPlacement terminalStackPlacement,
            bool useStart,
            double busbarWidth,
            double terminalAdditionalSpace,
            string terminalLabel,
            out string failureReason)
        {
            return HasFullCoverageWithAccessories(
                terminalAssembly,
                generatedMembers,
                terminalStackPlacement,
                useStart,
                busbarWidth,
                terminalAdditionalSpace,
                terminalLabel,
                Enumerable.Empty<LinkageRouteAccessory>(),
                out failureReason);
        }

        private static bool HasFullCoverageWithAccessories(
            LinkageTerminalAssembly terminalAssembly,
            IList<LinkageStackedRouteMember> generatedMembers,
            LinkageRouteStackTerminalPlacement terminalStackPlacement,
            bool useStart,
            double busbarWidth,
            double terminalAdditionalSpace,
            string terminalLabel,
            IEnumerable<LinkageRouteAccessory> routeAccessories,
            out string failureReason)
        {
            return HasFullCoverageCoreWithAccessories(
                terminalAssembly,
                generatedMembers,
                terminalStackPlacement,
                useStart,
                busbarWidth,
                terminalAdditionalSpace,
                terminalLabel,
                false,
                false,
                routeAccessories,
                out failureReason);
        }

        private static bool HasFullCoverageCore(
            LinkageTerminalAssembly terminalAssembly,
            IList<LinkageStackedRouteMember> generatedMembers,
            LinkageRouteStackTerminalPlacement terminalStackPlacement,
            bool useStart,
            double busbarWidth,
            double terminalAdditionalSpace,
            string terminalLabel,
            bool allowCollectiveParallelLap,
            out string failureReason)
        {
            return HasFullCoverageCoreWithAccessories(
                terminalAssembly,
                generatedMembers,
                terminalStackPlacement,
                useStart,
                busbarWidth,
                terminalAdditionalSpace,
                terminalLabel,
                allowCollectiveParallelLap,
                false,
                Enumerable.Empty<LinkageRouteAccessory>(),
                out failureReason);
        }

        private static bool HasFullCoverageCoreWithAccessories(
            LinkageTerminalAssembly terminalAssembly,
            IList<LinkageStackedRouteMember> generatedMembers,
            LinkageRouteStackTerminalPlacement terminalStackPlacement,
            bool useStart,
            double busbarWidth,
            double terminalAdditionalSpace,
            string terminalLabel,
            bool allowCollectiveParallelLap,
            bool allowPartialPerpendicularLap,
            IEnumerable<LinkageRouteAccessory> routeAccessories,
            out string failureReason,
            bool enableSingleStartConnectionBar = false)
        {
            failureReason = null;
            var fixedMembers = terminalAssembly?.Members;
            if (fixedMembers == null || fixedMembers.Count == 0)
            {
                failureReason = terminalLabel + "没有可校验的固定排成员。";
                return false;
            }

            if (terminalStackPlacement?.Contacts == null ||
                terminalStackPlacement.Contacts.Count == 0)
            {
                failureReason = terminalLabel + "缺少叠排夹层相邻关系。";
                return false;
            }

            var lapDiagnostics = new List<string>();
            var validTerminalContacts = new List<LinkageRouteStackTerminalContact>();
            foreach (var terminalContact in terminalStackPlacement.Contacts)
            {
                var fixedIndex = terminalContact.FixedMemberIndex;
                var generatedIndex = terminalContact.GeneratedMemberIndex;
                if (fixedIndex < 0 || fixedIndex >= fixedMembers.Count ||
                    generatedIndex < 0 || generatedIndex >= generatedMembers.Count)
                {
                    lapDiagnostics.Add(string.Format(
                        "固定排{0}/生成排{1}：夹层索引超出范围",
                        fixedIndex + 1,
                        generatedIndex + 1));
                    continue;
                }

                validTerminalContacts.Add(terminalContact);
            }

            foreach (var generatedContactGroup in validTerminalContacts.GroupBy(
                terminalContact => terminalContact.GeneratedMemberIndex))
            {
                var generatedIndex = generatedContactGroup.Key;
                var generatedParts = generatedMembers[generatedIndex].PlanarParts;
                var generatedSetting = useStart
                    ? generatedParts.FirstOrDefault()
                    : generatedParts.LastOrDefault();
                var generatedSegment = CreateBoundarySegment(generatedSetting, useStart);
                if (generatedSegment == null)
                {
                    lapDiagnostics.Add(string.Format(
                        "生成排{0}：端段无效",
                        generatedIndex + 1));
                    continue;
                }

                var fixedThicknessLayerContactGroups =
                    GroupTerminalContactsByFixedThicknessLayer(
                        terminalAssembly,
                        generatedContactGroup);
                if (fixedThicknessLayerContactGroups.Count == 0)
                {
                    lapDiagnostics.Add(string.Format(
                        "生成排{0}：无法解析声明接触的固定排厚度层",
                        generatedIndex + 1));
                    continue;
                }

                foreach (var fixedThicknessLayerContactGroup in
                    fixedThicknessLayerContactGroups)
                {
                    var layerFixedMemberIndicesForBridge =
                        fixedThicknessLayerContactGroup.Contacts
                            .Select(terminalContact =>
                                terminalContact.FixedMemberIndex)
                            .Distinct()
                            .ToList();
                    var doubleStartBridgeAccessory = (routeAccessories ??
                            Enumerable.Empty<LinkageRouteAccessory>())
                        .FirstOrDefault(routeAccessory =>
                            routeAccessory?.Kind ==
                                LinkageRouteAccessoryKind
                                    .DoubleStartHorizontalBridge &&
                            routeAccessory.TerminalSide ==
                            (useStart
                                ? LinkageRouteTerminalSide.Start
                                : LinkageRouteTerminalSide.End) &&
                            (routeAccessory.RelatedGeneratedMemberIndex ==
                                generatedIndex ||
                             (allowCollectiveParallelLap && useStart)));
                    if (doubleStartBridgeAccessory?.Setting != null &&
                        (layerFixedMemberIndicesForBridge.Count >= 2 ||
                         (enableSingleStartConnectionBar && useStart &&
                          terminalAssembly.Members.Count == 1)) &&
                        HasDoubleStartHorizontalBridgeCoverage(
                            generatedSetting,
                            useStart,
                            doubleStartBridgeAccessory.Setting,
                            terminalAssembly,
                            layerFixedMemberIndicesForBridge,
                            busbarWidth,
                            allowPartialPerpendicularLap))
                    {
                        continue;
                    }

                    var layerPairDiagnostics = new List<string>();
                    var satisfiedLayerPairCount = 0;
                    var layerContainsPerpendicularLap = false;
                    var layerContainsParallelLap = false;
                    var collectiveParallelLapSatisfied = false;
                    foreach (var terminalContact in
                        fixedThicknessLayerContactGroup.Contacts)
                    {
                        var fixedIndex = terminalContact.FixedMemberIndex;
                        var fixedSetting = fixedMembers[fixedIndex]?.FixedBusbar;
                        var fixedSegment = CreateBoundarySegment(fixedSetting, useStart);
                        if (fixedSegment == null)
                        {
                            layerPairDiagnostics.Add(string.Format(
                                "固定排{0}/生成排{1}：固定排端段无效",
                                fixedIndex + 1,
                                generatedIndex + 1));
                            continue;
                        }

                        var isPerpendicularLap = AreSegmentsPerpendicular(
                            fixedSegment,
                            generatedSegment);
                        layerContainsPerpendicularLap |= isPerpendicularLap;
                        layerContainsParallelLap |= !isPerpendicularLap;
                        var lapEvaluation =
                            allowPartialPerpendicularLap && isPerpendicularLap
                                ? EvaluateAcceptedOneSidedLap(
                                        fixedSegment,
                                        generatedSegment,
                                        busbarWidth,
                                        false,
                                        true)
                                : EvaluateAcceptedLap(
                                    fixedSegment,
                                    generatedSegment,
                                    busbarWidth);
                        string terminalEndpointDiagnostic = null;
                        var terminalEndpointInsideFixedFace = isPerpendicularLap ||
                            IsGeneratedTerminalEndpointInsideFixedFace(
                                fixedSegment,
                                generatedSegment,
                                useStart,
                                out terminalEndpointDiagnostic);
                        var isDirectFullLap = lapEvaluation.IsValid &&
                            terminalEndpointInsideFixedFace;
                        var spacerGeometryEvaluation =
                            allowPartialPerpendicularLap && isPerpendicularLap
                                ? LinkageRouteLapValidator
                                    .EvaluateAllowingOneSidedPerpendicularLap(
                                        fixedSegment,
                                        generatedSegment,
                                        busbarWidth,
                                        Math.Max(
                                            lapEvaluation.ThicknessDistance,
                                            GeometryTolerance),
                                        false,
                                        true,
                                        LinkageRouteGeometryTolerances.FinalAcceptanceComparison)
                                : LinkageRouteLapValidator.Evaluate(
                                    fixedSegment,
                                    generatedSegment,
                                    busbarWidth,
                                    Math.Max(
                                        lapEvaluation.ThicknessDistance,
                                        GeometryTolerance),
                                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison);
                        var isDeferredSpacerLap =
                            terminalAdditionalSpace > GeometryTolerance &&
                            terminalEndpointInsideFixedFace &&
                            spacerGeometryEvaluation.IsValid;
                        if (isDirectFullLap || isDeferredSpacerLap)
                        {
                            satisfiedLayerPairCount++;
                            continue;
                        }

                        var pairFailureDiagnostic = lapEvaluation.IsValid
                            ? terminalEndpointDiagnostic
                            : lapEvaluation.Message;
                        layerPairDiagnostics.Add(string.Format(
                            "固定排{0}/生成排{1}：{2}（实际中心距={3:F3}，要求中心距={4:F3}）",
                            fixedIndex + 1,
                            generatedIndex + 1,
                            pairFailureDiagnostic,
                            lapEvaluation.ThicknessDistance,
                            lapEvaluation.RequiredThicknessDistance));
                    }

                    var layerFixedMemberIndices =
                        fixedThicknessLayerContactGroup.Contacts
                            .Select(terminalContact =>
                                terminalContact.FixedMemberIndex)
                            .Distinct()
                            .ToList();
                    if (allowCollectiveParallelLap &&
                        !layerContainsPerpendicularLap &&
                        layerContainsParallelLap &&
                        layerFixedMemberIndices.Count >= 2 &&
                        LinkageRouteDoubleStartLapGeometry
                            .TryCreateCollectiveFixedBusbar(
                                terminalAssembly,
                                out var collectiveFixedBusbar))
                    {
                        var collectiveFixedSegment = CreateBoundarySegment(
                            collectiveFixedBusbar,
                            useStart);
                        if (collectiveFixedSegment != null &&
                            !AreSegmentsPerpendicular(
                                collectiveFixedSegment,
                                generatedSegment))
                        {
                            var collectiveLapEvaluation =
                                EvaluateAcceptedLap(
                                    collectiveFixedSegment,
                                    generatedSegment,
                                    busbarWidth);
                            string collectiveEndpointDiagnostic = null;
                            var collectiveParallelEndpointInsideFixedFace =
                                doubleStartBridgeAccessory?.Setting != null ||
                                IsGeneratedTerminalEndpointInsideFixedFace(
                                    collectiveFixedSegment,
                                    generatedSegment,
                                    useStart,
                                    out collectiveEndpointDiagnostic);
                            var collectiveSpacerEvaluation =
                                LinkageRouteLapValidator.Evaluate(
                                    collectiveFixedSegment,
                                    generatedSegment,
                                    busbarWidth,
                                    Math.Max(
                                        collectiveLapEvaluation.ThicknessDistance,
                                        GeometryTolerance),
                                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison);
                            collectiveParallelLapSatisfied =
                                collectiveParallelEndpointInsideFixedFace &&
                                (collectiveLapEvaluation.IsValid ||
                                    (terminalAdditionalSpace > GeometryTolerance &&
                                     collectiveSpacerEvaluation.IsValid));
                            if (!collectiveParallelLapSatisfied &&
                                doubleStartBridgeAccessory?.Setting != null)
                            {
                                // 双始端水平生成排可落在固定端整体宽度范围外；
                                // 此时由连接排沿排长方向延伸覆盖生成排，候选层
                                // 只需确认连接排与固定始端的真实垂直搭接。
                                collectiveParallelLapSatisfied =
                                    HasDoubleStartBridgeFixedCoverage(
                                        doubleStartBridgeAccessory.Setting,
                                        terminalAssembly,
                                        layerFixedMemberIndices,
                                        useStart,
                                        busbarWidth);
                            }
                            if (!collectiveParallelLapSatisfied)
                            {
                                layerPairDiagnostics.Add(
                                    "双始端连续整体范围：" +
                                    (collectiveLapEvaluation.IsValid
                                        ? collectiveEndpointDiagnostic
                                        : collectiveLapEvaluation.Message));
                            }
                        }
                    }

                    var layerRequiresEveryFixedMember =
                        layerContainsPerpendicularLap;
                    var collectiveTerminalEndpointInsideFixedFace = true;
                    if (layerRequiresEveryFixedMember &&
                        !IsGeneratedTerminalEndpointInsideCollectiveFixedFace(
                            terminalAssembly,
                            fixedThicknessLayerContactGroup.Contacts,
                            generatedSegment,
                            useStart,
                            out var collectiveTerminalEndpointDiagnostic))
                    {
                        collectiveTerminalEndpointInsideFixedFace = false;
                        layerPairDiagnostics.Add(
                            collectiveTerminalEndpointDiagnostic);
                    }

                    var layerCoverageIsValid =
                        !layerContainsPerpendicularLap || !layerContainsParallelLap;
                    layerCoverageIsValid &=
                        collectiveTerminalEndpointInsideFixedFace ||
                        (doubleStartBridgeAccessory?.Setting != null &&
                         collectiveParallelLapSatisfied);
                    layerCoverageIsValid &= layerRequiresEveryFixedMember
                        ? satisfiedLayerPairCount ==
                            fixedThicknessLayerContactGroup.Contacts.Count
                        : satisfiedLayerPairCount > 0 ||
                            collectiveParallelLapSatisfied;
                    if (layerCoverageIsValid)
                    {
                        continue;
                    }

                    var coverageRuleDiagnostic = layerRequiresEveryFixedMember
                        ? allowPartialPerpendicularLap
                            ? "垂直搭接必须实际搭接该厚度层的全部固定排，且远侧至少保留一个生成排排宽"
                            : "垂直搭接必须满搭接该厚度层的全部固定排"
                        : allowCollectiveParallelLap
                            ? "水平搭接必须完整落入双始端从最左侧到最右侧的连续整体范围"
                            : "水平搭接必须完整落入该厚度层任意一根固定排的独立范围";
                    lapDiagnostics.Add(string.Format(
                        "生成排{0}/固定层{1:F3}：{2}；{3}",
                        generatedIndex + 1,
                        fixedThicknessLayerContactGroup.CenterCoordinate,
                        coverageRuleDiagnostic,
                        string.Join("；", layerPairDiagnostics.Take(3))));
                }
            }

            if (lapDiagnostics.Count > 0)
            {
                failureReason = terminalLabel + "夹层相邻搭接校验失败：" +
                    string.Join("；", lapDiagnostics.Take(4));
                return false;
            }

            return true;
        }

        private static IReadOnlyList<TerminalFixedThicknessLayerContactGroup>
            GroupTerminalContactsByFixedThicknessLayer(
            LinkageTerminalAssembly terminalAssembly,
            IEnumerable<LinkageRouteStackTerminalContact> terminalContacts)
        {
            var groupedTerminalContacts =
                new List<TerminalFixedThicknessLayerContactGroup>();
            var fixedMembers = terminalAssembly?.Members;
            var referenceFixedCenterPoint = fixedMembers == null
                ? null
                : fixedMembers
                .Select(fixedMember => fixedMember?.FixedBusbar?.PointList == null
                    ? null
                    : fixedMember.FixedBusbar.PointList.FirstOrDefault())
                .FirstOrDefault(fixedCenterPoint => fixedCenterPoint != null);
            if (referenceFixedCenterPoint == null ||
                terminalAssembly.ThicknessDirection == null ||
                terminalAssembly.ThicknessDirection.Norm <= GeometryTolerance)
            {
                return groupedTerminalContacts;
            }

            var normalizedTerminalThicknessDirection = terminalAssembly
                .ThicknessDirection.Normalized;
            foreach (var terminalContact in terminalContacts ??
                Enumerable.Empty<LinkageRouteStackTerminalContact>())
            {
                if (terminalContact.FixedMemberIndex < 0 ||
                    terminalContact.FixedMemberIndex >= fixedMembers.Count)
                {
                    continue;
                }

                var fixedTerminalMember =
                    fixedMembers[terminalContact.FixedMemberIndex];
                var fixedCenterPoint = fixedTerminalMember?.FixedBusbar?.PointList == null
                    ? null
                    : fixedTerminalMember.FixedBusbar.PointList.FirstOrDefault();
                if (fixedCenterPoint == null)
                {
                    continue;
                }

                var fixedLayerCenterCoordinate = new Vector3d(
                        referenceFixedCenterPoint,
                        fixedCenterPoint)
                    .Dot(normalizedTerminalThicknessDirection);
                var matchingFixedThicknessLayer = groupedTerminalContacts
                    .FirstOrDefault(fixedThicknessLayer => Math.Abs(
                        fixedThicknessLayer.CenterCoordinate -
                        fixedLayerCenterCoordinate) <= 1e-6d);
                if (matchingFixedThicknessLayer == null)
                {
                    matchingFixedThicknessLayer =
                        new TerminalFixedThicknessLayerContactGroup
                        {
                            CenterCoordinate = fixedLayerCenterCoordinate
                        };
                    groupedTerminalContacts.Add(matchingFixedThicknessLayer);
                }

                matchingFixedThicknessLayer.Contacts.Add(terminalContact);
            }

            return groupedTerminalContacts
                .OrderBy(fixedThicknessLayer =>
                    fixedThicknessLayer.CenterCoordinate)
                .ToList();
        }

        private static bool AreSegmentsPerpendicular(
            LinkageRouteLapSegment firstLapSegment,
            LinkageRouteLapSegment secondLapSegment)
        {
            if (firstLapSegment == null || secondLapSegment == null)
            {
                return false;
            }

            var firstLapDirection = new Vector3d(
                firstLapSegment.Start,
                firstLapSegment.End);
            var secondLapDirection = new Vector3d(
                secondLapSegment.Start,
                secondLapSegment.End);
            return firstLapDirection.Norm > GeometryTolerance &&
                secondLapDirection.Norm > GeometryTolerance &&
                Math.Abs(firstLapDirection.Normalized.Dot(
                    secondLapDirection.Normalized)) <= 1e-6d;
        }

        private static bool HasDoubleStartHorizontalBridgeCoverage(
            PlanarBendBusbarSetting generatedSetting,
            bool generatedUseStart,
            PlanarBendBusbarSetting bridgeSetting,
            LinkageTerminalAssembly terminalAssembly,
            IEnumerable<int> fixedMemberIndices,
            double requiredBusbarWidth,
            bool allowPartialPerpendicularLap)
        {
            var generatedBoundarySegment = CreateBoundarySegment(
                generatedSetting,
                generatedUseStart);
            var bridgeBoundarySegment = CreateBoundarySegment(
                bridgeSetting,
                true);
            if (generatedBoundarySegment == null ||
                bridgeBoundarySegment == null ||
                !EvaluateAcceptedLap(
                    generatedBoundarySegment,
                    bridgeBoundarySegment,
                    requiredBusbarWidth).IsValid)
            {
                return false;
            }

            foreach (var fixedMemberIndex in fixedMemberIndices)
            {
                if (fixedMemberIndex < 0 ||
                    fixedMemberIndex >= terminalAssembly.Members.Count)
                {
                    return false;
                }

                var fixedBoundarySegment = CreateBoundarySegment(
                    terminalAssembly.Members[fixedMemberIndex]?.FixedBusbar,
                    generatedUseStart);
                var bridgeToFixedLapEvaluation = fixedBoundarySegment == null
                    ? null
                    : allowPartialPerpendicularLap
                        ? EvaluateAcceptedOneSidedLap(
                                bridgeBoundarySegment,
                                fixedBoundarySegment,
                                requiredBusbarWidth,
                                true,
                                false)
                        : EvaluateAcceptedLap(
                            bridgeBoundarySegment,
                            fixedBoundarySegment,
                            requiredBusbarWidth);
                if (bridgeToFixedLapEvaluation?.IsValid != true)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasDoubleStartBridgeFixedCoverage(
            PlanarBendBusbarSetting bridgeSetting,
            LinkageTerminalAssembly terminalAssembly,
            IEnumerable<int> fixedMemberIndices,
            bool useStart,
            double requiredBusbarWidth)
        {
            var bridgeBoundarySegment = CreateBoundarySegment(
                bridgeSetting,
                true);
            if (bridgeBoundarySegment == null || terminalAssembly?.Members == null)
            {
                return false;
            }

            foreach (var fixedMemberIndex in fixedMemberIndices ??
                Enumerable.Empty<int>())
            {
                if (fixedMemberIndex < 0 ||
                    fixedMemberIndex >= terminalAssembly.Members.Count)
                {
                    return false;
                }

                var fixedBoundarySegment = CreateBoundarySegment(
                    terminalAssembly.Members[fixedMemberIndex]?.FixedBusbar,
                    useStart);
                if (fixedBoundarySegment == null)
                {
                    return false;
                }

                var bridgeLapEvaluation =
                    EvaluateAcceptedLap(
                        bridgeBoundarySegment,
                        fixedBoundarySegment,
                        requiredBusbarWidth);
                if (!bridgeLapEvaluation.IsValid)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsGeneratedTerminalEndpointInsideCollectiveFixedFace(
            LinkageTerminalAssembly terminalAssembly,
            IEnumerable<LinkageRouteStackTerminalContact> fixedLayerContacts,
            LinkageRouteLapSegment generatedTerminalSegment,
            bool terminalLapAtStart,
            out string failureDiagnostic)
        {
            failureDiagnostic = null;
            var fixedMembers = terminalAssembly?.Members;
            var layerFixedSegments = (fixedLayerContacts ??
                    Enumerable.Empty<LinkageRouteStackTerminalContact>())
                .Select(terminalContact => terminalContact.FixedMemberIndex)
                .Distinct()
                .Where(fixedMemberIndex => fixedMembers != null &&
                    fixedMemberIndex >= 0 &&
                    fixedMemberIndex < fixedMembers.Count)
                .Select(fixedMemberIndex => CreateBoundarySegment(
                    fixedMembers[fixedMemberIndex]?.FixedBusbar,
                    terminalLapAtStart))
                .Where(fixedSegment => fixedSegment != null)
                .ToList();
            if (layerFixedSegments.Count == 0 || generatedTerminalSegment == null)
            {
                failureDiagnostic =
                    "双始端整体搭接面缺少有效固定排或生成排端段。";
                return false;
            }

            var referenceFixedSegment = layerFixedSegments[0];
            var referenceFixedLengthDirection = new Vector3d(
                    referenceFixedSegment.Start,
                    referenceFixedSegment.End)
                .Normalized;
            var referenceFixedWidthDirection = referenceFixedSegment
                .WidthDirection.Normalized;
            var referenceFixedThicknessDirection = referenceFixedSegment
                .ThicknessDirection.Normalized;
            var generatedTerminalEndpoint = terminalLapAtStart
                ? generatedTerminalSegment.Start
                : generatedTerminalSegment.End;
            var generatedEndpointThicknessCoordinate = new Vector3d(
                    referenceFixedSegment.Start,
                    generatedTerminalEndpoint)
                .Dot(referenceFixedThicknessDirection);
            var projectedGeneratedTerminalEndpoint = new Point3d(
                generatedTerminalEndpoint.X - referenceFixedThicknessDirection.X *
                    generatedEndpointThicknessCoordinate,
                generatedTerminalEndpoint.Y - referenceFixedThicknessDirection.Y *
                    generatedEndpointThicknessCoordinate,
                generatedTerminalEndpoint.Z - referenceFixedThicknessDirection.Z *
                    generatedEndpointThicknessCoordinate);
            var collectiveWidthMinimum = double.PositiveInfinity;
            var collectiveWidthMaximum = double.NegativeInfinity;
            const double terminalFaceTolerance =
                LinkageRouteGeometryTolerances.FinalAcceptanceComparison;
            foreach (var layerFixedSegment in layerFixedSegments)
            {
                var layerFixedLengthDirection = new Vector3d(
                    layerFixedSegment.Start,
                    layerFixedSegment.End);
                if (layerFixedLengthDirection.Norm <= GeometryTolerance ||
                    layerFixedLengthDirection.Normalized.Cross(
                        referenceFixedLengthDirection).Norm > 1e-6d ||
                    layerFixedSegment.WidthDirection.Normalized.Cross(
                        referenceFixedWidthDirection).Norm > 1e-6d)
                {
                    failureDiagnostic =
                        "双始端整体垂直搭接要求同层固定排的排长和排宽方向分别平行。";
                    return false;
                }

                var layerStartToProjectedEndpoint = new Vector3d(
                    layerFixedSegment.Start,
                    projectedGeneratedTerminalEndpoint);
                var layerEndpointLengthCoordinate = layerStartToProjectedEndpoint.Dot(
                    layerFixedLengthDirection.Normalized);
                if (layerEndpointLengthCoordinate < -terminalFaceTolerance ||
                    layerEndpointLengthCoordinate >
                        layerFixedLengthDirection.Norm + terminalFaceTolerance)
                {
                    failureDiagnostic = string.Format(
                        CultureInfo.InvariantCulture,
                        "生成排实际端点没有落入双始端每根固定排的共同排长范围，当前投影={0:F3}。",
                        layerEndpointLengthCoordinate);
                    return false;
                }

                var layerWidthCenterCoordinate = new Vector3d(
                        referenceFixedSegment.Start,
                        layerFixedSegment.Start)
                    .Dot(referenceFixedWidthDirection);
                var layerFixedHalfWidth = layerFixedSegment.BusbarWidth / 2d;
                collectiveWidthMinimum = Math.Min(
                    collectiveWidthMinimum,
                    layerWidthCenterCoordinate - layerFixedHalfWidth);
                collectiveWidthMaximum = Math.Max(
                    collectiveWidthMaximum,
                    layerWidthCenterCoordinate + layerFixedHalfWidth);
            }

            var generatedEndpointCollectiveWidthCoordinate = new Vector3d(
                    referenceFixedSegment.Start,
                    projectedGeneratedTerminalEndpoint)
                .Dot(referenceFixedWidthDirection);
            if (generatedEndpointCollectiveWidthCoordinate <
                    collectiveWidthMinimum - terminalFaceTolerance ||
                generatedEndpointCollectiveWidthCoordinate >
                    collectiveWidthMaximum + terminalFaceTolerance)
            {
                failureDiagnostic = string.Format(
                    CultureInfo.InvariantCulture,
                    "生成排实际端点沿双始端整体排宽投影为 {0:F3}，超出整体真实范围 [{1:F3},{2:F3}]。",
                    generatedEndpointCollectiveWidthCoordinate,
                    collectiveWidthMinimum,
                    collectiveWidthMaximum);
                return false;
            }

            return true;
        }

        private static bool IsGeneratedTerminalEndpointInsideFixedFace(
            LinkageRouteLapSegment fixedTerminalSegment,
            LinkageRouteLapSegment generatedTerminalSegment,
            bool generatedLapAtStart,
            out string failureDiagnostic)
        {
            failureDiagnostic = null;
            if (fixedTerminalSegment == null || generatedTerminalSegment == null ||
                fixedTerminalSegment.BusbarWidth <= GeometryTolerance)
            {
                failureDiagnostic = "固定端或生成排端段无效，无法检查真实搭接面范围。";
                return false;
            }

            var fixedTerminalDirection = new Vector3d(
                fixedTerminalSegment.Start,
                fixedTerminalSegment.End);
            if (fixedTerminalDirection.Norm <= GeometryTolerance)
            {
                failureDiagnostic = "固定端搭接段长度为零。";
                return false;
            }

            var normalizedFixedTerminalDirection = fixedTerminalDirection.Normalized;
            var normalizedFixedWidthDirection =
                fixedTerminalSegment.WidthDirection.Normalized;
            var normalizedFixedThicknessDirection =
                fixedTerminalSegment.ThicknessDirection.Normalized;
            var generatedTerminalEndpoint = generatedLapAtStart
                ? generatedTerminalSegment.Start
                : generatedTerminalSegment.End;
            var fixedStartToGeneratedEndpoint = new Vector3d(
                fixedTerminalSegment.Start,
                generatedTerminalEndpoint);
            var terminalThicknessCoordinate = fixedStartToGeneratedEndpoint.Dot(
                normalizedFixedThicknessDirection);
            var projectedEndpointOnFixedCenterPlane = new Point3d(
                generatedTerminalEndpoint.X -
                    normalizedFixedThicknessDirection.X * terminalThicknessCoordinate,
                generatedTerminalEndpoint.Y -
                    normalizedFixedThicknessDirection.Y * terminalThicknessCoordinate,
                generatedTerminalEndpoint.Z -
                    normalizedFixedThicknessDirection.Z * terminalThicknessCoordinate);
            var fixedStartToProjectedEndpoint = new Vector3d(
                fixedTerminalSegment.Start,
                projectedEndpointOnFixedCenterPlane);
            var terminalLengthCoordinate = fixedStartToProjectedEndpoint.Dot(
                normalizedFixedTerminalDirection);
            var terminalWidthCoordinate = fixedStartToProjectedEndpoint.Dot(
                normalizedFixedWidthDirection);
            var terminalHalfWidth = fixedTerminalSegment.BusbarWidth / 2d;
            const double terminalFaceTolerance =
                LinkageRouteGeometryTolerances.FinalAcceptanceComparison;
            if (terminalLengthCoordinate < -terminalFaceTolerance ||
                terminalLengthCoordinate >
                    fixedTerminalDirection.Norm + terminalFaceTolerance)
            {
                failureDiagnostic = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "生成排实际端点沿固定排排长投影为 {0:F3}，超出固定排真实厚面长度范围 [0,{1:F3}]。",
                    terminalLengthCoordinate,
                    fixedTerminalDirection.Norm);
                return false;
            }

            if (terminalWidthCoordinate < -terminalHalfWidth - terminalFaceTolerance ||
                terminalWidthCoordinate > terminalHalfWidth + terminalFaceTolerance)
            {
                failureDiagnostic = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "生成排实际端点沿固定排排宽投影为 {0:F3}，超出固定排真实厚面范围 [{1:F3},{2:F3}]。",
                    terminalWidthCoordinate,
                    -terminalHalfWidth,
                    terminalHalfWidth);
                return false;
            }

            return true;
        }

        private static LinkageRouteLapSegment CreateBoundarySegment(
            PlanarBendBusbarSetting busbarSetting,
            bool useStart)
        {
            if (busbarSetting?.PointList == null || busbarSetting.PointList.Count < 2 ||
                busbarSetting.WidthVector == null || busbarSetting.StartBusbarVector == null)
            {
                return null;
            }

            var segmentIndex = useStart ? 0 : busbarSetting.PointList.Count - 2;
            var segmentVector = new Vector3d(
                busbarSetting.PointList[segmentIndex],
                busbarSetting.PointList[segmentIndex + 1]);
            if (segmentVector.Norm <= GeometryTolerance ||
                busbarSetting.WidthVector.Norm <= GeometryTolerance)
            {
                return null;
            }

            var segmentDirection = segmentVector.Normalized;
            var localThicknessVector = segmentDirection.Cross(
                busbarSetting.WidthVector.Normalized);
            if (localThicknessVector.Norm <= GeometryTolerance)
            {
                return null;
            }

            var localThicknessDirection = localThicknessVector.Normalized;
            return new LinkageRouteLapSegment(
                busbarSetting.PointList[segmentIndex],
                busbarSetting.PointList[segmentIndex + 1],
                busbarSetting.WidthVector,
                localThicknessDirection,
                busbarSetting.BusBarThickness,
                busbarSetting.BusBarWidth);
        }

        private static bool TryAddTerminalSolids(
            ICollection<LinkageRouteSolid> staticSolids,
            string groupKey,
            string clearanceGroupKey,
            LinkageTerminalAssembly terminalAssembly,
            LinkageRouteSolid explicitContainmentSolid,
            string terminalLabel,
            out string failureDiagnostic)
        {
            failureDiagnostic = null;
            // 路由终端只包含外伸搭接面；静态碰撞和绝缘仍使用全部原固定排。
            terminalAssembly = terminalAssembly?.CabinetBridgeAssembly?.OriginalTerminal ?? terminalAssembly;
            foreach (var terminalMember in terminalAssembly?.Members ?? new List<LinkageTerminalMember>())
            {
                var fixedSetting = terminalMember?.FixedBusbar;
                if (fixedSetting?.PointList == null || fixedSetting.PointList.Count < 2)
                {
                    continue;
                }

                var memberIntersectsContainment = explicitContainmentSolid == null;
                for (var segmentIndex = 0; segmentIndex < fixedSetting.PointList.Count - 1; segmentIndex++)
                {
                    var fixedMemberSegmentSolid = LinkageRouteSolidGeometry.CreateSegmentSolid(
                        groupKey,
                        fixedSetting.PointList[segmentIndex],
                        fixedSetting.PointList[segmentIndex + 1],
                        fixedSetting.WidthVector,
                        fixedSetting.StartBusbarVector,
                        fixedSetting.BusBarWidth,
                        fixedSetting.BusBarThickness,
                        segmentIndex,
                        clearanceGroupKey,
                        LinkageRouteSolidRole.FixedTerminalBusbar,
                        terminalMember.MemberKey);
                    // 固定排允许部分越界，但每一根至少有一个真实实体段接触显式盒。
                    // 保留完整铜排参与后续绝缘/干涉，不能以组包络或裁剪后的铜排替代。
                    memberIntersectsContainment = memberIntersectsContainment ||
                        LinkageRouteSolidDistance.GetMinimumDistance(
                            fixedMemberSegmentSolid, explicitContainmentSolid) <=
                        LinkageRouteGeometryTolerances.FinalAcceptanceComparison;
                    staticSolids.Add(fixedMemberSegmentSolid);
                }

                if (!memberIntersectsContainment)
                {
                    failureDiagnostic = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}组{1}固定排 {2} 整体不与显式路径包围盒相交。",
                        groupKey, terminalLabel, terminalMember.MemberKey);
                    return false;
                }
            }

            return true;
        }

        private static bool TryAddCollectiveTerminalForbiddenRegion(
            ICollection<LinkageRouteSolid> staticSolids,
            LinkageMultiGroupRouteRequest routeRequest,
            bool useStartTerminal,
            out string failureDiagnostic)
        {
            failureDiagnostic = null;
            if (routeRequest?.ContainmentMinimum == null ||
                routeRequest.ContainmentMaximum == null)
            {
                return true;
            }

            var terminalForbiddenBoundaryDescriptors = new List<TerminalForbiddenBoundaryDescriptor>();
            foreach (var routeGroupRequest in routeRequest.Groups ??
                new List<LinkageRouteGroupRequest>())
            {
                var terminalAssembly = useStartTerminal
                    ? routeGroupRequest?.StartTerminal
                    : routeGroupRequest?.EndTerminal;
                terminalAssembly = terminalAssembly?.CabinetBridgeAssembly?.OriginalTerminal ?? terminalAssembly;
                foreach (var terminalMember in terminalAssembly?.Members ??
                    new List<LinkageTerminalMember>())
                {
                    if (!TryResolveTerminalForbiddenBoundary(
                            terminalAssembly,
                            terminalMember,
                            out var terminalForbiddenBoundaryDescriptor,
                            out failureDiagnostic))
                    {
                        failureDiagnostic =
                            (useStartTerminal ? "始端" : "柜顶") +
                            "整体禁区构建失败：" + failureDiagnostic;
                        return false;
                    }

                    if (terminalForbiddenBoundaryDescriptor != null)
                    {
                        terminalForbiddenBoundaryDescriptors.Add(
                            terminalForbiddenBoundaryDescriptor);
                    }
                }
            }

            if (terminalForbiddenBoundaryDescriptors.Count == 0)
            {
                return true;
            }

            var referenceForbiddenDirection = terminalForbiddenBoundaryDescriptors[0]
                .ForbiddenDirection.Normalized;
            var forbiddenAxisIndex = GetDominantAxisIndex(referenceForbiddenDirection);
            var referenceForbiddenAxisComponent = GetVectorAxisCoordinate(
                referenceForbiddenDirection,
                forbiddenAxisIndex);
            if (Math.Abs(referenceForbiddenAxisComponent) < 1d - 1e-6d)
            {
                failureDiagnostic =
                    (useStartTerminal ? "始端" : "柜顶") +
                    "整体禁区只支持 XYZ 轴向的不可伸出方向。";
                return false;
            }

            foreach (var terminalForbiddenBoundaryDescriptor in
                terminalForbiddenBoundaryDescriptors.Skip(1))
            {
                var memberForbiddenDirection = terminalForbiddenBoundaryDescriptor
                    .ForbiddenDirection.Normalized;
                var normalizedDirectionDifference = Math.Sqrt(
                    Math.Pow(memberForbiddenDirection.X - referenceForbiddenDirection.X, 2d) +
                    Math.Pow(memberForbiddenDirection.Y - referenceForbiddenDirection.Y, 2d) +
                    Math.Pow(memberForbiddenDirection.Z - referenceForbiddenDirection.Z, 2d));
                if (normalizedDirectionDifference >=
                    CollectiveForbiddenDirectionDifferenceTolerance)
                {
                    // 不同逻辑组可能分别位于整体区域的两侧，无法用一个
                    // 轴向长方体表达混合的不可伸出方向。此时跳过整体禁区，
                    // 保留每根固定排实体和后续候选的独立几何校验。
                    return true;
                }
            }

            var forbiddenDirectionSign = referenceForbiddenAxisComponent > 0d
                ? 1d
                : -1d;
            var outermostForbiddenBoundaryCoordinate =
                terminalForbiddenBoundaryDescriptors.Max(
                    terminalForbiddenBoundaryDescriptor =>
                        GetPointAxisCoordinate(
                            terminalForbiddenBoundaryDescriptor.BoundaryPoint,
                            forbiddenAxisIndex) * forbiddenDirectionSign) *
                forbiddenDirectionSign;
            var containmentMinimum = routeRequest.ContainmentMinimum;
            var containmentMaximum = routeRequest.ContainmentMaximum;
            var containmentAxisMinimum = GetPointAxisCoordinate(
                containmentMinimum,
                forbiddenAxisIndex);
            var containmentAxisMaximum = GetPointAxisCoordinate(
                containmentMaximum,
                forbiddenAxisIndex);
            var forbiddenRegionMinimum = new Point3d(
                containmentMinimum.X,
                containmentMinimum.Y,
                containmentMinimum.Z);
            var forbiddenRegionMaximum = new Point3d(
                containmentMaximum.X,
                containmentMaximum.Y,
                containmentMaximum.Z);
            if (forbiddenDirectionSign > 0d)
            {
                forbiddenRegionMinimum = CreatePointWithAxisCoordinate(
                    forbiddenRegionMinimum,
                    forbiddenAxisIndex,
                    Math.Max(containmentAxisMinimum, outermostForbiddenBoundaryCoordinate));
            }
            else
            {
                forbiddenRegionMaximum = CreatePointWithAxisCoordinate(
                    forbiddenRegionMaximum,
                    forbiddenAxisIndex,
                    Math.Min(containmentAxisMaximum, outermostForbiddenBoundaryCoordinate));
            }

            if (GetPointAxisCoordinate(forbiddenRegionMaximum, forbiddenAxisIndex) -
                GetPointAxisCoordinate(forbiddenRegionMinimum, forbiddenAxisIndex) <=
                GeometryTolerance)
            {
                return true;
            }

            var collectiveTerminalForbiddenRegion =
                LinkageRouteSolidGeometry.CreateAxisAlignedBoxSolid(
                    null,
                    forbiddenRegionMinimum,
                    forbiddenRegionMaximum,
                    solidRole: LinkageRouteSolidRole.ForbiddenTerminalRegion);
            collectiveTerminalForbiddenRegion.IsForbiddenTerminalRegion = true;
            staticSolids.Add(collectiveTerminalForbiddenRegion);
            return true;
        }

        private static bool TryResolveTerminalForbiddenBoundary(
                LinkageTerminalAssembly terminalAssembly,
                LinkageTerminalMember terminalMember,
                out TerminalForbiddenBoundaryDescriptor boundaryDescriptor,
                out string failureDiagnostic)
        {
            boundaryDescriptor = null;
            failureDiagnostic = null;
            var fixedSetting = terminalMember?.FixedBusbar;
            var configuredForbiddenDirection =
                terminalMember?.ForbiddenHorizontalExitDirection;
            if (fixedSetting?.PointList == null ||
                fixedSetting.PointList.Count < 2)
            {
                return true;
            }

            var firstFixedEndpoint = fixedSetting.PointList[0];
            var lastFixedEndpointIndex = fixedSetting.PointList.Count - 1;
            var lastFixedEndpoint = fixedSetting.PointList[lastFixedEndpointIndex];
            var firstEndpointOutwardDirection = FindFixedEndpointOutwardDirection(
                fixedSetting.PointList,
                true);
            var lastEndpointOutwardDirection = FindFixedEndpointOutwardDirection(
                fixedSetting.PointList,
                false);
            Point3d forbiddenSideFixedEndpoint;
            Vector3d resolvedForbiddenDirection;
            if (configuredForbiddenDirection != null)
            {
                if (configuredForbiddenDirection.Norm <= GeometryTolerance)
                {
                    failureDiagnostic =
                        "显式不可伸出方向为零向量。";
                    return false;
                }

                var normalizedConfiguredForbiddenDirection =
                    configuredForbiddenDirection.Normalized;
                var firstEndpointDirectionScore = firstEndpointOutwardDirection == null
                    ? double.NegativeInfinity
                    : firstEndpointOutwardDirection.Dot(
                        normalizedConfiguredForbiddenDirection);
                var lastEndpointDirectionScore = lastEndpointOutwardDirection == null
                    ? double.NegativeInfinity
                    : lastEndpointOutwardDirection.Dot(
                        normalizedConfiguredForbiddenDirection);
                var bestEndpointDirectionScore = Math.Max(
                    firstEndpointDirectionScore,
                    lastEndpointDirectionScore);
                if (bestEndpointDirectionScore < 1d - 1e-4d)
                {
                    failureDiagnostic = string.Format(
                        CultureInfo.InvariantCulture,
                        "显式不可伸出方向与固定排两端的外向方向均不匹配，最佳点积为 {0:F6}。",
                        bestEndpointDirectionScore);
                    return false;
                }

                var useFirstConfiguredForbiddenEndpoint =
                    firstEndpointDirectionScore >= lastEndpointDirectionScore;
                forbiddenSideFixedEndpoint = useFirstConfiguredForbiddenEndpoint
                    ? firstFixedEndpoint
                    : lastFixedEndpoint;
                resolvedForbiddenDirection =
                    normalizedConfiguredForbiddenDirection;
            }
            else
            {
                if (terminalAssembly?.RouteAnchor == null)
                {
                    failureDiagnostic =
                        "未配置显式不可伸出方向时必须提供本端搭接锚点。";
                    return false;
                }

                var firstEndpointDistanceToRouteAnchor =
                    firstFixedEndpoint.DistanceTo(terminalAssembly.RouteAnchor);
                var lastEndpointDistanceToRouteAnchor =
                    lastFixedEndpoint.DistanceTo(terminalAssembly.RouteAnchor);
                if (Math.Abs(
                        firstEndpointDistanceToRouteAnchor -
                        lastEndpointDistanceToRouteAnchor) <= GeometryTolerance)
                {
                    failureDiagnostic =
                        "固定排两端到本端搭接锚点距离相同，无法确定不可伸出端面；请配置显式不可伸出方向。";
                    return false;
                }

                var useFirstInferredForbiddenEndpoint =
                    firstEndpointDistanceToRouteAnchor >
                    lastEndpointDistanceToRouteAnchor;
                forbiddenSideFixedEndpoint = useFirstInferredForbiddenEndpoint
                    ? firstFixedEndpoint
                    : lastFixedEndpoint;
                resolvedForbiddenDirection =
                    useFirstInferredForbiddenEndpoint
                        ? firstEndpointOutwardDirection
                        : lastEndpointOutwardDirection;
                if (resolvedForbiddenDirection == null ||
                    resolvedForbiddenDirection.Norm <= GeometryTolerance)
                {
                    failureDiagnostic =
                        "固定排点集合无法解析不可伸出端的外向方向。";
                    return false;
                }
            }

            boundaryDescriptor = new TerminalForbiddenBoundaryDescriptor
            {
                BoundaryPoint = forbiddenSideFixedEndpoint,
                ForbiddenDirection = resolvedForbiddenDirection.Normalized
            };
            return true;
        }

        private static double GetPointAxisCoordinate(Point3d point, int axisIndex)
        {
            if (axisIndex == 0)
            {
                return point.X;
            }

            return axisIndex == 1 ? point.Y : point.Z;
        }

        private static double GetVectorAxisCoordinate(Vector3d vector, int axisIndex)
        {
            if (axisIndex == 0)
            {
                return vector.X;
            }

            return axisIndex == 1 ? vector.Y : vector.Z;
        }

        private static Point3d CreatePointWithAxisCoordinate(
            Point3d point,
            int axisIndex,
            double coordinate)
        {
            if (axisIndex == 0)
            {
                return new Point3d(coordinate, point.Y, point.Z);
            }

            if (axisIndex == 1)
            {
                return new Point3d(point.X, coordinate, point.Z);
            }

            return new Point3d(point.X, point.Y, coordinate);
        }

        private static Vector3d FindFixedEndpointOutwardDirection(
            IList<Point3d> fixedCenterlinePoints,
            bool useFirstEndpoint)
        {
            if (fixedCenterlinePoints == null || fixedCenterlinePoints.Count < 2)
            {
                return null;
            }

            var endpointIndex = useFirstEndpoint
                ? 0
                : fixedCenterlinePoints.Count - 1;
            var endpointPoint = fixedCenterlinePoints[endpointIndex];
            for (var pointOffset = 1;
                pointOffset < fixedCenterlinePoints.Count;
                pointOffset++)
            {
                var interiorPointIndex = useFirstEndpoint
                    ? pointOffset
                    : endpointIndex - pointOffset;
                var endpointOutwardVector = new Vector3d(
                    fixedCenterlinePoints[interiorPointIndex],
                    endpointPoint);
                if (endpointOutwardVector.Norm > GeometryTolerance)
                {
                    return endpointOutwardVector.Normalized;
                }
            }

            return null;
        }

        private static bool ValidateMinimumSegments(IList<Point3d> centerlinePoints, double minimumSegmentLength)
        {
            for (var pointIndex = 0; pointIndex < centerlinePoints.Count - 1; pointIndex++)
            {
                if (centerlinePoints[pointIndex].DistanceTo(centerlinePoints[pointIndex + 1]) + 1e-6 <
                    minimumSegmentLength)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryValidateStackMemberMinimumSegments(
            IEnumerable<LinkageStackedRouteMember> stackedRouteMembers,
            double minimumSegmentLength,
            out string failureReason)
        {
            failureReason = null;
            foreach (var stackedRouteMember in stackedRouteMembers ??
                Enumerable.Empty<LinkageStackedRouteMember>())
            {
                if (stackedRouteMember?.PlanarParts == null)
                {
                    failureReason = "叠排成员缺少实际平面铜排，无法校验最小段长。";
                    return false;
                }

                for (var planarPartIndex = 0;
                    planarPartIndex < stackedRouteMember.PlanarParts.Count;
                    planarPartIndex++)
                {
                    var planarPart = stackedRouteMember.PlanarParts[
                        planarPartIndex];
                    if (planarPart?.PointList == null ||
                        planarPart.PointList.Count < 2)
                    {
                        failureReason = string.Format(
                            CultureInfo.InvariantCulture,
                            "叠排第 {0} 层第 {1} 根平面铜排缺少有效点集合。",
                            stackedRouteMember.MemberIndex + 1,
                            planarPartIndex + 1);
                        return false;
                    }

                    for (var pointIndex = 0;
                        pointIndex < planarPart.PointList.Count - 1;
                        pointIndex++)
                    {
                        var actualSegmentLength = planarPart.PointList[pointIndex]
                            .DistanceTo(planarPart.PointList[pointIndex + 1]);
                        if (actualSegmentLength +
                            LinkageRouteGeometryTolerances.FinalAcceptanceComparison >= minimumSegmentLength)
                        {
                            continue;
                        }

                        failureReason = string.Format(
                            CultureInfo.InvariantCulture,
                            "叠排第 {0} 层第 {1} 根平面铜排的第 {2} 段实际长度 " +
                            "{3:F6} mm，小于 MinimumSegmentLength={4:F6} mm。",
                            stackedRouteMember.MemberIndex + 1,
                            planarPartIndex + 1,
                            pointIndex + 1,
                            actualSegmentLength,
                            minimumSegmentLength);
                        return false;
                    }
                }
            }

            return true;
        }

        internal static bool IsInsideContainment(
            IEnumerable<LinkageRouteSolid> routeSolids,
            LinkageMultiGroupRouteRequest routeRequest,
            double acceptanceTolerance = GeometryTolerance)
        {
            if (routeRequest.ContainmentMinimum == null || routeRequest.ContainmentMaximum == null)
            {
                return true;
            }

            return routeSolids.SelectMany(routeSolid => routeSolid.GetVertices()).All(vertex =>
                vertex.X >= routeRequest.ContainmentMinimum.X - acceptanceTolerance &&
                vertex.Y >= routeRequest.ContainmentMinimum.Y - acceptanceTolerance &&
                vertex.Z >= routeRequest.ContainmentMinimum.Z - acceptanceTolerance &&
                vertex.X <= routeRequest.ContainmentMaximum.X + acceptanceTolerance &&
                vertex.Y <= routeRequest.ContainmentMaximum.Y + acceptanceTolerance &&
                vertex.Z <= routeRequest.ContainmentMaximum.Z + acceptanceTolerance);
        }

        private static LinkageRouteConversionContext CreateConversionContext(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IList<Point3d> axisSkeleton,
            IEnumerable<LinkageRouteSolid> clearanceSolids = null)
        {
            var busbarWidth = routeGroupRequest.GetBusbarWidth(routeRequest);
            var singleBusbarThickness = routeGroupRequest.GetBusbarThickness(
                routeRequest);
            var routeStackCount = routeGroupRequest.GetStackCount(routeRequest);
            var stackBendAllowance = LinkageRouteStackGeometry
                .GetStackBendAllowance(
                    singleBusbarThickness,
                    routeStackCount,
                    routeGroupRequest.StackMiddleSpace,
                    routeRequest.GetEffectiveBendAllowance());
            var startMatingBusbarWidth = GetTerminalConversionMatingWidth(
                routeGroupRequest.StartTerminal,
                busbarWidth,
                new Vector3d(axisSkeleton[0], axisSkeleton[1]));
            var endMatingBusbarWidth = GetTerminalConversionMatingWidth(
                routeGroupRequest.EndTerminal,
                busbarWidth,
                new Vector3d(
                    axisSkeleton[axisSkeleton.Count - 2],
                    axisSkeleton[axisSkeleton.Count - 1]));
            var startTerminalExtension = startMatingBusbarWidth +
                stackBendAllowance;
            var endTerminalExtension = endMatingBusbarWidth +
                stackBendAllowance;
            var minimumSegmentLength = Math.Max(0d, routeRequest.MinimumSegmentLength);
            var startLegDirection = new Vector3d(axisSkeleton[0], axisSkeleton[1]);
            var endLegDirection = new Vector3d(
                axisSkeleton[axisSkeleton.Count - 1],
                axisSkeleton[axisSkeleton.Count - 2]);
            var requiredRouteGroupInsulationClearance = routeGroupRequest
                .GetRequiredInsulationClearance(routeRequest);
            return new LinkageRouteConversionContext
            {
                UseTerminalTrimRule = true,
                GeneratedBusbarWidth = busbarWidth,
                StartMatingBusbarWidth = startMatingBusbarWidth,
                EndMatingBusbarWidth = endMatingBusbarWidth,
                StackBendAllowance = stackBendAllowance,
                StartStackBendAllowance = stackBendAllowance,
                EndStackBendAllowance = stackBendAllowance,
                InternalLapMinimumRetainedLength = busbarWidth / 2d +
                    stackBendAllowance,
                MinimumSegmentLength = minimumSegmentLength,
                InitialThicknessDirection =
                    routeGroupRequest.StartTerminal?.ThicknessDirection == null
                        ? null
                        : new Vector3d(
                            routeGroupRequest.StartTerminal.ThicknessDirection.X,
                            routeGroupRequest.StartTerminal.ThicknessDirection.Y,
                            routeGroupRequest.StartTerminal.ThicknessDirection.Z),
                ClearanceViolatedAxisPointIndices =
                    BuildClearanceViolatedAxisPointIndices(
                        axisSkeleton,
                        clearanceSolids,
                        routeGroupRequest.GetClearanceGroupKey(),
                        requiredRouteGroupInsulationClearance),
                ClearanceSolids = (clearanceSolids ??
                        Enumerable.Empty<LinkageRouteSolid>())
                    .Where(clearanceSolid => clearanceSolid != null)
                    .ToList(),
                CurrentClearanceGroupKey =
                    routeGroupRequest.GetClearanceGroupKey(),
                RequiredClearance = requiredRouteGroupInsulationClearance,
                RequireAxisAlignedRoutes = routeRequest.RequireAxisAlignedRoutes,
                MaximumRestrictedWholePathCandidateCount = routeRequest
                    .SearchOptions.MaximumRestrictedWholePathCandidateCount,
                StartMinimumExtension = startTerminalExtension,
                StartMaximumExtension = startLegDirection.Norm,
                EndMinimumExtension = endTerminalExtension,
                EndMaximumExtension = endLegDirection.Norm,
                MinimumAdaptiveStep = routeRequest.SearchOptions.MinimumAdaptiveRepairStep,
                StartCriticalExtensions = BuildObstacleCriticalExtensions(
                    axisSkeleton[0],
                    startLegDirection,
                    startTerminalExtension,
                    clearanceSolids,
                    routeGroupRequest.GetClearanceGroupKey(),
                    requiredRouteGroupInsulationClearance,
                    busbarWidth),
                EndCriticalExtensions = BuildObstacleCriticalExtensions(
                    axisSkeleton[axisSkeleton.Count - 1],
                    endLegDirection,
                    endTerminalExtension,
                    clearanceSolids,
                    routeGroupRequest.GetClearanceGroupKey(),
                    requiredRouteGroupInsulationClearance,
                    busbarWidth)
            };
        }

        private static IList<int> BuildClearanceViolatedAxisPointIndices(
            IList<Point3d> axisSkeleton,
            IEnumerable<LinkageRouteSolid> clearanceSolids,
            string currentClearanceGroupKey,
            double requiredClearance)
        {
            var clearanceViolatedAxisPointIndices = new List<int>();
            if (axisSkeleton == null || axisSkeleton.Count == 0 ||
                clearanceSolids == null)
            {
                return clearanceViolatedAxisPointIndices;
            }

            var relevantClearanceSolids = clearanceSolids
                .Where(clearanceSolid =>
                    clearanceSolid != null &&
                    (clearanceSolid.IsForbiddenTerminalRegion ||
                     clearanceSolid.SolidRole ==
                        LinkageRouteSolidRole.InputObstacle ||
                     string.IsNullOrWhiteSpace(
                         clearanceSolid.ClearanceGroupKey) ||
                     !string.Equals(
                         clearanceSolid.ClearanceGroupKey,
                         currentClearanceGroupKey,
                        StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (relevantClearanceSolids.Count == 0)
            {
                return clearanceViolatedAxisPointIndices;
            }

            var normalizedRequiredClearance = Math.Max(0d, requiredClearance);
            for (var axisPointIndex = 0;
                axisPointIndex < axisSkeleton.Count;
                axisPointIndex++)
            {
                var axisPoint = axisSkeleton[axisPointIndex];
                var pointRequiresMovement = relevantClearanceSolids.Any(
                    clearanceSolid =>
                    {
                        if (clearanceSolid.IsForbiddenTerminalRegion)
                        {
                            if (LinkageRouteSolidDistance
                                    .GetPointAxisAlignedBoundingBoxDistanceLowerBound(
                                        axisPoint,
                                        clearanceSolid) > GeometryTolerance)
                            {
                                return false;
                            }

                            return IsPointStrictlyInsideSolid(
                                axisPoint,
                                clearanceSolid);
                        }

                        if (clearanceSolid.SolidRole ==
                            LinkageRouteSolidRole.InputObstacle)
                        {
                            return IsPointStrictlyInsideSolid(
                                axisPoint,
                                clearanceSolid);
                        }

                        if (normalizedRequiredClearance > GeometryTolerance &&
                            LinkageRouteSolidDistance
                                .GetPointAxisAlignedBoundingBoxDistanceLowerBound(
                                    axisPoint,
                                    clearanceSolid) + GeometryTolerance >=
                            normalizedRequiredClearance)
                        {
                            return false;
                        }

                        var pointToSolidDistance = LinkageRouteSolidDistance
                            .GetPointMinimumDistance(axisPoint, clearanceSolid);
                        if (normalizedRequiredClearance > GeometryTolerance)
                        {
                            return pointToSolidDistance + GeometryTolerance <
                                normalizedRequiredClearance;
                        }

                        return IsPointStrictlyInsideSolid(
                            axisPoint,
                            clearanceSolid);
                    });
                if (pointRequiresMovement)
                {
                    clearanceViolatedAxisPointIndices.Add(axisPointIndex);
                }
            }

            return clearanceViolatedAxisPointIndices;
        }

        private static bool IsPointStrictlyInsideSolid(
            Point3d sourcePoint,
            LinkageRouteSolid targetSolid)
        {
            if (sourcePoint == null || targetSolid == null)
            {
                return false;
            }

            var solidCenterToPointDirection = new Vector3d(
                targetSolid.Center,
                sourcePoint);
            return Math.Abs(solidCenterToPointDirection.Dot(
                       targetSolid.LengthDirection)) <
                   targetSolid.HalfLength - GeometryTolerance &&
                Math.Abs(solidCenterToPointDirection.Dot(
                    targetSolid.WidthDirection)) <
                targetSolid.HalfWidth - GeometryTolerance &&
                Math.Abs(solidCenterToPointDirection.Dot(
                    targetSolid.ThicknessDirection)) <
                targetSolid.HalfThickness - GeometryTolerance;
        }

        private static IList<double> BuildObstacleCriticalExtensions(
            Point3d terminalAnchor,
            Vector3d inwardLegDirection,
            double minimumExtension,
            IEnumerable<LinkageRouteSolid> clearanceSolids,
            string currentClearanceGroupKey,
            double requiredClearance,
            double generatedBusbarWidth)
        {
            if (terminalAnchor == null || inwardLegDirection == null ||
                inwardLegDirection.Norm <= GeometryTolerance)
            {
                return new List<double>();
            }

            var normalizedLegDirection = inwardLegDirection.Normalized;
            var maximumExtension = inwardLegDirection.Norm;
            var criticalExtensions = new List<double>();
            foreach (var clearanceSolid in clearanceSolids ??
                Enumerable.Empty<LinkageRouteSolid>())
            {
                var isCurrentGroupSolid = clearanceSolid != null &&
                    !string.IsNullOrWhiteSpace(
                        clearanceSolid.ClearanceGroupKey) &&
                    string.Equals(
                        clearanceSolid.ClearanceGroupKey,
                        currentClearanceGroupKey,
                        StringComparison.OrdinalIgnoreCase);
                var isInputObstacle = clearanceSolid?.SolidRole ==
                    LinkageRouteSolidRole.InputObstacle;
                if (clearanceSolid == null ||
                    (isCurrentGroupSolid &&
                     !clearanceSolid.IsForbiddenTerminalRegion &&
                     !isInputObstacle))
                {
                    continue;
                }

                var solidRequiredClearance =
                    clearanceSolid.IsForbiddenTerminalRegion ||
                    isCurrentGroupSolid ||
                    isInputObstacle
                    ? 0d
                    : Math.Max(0d, requiredClearance);
                var criticalSafetyMargin = solidRequiredClearance +
                    generatedBusbarWidth / 2d;

                var projectedSolidCoordinates = clearanceSolid.GetVertices()
                    .Select(solidVertex => new Vector3d(
                        terminalAnchor,
                        solidVertex).Dot(normalizedLegDirection))
                    .ToList();
                if (projectedSolidCoordinates.Count == 0)
                {
                    continue;
                }

                criticalExtensions.Add(
                    projectedSolidCoordinates.Min() - criticalSafetyMargin);
                criticalExtensions.Add(
                    projectedSolidCoordinates.Max() + criticalSafetyMargin);
            }

            return criticalExtensions
                .Where(criticalExtension =>
                    criticalExtension >= minimumExtension - GeometryTolerance &&
                    criticalExtension <= maximumExtension + GeometryTolerance)
                .Select(criticalExtension => Math.Round(criticalExtension, 6))
                .Distinct()
                .OrderBy(criticalExtension =>
                    Math.Abs(criticalExtension - minimumExtension))
                .ToList();
        }

        private static double GetCollectivePerpendicularLapSpan(
            LinkageTerminalAssembly terminalAssembly,
            double fallbackWidth)
        {
            return LinkageRouteTerminalPoseFactory.GetCollectivePerpendicularLapSpan(
                terminalAssembly,
                fallbackWidth);
        }

        private static double GetTerminalConversionMatingWidth(
            LinkageTerminalAssembly terminalAssembly,
            double generatedBusbarWidth,
            Vector3d generatedTerminalTravelDirection)
        {
            if (terminalAssembly?.LengthDirection != null &&
                terminalAssembly.LengthDirection.Norm > 1e-8 &&
                generatedTerminalTravelDirection != null &&
                generatedTerminalTravelDirection.Norm > 1e-8 &&
                terminalAssembly.LengthDirection.Normalized.Cross(
                    generatedTerminalTravelDirection.Normalized).Norm < 1e-6)
            {
                return generatedBusbarWidth;
            }

            return GetCollectivePerpendicularLapSpan(
                terminalAssembly,
                generatedBusbarWidth);
        }

        private static LinkageRouteCandidate Fail(
            LinkageRouteCandidate routeCandidate,
            LinkageRouteFailureStage failureStage,
            string diagnostic,
            double violationDepth)
        {
            routeCandidate.FailureStage = failureStage;
            routeCandidate.Diagnostic = diagnostic;
            routeCandidate.ViolationCount = Math.Max(1, routeCandidate.ViolationCount);
            routeCandidate.MaximumViolationDepth = Math.Max(routeCandidate.MaximumViolationDepth, violationDepth);
            return routeCandidate;
        }

        private static void AddCandidate(
            LinkageRouteCandidatePool candidatePool,
            LinkageRouteCandidate routeCandidate)
        {
            if (routeCandidate.IsValid)
            {
                candidatePool.ValidCandidates.Add(routeCandidate);
                if (routeCandidate.ReverseUSplitCandidate?.IsValid == true)
                    candidatePool.ValidCandidates.Add(routeCandidate.ReverseUSplitCandidate);
                return;
            }

            if (!candidatePool.FailureStageCounts.ContainsKey(routeCandidate.FailureStage))
            {
                candidatePool.FailureStageCounts.Add(routeCandidate.FailureStage, 0);
            }

            candidatePool.FailureStageCounts[routeCandidate.FailureStage]++;
            if (!candidatePool.FailureDiagnosticSamples.TryGetValue(
                    routeCandidate.FailureStage,
                    out var failureDiagnosticSamples))
            {
                failureDiagnosticSamples = new List<string>();
                candidatePool.FailureDiagnosticSamples[routeCandidate.FailureStage] =
                    failureDiagnosticSamples;
            }

            var failureDiagnosticSample =
                (routeCandidate.TopologySignature ?? "<无拓扑>") + "：" +
                routeCandidate.Diagnostic;
            if (failureDiagnosticSamples.Count < 8 &&
                !failureDiagnosticSamples.Contains(failureDiagnosticSample))
            {
                failureDiagnosticSamples.Add(failureDiagnosticSample);
            }

            if (!candidatePool.RepresentativeFailedCandidates.TryGetValue(
                    routeCandidate.FailureStage,
                    out var representativeFailedCandidate) ||
                IsBetterFailureCandidate(routeCandidate, representativeFailedCandidate))
            {
                candidatePool.RepresentativeFailedCandidates[routeCandidate.FailureStage] =
                    routeCandidate;
            }

            if (IsBetterFailureCandidate(
                routeCandidate,
                candidatePool.BestFailedCandidate))
            {
                candidatePool.BestFailedCandidate = routeCandidate;
            }
        }

        private static bool IsBetterFailureCandidate(
            LinkageRouteCandidate candidateFailure,
            LinkageRouteCandidate currentFailure)
        {
            return currentFailure == null ||
                candidateFailure.ViolationCount < currentFailure.ViolationCount ||
                (candidateFailure.ViolationCount == currentFailure.ViolationCount &&
                 candidateFailure.MaximumViolationDepth <
                    currentFailure.MaximumViolationDepth);
        }

        private static LinkageRouteCandidate BuildAndAddUniqueCandidate(
            LinkageRouteCandidatePool candidatePool,
            IDictionary<string, LinkageRouteCandidate> evaluatedCenterlineCandidates,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IList<Point3d> axisSkeleton,
            IList<Point3d> manufacturableCenterline,
            IReadOnlyList<LinkageRouteSolid> clearanceSolids,
            bool isConvertedShape,
            string topologySignature,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken,
            LinkageRouteGroupRequest proxyTopologyGroup = null,
            Task<LinkageRouteCandidate> prefetchedCandidate = null)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var centerlineSignature = GetCenterlineSignature(manufacturableCenterline);
            if (evaluatedCenterlineCandidates.TryGetValue(
                    centerlineSignature,
                    out var evaluatedCenterlineCandidate))
            {
                return evaluatedCenterlineCandidate;
            }

            // 预取结果与此处同步构建使用完全相同的参数；按原顺序消费，
            // 去重、入池和提前结束判断仍只在当前线程执行。
            var routeCandidate = prefetchedCandidate != null
                ? prefetchedCandidate.GetAwaiter().GetResult()
                : BuildUniqueCandidateCore(
                    routeRequest,
                    routeGroupRequest,
                    axisSkeleton,
                    manufacturableCenterline,
                    clearanceSolids,
                    isConvertedShape,
                    topologySignature,
                    traversalDirection,
                    cancellationToken,
                    proxyTopologyGroup);
            if (routeCandidate == null)
            {
                return null;
            }

            evaluatedCenterlineCandidates[centerlineSignature] = routeCandidate;
            AddCandidate(candidatePool, routeCandidate);
            if (!candidatePool.DetourShortcutsEvaluated && proxyTopologyGroup == null &&
                routeCandidate.IsValid && routeCandidate.PlanarConversion.CenterlineParts.Count >= 4)
            {
                candidatePool.DetourShortcutsEvaluated = true;
                AddDetourShortcutCandidates(candidatePool, evaluatedCenterlineCandidates, routeRequest,
                    routeCandidate, clearanceSolids, cancellationToken);
            }
            return routeCandidate;
        }

        private static LinkageRouteCandidate BuildUniqueCandidateCore(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IList<Point3d> axisSkeleton,
            IList<Point3d> manufacturableCenterline,
            IReadOnlyList<LinkageRouteSolid> clearanceSolids,
            bool isConvertedShape,
            string topologySignature,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken,
            LinkageRouteGroupRequest proxyTopologyGroup)
        {
            return proxyTopologyGroup != null
                ? RebuildProxyCandidateForPhysicalGroup(routeRequest, routeGroupRequest,
                    CreateCandidateShell(proxyTopologyGroup, axisSkeleton, manufacturableCenterline,
                        isConvertedShape, topologySignature, traversalDirection), clearanceSolids, cancellationToken)
                : BuildCandidate(
                routeRequest,
                routeGroupRequest,
                axisSkeleton,
                manufacturableCenterline,
                clearanceSolids,
                isConvertedShape,
                topologySignature,
                traversalDirection,
                cancellationToken);
        }

        /// <summary>
        /// 同一逻辑组骨架候选的有序预取：只提前并行执行纯构建（转换、搭接、
        /// 实体及静态净距校验），消费仍由调用线程按原顺序逐个去重、入池并判断
        /// 提前结束，因此候选池内容与顺序和串行构建一致。窗口为 1 时不预取。
        /// 预取构建使用骨架深拷贝，原骨架对象不会被多个线程共享。
        /// </summary>
        private sealed class OrderedSkeletonCandidatePrefetcher : IDisposable
        {
            private readonly IReadOnlyList<IList<Point3d>> orderedSkeletons;
            private readonly Func<IList<Point3d>, bool> isAlreadyEvaluated;
            private readonly Func<IList<Point3d>, LinkageRouteCandidate> buildCandidate;
            private readonly Task<LinkageRouteCandidate>[] scheduledBuilds;
            private readonly int prefetchWindow;
            private int nextScheduledIndex;
            private int abandoned;

            public OrderedSkeletonCandidatePrefetcher(
                IReadOnlyList<IList<Point3d>> orderedSkeletons,
                int prefetchWindow,
                Func<IList<Point3d>, bool> isAlreadyEvaluated,
                Func<IList<Point3d>, LinkageRouteCandidate> buildCandidate)
            {
                this.orderedSkeletons = orderedSkeletons;
                this.prefetchWindow = Math.Max(1, prefetchWindow);
                this.isAlreadyEvaluated = isAlreadyEvaluated;
                this.buildCandidate = buildCandidate;
                scheduledBuilds = new Task<LinkageRouteCandidate>[orderedSkeletons.Count];
            }

            /// <summary>
            /// 返回第 index 个骨架的预取构建（窗口为 1 或已评估时为 null，由调用方
            /// 按原逻辑同步处理），并保证其后窗口内的骨架已开始构建。
            /// </summary>
            public Task<LinkageRouteCandidate> Take(int skeletonIndex)
            {
                if (prefetchWindow <= 1)
                {
                    return null;
                }

                var lastScheduledIndex = Math.Min(
                    orderedSkeletons.Count - 1,
                    skeletonIndex + prefetchWindow - 1);
                while (nextScheduledIndex <= lastScheduledIndex)
                {
                    var scheduledSkeleton = orderedSkeletons[nextScheduledIndex];
                    if (!isAlreadyEvaluated(scheduledSkeleton))
                    {
                        var copiedSkeleton = scheduledSkeleton
                            .Select(CopyPoint)
                            .ToList();
                        scheduledBuilds[nextScheduledIndex] = Task.Run(() =>
                            Volatile.Read(ref abandoned) != 0
                                ? null
                                : buildCandidate(copiedSkeleton));
                    }

                    nextScheduledIndex++;
                }

                var scheduledBuild = scheduledBuilds[skeletonIndex];
                scheduledBuilds[skeletonIndex] = null;
                return scheduledBuild;
            }

            public void Dispose()
            {
                // 提前结束后尚未开始的预取直接放弃；已开始的只构建独立新对象，
                // 不写入任何候选池，随后由请求取消或自然结束回收。
                Interlocked.Exchange(ref abandoned, 1);
            }
        }

        private static Vector3d GetSuggestedAxisSeparationDirection(
            LinkageRouteSolid movableSolid,
            LinkageRouteSolid fixedSolid)
        {
            if (movableSolid?.Center == null || fixedSolid?.Center == null)
            {
                return null;
            }

            var fixedToMovableCenter = new Vector3d(
                fixedSolid.Center,
                movableSolid.Center);
            var absoluteX = Math.Abs(fixedToMovableCenter.X);
            var absoluteY = Math.Abs(fixedToMovableCenter.Y);
            var absoluteZ = Math.Abs(fixedToMovableCenter.Z);
            if (absoluteX >= absoluteY && absoluteX >= absoluteZ)
            {
                return fixedToMovableCenter.X < 0d
                    ? new Vector3d(-1d, 0d, 0d)
                    : new Vector3d(1d, 0d, 0d);
            }

            if (absoluteY >= absoluteZ)
            {
                return fixedToMovableCenter.Y < 0d
                    ? new Vector3d(0d, -1d, 0d)
                    : new Vector3d(0d, 1d, 0d);
            }

            return fixedToMovableCenter.Z < 0d
                ? new Vector3d(0d, 0d, -1d)
                : new Vector3d(0d, 0d, 1d);
        }

        private static string FormatAxisDirection(Vector3d axisDirection)
        {
            if (axisDirection == null || axisDirection.Norm <= GeometryTolerance)
            {
                return "<未知>";
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "({0:F0},{1:F0},{2:F0})",
                axisDirection.X,
                axisDirection.Y,
                axisDirection.Z);
        }

        private static Point3d CopyPoint(Point3d point)
        {
            return GeometRiMethod.Copy(point);
        }

        private sealed class LinkageRouteStraightSegmentScanReference
        {
            public LinkageRouteSolid ScanSolid { get; set; }
            public int ScanSequenceIndex { get; set; }
            public string SourceLabel { get; set; }
            public int PlanarPartIndex { get; set; }
            public int SegmentIndexWithinPlanarPart { get; set; }
            public int SegmentCountWithinPlanarPart { get; set; }
        }
    }
}
