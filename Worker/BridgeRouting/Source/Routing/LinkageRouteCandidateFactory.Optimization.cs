using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using RouteLinkBar.Algorithm.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteCandidateFactory
    {
        private sealed class CartesianSegmentMotion
        {
            public int SegmentStartPointIndex { get; set; }
            public int CoordinateAxisIndex { get; set; }
            public double TargetCoordinate { get; set; }
            public double EstimatedPolylineLength { get; set; }
            public bool IsUShapeDetour { get; set; }
        }

        private sealed class StartHorizontalLapWidthInterval
        {
            public Vector3d WidthDirection { get; set; }
            public double MinimumCenterCoordinate { get; set; }
            public double MaximumCenterCoordinate { get; set; }
        }

        /// <summary>
        /// 单始端水平居中是最终搭接约束。路径层允许先使用固定排合法排宽
        /// 区间内的偏心姿态提出可成路方案；进入实体转换前，再把与固定始端
        /// 平行的前缀移回排宽中心，并仅改变后续首个排宽向连接段长度。
        /// 垂直搭接的首段沿固定排排宽方向，本方法保持原路径不变。
        /// </summary>
        internal static IList<Point3d>
            CreateSingleStartCenteredHorizontalLapCenterline(
                LinkageMultiGroupRouteRequest routeRequest,
                LinkageRouteGroupRequest routeGroupRequest,
                IList<Point3d> sourceCenterline)
        {
            var startTerminalAssembly = routeGroupRequest?.StartTerminal;
            if (routeRequest?.RequireSingleStartMiddleLap != true ||
                startTerminalAssembly?.Members?.Count != 1 ||
                sourceCenterline == null ||
                sourceCenterline.Count < 3 ||
                !LinkageRouteTerminalPoseFactory.TryResolveSingleMemberLapBoundary(
                    startTerminalAssembly,
                    routeGroupRequest.EndTerminal?.RouteAnchor,
                    routeGroupRequest.GetBusbarWidth(routeRequest),
                    out var allowedStartBoundaryPoint,
                    out var inwardStartLengthDirection,
                    out var fixedStartWidthDirection))
            {
                return sourceCenterline;
            }

            var firstSegmentDirection = new Vector3d(
                sourceCenterline[0],
                sourceCenterline[1]);
            if (firstSegmentDirection.Norm <= GeometryTolerance ||
                Math.Abs(firstSegmentDirection.Normalized.Dot(
                    inwardStartLengthDirection.Normalized)) <
                        1d - GeometryTolerance)
            {
                // 首段沿固定排排宽方向时属于垂直搭接，绝不能受居中开关影响。
                return sourceCenterline;
            }

            var normalizedFixedStartWidthDirection = fixedStartWidthDirection
                .Normalized;
            var currentStartWidthOffset = new Vector3d(
                    allowedStartBoundaryPoint,
                    sourceCenterline[0])
                .Dot(normalizedFixedStartWidthDirection);
            if (Math.Abs(currentStartWidthOffset) <= GeometryTolerance)
            {
                return sourceCenterline;
            }

            var widthAbsorbingSegmentStartPointIndex = -1;
            for (var segmentStartPointIndex = 1;
                segmentStartPointIndex + 1 < sourceCenterline.Count;
                segmentStartPointIndex++)
            {
                var followingSegmentDirection = new Vector3d(
                    sourceCenterline[segmentStartPointIndex],
                    sourceCenterline[segmentStartPointIndex + 1]);
                if (followingSegmentDirection.Norm <= GeometryTolerance)
                {
                    return sourceCenterline;
                }

                var followingWidthAlignment = Math.Abs(
                    followingSegmentDirection.Normalized.Dot(
                        normalizedFixedStartWidthDirection));
                if (followingWidthAlignment >= 1d - GeometryTolerance)
                {
                    widthAbsorbingSegmentStartPointIndex =
                        segmentStartPointIndex;
                    break;
                }

                if (followingWidthAlignment > GeometryTolerance)
                {
                    // 在首个排宽向线段之前不允许存在斜段，否则整体平移前缀
                    // 会改变原拓扑，不能作为居中转换处理。
                    return sourceCenterline;
                }
            }

            if (widthAbsorbingSegmentStartPointIndex < 0)
            {
                return sourceCenterline;
            }

            var centeredCenterline = sourceCenterline
                .Select(CopyPoint)
                .ToList();
            for (var translatedPrefixPointIndex = 0;
                translatedPrefixPointIndex <=
                    widthAbsorbingSegmentStartPointIndex;
                translatedPrefixPointIndex++)
            {
                centeredCenterline[translatedPrefixPointIndex] = TranslatePoint(
                    centeredCenterline[translatedPrefixPointIndex],
                    normalizedFixedStartWidthDirection,
                    -currentStartWidthOffset);
            }

            if (centeredCenterline[widthAbsorbingSegmentStartPointIndex]
                    .DistanceTo(centeredCenterline[
                        widthAbsorbingSegmentStartPointIndex + 1]) <=
                GeometryTolerance)
            {
                return sourceCenterline;
            }

            return centeredCenterline;
        }

        internal static int GetSolutionOptimizationSectionPriority(
            LinkageRouteCandidate sourceRouteCandidate,
            LinkageRouteCandidate optimizationRouteCandidate)
        {
            if (sourceRouteCandidate?.ManufacturableCenterline == null ||
                optimizationRouteCandidate?.ManufacturableCenterline == null)
            {
                return 1;
            }

            if ((optimizationRouteCandidate.TopologySignature ?? string.Empty)
                .StartsWith(
                    "OPT-START-HORIZONTAL-LAP",
                    StringComparison.Ordinal))
            {
                return 0;
            }

            var sourceCenterline = sourceRouteCandidate.ManufacturableCenterline;
            var optimizationCenterline = optimizationRouteCandidate
                .ManufacturableCenterline;
            if (sourceCenterline.Count < 2 || optimizationCenterline.Count < 2)
            {
                return 1;
            }

            var startTerminalSectionChanged = sourceCenterline[0].DistanceTo(
                    optimizationCenterline[0]) > GeometryTolerance ||
                sourceCenterline[1].DistanceTo(optimizationCenterline[1]) >
                    GeometryTolerance;
            var sourceLastPointIndex = sourceCenterline.Count - 1;
            var optimizationLastPointIndex = optimizationCenterline.Count - 1;
            var endTerminalSectionChanged = sourceCenterline[
                    sourceLastPointIndex].DistanceTo(
                    optimizationCenterline[optimizationLastPointIndex]) >
                    GeometryTolerance ||
                sourceCenterline[sourceLastPointIndex - 1].DistanceTo(
                    optimizationCenterline[optimizationLastPointIndex - 1]) >
                    GeometryTolerance;
            return startTerminalSectionChanged || endTerminalSectionChanged
                ? 0
                : 1;
        }

        internal static IReadOnlyList<LinkageRouteCandidate>
            BuildStartHorizontalLapOptimizationCandidates(
                LinkageMultiGroupRouteRequest routeRequest,
                LinkageRouteCandidate sourceRouteCandidate,
                IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
                IEnumerable<LinkageRouteCandidate> selectedOtherRouteCandidates,
                int maximumOptimizationCandidateCount,
                CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested ||
                routeRequest == null ||
                sourceRouteCandidate?.GroupRequest == null ||
                sourceRouteCandidate.ManufacturableCenterline == null ||
                sourceRouteCandidate.ManufacturableCenterline.Count < 2 ||
                sourceRouteCandidate.PlanarConversion?.IsValid != true)
            {
                return Array.Empty<LinkageRouteCandidate>();
            }

            var dynamicClearanceSolids = (fixedTerminalAndObstacleSolids ??
                    Array.Empty<LinkageRouteSolid>())
                .Concat((selectedOtherRouteCandidates ??
                        Enumerable.Empty<LinkageRouteCandidate>())
                    .Where(selectedRouteCandidate => selectedRouteCandidate != null)
                    .SelectMany(selectedRouteCandidate =>
                        selectedRouteCandidate.ScanSolids))
                .ToList();
            var localOptimizationCandidatePool = new LinkageRouteCandidatePool
            {
                GroupRequest = sourceRouteCandidate.GroupRequest
            };
            localOptimizationCandidatePool.TraversalDirections.Add(
                sourceRouteCandidate.TraversalDirection);
            var evaluatedOptimizationCenterlines =
                new Dictionary<string, LinkageRouteCandidate>(
                    StringComparer.Ordinal);
            EvaluateStartHorizontalLapOptimizationCandidates(
                localOptimizationCandidatePool,
                evaluatedOptimizationCenterlines,
                routeRequest,
                sourceRouteCandidate,
                dynamicClearanceSolids,
                cancellationToken);

            var sourceActualCopperLength = sourceRouteCandidate.PlanarConversion
                .ActualCopperLength;
            return localOptimizationCandidatePool.ValidCandidates
                .Where(optimizationCandidate =>
                    optimizationCandidate?.PlanarConversion?.IsValid == true &&
                    optimizationCandidate.PlanarConversion.ActualCopperLength +
                        GeometryTolerance < sourceActualCopperLength)
                .GroupBy(GetCandidateSignature)
                .Select(optimizationCandidateGroup => optimizationCandidateGroup
                    .OrderBy(optimizationCandidate => optimizationCandidate
                        .PlanarConversion.ActualCopperLength)
                    .ThenBy(optimizationCandidate =>
                        optimizationCandidate.GeneratedBusbarPartCount)
                    .First())
                .OrderBy(optimizationCandidate => optimizationCandidate
                    .PlanarConversion.ActualCopperLength)
                .ThenBy(optimizationCandidate =>
                    optimizationCandidate.GeneratedBusbarPartCount)
                .ThenBy(optimizationCandidate =>
                    GetSolutionOptimizationSectionPriority(
                        sourceRouteCandidate,
                        optimizationCandidate))
                .ThenBy(GetCandidateSignature, StringComparer.Ordinal)
                .Take(Math.Max(1, maximumOptimizationCandidateCount))
                .ToList();
        }

        /// <summary>
        /// 只在普通完整方案成功后的后台优化链调用。所有局部几何都重新经过
        /// BuildCandidate 的段长、折弯余量、搭接、叠排、包围盒、实体干涉和
        /// 静态绝缘校验；跨组绝缘仍由 SearchContext 在替换当前组时复核。
        /// </summary>
        internal static IReadOnlyList<LinkageRouteCandidate>
            BuildDirectGeometryOptimizationCandidates(
                LinkageMultiGroupRouteRequest routeRequest,
                LinkageRouteCandidate sourceRouteCandidate,
                IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
                IEnumerable<LinkageRouteCandidate> selectedOtherRouteCandidates,
                int maximumOptimizationCandidateCount,
                CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested ||
                routeRequest == null ||
                sourceRouteCandidate?.GroupRequest == null ||
                sourceRouteCandidate.ManufacturableCenterline == null ||
                sourceRouteCandidate.ManufacturableCenterline.Count < 2 ||
                sourceRouteCandidate.PlanarConversion?.IsValid != true)
            {
                return Array.Empty<LinkageRouteCandidate>();
            }

            var dynamicClearanceSolids = (fixedTerminalAndObstacleSolids ??
                    Array.Empty<LinkageRouteSolid>())
                .Concat((selectedOtherRouteCandidates ??
                        Enumerable.Empty<LinkageRouteCandidate>())
                    .Where(selectedRouteCandidate => selectedRouteCandidate != null)
                    .SelectMany(selectedRouteCandidate =>
                        selectedRouteCandidate.ScanSolids))
                .ToList();
            var localOptimizationCandidatePool = new LinkageRouteCandidatePool
            {
                GroupRequest = sourceRouteCandidate.GroupRequest
            };
            localOptimizationCandidatePool.TraversalDirections.Add(
                sourceRouteCandidate.TraversalDirection);
            var evaluatedOptimizationCenterlines =
                new Dictionary<string, LinkageRouteCandidate>(
                    StringComparer.Ordinal);

            AddDetourShortcutCandidates(localOptimizationCandidatePool, evaluatedOptimizationCenterlines,
                routeRequest, sourceRouteCandidate, dynamicClearanceSolids, cancellationToken);

            EvaluateStartHorizontalLapOptimizationCandidates(
                localOptimizationCandidatePool,
                evaluatedOptimizationCenterlines,
                routeRequest,
                sourceRouteCandidate,
                dynamicClearanceSolids,
                cancellationToken);
            foreach (var directTerminalCandidate in BuildTerminalDirectLapOptimizationCandidates(
                routeRequest, sourceRouteCandidate, dynamicClearanceSolids, cancellationToken))
                AddCandidate(localOptimizationCandidatePool, directTerminalCandidate);
            EvaluateCartesianLimitOptimizationCandidates(
                localOptimizationCandidatePool,
                evaluatedOptimizationCenterlines,
                routeRequest,
                sourceRouteCandidate,
                dynamicClearanceSolids,
                cancellationToken);
            EvaluateExtremeReserveConversionCandidates(
                localOptimizationCandidatePool,
                evaluatedOptimizationCenterlines,
                routeRequest,
                sourceRouteCandidate,
                dynamicClearanceSolids,
                Math.Max(1, maximumOptimizationCandidateCount),
                cancellationToken);
            foreach (var mergedMiddleCenterline in
                CreateMiddleSegmentMergeCenterlines(
                    sourceRouteCandidate.ManufacturableCenterline))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Array.Empty<LinkageRouteCandidate>();
                }

                BuildAndAddUniqueCandidate(
                    localOptimizationCandidatePool,
                    evaluatedOptimizationCenterlines,
                    routeRequest,
                    sourceRouteCandidate.GroupRequest,
                    sourceRouteCandidate.AxisSkeleton,
                    mergedMiddleCenterline,
                    dynamicClearanceSolids,
                    true,
                    "OPT-MIDDLE-MERGE",
                    sourceRouteCandidate.TraversalDirection,
                    cancellationToken);
            }

            AddSegmentReductionCandidates(localOptimizationCandidatePool,
                evaluatedOptimizationCenterlines, routeRequest, sourceRouteCandidate,
                CreateSegmentReductionCenterlines(sourceRouteCandidate.ManufacturableCenterline),
                dynamicClearanceSolids, cancellationToken);

            var sourceActualCopperLength = sourceRouteCandidate.PlanarConversion
                .ActualCopperLength;
            return localOptimizationCandidatePool.ValidCandidates
                .Where(optimizationCandidate =>
                    optimizationCandidate?.PlanarConversion?.IsValid == true &&
                    (optimizationCandidate.PlanarConversion.ActualCopperLength +
                        GeometryTolerance < sourceActualCopperLength ||
                     IsImprovedOptimizationCandidate(optimizationCandidate, sourceRouteCandidate)))
                .GroupBy(GetCandidateSignature)
                .Select(optimizationCandidateGroup => optimizationCandidateGroup
                    .OrderBy(optimizationCandidate => optimizationCandidate
                        .PlanarConversion.ActualCopperLength)
                    .ThenBy(optimizationCandidate =>
                        optimizationCandidate.GeneratedBusbarPartCount)
                    .First())
                .OrderBy(optimizationCandidate => optimizationCandidate
                    .PlanarConversion.ActualCopperLength)
                .ThenBy(optimizationCandidate =>
                    optimizationCandidate.GeneratedBusbarPartCount)
                .ThenBy(optimizationCandidate =>
                    GetSolutionOptimizationSectionPriority(
                        sourceRouteCandidate,
                        optimizationCandidate))
                .ThenBy(GetCandidateSignature, StringComparer.Ordinal)
                .Take(Math.Max(1, maximumOptimizationCandidateCount))
                .ToList();
        }

        private static void EvaluateStartHorizontalLapOptimizationCandidates(
            LinkageRouteCandidatePool localOptimizationCandidatePool,
            IDictionary<string, LinkageRouteCandidate>
                evaluatedOptimizationCenterlines,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteCandidate sourceRouteCandidate,
            IReadOnlyList<LinkageRouteSolid> dynamicClearanceSolids,
            CancellationToken cancellationToken)
        {
            var optimizedTerminalLapCenterlines =
                CreateStartHorizontalLapOptimizationCenterlines(
                        routeRequest,
                        sourceRouteCandidate)
                    .Concat(CreateEndHorizontalLapOptimizationCenterlines(
                        routeRequest,
                        sourceRouteCandidate))
                    .GroupBy(GetCenterlineSignature, StringComparer.Ordinal)
                    .Select(centerlineGroup => centerlineGroup.First());
            foreach (var optimizedStartLapCenterline in
                optimizedTerminalLapCenterlines)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var evaluatedStartLapMovementFactors =
                    new Dictionary<double, bool>
                    {
                        { 0d, true }
                    };
                foreach (var startLapMovementFactor in new[]
                {
                    0.25d,
                    0.5d,
                    0.75d,
                    1d
                })
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var startLapMovementCandidate =
                        BuildStartHorizontalLapMotionCandidate(
                            localOptimizationCandidatePool,
                            evaluatedOptimizationCenterlines,
                            routeRequest,
                            sourceRouteCandidate,
                            optimizedStartLapCenterline,
                            dynamicClearanceSolids,
                            startLapMovementFactor,
                            cancellationToken);
                    evaluatedStartLapMovementFactors[startLapMovementFactor] =
                        startLapMovementCandidate?.IsValid == true;
                }

                var largestValidStartLapMovementFactor =
                    evaluatedStartLapMovementFactors
                        .Where(movementFactorPair => movementFactorPair.Value)
                        .Select(movementFactorPair => movementFactorPair.Key)
                        .Max();
                if (largestValidStartLapMovementFactor >=
                    1d - GeometryTolerance)
                {
                    continue;
                }

                var smallestInvalidStartLapMovementFactor =
                    evaluatedStartLapMovementFactors
                        .Where(movementFactorPair =>
                            !movementFactorPair.Value &&
                            movementFactorPair.Key >
                                largestValidStartLapMovementFactor)
                        .Select(movementFactorPair => movementFactorPair.Key)
                        .DefaultIfEmpty(1d)
                        .Min();
                var totalStartLapMovement = sourceRouteCandidate
                    .ManufacturableCenterline[0]
                    .DistanceTo(optimizedStartLapCenterline[0]);
                var minimumStartLapRefinementStep = Math.Max(
                    0.05d,
                    routeRequest.SearchOptions?.MinimumAdaptiveRepairStep ??
                        0.5d);
                for (var startLapRefinementIndex = 0;
                    startLapRefinementIndex <
                        routeRequest.SearchOptions
                            .MaximumCartesianBoundaryRefinementCount &&
                    (smallestInvalidStartLapMovementFactor -
                        largestValidStartLapMovementFactor) *
                        totalStartLapMovement >
                    minimumStartLapRefinementStep;
                    startLapRefinementIndex++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var refinedStartLapMovementFactor =
                        (largestValidStartLapMovementFactor +
                            smallestInvalidStartLapMovementFactor) / 2d;
                    var refinedStartLapMovementCandidate =
                        BuildStartHorizontalLapMotionCandidate(
                            localOptimizationCandidatePool,
                            evaluatedOptimizationCenterlines,
                            routeRequest,
                            sourceRouteCandidate,
                            optimizedStartLapCenterline,
                            dynamicClearanceSolids,
                            refinedStartLapMovementFactor,
                            cancellationToken);
                    if (refinedStartLapMovementCandidate?.IsValid == true)
                    {
                        largestValidStartLapMovementFactor =
                            refinedStartLapMovementFactor;
                    }
                    else
                    {
                        smallestInvalidStartLapMovementFactor =
                            refinedStartLapMovementFactor;
                    }
                }
            }
        }

        private static LinkageRouteCandidate
            BuildStartHorizontalLapMotionCandidate(
                LinkageRouteCandidatePool localOptimizationCandidatePool,
                IDictionary<string, LinkageRouteCandidate>
                    evaluatedOptimizationCenterlines,
                LinkageMultiGroupRouteRequest routeRequest,
                LinkageRouteCandidate sourceRouteCandidate,
                IList<Point3d> optimizedStartLapCenterline,
                IReadOnlyList<LinkageRouteSolid> dynamicClearanceSolids,
                double startLapMovementFactor,
                CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var sourceStartLapCenterline = sourceRouteCandidate
                .ManufacturableCenterline;
            var interpolatedStartLapCenterline = sourceStartLapCenterline
                .Select((sourceCenterlinePoint, sourceCenterlinePointIndex) =>
                    new Point3d(
                        sourceCenterlinePoint.X +
                            (optimizedStartLapCenterline[
                                sourceCenterlinePointIndex].X -
                             sourceCenterlinePoint.X) *
                                startLapMovementFactor,
                        sourceCenterlinePoint.Y +
                            (optimizedStartLapCenterline[
                                sourceCenterlinePointIndex].Y -
                             sourceCenterlinePoint.Y) *
                                startLapMovementFactor,
                        sourceCenterlinePoint.Z +
                            (optimizedStartLapCenterline[
                                sourceCenterlinePointIndex].Z -
                             sourceCenterlinePoint.Z) *
                                startLapMovementFactor))
                .ToList();
            return BuildAndAddUniqueCandidate(
                localOptimizationCandidatePool,
                evaluatedOptimizationCenterlines,
                routeRequest,
                sourceRouteCandidate.GroupRequest,
                sourceRouteCandidate.AxisSkeleton,
                interpolatedStartLapCenterline,
                dynamicClearanceSolids,
                true,
                "OPT-START-HORIZONTAL-LAP",
                sourceRouteCandidate.TraversalDirection,
                cancellationToken);
        }

        /// <summary>
        /// 仅供成功方案后台最短化使用。始端水平搭接的首段作为刚体沿固定
        /// 排排宽方向移动；把下一控制点投影到合法搭接中心区间即可得到该
        /// 相邻段的连续最短位置。双始端使用共同固定排时，候选不再被某一
        /// 始端排的宽度区间提前截断，而是允许生成排先按路径最短位置搭接，
        /// 再由连接排沿排长方向延长并完成必要的垂直搭接；最终实体覆盖和
        /// 干涉校验仍在候选输出阶段执行。主搜索仍只枚举原有离散姿态，
        /// 垂直搭接和开启“单始端水平居中”的单始端方案均不会进入此入口。
        /// </summary>
        private static IReadOnlyList<IList<Point3d>>
            CreateStartHorizontalLapOptimizationCenterlines(
                LinkageMultiGroupRouteRequest routeRequest,
                LinkageRouteCandidate sourceRouteCandidate)
        {
            var sourceRouteCenterline = sourceRouteCandidate
                ?.ManufacturableCenterline;
            var sourceRouteGroupRequest = sourceRouteCandidate?.GroupRequest;
            var sourceStartTerminalAssembly = sourceRouteGroupRequest
                ?.StartTerminal;
            var sourceStartTerminalMemberCount = sourceStartTerminalAssembly
                ?.Members?.Count ?? 0;
            if (routeRequest == null ||
                sourceRouteCenterline == null ||
                sourceRouteCenterline.Count < 3 ||
                sourceRouteCandidate.StartPerpendicularLapVariant !=
                    LinkageStartPerpendicularLapVariant.None ||
                sourceStartTerminalMemberCount < 1 ||
                (sourceStartTerminalMemberCount == 1 &&
                 routeRequest.RequireSingleStartMiddleLap))
            {
                return Array.Empty<IList<Point3d>>();
            }

            var sourceTerminalSegmentDirection = new Vector3d(
                sourceRouteCenterline[0],
                sourceRouteCenterline[1]);
            if (sourceTerminalSegmentDirection.Norm <= GeometryTolerance)
            {
                return Array.Empty<IList<Point3d>>();
            }

            sourceTerminalSegmentDirection =
                sourceTerminalSegmentDirection.Normalized;
            var terminalRigidPrefixLastPointIndex = 1;
            while (terminalRigidPrefixLastPointIndex + 1 <
                sourceRouteCenterline.Count)
            {
                var followingTerminalSegmentDirection = new Vector3d(
                    sourceRouteCenterline[terminalRigidPrefixLastPointIndex],
                    sourceRouteCenterline[
                        terminalRigidPrefixLastPointIndex + 1]);
                if (followingTerminalSegmentDirection.Norm <=
                        GeometryTolerance ||
                    followingTerminalSegmentDirection.Normalized.Dot(
                        sourceTerminalSegmentDirection) < 1d - 1e-6d)
                {
                    break;
                }

                terminalRigidPrefixLastPointIndex++;
            }

            if (terminalRigidPrefixLastPointIndex + 1 >=
                sourceRouteCenterline.Count)
            {
                return Array.Empty<IList<Point3d>>();
            }

            // 这里移动的是始端搭接排的水平搭接位置。普通单始端仍使用
            // 固定排自身的合法中心区间；双始端共同固定排则只用该区间
            // 确定方向，不用它把更短的生成排位置提前过滤掉。
            var generatedRouteBusbarWidth = sourceRouteGroupRequest
                .GetStartLapBusbarWidth(routeRequest);
            if (generatedRouteBusbarWidth <= GeometryTolerance)
            {
                return Array.Empty<IList<Point3d>>();
            }

            PlanarBendBusbarSetting collectiveStartHorizontalLapSupport = null;
            var horizontalLapSupportSettings = sourceStartTerminalAssembly
                .Members
                .Select(startTerminalMember =>
                    startTerminalMember?.FixedBusbar)
                .Where(fixedBusbarSetting =>
                    fixedBusbarSetting?.PointList != null &&
                    fixedBusbarSetting.PointList.Count >= 2)
                .ToList();
            if (sourceStartTerminalMemberCount >= 2 &&
                routeRequest.AllowDoubleStartMiddleLap &&
                LinkageRouteDoubleStartLapGeometry.TryCreateCollectiveFixedBusbar(
                    sourceStartTerminalAssembly,
                    out collectiveStartHorizontalLapSupport))
            {
                horizontalLapSupportSettings.Insert(
                    0,
                    collectiveStartHorizontalLapSupport);
            }

            var sourceRouteLength = GetOptimizationPolylineLength(
                sourceRouteCenterline);
            var optimizedStartHorizontalLapCenterlines =
                new Dictionary<string, IList<Point3d>>(StringComparer.Ordinal);
            foreach (var horizontalLapSupportSetting in
                horizontalLapSupportSettings)
            {
                var usesCollectiveDoubleStartHorizontalLap =
                    collectiveStartHorizontalLapSupport != null &&
                    ReferenceEquals(
                        horizontalLapSupportSetting,
                        collectiveStartHorizontalLapSupport);
                if (horizontalLapSupportSetting.WidthVector == null ||
                    horizontalLapSupportSetting.WidthVector.Norm <=
                        GeometryTolerance ||
                    horizontalLapSupportSetting.BusBarWidth +
                        GeometryTolerance < generatedRouteBusbarWidth)
                {
                    continue;
                }

                var horizontalLapWidthDirection = horizontalLapSupportSetting
                    .WidthVector.Normalized;
                if (Math.Abs(sourceTerminalSegmentDirection.Dot(
                        horizontalLapWidthDirection)) > 1e-6d)
                {
                    continue;
                }

                var horizontalLapSupportCenterCoordinate =
                    GetDirectionCoordinate(
                        horizontalLapSupportSetting.PointList[0],
                        horizontalLapWidthDirection);
                var maximumHorizontalLapCenterOffset = Math.Max(
                    0d,
                    (horizontalLapSupportSetting.BusBarWidth -
                        generatedRouteBusbarWidth) / 2d);
                var horizontalLapWidthInterval =
                    new StartHorizontalLapWidthInterval
                    {
                        WidthDirection = horizontalLapWidthDirection,
                        MinimumCenterCoordinate =
                            horizontalLapSupportCenterCoordinate -
                            maximumHorizontalLapCenterOffset,
                        MaximumCenterCoordinate =
                            horizontalLapSupportCenterCoordinate +
                            maximumHorizontalLapCenterOffset
                    };
                var sourceStartLapCenterCoordinate = GetDirectionCoordinate(
                    sourceRouteCenterline[0],
                    horizontalLapWidthInterval.WidthDirection);
                if (!usesCollectiveDoubleStartHorizontalLap &&
                    (sourceStartLapCenterCoordinate <
                         horizontalLapWidthInterval.MinimumCenterCoordinate -
                             GeometryTolerance ||
                     sourceStartLapCenterCoordinate >
                         horizontalLapWidthInterval.MaximumCenterCoordinate +
                             GeometryTolerance))
                {
                    continue;
                }

                var followingControlPointCoordinate = GetDirectionCoordinate(
                    sourceRouteCenterline[
                        terminalRigidPrefixLastPointIndex + 1],
                    horizontalLapWidthInterval.WidthDirection);
                var shortestContainedStartLapCoordinate =
                    usesCollectiveDoubleStartHorizontalLap
                        ? followingControlPointCoordinate
                        : Math.Max(
                            horizontalLapWidthInterval.MinimumCenterCoordinate,
                            Math.Min(
                                horizontalLapWidthInterval.MaximumCenterCoordinate,
                                followingControlPointCoordinate));
                var startLapWidthMovement =
                    shortestContainedStartLapCoordinate -
                    sourceStartLapCenterCoordinate;
                if (Math.Abs(startLapWidthMovement) <= GeometryTolerance)
                {
                    continue;
                }

                var optimizedStartLapCenterline = sourceRouteCenterline
                    .Select(CopyPoint)
                    .ToList();
                for (var terminalRigidPointIndex = 0;
                    terminalRigidPointIndex <=
                        terminalRigidPrefixLastPointIndex;
                    terminalRigidPointIndex++)
                {
                    optimizedStartLapCenterline[terminalRigidPointIndex] =
                        TranslatePoint(
                            optimizedStartLapCenterline[
                                terminalRigidPointIndex],
                            horizontalLapWidthInterval.WidthDirection,
                            startLapWidthMovement);
                }

                if (GetOptimizationPolylineLength(
                        optimizedStartLapCenterline) + GeometryTolerance >=
                    sourceRouteLength)
                {
                    continue;
                }

                optimizedStartHorizontalLapCenterlines[
                    GetCenterlineSignature(optimizedStartLapCenterline)] =
                        optimizedStartLapCenterline;
            }

            return optimizedStartHorizontalLapCenterlines.Values.ToList();
        }

        /// <summary>
        /// 成功方案的柜顶水平搭接最短化。末端刚性直段沿实际柜顶搭接排的
        /// 排宽方向移动，合法中心区间使用柜顶搭接段排宽，不改变主搜索候选。
        /// </summary>
        private static IReadOnlyList<IList<Point3d>>
            CreateEndHorizontalLapOptimizationCenterlines(
                LinkageMultiGroupRouteRequest routeRequest,
                LinkageRouteCandidate sourceRouteCandidate)
        {
            var sourceRouteCenterline = sourceRouteCandidate
                ?.ManufacturableCenterline;
            var sourceRouteGroupRequest = sourceRouteCandidate?.GroupRequest;
            var sourceEndTerminalAssembly = sourceRouteGroupRequest
                ?.EndTerminal;
            if (routeRequest == null ||
                sourceRouteCenterline == null ||
                sourceRouteCenterline.Count < 3 ||
                sourceEndTerminalAssembly?.Members == null ||
                sourceEndTerminalAssembly.Members.Count == 0)
            {
                return Array.Empty<IList<Point3d>>();
            }

            var terminalEndpointIndex = sourceRouteCenterline.Count - 1;
            var sourceTerminalSegmentDirection = new Vector3d(
                sourceRouteCenterline[terminalEndpointIndex],
                sourceRouteCenterline[terminalEndpointIndex - 1]);
            if (sourceTerminalSegmentDirection.Norm <= GeometryTolerance)
            {
                return Array.Empty<IList<Point3d>>();
            }

            sourceTerminalSegmentDirection =
                sourceTerminalSegmentDirection.Normalized;
            var terminalRigidSuffixFirstPointIndex = terminalEndpointIndex - 1;
            while (terminalRigidSuffixFirstPointIndex - 1 >= 0)
            {
                var followingInteriorSegmentDirection = new Vector3d(
                    sourceRouteCenterline[terminalRigidSuffixFirstPointIndex],
                    sourceRouteCenterline[
                        terminalRigidSuffixFirstPointIndex - 1]);
                if (followingInteriorSegmentDirection.Norm <=
                        GeometryTolerance ||
                    followingInteriorSegmentDirection.Normalized.Dot(
                        sourceTerminalSegmentDirection) < 1d - 1e-6d)
                {
                    break;
                }

                terminalRigidSuffixFirstPointIndex--;
            }

            if (terminalRigidSuffixFirstPointIndex - 1 < 0)
            {
                return Array.Empty<IList<Point3d>>();
            }

            var generatedCabinetTopLapBusbarWidth = sourceRouteGroupRequest
                .GetCabinetTopLapBusbarWidth(routeRequest);
            if (generatedCabinetTopLapBusbarWidth <= GeometryTolerance)
            {
                return Array.Empty<IList<Point3d>>();
            }

            var cabinetTopHorizontalLapSupportSettings =
                sourceEndTerminalAssembly.Members
                    .Select(endTerminalMember =>
                        endTerminalMember?.FixedBusbar)
                    .Where(fixedBusbarSetting =>
                        fixedBusbarSetting?.PointList != null &&
                        fixedBusbarSetting.PointList.Count >= 2)
                    .ToList();
            var sourceRouteLength = GetOptimizationPolylineLength(
                sourceRouteCenterline);
            var optimizedEndHorizontalLapCenterlines =
                new Dictionary<string, IList<Point3d>>(StringComparer.Ordinal);
            foreach (var cabinetTopHorizontalLapSupportSetting in
                cabinetTopHorizontalLapSupportSettings)
            {
                if (cabinetTopHorizontalLapSupportSetting.WidthVector == null ||
                    cabinetTopHorizontalLapSupportSetting.WidthVector.Norm <=
                        GeometryTolerance ||
                    cabinetTopHorizontalLapSupportSetting.BusBarWidth +
                        GeometryTolerance < generatedCabinetTopLapBusbarWidth)
                {
                    continue;
                }

                var cabinetTopLapWidthDirection =
                    cabinetTopHorizontalLapSupportSetting.WidthVector.Normalized;
                if (Math.Abs(sourceTerminalSegmentDirection.Dot(
                        cabinetTopLapWidthDirection)) > 1e-6d)
                {
                    continue;
                }

                var cabinetTopSupportCenterCoordinate = GetDirectionCoordinate(
                    cabinetTopHorizontalLapSupportSetting.PointList[0],
                    cabinetTopLapWidthDirection);
                var maximumCabinetTopLapCenterOffset = Math.Max(
                    0d,
                    (cabinetTopHorizontalLapSupportSetting.BusBarWidth -
                        generatedCabinetTopLapBusbarWidth) / 2d);
                var cabinetTopLapWidthInterval =
                    new StartHorizontalLapWidthInterval
                    {
                        WidthDirection = cabinetTopLapWidthDirection,
                        MinimumCenterCoordinate =
                            cabinetTopSupportCenterCoordinate -
                            maximumCabinetTopLapCenterOffset,
                        MaximumCenterCoordinate =
                            cabinetTopSupportCenterCoordinate +
                            maximumCabinetTopLapCenterOffset
                    };
                var sourceEndLapCenterCoordinate = GetDirectionCoordinate(
                    sourceRouteCenterline[terminalEndpointIndex],
                    cabinetTopLapWidthDirection);
                if (sourceEndLapCenterCoordinate <
                        cabinetTopLapWidthInterval.MinimumCenterCoordinate -
                            GeometryTolerance ||
                    sourceEndLapCenterCoordinate >
                        cabinetTopLapWidthInterval.MaximumCenterCoordinate +
                            GeometryTolerance)
                {
                    continue;
                }

                var precedingControlPointCoordinate = GetDirectionCoordinate(
                    sourceRouteCenterline[
                        terminalRigidSuffixFirstPointIndex - 1],
                    cabinetTopLapWidthDirection);
                var shortestContainedEndLapCoordinate = Math.Max(
                    cabinetTopLapWidthInterval.MinimumCenterCoordinate,
                    Math.Min(
                        cabinetTopLapWidthInterval.MaximumCenterCoordinate,
                        precedingControlPointCoordinate));
                var endLapWidthMovement =
                    shortestContainedEndLapCoordinate -
                    sourceEndLapCenterCoordinate;
                if (Math.Abs(endLapWidthMovement) <= GeometryTolerance)
                {
                    continue;
                }

                var optimizedEndLapCenterline = sourceRouteCenterline
                    .Select(CopyPoint)
                    .ToList();
                for (var terminalRigidPointIndex =
                        terminalRigidSuffixFirstPointIndex;
                    terminalRigidPointIndex <= terminalEndpointIndex;
                    terminalRigidPointIndex++)
                {
                    optimizedEndLapCenterline[terminalRigidPointIndex] =
                        TranslatePoint(
                            optimizedEndLapCenterline[
                                terminalRigidPointIndex],
                            cabinetTopLapWidthDirection,
                            endLapWidthMovement);
                }

                if (GetOptimizationPolylineLength(
                        optimizedEndLapCenterline) + GeometryTolerance >=
                    sourceRouteLength)
                {
                    continue;
                }

                optimizedEndHorizontalLapCenterlines[
                    GetCenterlineSignature(optimizedEndLapCenterline)] =
                        optimizedEndLapCenterline;
            }

            return optimizedEndHorizontalLapCenterlines.Values.ToList();
        }

        private static double GetDirectionCoordinate(
            Point3d sourcePoint,
            Vector3d coordinateDirection)
        {
            return sourcePoint.X * coordinateDirection.X +
                sourcePoint.Y * coordinateDirection.Y +
                sourcePoint.Z * coordinateDirection.Z;
        }

        /// <summary>
        /// 以已成功方案中的单组中心线为种子，沿当前最小净距对给出的真实
        /// 分离方向移动一段或连续多段的内部控制点。首末端点保持不变，
        /// 每个结果仍通过 BuildCandidate 的折弯余量、段长、实体和目标净距
        /// 校验；因此这是对既有方案的局部几何优化，不是重新搜索所有组。
        /// </summary>
        internal static IReadOnlyList<LinkageRouteCandidate>
            BuildClearanceDirectedGeometryOptimizationCandidates(
                LinkageMultiGroupRouteRequest routeRequest,
                LinkageRouteCandidate sourceRouteCandidate,
                IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
                IEnumerable<LinkageRouteCandidate> selectedOtherRouteCandidates,
                Vector3d separationDirection,
                double separationDistance,
                int maximumOptimizationCandidateCount,
                CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested ||
                routeRequest == null ||
                sourceRouteCandidate?.GroupRequest == null ||
                sourceRouteCandidate.ManufacturableCenterline == null ||
                sourceRouteCandidate.ManufacturableCenterline.Count < 4 ||
                separationDirection == null ||
                separationDirection.Norm <= GeometryTolerance ||
                separationDistance <= GeometryTolerance)
            {
                return Array.Empty<LinkageRouteCandidate>();
            }

            var dynamicClearanceSolids = (fixedTerminalAndObstacleSolids ??
                    Array.Empty<LinkageRouteSolid>())
                .Concat((selectedOtherRouteCandidates ??
                        Enumerable.Empty<LinkageRouteCandidate>())
                    .Where(routeCandidate => routeCandidate != null)
                    .SelectMany(routeCandidate => routeCandidate.ScanSolids))
                .ToList();
            var optimizationCandidatePool = new LinkageRouteCandidatePool
            {
                GroupRequest = sourceRouteCandidate.GroupRequest
            };
            optimizationCandidatePool.TraversalDirections.Add(
                sourceRouteCandidate.TraversalDirection);
            var evaluatedCenterlines = new Dictionary<string,
                LinkageRouteCandidate>(StringComparer.Ordinal);
            var movementDirections = new[]
            {
                separationDirection.Normalized,
                new Vector3d(1d, 0d, 0d),
                new Vector3d(-1d, 0d, 0d),
                new Vector3d(0d, 1d, 0d),
                new Vector3d(0d, -1d, 0d),
                new Vector3d(0d, 0d, 1d),
                new Vector3d(0d, 0d, -1d)
            }
                .GroupBy(movementDirection => string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:F6},{1:F6},{2:F6}",
                    movementDirection.X,
                    movementDirection.Y,
                    movementDirection.Z))
                .Select(movementDirectionGroup =>
                    movementDirectionGroup.First())
                .ToList();
            var sourceCenterline = sourceRouteCandidate.ManufacturableCenterline;
            foreach (var movementDirection in movementDirections)
            {
                for (var firstMovedPointIndex = 1;
                    firstMovedPointIndex < sourceCenterline.Count - 1;
                    firstMovedPointIndex++)
                {
                    for (var lastMovedPointIndex = firstMovedPointIndex;
                        lastMovedPointIndex < sourceCenterline.Count - 1;
                        lastMovedPointIndex++)
                    {
                        foreach (var movementFactor in new[]
                        {
                            0.5d,
                            1d,
                            1.5d
                        })
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                return Array.Empty<LinkageRouteCandidate>();
                            }

                            var movedCenterline = sourceCenterline
                                .Select(CopyPoint)
                                .ToList();
                            for (var movedPointIndex = firstMovedPointIndex;
                                movedPointIndex <= lastMovedPointIndex;
                                movedPointIndex++)
                            {
                                movedCenterline[movedPointIndex] = TranslatePoint(
                                    movedCenterline[movedPointIndex],
                                    movementDirection,
                                    separationDistance * movementFactor);
                            }

                            BuildAndAddUniqueCandidate(
                                optimizationCandidatePool,
                                evaluatedCenterlines,
                                routeRequest,
                                sourceRouteCandidate.GroupRequest,
                                sourceRouteCandidate.AxisSkeleton ?? sourceCenterline,
                                movedCenterline,
                                dynamicClearanceSolids,
                                true,
                                "OPT-CLEARANCE-DIRECTED",
                                sourceRouteCandidate.TraversalDirection,
                                cancellationToken);
                            if (optimizationCandidatePool.ValidCandidates.Count >=
                                Math.Max(1, maximumOptimizationCandidateCount))
                            {
                                return optimizationCandidatePool.ValidCandidates
                                    .Take(Math.Max(
                                        1,
                                        maximumOptimizationCandidateCount))
                                    .ToList();
                            }
                        }
                    }
                }
            }

            return optimizationCandidatePool.ValidCandidates
                .Take(Math.Max(1, maximumOptimizationCandidateCount))
                .ToList();
        }

        private static void EvaluateExtremeReserveConversionCandidates(
            LinkageRouteCandidatePool localOptimizationCandidatePool,
            IDictionary<string, LinkageRouteCandidate>
                evaluatedOptimizationCenterlines,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteCandidate sourceRouteCandidate,
            IReadOnlyList<LinkageRouteSolid> dynamicClearanceSolids,
            int maximumOptimizationCandidateCount,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested ||
                routeRequest.RequireAxisAlignedRoutes ||
                sourceRouteCandidate.AxisSkeleton == null ||
                sourceRouteCandidate.AxisSkeleton.Count < 3)
            {
                return;
            }

            var extremeReserveConversionContext = CreateConversionContext(
                routeRequest,
                sourceRouteCandidate.GroupRequest,
                sourceRouteCandidate.AxisSkeleton,
                dynamicClearanceSolids);
            foreach (var extremeReserveConversionCandidate in
                LinkageRouteConversionCandidate.CreateContinuousCandidates(
                        sourceRouteCandidate.AxisSkeleton,
                        extremeReserveConversionContext)
                    .Where(conversionCandidate => conversionCandidate.IsConverted)
                    .Take(Math.Max(1, maximumOptimizationCandidateCount)))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                BuildAndAddUniqueCandidate(
                    localOptimizationCandidatePool,
                    evaluatedOptimizationCenterlines,
                    routeRequest,
                    sourceRouteCandidate.GroupRequest,
                    sourceRouteCandidate.AxisSkeleton,
                    extremeReserveConversionCandidate.Centerline,
                    dynamicClearanceSolids,
                    true,
                    "OPT-EXTREME-" +
                        extremeReserveConversionCandidate.TopologySignature,
                    sourceRouteCandidate.TraversalDirection,
                    cancellationToken);
            }
        }

        private static void EvaluateCartesianLimitOptimizationCandidates(
            LinkageRouteCandidatePool localOptimizationCandidatePool,
            IDictionary<string, LinkageRouteCandidate>
                evaluatedOptimizationCenterlines,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteCandidate sourceRouteCandidate,
            IReadOnlyList<LinkageRouteSolid> dynamicClearanceSolids,
            CancellationToken cancellationToken)
        {
            var cartesianSegmentMotions = CreateCartesianSegmentMotions(
                sourceRouteCandidate.ManufacturableCenterline,
                routeRequest.SearchOptions
                    .MaximumCartesianOptimizationMotionCount);
            foreach (var cartesianSegmentMotion in cartesianSegmentMotions)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var evaluatedMotionFactors = new Dictionary<double, bool>();
                foreach (var cartesianMotionFactor in new[]
                {
                    0.25d,
                    0.5d,
                    0.75d,
                    1d
                })
                {
                    var cartesianMotionCandidate =
                        BuildCartesianMotionCandidate(
                            localOptimizationCandidatePool,
                            evaluatedOptimizationCenterlines,
                            routeRequest,
                            sourceRouteCandidate,
                            dynamicClearanceSolids,
                            cartesianSegmentMotion,
                            cartesianMotionFactor,
                            cancellationToken);
                    evaluatedMotionFactors[cartesianMotionFactor] =
                        cartesianMotionCandidate?.IsValid == true;
                }

                var largestValidMotionFactor = evaluatedMotionFactors
                    .Where(motionFactorPair => motionFactorPair.Value)
                    .Select(motionFactorPair => motionFactorPair.Key)
                    .DefaultIfEmpty(0d)
                    .Max();
                if (largestValidMotionFactor >= 1d - GeometryTolerance)
                {
                    continue;
                }

                var smallestInvalidMotionFactor = evaluatedMotionFactors
                    .Where(motionFactorPair =>
                        !motionFactorPair.Value &&
                        motionFactorPair.Key > largestValidMotionFactor)
                    .Select(motionFactorPair => motionFactorPair.Key)
                    .DefaultIfEmpty(1d)
                    .Min();
                var sourceCoordinate = GetPointCoordinate(
                    sourceRouteCandidate.ManufacturableCenterline[
                        cartesianSegmentMotion.SegmentStartPointIndex],
                    cartesianSegmentMotion.CoordinateAxisIndex);
                var totalCoordinateMovement = Math.Abs(
                    cartesianSegmentMotion.TargetCoordinate - sourceCoordinate);
                var minimumCartesianRefinementStep = Math.Max(
                    0.05d,
                    routeRequest.SearchOptions?.MinimumAdaptiveRepairStep ??
                        0.5d);
                for (var cartesianRefinementIndex = 0;
                    cartesianRefinementIndex <
                        routeRequest.SearchOptions
                            .MaximumCartesianBoundaryRefinementCount &&
                    (smallestInvalidMotionFactor - largestValidMotionFactor) *
                        totalCoordinateMovement >
                        minimumCartesianRefinementStep;
                    cartesianRefinementIndex++)
                {
                    var refinedMotionFactor =
                        (largestValidMotionFactor + smallestInvalidMotionFactor) /
                        2d;
                    var refinedMotionCandidate = BuildCartesianMotionCandidate(
                        localOptimizationCandidatePool,
                        evaluatedOptimizationCenterlines,
                        routeRequest,
                        sourceRouteCandidate,
                        dynamicClearanceSolids,
                        cartesianSegmentMotion,
                        refinedMotionFactor,
                        cancellationToken);
                    if (refinedMotionCandidate?.IsValid == true)
                    {
                        largestValidMotionFactor = refinedMotionFactor;
                    }
                    else
                    {
                        smallestInvalidMotionFactor = refinedMotionFactor;
                    }
                }
            }
        }

        private static LinkageRouteCandidate BuildCartesianMotionCandidate(
            LinkageRouteCandidatePool localOptimizationCandidatePool,
            IDictionary<string, LinkageRouteCandidate>
                evaluatedOptimizationCenterlines,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteCandidate sourceRouteCandidate,
            IReadOnlyList<LinkageRouteSolid> dynamicClearanceSolids,
            CartesianSegmentMotion cartesianSegmentMotion,
            double cartesianMotionFactor,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var cartesianLimitCenterline = CreateCartesianMotionCenterline(
                sourceRouteCandidate.ManufacturableCenterline,
                cartesianSegmentMotion.SegmentStartPointIndex,
                cartesianSegmentMotion.CoordinateAxisIndex,
                cartesianSegmentMotion.TargetCoordinate,
                cartesianMotionFactor);
            return BuildAndAddUniqueCandidate(
                localOptimizationCandidatePool,
                evaluatedOptimizationCenterlines,
                routeRequest,
                sourceRouteCandidate.GroupRequest,
                sourceRouteCandidate.AxisSkeleton,
                cartesianLimitCenterline,
                dynamicClearanceSolids,
                true,
                "OPT-CARTESIAN-LIMIT",
                sourceRouteCandidate.TraversalDirection,
                cancellationToken);
        }

        private static IReadOnlyList<CartesianSegmentMotion>
            CreateCartesianSegmentMotions(
            IList<Point3d> sourceCenterline,
            int maximumCartesianOptimizationMotionCount)
        {
            if (sourceCenterline == null || sourceCenterline.Count < 4)
            {
                return Array.Empty<CartesianSegmentMotion>();
            }

            var sourcePolylineLength = GetOptimizationPolylineLength(
                sourceCenterline);
            var cartesianSegmentMotions = new List<CartesianSegmentMotion>();
            var registeredMotionSignatures = new HashSet<string>(
                StringComparer.Ordinal);
            for (var segmentStartPointIndex = 1;
                segmentStartPointIndex + 2 < sourceCenterline.Count;
                segmentStartPointIndex++)
            {
                var segmentStartPoint = sourceCenterline[
                    segmentStartPointIndex];
                var segmentEndPoint = sourceCenterline[
                    segmentStartPointIndex + 1];
                var precedingPoint = sourceCenterline[
                    segmentStartPointIndex - 1];
                var followingPoint = sourceCenterline[
                    segmentStartPointIndex + 2];
                var precedingSegmentDirection = new Vector3d(
                    precedingPoint,
                    segmentStartPoint);
                var followingSegmentDirection = new Vector3d(
                    segmentEndPoint,
                    followingPoint);
                var isUShapeDetour = precedingSegmentDirection.Norm >
                        GeometryTolerance &&
                    followingSegmentDirection.Norm > GeometryTolerance &&
                    precedingSegmentDirection.Normalized.Dot(
                        followingSegmentDirection.Normalized) <
                        -1d + 1e-6d;
                for (var coordinateAxisIndex = 0;
                    coordinateAxisIndex < 3;
                    coordinateAxisIndex++)
                {
                    var segmentStartCoordinate = GetPointCoordinate(
                        segmentStartPoint,
                        coordinateAxisIndex);
                    if (Math.Abs(segmentStartCoordinate - GetPointCoordinate(
                            segmentEndPoint,
                            coordinateAxisIndex)) > GeometryTolerance)
                    {
                        continue;
                    }

                    foreach (var targetCoordinate in new[]
                    {
                        GetPointCoordinate(precedingPoint, coordinateAxisIndex),
                        GetPointCoordinate(followingPoint, coordinateAxisIndex)
                    }.Distinct())
                    {
                        if (Math.Abs(targetCoordinate - segmentStartCoordinate) <=
                            GeometryTolerance)
                        {
                            continue;
                        }

                        var motionSignature = string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "{0}:{1}:{2:F6}",
                            segmentStartPointIndex,
                            coordinateAxisIndex,
                            targetCoordinate);
                        if (!registeredMotionSignatures.Add(motionSignature))
                        {
                            continue;
                        }

                        var estimatedPolylineLength = new[]
                            {
                                0.25d,
                                0.5d,
                                0.75d,
                                1d
                            }
                            .Select(motionFactor =>
                                GetOptimizationPolylineLength(
                                    CreateCartesianMotionCenterline(
                                        sourceCenterline,
                                        segmentStartPointIndex,
                                        coordinateAxisIndex,
                                        targetCoordinate,
                                        motionFactor)))
                            .Min();
                        if (estimatedPolylineLength + GeometryTolerance >=
                            sourcePolylineLength)
                        {
                            continue;
                        }

                        cartesianSegmentMotions.Add(new CartesianSegmentMotion
                        {
                            SegmentStartPointIndex = segmentStartPointIndex,
                            CoordinateAxisIndex = coordinateAxisIndex,
                            TargetCoordinate = targetCoordinate,
                            EstimatedPolylineLength = estimatedPolylineLength,
                            IsUShapeDetour = isUShapeDetour
                        });
                    }
                }
            }

            return cartesianSegmentMotions
                .OrderBy(cartesianSegmentMotion =>
                    cartesianSegmentMotion.SegmentStartPointIndex == 1 ||
                    cartesianSegmentMotion.SegmentStartPointIndex + 2 ==
                        sourceCenterline.Count - 1
                        ? 0
                        : 1)
                .ThenBy(cartesianSegmentMotion =>
                    cartesianSegmentMotion.IsUShapeDetour ? 0 : 1)
                .ThenBy(cartesianSegmentMotion =>
                    cartesianSegmentMotion.EstimatedPolylineLength)
                .ThenBy(cartesianSegmentMotion =>
                    cartesianSegmentMotion.SegmentStartPointIndex)
                .ThenBy(cartesianSegmentMotion =>
                    cartesianSegmentMotion.CoordinateAxisIndex)
                .ThenBy(cartesianSegmentMotion =>
                    cartesianSegmentMotion.TargetCoordinate)
                .Take(Math.Max(1, maximumCartesianOptimizationMotionCount))
                .ToList();
        }

        /// <summary>减段优化只用于 4 段及以上的排（用户 2026-11-07 要求）。</summary>
        internal const int MinimumSegmentCountForSegmentReduction = 4;

        /// <summary>
        /// 优化结果是否优于来源：更短；或总长在容差内相同但折弯更少。
        /// 两条件按（总长, 折弯数）字典序严格下降，优化链不会来回替换。
        /// </summary>
        internal static bool IsImprovedOptimizationCandidate(
            LinkageRouteCandidate optimizationCandidate,
            LinkageRouteCandidate sourceRouteCandidate)
        {
            if (optimizationCandidate?.PlanarConversion?.IsValid != true ||
                sourceRouteCandidate?.PlanarConversion == null)
                return false;
            return IsImprovedOptimizationLength(
                optimizationCandidate.PlanarConversion.ActualCopperLength,
                LinkageRouteTopologySignature.CountCenterlineBends(optimizationCandidate.ManufacturableCenterline),
                sourceRouteCandidate.PlanarConversion.ActualCopperLength,
                LinkageRouteTopologySignature.CountCenterlineBends(sourceRouteCandidate.ManufacturableCenterline));
        }

        internal static bool IsImprovedOptimizationLength(double candidateLength, int candidateBendCount,
            double sourceLength, int sourceBendCount)
        {
            if (candidateLength + 1e-8d < sourceLength)
                return true;
            return Math.Abs(candidateLength - sourceLength) <= 1e-8d &&
                candidateBendCount < sourceBendCount;
        }

        /// <summary>
        /// 减折弯段优化：4 段及以上的排，把首末段（固定端搭接段）以外两段同向段合并成一段，
        /// 夹在中间的段整体平移。中心线总长不变、两端点及首末段方向不变，折弯数至少减 1。
        /// 每个提案仍经 BuildCandidate 的段长、折弯余量、搭接、叠排、包围盒、实体干涉和
        /// 静态绝缘完整校验，跨组绝缘由搜索上下文复核；只是候选来源，不放宽任何门禁。
        /// </summary>
        internal static IReadOnlyList<LinkageRouteCandidate>
            BuildSegmentReductionOptimizationCandidates(
                LinkageMultiGroupRouteRequest routeRequest,
                LinkageRouteCandidate sourceRouteCandidate,
                IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
                IEnumerable<LinkageRouteCandidate> selectedOtherRouteCandidates,
                int maximumOptimizationCandidateCount,
                CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested ||
                routeRequest == null ||
                sourceRouteCandidate?.GroupRequest == null ||
                sourceRouteCandidate.ManufacturableCenterline == null ||
                sourceRouteCandidate.ManufacturableCenterline.Count <
                    MinimumSegmentCountForSegmentReduction + 1 ||
                sourceRouteCandidate.PlanarConversion?.IsValid != true)
            {
                return Array.Empty<LinkageRouteCandidate>();
            }

            var reducedCenterlines = CreateSegmentReductionCenterlines(
                sourceRouteCandidate.ManufacturableCenterline);
            if (reducedCenterlines.Count == 0)
                return Array.Empty<LinkageRouteCandidate>();
            var dynamicClearanceSolids = (fixedTerminalAndObstacleSolids ??
                    Array.Empty<LinkageRouteSolid>())
                .Concat((selectedOtherRouteCandidates ??
                        Enumerable.Empty<LinkageRouteCandidate>())
                    .Where(selectedRouteCandidate => selectedRouteCandidate != null)
                    .SelectMany(selectedRouteCandidate =>
                        selectedRouteCandidate.ScanSolids))
                .ToList();
            var localOptimizationCandidatePool = new LinkageRouteCandidatePool
            {
                GroupRequest = sourceRouteCandidate.GroupRequest
            };
            localOptimizationCandidatePool.TraversalDirections.Add(
                sourceRouteCandidate.TraversalDirection);
            var evaluatedOptimizationCenterlines =
                new Dictionary<string, LinkageRouteCandidate>(StringComparer.Ordinal);
            AddSegmentReductionCandidates(localOptimizationCandidatePool,
                evaluatedOptimizationCenterlines, routeRequest, sourceRouteCandidate,
                reducedCenterlines, dynamicClearanceSolids, cancellationToken);
            return localOptimizationCandidatePool.ValidCandidates
                .Where(optimizationCandidate =>
                    IsImprovedOptimizationCandidate(optimizationCandidate, sourceRouteCandidate))
                .GroupBy(GetCandidateSignature)
                .Select(optimizationCandidateGroup => optimizationCandidateGroup
                    .OrderBy(optimizationCandidate => optimizationCandidate
                        .PlanarConversion.ActualCopperLength)
                    .ThenBy(optimizationCandidate =>
                        optimizationCandidate.GeneratedBusbarPartCount)
                    .First())
                .OrderBy(optimizationCandidate => optimizationCandidate
                    .PlanarConversion.ActualCopperLength)
                .ThenBy(optimizationCandidate => LinkageRouteTopologySignature
                    .CountCenterlineBends(optimizationCandidate.ManufacturableCenterline))
                .ThenBy(optimizationCandidate =>
                    optimizationCandidate.GeneratedBusbarPartCount)
                .ThenBy(GetCandidateSignature, StringComparer.Ordinal)
                .Take(Math.Max(1, maximumOptimizationCandidateCount))
                .ToList();
        }

        private static void AddSegmentReductionCandidates(
            LinkageRouteCandidatePool localOptimizationCandidatePool,
            IDictionary<string, LinkageRouteCandidate> evaluatedOptimizationCenterlines,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteCandidate sourceRouteCandidate,
            IEnumerable<IList<Point3d>> reducedCenterlines,
            IReadOnlyList<LinkageRouteSolid> dynamicClearanceSolids,
            CancellationToken cancellationToken)
        {
            foreach (var reducedCenterline in reducedCenterlines)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                BuildAndAddUniqueCandidate(
                    localOptimizationCandidatePool,
                    evaluatedOptimizationCenterlines,
                    routeRequest,
                    sourceRouteCandidate.GroupRequest,
                    sourceRouteCandidate.AxisSkeleton,
                    reducedCenterline,
                    dynamicClearanceSolids,
                    true,
                    "OPT-SEGMENT-REDUCTION",
                    sourceRouteCandidate.TraversalDirection,
                    cancellationToken);
            }
        }

        internal static IReadOnlyList<IList<Point3d>> CreateSegmentReductionCenterlines(
            IList<Point3d> sourceCenterline)
        {
            var sourceSegments = GetNonDegenerateSegments(sourceCenterline);
            if (sourceSegments.Count < MinimumSegmentCountForSegmentReduction)
                return Array.Empty<IList<Point3d>>();

            var sourceBendCount = LinkageRouteTopologySignature.CountCenterlineBends(sourceCenterline);
            var proposals = new List<IList<Point3d>>();
            var proposalSignatures = new HashSet<string>(StringComparer.Ordinal);
            var lastMiddleIndex = sourceSegments.Count - 2;
            for (var firstIndex = 1; firstIndex < lastMiddleIndex; firstIndex++)
            for (var secondIndex = firstIndex + 1; secondIndex <= lastMiddleIndex; secondIndex++)
            {
                var firstSegment = sourceSegments[firstIndex];
                var secondSegment = sourceSegments[secondIndex];
                if (firstSegment.Normalized.Dot(secondSegment.Normalized) < 1d - 1e-9d)
                    continue;
                // 合并到前段：second 前移；合并到后段：first 后移。夹在中间的段整体平移。
                foreach (var mergeIntoFirst in new[] { true, false })
                {
                    var reorderedSegments = new List<Vector3d>();
                    for (var segmentIndex = 0; segmentIndex < sourceSegments.Count; segmentIndex++)
                    {
                        if (segmentIndex == firstIndex)
                        {
                            if (mergeIntoFirst)
                                reorderedSegments.Add(firstSegment + secondSegment);
                            continue;
                        }

                        if (segmentIndex == secondIndex)
                        {
                            if (!mergeIntoFirst)
                                reorderedSegments.Add(firstSegment + secondSegment);
                            continue;
                        }

                        reorderedSegments.Add(sourceSegments[segmentIndex]);
                    }

                    var proposal = CreateCenterlineFromSegments(sourceCenterline[0], reorderedSegments);
                    if (proposal == null ||
                        proposal.Count >= sourceCenterline.Count ||
                        proposal[proposal.Count - 1].DistanceTo(sourceCenterline[sourceCenterline.Count - 1]) >
                            GeometryTolerance ||
                        LinkageRouteTopologySignature.CountCenterlineBends(proposal) >= sourceBendCount ||
                        !proposalSignatures.Add(GetCenterlineSignature(proposal)))
                        continue;
                    proposals.Add(proposal);
                }
            }

            return proposals
                .OrderBy(proposal => LinkageRouteTopologySignature.CountCenterlineBends(proposal))
                .ThenBy(GetCenterlineSignature, StringComparer.Ordinal)
                .Take(24)
                .ToList();
        }

        private static List<Vector3d> GetNonDegenerateSegments(IList<Point3d> centerline)
        {
            var segments = new List<Vector3d>();
            if (centerline == null)
                return segments;
            for (var pointIndex = 1; pointIndex < centerline.Count; pointIndex++)
            {
                var segment = new Vector3d(centerline[pointIndex - 1], centerline[pointIndex]);
                if (segment.Norm > GeometryTolerance)
                    segments.Add(segment);
            }

            return segments;
        }

        // 由段向量重建中心线并合并共线同向段；出现折返（相邻段反向）的提案作废。
        private static IList<Point3d> CreateCenterlineFromSegments(Point3d startPoint,
            IReadOnlyList<Vector3d> segments)
        {
            var mergedSegments = new List<Vector3d>();
            foreach (var segment in segments)
            {
                if (segment.Norm <= GeometryTolerance)
                    continue;
                if (mergedSegments.Count > 0)
                {
                    var previous = mergedSegments[mergedSegments.Count - 1];
                    var alignment = previous.Normalized.Dot(segment.Normalized);
                    if (alignment >= 1d - 1e-9d)
                    {
                        mergedSegments[mergedSegments.Count - 1] = previous + segment;
                        continue;
                    }

                    if (alignment <= -1d + 1e-9d)
                        return null;
                }

                mergedSegments.Add(segment);
            }

            var centerline = new List<Point3d> { CopyPoint(startPoint) };
            foreach (var segment in mergedSegments)
            {
                var previousPoint = centerline[centerline.Count - 1];
                centerline.Add(new Point3d(previousPoint.X + segment.X,
                    previousPoint.Y + segment.Y, previousPoint.Z + segment.Z));
            }

            return centerline;
        }

        private static IReadOnlyList<IList<Point3d>>
            CreateMiddleSegmentMergeCenterlines(IList<Point3d> sourceCenterline)
        {
            if (sourceCenterline == null || sourceCenterline.Count <= 4)
            {
                return Array.Empty<IList<Point3d>>();
            }

            var centerPointIndex = (sourceCenterline.Count - 1) / 2d;
            return Enumerable.Range(2, sourceCenterline.Count - 4)
                .OrderBy(pointIndex => Math.Abs(pointIndex - centerPointIndex))
                .ThenBy(pointIndex => pointIndex)
                .Where(pointIndex => sourceCenterline[pointIndex - 1]
                    .DistanceTo(sourceCenterline[pointIndex + 1]) >
                    GeometryTolerance)
                .Select(pointIndex => (IList<Point3d>)sourceCenterline
                    .Where((sourcePoint, sourcePointIndex) =>
                        sourcePointIndex != pointIndex)
                    .Select(CopyPoint)
                    .ToList())
                .ToList();
        }

        private static IList<Point3d> CreateCartesianMotionCenterline(
            IList<Point3d> sourceCenterline,
            int segmentStartPointIndex,
            int coordinateAxisIndex,
            double targetCoordinate,
            double motionFactor)
        {
            var cartesianMotionCenterline = sourceCenterline
                .Select(CopyPoint)
                .ToList();
            var boundedMotionFactor = Math.Max(0d, Math.Min(1d, motionFactor));
            for (var movedPointIndex = segmentStartPointIndex;
                movedPointIndex <= segmentStartPointIndex + 1;
                movedPointIndex++)
            {
                var movedPoint = cartesianMotionCenterline[movedPointIndex];
                var sourceCoordinate = GetPointCoordinate(
                    movedPoint,
                    coordinateAxisIndex);
                var movedCoordinate = sourceCoordinate +
                    (targetCoordinate - sourceCoordinate) * boundedMotionFactor;
                cartesianMotionCenterline[movedPointIndex] = SetPointCoordinate(
                    movedPoint,
                    coordinateAxisIndex,
                    movedCoordinate);
            }

            return cartesianMotionCenterline;
        }

        private static double GetOptimizationPolylineLength(
            IList<Point3d> centerline)
        {
            var polylineLength = 0d;
            for (var pointIndex = 0;
                pointIndex + 1 < centerline.Count;
                pointIndex++)
            {
                polylineLength += centerline[pointIndex].DistanceTo(
                    centerline[pointIndex + 1]);
            }

            return polylineLength;
        }

        private static double GetPointCoordinate(
            Point3d sourcePoint,
            int coordinateAxisIndex)
        {
            switch (coordinateAxisIndex)
            {
                case 0:
                    return sourcePoint.X;
                case 1:
                    return sourcePoint.Y;
                case 2:
                    return sourcePoint.Z;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(coordinateAxisIndex));
            }
        }

        private static Point3d SetPointCoordinate(
            Point3d sourcePoint,
            int coordinateAxisIndex,
            double coordinateValue)
        {
            switch (coordinateAxisIndex)
            {
                case 0:
                    return new Point3d(
                        coordinateValue,
                        sourcePoint.Y,
                        sourcePoint.Z);
                case 1:
                    return new Point3d(
                        sourcePoint.X,
                        coordinateValue,
                        sourcePoint.Z);
                case 2:
                    return new Point3d(
                        sourcePoint.X,
                        sourcePoint.Y,
                        coordinateValue);
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(coordinateAxisIndex));
            }
        }
    }
}
