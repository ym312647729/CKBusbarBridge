using GeometRi;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using RouteLinkBar.Algorithm.Routing.Algorithms;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RouteLinkBar.Algorithm.Routing
{
    public static partial class LinkageSchemeLibraryRouteRebuilder
    {
        private const double DirectPlacementTolerance = 1e-6d;

        /// <summary>
        /// 方案库独立落位：保留数据库每根中间铜排的点数、折弯顺序和排规。
        /// 先按实际端部排建立刚体变换，再只改变历史直腿沿既有轴的长度。
        /// 这里不调用普通候选池、中心线转换器或路径搜索。
        /// </summary>
        public static LinkageRouteSolution RebuildByDirectPlacement(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageSchemeLibraryMatchResult matchResult,
            LinkageSchemeLibraryRouteTemplate routeTemplate,
            CancellationToken cancellationToken)
        {
            return RebuildByDirectPlacement(routeRequest, matchResult, routeTemplate, null, cancellationToken);
        }

        public static LinkageRouteSolution RebuildByDirectPlacement(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageSchemeLibraryMatchResult matchResult,
            LinkageSchemeLibraryRouteTemplate routeTemplate,
            LinkageHistoricalStackContext stackContext,
            CancellationToken cancellationToken)
        {
            var result = CreateSchemeLibrarySolutionShell(routeRequest, matchResult);
            if (routeRequest?.Groups == null || matchResult == null ||
                routeTemplate?.Routes == null || routeTemplate.Routes.Count == 0)
            {
                AddFailure(result, "方案库未提供完整的历史逐排路径或目标固定端。");
                return result;
            }

            if (stackContext != null)
            {
                var invalidSource = routeTemplate.Routes.Where(route => route?.OrderedMembers != null)
                    .SelectMany(route => route.OrderedMembers).FirstOrDefault(member => member != null &&
                        (!IsFinitePositiveHistoricalDimension(member.BusbarThickness) || !IsFinitePositiveHistoricalDimension(member.BusbarWidth)));
                if (invalidSource != null)
                {
                    AddFailure(result, "源实体排规必须为有限正数：" + invalidSource.MemberKey);
                    return result;
                }
            }
            var placedRoutes = new List<DirectPlacedRoute>();
            foreach (var sourceRoute in routeTemplate.Routes)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    result.IsCancelled = true;
                    AddFailure(result, "方案库历史逐排落位已取消。");
                    return result;
                }
                if (!TryPlaceHistoricalMembers(routeRequest, matchResult,
                    sourceRoute, out var placedRoute, out var diagnostic))
                {
                    AddFailure(result, (sourceRoute?.RouteKey ?? "路径") + "：" + diagnostic);
                    return result;
                }
                placedRoutes.Add(placedRoute);
            }

            HistoricalPhysicalNetwork sharedNetwork = null;
            if (stackContext != null && !TryCloseSharedHistoricalNetwork(placedRoutes, stackContext,
                routeRequest.MinimumSegmentLength, cancellationToken, out sharedNetwork, out var sharedDiagnostic))
            {
                result.IsCancelled = cancellationToken.IsCancellationRequested;
                AddFailure(result, sharedDiagnostic);
                return result;
            }
            foreach (var placedRoute in stackContext == null ? placedRoutes : Enumerable.Empty<DirectPlacedRoute>())
            {
                var targetGroup = routeRequest.Groups.FirstOrDefault(group =>
                    string.Equals(group.GroupKey, placedRoute.GroupKey,
                        StringComparison.OrdinalIgnoreCase));
                if (!TryCloseHistoricalInterfaces(placedRoute, targetGroup,
                    routeRequest.MinimumSegmentLength, out var closureFailure))
                {
                    AddFailure(result, placedRoute.RouteKey + "：" + closureFailure);
                    return result;
                }
            }

            foreach (var routeGroupRequest in routeRequest.Groups)
            {
                var traversedGroupRoutes = placedRoutes.Where(item => string.Equals(
                    item.GroupKey, routeGroupRequest.GroupKey,
                    StringComparison.OrdinalIgnoreCase)).ToList();
                if (!TryCollectPhysicalHistoricalRoutes(traversedGroupRoutes,
                    out var groupRoutes, out var sharedRouteFailure))
                {
                    AddFailure(result, sharedRouteFailure);
                    return result;
                }
                if (groupRoutes.Count == 0)
                {
                    AddFailure(result, "方案库的历史排未完整覆盖目标组 " +
                        routeGroupRequest.GroupKey + " 或各层物理段数不同。");
                    return result;
                }
                // 所有共享实体合并后，按全部支路接口共同确定端头，避免逐支路回收同一排的端点。
                foreach (var physicalRoute in groupRoutes)
                    if (!TryNormalizeHistoricalGraphPerpendicularEndpoints(physicalRoute.Settings,
                        routeGroupRequest, routeRequest.MinimumSegmentLength, true, out var endpointFailure))
                    { AddFailure(result, physicalRoute.RouteKey + "：" + endpointFailure); return result; }

                var groupSolution = new LinkageGroupRouteSolution
                {
                    GroupKey = routeGroupRequest.GroupKey,
                    TopologySignature = "LIBRARY_DIRECT:" +
                        matchResult.CandidateSchemeRecordId + ":" +
                        string.Join(",", traversedGroupRoutes.Select(item => item.RouteKey))
                };
                for (var routeIndex = 0; routeIndex < groupRoutes.Count; routeIndex++)
                {
                    var placedRoute = groupRoutes[routeIndex];
                    groupSolution.StackMembers.Add(new LinkageStackedRouteMember
                    {
                        MemberIndex = routeIndex,
                        PlanarParts = placedRoute.Settings
                    });
                    groupSolution.ActualCopperLength += placedRoute.Settings.Sum(
                        setting => setting.GetDevelopedLength());
                }
                groupSolution.PlanarParts = groupRoutes[0].Settings.Select(
                    setting => setting.Copy()).ToList();
                groupSolution.PerpendicularLapCount = Math.Max(0,
                    groupSolution.PlanarParts.Count - 1);
                groupSolution.AxisSkeleton = groupRoutes[0].Settings
                    .SelectMany(setting => setting.PointList)
                    .Select(CopyPoint).ToList();
                groupSolution.ManufacturableCenterline = groupSolution.AxisSkeleton
                    .Select(CopyPoint).ToList();
                result.GroupSolutions.Add(groupSolution);
                result.ActualCopperLength += groupSolution.ActualCopperLength;
                result.PerpendicularLapCount += groupSolution.PerpendicularLapCount;
            }

            if (sharedNetwork != null && !TryMaterializeNetworkGaps(sharedNetwork, placedRoutes, result, cancellationToken, out var materializeDiagnostic))
            {
                result.IsCancelled = cancellationToken.IsCancellationRequested;
                AddFailure(result, materializeDiagnostic);
                return result;
            }
            LinkageHistoricalStackPlacementEvidence stackEvidence = null;
            if (stackContext != null && !TryBuildHistoricalStackPlacementEvidence(stackContext,
                placedRoutes, out stackEvidence, out var stackDiagnostic))
            {
                AddFailure(result, stackDiagnostic);
                return result;
            }
            result.IsSuccess = true;
            var unadjustedGeometry = CopyRouteSolution(result,
                matchResult.CandidateSchemeRecordId);
            if (!LinkageMultiGroupRouteEngine.TryValidateSchemeLibraryOutputWithStackEvidence(
                routeRequest, result, out var failureReason,
                preserveHistoricalSpecifications: true, historicalStackEvidence: stackEvidence))
            {
                if (failureReason != null &&
                    failureReason.Contains("实际绝缘净距") && stackContext == null &&
                    TryImproveDirectPlacementClearance(routeRequest,
                        matchResult, unadjustedGeometry, cancellationToken,
                        out var improvedSolution))
                {
                    if (!HasPreservedHistoricalMemberSignature(
                        routeTemplate, placedRoutes, improvedSolution,
                        routeRequest.MinimumSegmentLength))
                    {
                        improvedSolution.IsSuccess = false;
                        AddFailure(improvedSolution,
                            "接口调整改变了原排数量、排规、折点或最小直腿，已拒绝发布。");
                    }
                    return improvedSolution;
                }
                result.IsSuccess = false;
                result.MinimumClearance = double.NaN;
                AddFailure(result, "历史逐排落位后未通过实际搭接/实体/绝缘复核：" +
                    (failureReason ?? "未提供原因"));
            }
            else if (!HasPreservedHistoricalMemberSignature(
                routeTemplate, placedRoutes, result,
                routeRequest.MinimumSegmentLength, sharedNetwork))
            {
                result.IsSuccess = false;
                AddFailure(result,
                    "历史逐排落位改变了原排数量、排规、折点或最小直腿。");
            }
            return result;
        }

        private static bool HasPreservedHistoricalMemberSignature(
            LinkageSchemeLibraryRouteTemplate routeTemplate,
            IList<DirectPlacedRoute> placedRoutes,
            LinkageRouteSolution output,
            double minimumSegmentLength,
            HistoricalPhysicalNetwork sharedNetwork = null)
        {
            if (output?.GroupSolutions == null ||
                routeTemplate?.Routes?.Count != placedRoutes?.Count)
            {
                return false;
            }
            if (sharedNetwork != null && !ValidateMaterializedNetworkContacts(sharedNetwork, output, out _)) return false;
            foreach (var outputGroup in output.GroupSolutions)
            foreach (var outputMember in outputGroup.StackMembers)
            {
                var expectedIndices = placedRoutes.Where(route => route.GroupKey == outputGroup.GroupKey &&
                        route.OutputMemberIndex == outputMember.MemberIndex)
                    .SelectMany(route => route.OutputPartIndices).Distinct().OrderBy(index => index);
                if (!expectedIndices.SequenceEqual(Enumerable.Range(0, outputMember.PlanarParts.Count))) return false;
            }
            for (var routeIndex = 0; routeIndex < placedRoutes.Count;
                routeIndex++)
            {
                var groupKey = placedRoutes[routeIndex].GroupKey;
                var memberIndex = placedRoutes[routeIndex].OutputMemberIndex;
                var outputGroup = output.GroupSolutions.FirstOrDefault(group =>
                    string.Equals(group.GroupKey, groupKey,
                        StringComparison.OrdinalIgnoreCase));
                var outputMember = outputGroup?.StackMembers?.FirstOrDefault(member =>
                    member.MemberIndex == memberIndex);
                var originalMiddle = routeTemplate.Routes[routeIndex]
                    .OrderedMembers.Where(member => member?.PositionType ==
                        MiddlePositionType).ToList();
                var outputIndices = placedRoutes[routeIndex].OutputPartIndices;
                if (outputMember?.PlanarParts == null || outputIndices.Count != originalMiddle.Count ||
                    outputIndices.Any(index => index < 0 || index >= outputMember.PlanarParts.Count))
                {
                    return false;
                }
                // 同一历史物理链可以出现在多条始端遍历中，但每个原始
                // 固定端的接触约束仍须逐条保留，不能只验证代表遍历。
                var outputPath = outputIndices.Select(index => outputMember.PlanarParts[index]).ToList();
                if (sharedNetwork == null && (!HasHistoricalTerminalContact(outputPath.First(),
                        placedRoutes[routeIndex].TargetStartSetting) ||
                    !HasHistoricalTerminalContact(outputPath.Last(),
                        placedRoutes[routeIndex].TargetEndSetting) ||
                    outputPath.Zip(outputPath.Skip(1), HasHistoricalTerminalContact).Any(contact => !contact)))
                {
                    return false;
                }
                for (var partIndex = 0; partIndex < originalMiddle.Count;
                    partIndex++)
                {
                    var original = originalMiddle[partIndex];
                    var final = outputPath[partIndex];
                    if (final?.PointList?.Count != original.CenterlinePoints.Count ||
                        Math.Abs(final.BusBarWidth - original.BusbarWidth) >
                            DirectPlacementTolerance ||
                        Math.Abs(final.BusBarThickness -
                            original.BusbarThickness) >
                            DirectPlacementTolerance ||
                        final.PointList.Zip(final.PointList.Skip(1),
                            (first, second) => first.DistanceTo(second))
                            .Any(length => length +
                                LinkageRouteGeometryTolerances.FinalAcceptanceComparison < minimumSegmentLength))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// 在当前固定端与原排型不动的前提下，沿相邻两根历史排的共同
        /// 搭接端局部移动一对端腿。只试有限的接口偏移量，不生成新排、
        /// 新折弯或普通算法候选。柜顶端作为锚始终不动。
        /// </summary>
        private static bool TryImproveDirectPlacementClearance(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageSchemeLibraryMatchResult matchResult,
            LinkageRouteSolution originalGeometry,
            CancellationToken cancellationToken,
            out LinkageRouteSolution improvedSolution)
        {
            improvedSolution = null;
            var currentGeometry = originalGeometry;
            var currentMinimum = 0d;
            var clearanceOffsets = new[] { -80d, -50d, -35d, -20d,
                20d, 35d, 50d, 80d };
            for (var adjustmentRound = 0; adjustmentRound < 3;
                adjustmentRound++)
            {
                LinkageRouteSolution bestGeometry = null;
                var bestMinimum = currentMinimum;
                foreach (var groupSolution in currentGeometry.GroupSolutions)
                {
                    for (var boundaryIndex = 0;
                        boundaryIndex + 1 < groupSolution.PlanarParts.Count;
                        boundaryIndex++)
                    {
                        foreach (var offset in clearanceOffsets)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                return false;
                            }
                            var trial = CopyRouteSolution(currentGeometry,
                                matchResult.CandidateSchemeRecordId);
                            var trialGroup = trial.GroupSolutions.First(item =>
                                item.GroupKey == groupSolution.GroupKey);
                            if (!TryShiftOriginalInterface(
                                    trialGroup, boundaryIndex, offset,
                                    routeRequest.MinimumSegmentLength))
                            {
                                continue;
                            }

                            trial.IsSuccess = true;
                            var trialIsValid = LinkageMultiGroupRouteEngine
                                .TryValidateSchemeLibraryOutput(routeRequest,
                                    trial, out var trialFailureReason,
                                    preserveHistoricalSpecifications: true);
                            if (trialIsValid)
                            {
                                improvedSolution = trial;
                                return true;
                            }
                            if (trialFailureReason == null ||
                                !trialFailureReason.Contains("实际绝缘净距"))
                            {
                                continue;
                            }

                            var trialMinimum = trial.GroupSolutions
                                .SelectMany(item => item.MinimumClearanceFaceGroups)
                                .Select(item => item.Distance)
                                .DefaultIfEmpty(0d).Min();
                            if (trialMinimum <= bestMinimum + 1e-5d)
                            {
                                continue;
                            }
                            bestMinimum = trialMinimum;
                            // Validate mutates its own output (including final
                            // spacers). Keep the unvalidated geometry for the
                            // next adjustment and build spacers only at finish.
                            bestGeometry = CopyRouteSolution(currentGeometry,
                                matchResult.CandidateSchemeRecordId);
                            TryShiftOriginalInterface(
                                bestGeometry.GroupSolutions.First(item =>
                                    item.GroupKey == groupSolution.GroupKey),
                                boundaryIndex, offset,
                                routeRequest.MinimumSegmentLength);
                        }
                    }
                }
                if (bestGeometry == null)
                {
                    return false;
                }
                currentGeometry = bestGeometry;
                currentMinimum = bestMinimum;
            }
            return false;
        }

        private static bool TryShiftOriginalInterface(
            LinkageGroupRouteSolution groupSolution,
            int boundaryIndex,
            double offset,
            double minimumSegmentLength)
        {
            if (groupSolution?.StackMembers?.Count != 1 ||
                groupSolution.PlanarParts?.Count <= boundaryIndex + 1)
            {
                return false;
            }
            var firstPart = groupSolution.PlanarParts[boundaryIndex];
            var secondPart = groupSolution.PlanarParts[boundaryIndex + 1];
            if (firstPart?.PointList?.Count < 3 ||
                secondPart?.PointList?.Count < 3)
            {
                return false;
            }

            var firstEnd = FindNearestInterfaceEnd(firstPart, secondPart,
                true);
            var secondEnd = FindNearestInterfaceEnd(firstPart, secondPart,
                false);
            var precedingLegStart = firstEnd == 0 ? 1 :
                firstPart.PointList.Count - 3;
            var precedingLegEnd = firstEnd == 0 ? 2 :
                firstPart.PointList.Count - 2;
            var adjustmentDirection = new Vector3d(
                firstPart.PointList[precedingLegStart],
                firstPart.PointList[precedingLegEnd]);
            if (adjustmentDirection.Norm <= DirectPlacementTolerance)
            {
                return false;
            }
            adjustmentDirection = adjustmentDirection.Normalized * offset;
            foreach (var setting in new[]
            {
                firstPart, secondPart,
                groupSolution.StackMembers[0].PlanarParts[boundaryIndex],
                groupSolution.StackMembers[0].PlanarParts[boundaryIndex + 1]
            })
            {
                var useFirstEnd = ReferenceEquals(setting, firstPart) ||
                    ReferenceEquals(setting,
                        groupSolution.StackMembers[0].PlanarParts[boundaryIndex]);
                var shiftStart = (useFirstEnd ? firstEnd : secondEnd) == 0
                    ? 0 : setting.PointList.Count - 2;
                for (var index = shiftStart; index < shiftStart + 2; index++)
                {
                    var oldPoint = setting.PointList[index];
                    setting.PointList[index] = new Point3d(
                        oldPoint.X + adjustmentDirection.X,
                        oldPoint.Y + adjustmentDirection.Y,
                        oldPoint.Z + adjustmentDirection.Z);
                }
                if (setting.PointList.Zip(setting.PointList.Skip(1),
                    (start, end) => start.DistanceTo(end)).Any(length =>
                        length + DirectPlacementTolerance < minimumSegmentLength))
                {
                    return false;
                }
            }
            return true;
        }

        private static int FindNearestInterfaceEnd(
            PlanarBendBusbarSetting firstPart,
            PlanarBendBusbarSetting secondPart,
            bool resolveFirstPart)
        {
            var shortestDistance = double.PositiveInfinity;
            var selectedEnd = 0;
            for (var firstEnd = 0; firstEnd <= 1; firstEnd++)
            {
                for (var secondEnd = 0; secondEnd <= 1; secondEnd++)
                {
                    var firstPoint = firstPart.PointList[firstEnd == 0
                        ? 0 : firstPart.PointList.Count - 1];
                    var secondPoint = secondPart.PointList[secondEnd == 0
                        ? 0 : secondPart.PointList.Count - 1];
                    var distance = firstPoint.DistanceTo(secondPoint);
                    if (distance < shortestDistance)
                    {
                        shortestDistance = distance;
                        selectedEnd = resolveFirstPart ? firstEnd : secondEnd;
                    }
                }
            }
            return selectedEnd;
        }

        private sealed class DirectPlacedRoute
        {
            public string RouteKey { get; set; }
            public string GroupKey { get; set; }
            public LinkageSchemeLibraryRouteTemplateRoute SourceRoute { get; set; }
            public int OutputMemberIndex { get; set; }
            public bool SharesPhysicalRoute { get; set; }
            public IList<int> OutputPartIndices { get; } = new List<int>();
            public PlanarBendBusbarSetting TargetStartSetting { get; set; }
            public PlanarBendBusbarSetting TargetEndSetting { get; set; }
            public IList<PlanarBendBusbarSetting> Settings { get; } =
                new List<PlanarBendBusbarSetting>();
        }

        private static bool HasHistoricalTerminalContact(
            PlanarBendBusbarSetting generated, PlanarBendBusbarSetting fixedSetting)
        {
            return generated != null && fixedSetting != null &&
                new[] { true, false }.Any(generatedStart =>
                    new[] { true, false }.Any(fixedStart =>
                        LinkageRouteTerminalLapRegionValidator.TryCreateActualLapFootprint(
                            generated, generatedStart, fixedSetting, fixedStart,
                            Math.Max(generated.BusBarWidth, fixedSetting.BusBarWidth), out _,
                            tolerance: LinkageRouteGeometryTolerances.FinalAcceptanceComparison)));
        }

        private static bool TryCollectPhysicalHistoricalRoutes(
            IList<DirectPlacedRoute> traversals, out List<DirectPlacedRoute> physicalRoutes,
            out string diagnostic)
        {
            return TryCollectHistoricalBranchComponents(traversals, out physicalRoutes, out diagnostic);
        }

        // 只修正现有铜排的端腿：柜顶整根贴面、始端整根贴面，
        // 最后在不移动已锁定柜顶排的前提下调整相邻接口。
        private static bool TryCloseHistoricalInterfaces(
            DirectPlacedRoute placedRoute,
            LinkageRouteGroupRequest targetGroup,
            double minimumSegmentLength,
            out string diagnostic)
        {
            diagnostic = null;
            if (placedRoute?.Settings?.Count == 0 || targetGroup == null ||
                !TryFitHistoricalTerminal(placedRoute.Settings,
                    placedRoute.Settings.Count - 1,
                    targetGroup.EndTerminal, out diagnostic) ||
                !TryFitHistoricalTerminal(placedRoute.Settings, 0,
                    targetGroup.StartTerminal, out diagnostic))
            {
                diagnostic = diagnostic ?? "当前固定端缺少可贴合的原始排厚面。";
                return false;
            }

            // Propagate from the fitted start toward the fitted cabinet. A free
            // intermediate I can then move as a rigid follower, with its next
            // joint absorbed by the following bent leg rather than undoing a
            // previously closed joint.
            for (var partIndex = 0;
                partIndex + 1 < placedRoute.Settings.Count; partIndex++)
            {
                if (!TryFitHistoricalInternal(placedRoute.Settings,
                    partIndex, minimumSegmentLength, out diagnostic))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryFitHistoricalTerminal(
            IList<PlanarBendBusbarSetting> settings,
            int partIndex,
            LinkageTerminalAssembly terminal,
            out string diagnostic)
        {
            diagnostic = "历史端部排不能在当前固定排的真实厚面上形成满搭接。";
            var source = settings[partIndex];
            PlanarBendBusbarSetting best = null;
            var bestMovement = double.PositiveInfinity;
            foreach (var fixedMember in terminal?.Members ??
                new List<LinkageTerminalMember>())
            {
                var fixedSetting = fixedMember?.FixedBusbar;
                foreach (var sourceStart in new[] { true, false })
                foreach (var fixedStart in new[] { true, false })
                {
                    if (!TryGetBoundaryNormal(source, sourceStart,
                        out var sourceNormal, out var sourcePoint) ||
                        !TryGetBoundaryNormal(fixedSetting, fixedStart,
                            out var fixedNormal, out var fixedPoint) ||
                        sourceNormal.Cross(fixedNormal).Norm > 1e-5d)
                    {
                        continue;
                    }
                    var planeOffset = new Vector3d(sourcePoint, fixedPoint)
                        .Dot(sourceNormal);
                    var halfSum = (source.BusBarThickness +
                        fixedSetting.BusBarThickness) / 2d;
                    foreach (var signedSide in new[] { -1d, 1d })
                    {
                        var displacement = planeOffset + signedSide * halfSum;
                        if (Math.Abs(displacement) >= bestMovement)
                        {
                            continue;
                        }
                        var trial = source.Copy();
                        TranslateHistoricalPart(trial,
                            sourceNormal * displacement);
                        if (!HasHistoricalLap(trial, sourceStart,
                            fixedSetting, fixedStart,
                            LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
                        {
                            AlignHistoricalTerminalWidthCoverage(trial, sourceStart,
                                fixedSetting, fixedStart);
                            ExtendHistoricalInterfaceEnd(trial, sourceStart,
                                fixedSetting, fixedStart);
                        }
                        if (!HasHistoricalLap(trial, sourceStart,
                            fixedSetting, fixedStart,
                            LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
                        {
                            continue;
                        }
                        best = trial;
                        bestMovement = Math.Abs(displacement);
                    }
                }
            }
            if (best == null)
            {
                diagnostic += " 原排=" + string.Join("→",
                    source.PointList.Select(point => string.Format(
                        "({0:F2},{1:F2},{2:F2})", point.X, point.Y, point.Z))) +
                    " 固定=" + string.Join(";",
                        (terminal?.Members ?? new List<LinkageTerminalMember>())
                            .Select(member => string.Join("→",
                                member.FixedBusbar.PointList.Select(point =>
                                    string.Format("({0:F2},{1:F2},{2:F2})",
                                        point.X, point.Y, point.Z)))));
                return false;
            }
            settings[partIndex] = best;
            diagnostic = null;
            return true;
        }

        private static void AlignHistoricalTerminalWidthCoverage(
            PlanarBendBusbarSetting movable, bool movableStart,
            PlanarBendBusbarSetting fixedSetting, bool fixedStart)
        {
            var widthAxis = movable.WidthVector.Normalized;
            var fixedIndex = fixedStart ? 0 : fixedSetting.PointList.Count - 2;
            var fixedFirst = fixedSetting.PointList[fixedIndex];
            var fixedLast = fixedSetting.PointList[fixedIndex + 1];
            var fixedLength = new Vector3d(fixedFirst, fixedLast);
            if (fixedLength.Norm <= DirectPlacementTolerance ||
                fixedLength.Normalized.Cross(widthAxis).Norm > 1e-5d) return;
            var origin = movable.PointList[movableStart ? 0 : movable.PointList.Count - 1];
            var firstProjection = new Vector3d(origin, fixedFirst).Dot(widthAxis);
            var lastProjection = new Vector3d(origin, fixedLast).Dot(widthAxis);
            var minimumCenter = Math.Min(firstProjection, lastProjection) + movable.BusBarWidth / 2d;
            var maximumCenter = Math.Max(firstProjection, lastProjection) - movable.BusBarWidth / 2d;
            if (minimumCenter > maximumCenter + DirectPlacementTolerance) return;
            var displacement = minimumCenter > 0d ? minimumCenter : maximumCenter < 0d ? maximumCenter : 0d;
            // 仍保持原排整体形状；相邻接口由后续历史链闭合吸收位移，
            // 不以放宽满搭接容差吞掉源数据中0.001mm等真实投影缺口。
            TranslateHistoricalPart(movable, widthAxis * displacement);
        }

        private static bool TryFitHistoricalInternal(
            IList<PlanarBendBusbarSetting> settings,
            int firstIndex,
            double minimumSegmentLength,
            out string diagnostic)
        {
            diagnostic = "历史相邻排无法通过原有直腿形成真实满搭接。";
            var first = settings[firstIndex];
            var second = settings[firstIndex + 1];
            if (first?.PointList == null || first.PointList.Count < 2 ||
                second?.PointList == null || second.PointList.Count < 2)
            {
                return false;
            }
            foreach (var firstStart in new[] { true, false })
            foreach (var secondStart in new[] { true, false })
            {
                if (HasHistoricalLap(first, firstStart, second, secondStart,
                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
                {
                    diagnostic = null;
                    return true;
                }
            }
            first = first.Copy();
            second = second.Copy();
            var firstInterfaceStart = FindNearestInterfaceEnd(first, second, true) == 0;
            var secondInterfaceStart = FindNearestInterfaceEnd(first, second, false) == 0;
            // Full-width overlap may require extending either endpoint, even
            // when only the other bar is free to move in thickness direction.
            ExtendHistoricalInterfaceEnd(first, firstInterfaceStart, second, secondInterfaceStart);
            ExtendHistoricalInterfaceEnd(second, secondInterfaceStart, first, firstInterfaceStart);
            if (TryFitHistoricalMovableEnd(first, second, minimumSegmentLength,
                out var fittedFirst))
            {
                settings[firstIndex] = fittedFirst;
                settings[firstIndex + 1] = second;
                diagnostic = null;
                return true;
            }
            // A straight I bar has no bend leg to shift. Keep it in place and
            // fit the adjacent bent bar's interface end; never move both ends
            // or bypass the final terminal/internal lap validation.
            if (TryFitHistoricalMovableEnd(second, first, minimumSegmentLength,
                out var fittedSecond))
            {
                settings[firstIndex] = first;
                settings[firstIndex + 1] = fittedSecond;
                diagnostic = null;
                return true;
            }
            if (second.PointList.Count == 2 && firstIndex + 1 < settings.Count - 1 &&
                TryFitHistoricalStraightFollower(second, first, minimumSegmentLength,
                    out var fittedStraightFollower))
            {
                settings[firstIndex] = first;
                settings[firstIndex + 1] = fittedStraightFollower;
                diagnostic = null;
                return true;
            }
            return false;
        }

        private static bool TryFitHistoricalStraightFollower(
            PlanarBendBusbarSetting movable,
            PlanarBendBusbarSetting fixedPart,
            double minimumSegmentLength,
            out PlanarBendBusbarSetting fittedPart)
        {
            fittedPart = null;
            var minimumMovement = double.PositiveInfinity;
            foreach (var movableStart in new[] { true, false })
            foreach (var fixedStart in new[] { true, false })
            {
                if (!TryGetBoundaryNormal(movable, movableStart, out var movableNormal, out var movablePoint) ||
                    !TryGetBoundaryNormal(fixedPart, fixedStart, out var fixedNormal, out var fixedPoint) ||
                    movableNormal.Cross(fixedNormal).Norm > 1e-5d)
                {
                    continue;
                }
                var planeOffset = new Vector3d(movablePoint, fixedPoint).Dot(movableNormal);
                var halfThickness = (movable.BusBarThickness + fixedPart.BusBarThickness) / 2d;
                foreach (var signedSide in new[] { -1d, 1d })
                {
                    var displacement = planeOffset + signedSide * halfThickness;
                    if (Math.Abs(displacement) >= minimumMovement) continue;
                    var trial = movable.Copy();
                    TranslateHistoricalPart(trial, movableNormal * displacement);
                    if (!HasHistoricalLap(trial, movableStart, fixedPart, fixedStart))
                        ExtendHistoricalInterfaceEnd(trial, movableStart, fixedPart, fixedStart);
                    if (!HasHistoricalLap(trial, movableStart, fixedPart, fixedStart) ||
                        trial.PointList[0].DistanceTo(trial.PointList[1]) + DirectPlacementTolerance < minimumSegmentLength)
                        continue;
                    fittedPart = trial;
                    minimumMovement = Math.Abs(displacement);
                }
            }
            return fittedPart != null;
        }

        private static bool TryFitHistoricalMovableEnd(
            PlanarBendBusbarSetting first,
            PlanarBendBusbarSetting second,
            double minimumSegmentLength,
            out PlanarBendBusbarSetting fittedPart)
        {
            fittedPart = null;
            if (first?.PointList?.Count < 3 || second?.PointList?.Count < 2)
            {
                return false;
            }
            PlanarBendBusbarSetting best = null;
            var bestMovement = double.PositiveInfinity;
            foreach (var firstStart in new[] { true, false })
            foreach (var secondStart in new[] { true, false })
            {
                if (!TryGetBoundaryNormal(first, firstStart,
                    out var firstNormal, out var firstPoint) ||
                    !TryGetBoundaryNormal(second, secondStart,
                        out var secondNormal, out var secondPoint) ||
                    firstNormal.Cross(secondNormal).Norm > 1e-5d)
                {
                    continue;
                }
                var points = first.PointList;
                var nearBend = firstStart ? 1 : points.Count - 2;
                var preceding = firstStart ? 2 : points.Count - 3;
                if (preceding < 0 || preceding >= points.Count)
                {
                    continue;
                }
                var adjustmentAxis = new Vector3d(
                    points[preceding], points[nearBend]);
                if (adjustmentAxis.Norm <= DirectPlacementTolerance)
                {
                    continue;
                }
                adjustmentAxis = adjustmentAxis.Normalized;
                var normalProjection = adjustmentAxis.Dot(firstNormal);
                if (Math.Abs(normalProjection) <= DirectPlacementTolerance)
                {
                    continue;
                }
                var planeOffset = new Vector3d(firstPoint, secondPoint)
                    .Dot(firstNormal);
                var halfSum = (first.BusBarThickness +
                    second.BusBarThickness) / 2d;
                foreach (var signedSide in new[] { -1d, 1d })
                {
                    var movement = (planeOffset + signedSide * halfSum) /
                        normalProjection;
                    if (Math.Abs(movement) >= bestMovement)
                    {
                        continue;
                    }
                    var trial = first.Copy();
                    foreach (var pointIndex in new[]
                        { firstStart ? 0 : points.Count - 2,
                          firstStart ? 1 : points.Count - 1 })
                    {
                        trial.PointList[pointIndex] =
                            MoveHistoricalPoint(trial.PointList[pointIndex],
                                adjustmentAxis * movement);
                    }
                    if (!HasHistoricalLap(trial, firstStart, second,
                            secondStart))
                    {
                        ExtendHistoricalInterfaceEnd(trial, firstStart,
                            second, secondStart);
                    }
                    if (!HasHistoricalLap(trial, firstStart, second,
                            secondStart) ||
                        trial.PointList.Zip(trial.PointList.Skip(1),
                            (start, end) => start.DistanceTo(end)).Any(length =>
                                length + DirectPlacementTolerance <
                                    minimumSegmentLength))
                    {
                        continue;
                    }
                    best = trial;
                    bestMovement = Math.Abs(movement);
                }
            }
            if (best == null)
            {
                return false;
            }
            fittedPart = best;
            return true;
        }

        private static bool HasHistoricalLap(
            PlanarBendBusbarSetting first, bool firstStart,
            PlanarBendBusbarSetting second, bool secondStart,
            double acceptanceTolerance = DirectPlacementTolerance)
        {
            return LinkageRouteTerminalLapRegionValidator
                .TryCreateActualLapFootprint(first, firstStart,
                    second, secondStart,
                    Math.Max(first.BusBarWidth, second.BusBarWidth), out _,
                    tolerance: acceptanceTolerance);
        }

        private static void ExtendHistoricalInterfaceEnd(
            PlanarBendBusbarSetting movable, bool useStart,
            PlanarBendBusbarSetting fixedSetting, bool fixedStart)
        {
            var points = movable.PointList;
            var terminalIndex = useStart ? 0 : points.Count - 1;
            var adjacentIndex = useStart ? 1 : points.Count - 2;
            var outward = new Vector3d(points[adjacentIndex],
                points[terminalIndex]);
            if (outward.Norm <= DirectPlacementTolerance)
            {
                return;
            }
            outward = outward.Normalized;
            var fixedPoints = fixedSetting.PointList;
            var fixedIndex = fixedStart ? 0 : fixedPoints.Count - 2;
            var fixedNext = fixedIndex + 1;
            var fixedWidth = fixedSetting.WidthVector.Normalized *
                (fixedSetting.BusBarWidth / 2d);
            var neighborProjections = new[]
            {
                MoveHistoricalPoint(fixedPoints[fixedIndex], fixedWidth),
                MoveHistoricalPoint(fixedPoints[fixedIndex],
                    fixedWidth * -1d),
                MoveHistoricalPoint(fixedPoints[fixedNext], fixedWidth),
                MoveHistoricalPoint(fixedPoints[fixedNext],
                    fixedWidth * -1d)
            }.Select(point => new Vector3d(points[terminalIndex], point)
                .Dot(outward)).ToList();
            var fixedDirection = new Vector3d(fixedPoints[fixedIndex], fixedPoints[fixedNext]);
            var neighborProjection = fixedDirection.Norm > DirectPlacementTolerance &&
                fixedDirection.Normalized.Cross(outward).Norm < 1e-5d
                ? neighborProjections.Min() + Math.Max(movable.BusBarWidth, fixedSetting.BusBarWidth)
                : neighborProjections.Max();
            if (neighborProjection > DirectPlacementTolerance &&
                neighborProjection < 2d * fixedSetting.BusBarWidth)
            {
                points[terminalIndex] = MoveHistoricalPoint(
                    points[terminalIndex], outward * neighborProjection);
            }
        }

        private static bool TryGetBoundaryNormal(
            PlanarBendBusbarSetting setting, bool useStart,
            out Vector3d normal, out Point3d point)
        {
            normal = null;
            point = null;
            if (setting?.PointList?.Count < 2 ||
                setting.WidthVector == null ||
                setting.WidthVector.Norm < DirectPlacementTolerance)
            {
                return false;
            }
            var segmentIndex = useStart ? 0 : setting.PointList.Count - 2;
            point = setting.PointList[segmentIndex];
            var direction = new Vector3d(point,
                setting.PointList[segmentIndex + 1]);
            if (direction.Norm < DirectPlacementTolerance)
            {
                return false;
            }
            normal = direction.Normalized.Cross(
                setting.WidthVector.Normalized);
            return normal.Norm > DirectPlacementTolerance;
        }

        private static void TranslateHistoricalPart(
            PlanarBendBusbarSetting setting, Vector3d displacement)
        {
            for (var pointIndex = 0; pointIndex < setting.PointList.Count;
                pointIndex++)
            {
                setting.PointList[pointIndex] = MoveHistoricalPoint(
                    setting.PointList[pointIndex], displacement);
            }
        }

        private static Point3d MoveHistoricalPoint(
            Point3d point, Vector3d displacement)
        {
            return new Point3d(point.X + displacement.X,
                point.Y + displacement.Y, point.Z + displacement.Z);
        }

        private static bool TryPlaceHistoricalMembers(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageSchemeLibraryMatchResult matchResult,
            LinkageSchemeLibraryRouteTemplateRoute sourceRoute,
            out DirectPlacedRoute placedRoute,
            out string diagnostic)
        {
            placedRoute = null;
            if (!TryResolveTargetGroup(routeRequest, matchResult, sourceRoute,
                out var targetGroup, out diagnostic))
            {
                return false;
            }

            var sourceMembers = sourceRoute.OrderedMembers?.Where(
                member => member != null).ToList();
            var sourceMiddle = sourceMembers?.Where(member =>
                member.PositionType == MiddlePositionType).ToList();
            if (sourceMembers == null || sourceMiddle == null ||
                sourceMiddle.Count == 0 ||
                sourceMembers[0].PositionType != StartPositionType ||
                sourceMembers[sourceMembers.Count - 1].PositionType !=
                    CabinetTopPositionType ||
                sourceMembers.Count != sourceMiddle.Count + 2)
            {
                diagnostic = "历史拓扑的固定端或中间排不完整。";
                return false;
            }

            var sourceStart = sourceMembers[0];
            var sourceEnd = sourceMembers[sourceMembers.Count - 1];
            var targetCabinetMember = ResolveTargetTerminalMember(
                sourceEnd, targetGroup.EndTerminal?.Members,
                matchResult.CandidateToTargetTransform);
            var rigidTransform = TryBuildHistoricalCabinetTransform(
                sourceStart, sourceEnd, targetCabinetMember, targetGroup,
                matchResult, out var cabinetBasisDiagnostic) ??
                BuildRouteCandidateToTargetTransform(
                    sourceRoute, targetGroup, matchResult);
            if (rigidTransform == null)
            {
                diagnostic = "历史排无法对齐当前柜顶的排宽/排厚坐标系：" +
                    cabinetBasisDiagnostic;
                return false;
            }

            if (!TryGetTerminalPlacementDelta(sourceRoute, sourceStart,
                    sourceMiddle[0], targetGroup.StartTerminal,
                    rigidTransform, out var startAnchor, out var startDelta) ||
                !TryGetTerminalPlacementDelta(sourceRoute, sourceEnd,
                    sourceMiddle[sourceMiddle.Count - 1], targetGroup.EndTerminal,
                    rigidTransform, out var endAnchor, out var endDelta))
            {
                diagnostic = "历史搭接接口或当前固定端几何缺失，不能逐排落位。";
                return false;
            }

            var sourceWidth = ToVector(TransformVector(
                rigidTransform, sourceMiddle[0].WidthDirection));
            var sourceThickness = ToVector(TransformVector(
                rigidTransform, sourceMiddle[0].StartThicknessDirection));
            if (sourceWidth == null || sourceThickness == null ||
                sourceWidth.Norm <= DirectPlacementTolerance ||
                sourceThickness.Norm <= DirectPlacementTolerance)
            {
                diagnostic = "历史排的排宽/排厚方向无效。";
                return false;
            }
            var frameWidth = sourceWidth.Normalized;
            var frameThickness = sourceThickness -
                frameWidth * sourceThickness.Dot(frameWidth);
            if (frameThickness.Norm <= DirectPlacementTolerance)
            {
                diagnostic = "历史排的排宽与排厚方向不正交。";
                return false;
            }
            frameThickness = frameThickness.Normalized;
            var frameLength = frameWidth.Cross(frameThickness).Normalized;
            var frameAxes = new[] { frameWidth, frameThickness, frameLength };
            // 先用柜顶接口的位移平移整组历史排；随后只让已有直腿沿
            // 三个历史坐标轴吸收始端残差。这样柜顶搭接始终是固定锚。
            var anchorDifference = new Vector3d(endAnchor, startAnchor);
            var terminalDeltaDifference = startDelta - endDelta;
            var axisScales = new double[3];
            for (var axisIndex = 0; axisIndex < frameAxes.Length; axisIndex++)
            {
                var span = anchorDifference.Dot(frameAxes[axisIndex]);
                var delta = terminalDeltaDifference.Dot(frameAxes[axisIndex]);
                if (Math.Abs(span) <= DirectPlacementTolerance)
                {
                    if (Math.Abs(delta) > 0.5d)
                    {
                        diagnostic = "两端在历史零跨度轴上错开，保持原排型无法直接偏移。";
                        return false;
                    }
                    axisScales[axisIndex] = 1d;
                }
                else
                {
                    axisScales[axisIndex] = 1d + delta / span;
                    if (axisScales[axisIndex] <= DirectPlacementTolerance)
                    {
                        diagnostic = "当前固定端跨越历史排的折弯方向，不能只调整原直腿。";
                        return false;
                    }
                }
            }

            placedRoute = new DirectPlacedRoute
            {
                RouteKey = sourceRoute.RouteKey,
                GroupKey = targetGroup.GroupKey,
                SourceRoute = sourceRoute,
                TargetStartSetting = ResolveTargetTerminalMember(sourceStart,
                    targetGroup.StartTerminal?.Members, rigidTransform)?.FixedBusbar,
                TargetEndSetting = ResolveTargetTerminalMember(sourceEnd,
                    targetGroup.EndTerminal?.Members, rigidTransform)?.FixedBusbar
            };
            for (var memberIndex = 0; memberIndex < sourceMiddle.Count; memberIndex++)
            {
                var middleMember = sourceMiddle[memberIndex];
                var originalPoints = middleMember.CenterlinePoints?.Select(point =>
                    TransformPoint(rigidTransform, point)).ToList();
                if (originalPoints == null || originalPoints.Count < 2 ||
                    originalPoints.Any(point => point == null))
                {
                    diagnostic = "历史中间排 " + middleMember.MemberKey + " 的点列无效。";
                    return false;
                }

                var adjustedPoints = originalPoints.Select(point =>
                {
                    var local = new Vector3d(endAnchor, point);
                    var move = endDelta;
                    for (var axisIndex = 0; axisIndex < frameAxes.Length; axisIndex++)
                    {
                        move += frameAxes[axisIndex] *
                            ((axisScales[axisIndex] - 1d) *
                             local.Dot(frameAxes[axisIndex]));
                    }
                    return new Point3d(point.X + move.X,
                        point.Y + move.Y, point.Z + move.Z);
                }).ToList();
                var previousMemberKey = memberIndex == 0
                    ? sourceStart.MemberKey
                    : sourceMiddle[memberIndex - 1].MemberKey;
                var nextMemberKey = memberIndex == sourceMiddle.Count - 1
                    ? sourceEnd.MemberKey
                    : sourceMiddle[memberIndex + 1].MemberKey;
                var previousInterface = ResolveTemplateInterfacePoint(
                    sourceRoute, previousMemberKey, middleMember.MemberKey,
                    rigidTransform);
                var nextInterface = ResolveTemplateInterfacePoint(
                    sourceRoute, middleMember.MemberKey, nextMemberKey,
                    rigidTransform);
                var previousProjection = ProjectPointToPolyline(
                    previousInterface, originalPoints);
                var nextProjection = ProjectPointToPolyline(
                    nextInterface, originalPoints);
                if (previousProjection == null || nextProjection == null)
                {
                    diagnostic = "历史中间排 " + middleMember.MemberKey +
                        " 缺少前后实际搭接接口。";
                    return false;
                }
                if (adjustedPoints.Zip(adjustedPoints.Skip(1),
                    (firstPoint, secondPoint) => firstPoint.DistanceTo(secondPoint))
                    .Any(length => length + DirectPlacementTolerance <
                        routeRequest.MinimumSegmentLength))
                {
                    diagnostic = "历史中间排 " + middleMember.MemberKey +
                        " 在当前端部位置的原直腿短于最小段长。";
                    return false;
                }

                var transformedThickness = ToVector(TransformVector(
                    rigidTransform, middleMember.StartThicknessDirection));
                var transformedWidth = ToVector(TransformVector(
                    rigidTransform, middleMember.WidthDirection));
                if (transformedThickness == null || transformedWidth == null ||
                    transformedThickness.Norm <= DirectPlacementTolerance ||
                    transformedWidth.Norm <= DirectPlacementTolerance)
                {
                    diagnostic = "历史中间排 " + middleMember.MemberKey +
                        " 的板材方向无效。";
                    return false;
                }

                // 保留源铜排自身点列方向；一个物理排可能原本按
                // 柜顶→始端建模，强制反转会改变后续厚面和端头含义。

                var setting = new PlanarBendBusbarSetting
                {
                    GroupKey = targetGroup.GroupKey,
                    IsCenter = true,
                    PointList = adjustedPoints,
                    StartBusbarVector = transformedThickness.Normalized,
                    WidthVector = transformedWidth.Normalized,
                    BusBarWidth = middleMember.BusbarWidth,
                    BusBarThickness = middleMember.BusbarThickness,
                    BarBendingRadius = middleMember.BendRadius,
                    BendAllowance = routeRequest.BendAllowance,
                    PhysicalSectionIndex = memberIndex,
                    SpecificationSectionIndex = memberIndex,
                    StartLapWidth = memberIndex == 0
                        ? targetGroup.StartTerminal?.Members?.FirstOrDefault()
                            ?.FixedBusbar?.BusBarWidth ?? 0d
                        : sourceMiddle[memberIndex - 1].BusbarWidth,
                    EndLapWidth = memberIndex == sourceMiddle.Count - 1
                        ? targetGroup.EndTerminal?.Members?.FirstOrDefault()
                            ?.FixedBusbar?.BusBarWidth ?? 0d
                        : sourceMiddle[memberIndex + 1].BusbarWidth
                };
                placedRoute.Settings.Add(setting);
            }

            diagnostic = null;
            return true;
        }

        private static LinkageSchemeLibraryRigidTransform TryBuildHistoricalCabinetTransform(
            LinkageSchemeLibraryRouteTemplateMember sourceStart,
            LinkageSchemeLibraryRouteTemplateMember sourceCabinet,
            LinkageTerminalMember targetCabinet,
            LinkageRouteGroupRequest targetGroup,
            LinkageSchemeLibraryMatchResult matchResult,
            out string diagnostic)
        {
            diagnostic = "固定端坐标系或中心点无效。";
            var targetStart = ResolveTargetTerminalMember(sourceStart,
                targetGroup.StartTerminal?.Members, matchResult.CandidateToTargetTransform);
            var sourceOrigin = GetCenterPoint(sourceCabinet.CenterlinePoints);
            var targetOrigin = GetCenterPoint(targetCabinet?.FixedBusbar?.PointList);
            var sourceOtherEnd = GetCenterPoint(sourceStart.CenterlinePoints);
            var targetOtherEnd = GetCenterPoint(targetStart?.FixedBusbar?.PointList);
            if (sourceOrigin == null || targetOrigin == null || sourceOtherEnd == null || targetOtherEnd == null ||
                !TryBuildFrame(ToVector(sourceCabinet.StartThicknessDirection), ToVector(sourceCabinet.WidthDirection),
                    out var sourceAxisA, out var sourceAxisB, out var sourceAxisC)) return null;

            LinkageSchemeLibraryRigidTransform bestTransform = null;
            var bestResidual = double.PositiveInfinity;
            // A rectangular terminal may be recorded in either endpoint order.
            // Resolve its width-axis sign using BOTH physical ends of the route,
            // not by treating a point-list reversal as an actual 180° rotation.
            foreach (var widthSign in new[] { 1d, -1d })
            {
                if (!TryBuildFrame(targetCabinet.FixedBusbar.StartBusbarVector,
                    targetCabinet.FixedBusbar.WidthVector * widthSign,
                    out var targetAxisA, out var targetAxisB, out var targetAxisC)) continue;
                var candidateTransform = CreateBasisTransform(sourceAxisA, sourceAxisB, sourceAxisC,
                    targetAxisA, targetAxisB, targetAxisC, sourceOrigin, targetOrigin);
                var residual = TransformPoint(candidateTransform, sourceOtherEnd).DistanceTo(targetOtherEnd);
                if (residual + DirectPlacementTolerance < bestResidual)
                {
                    bestResidual = residual;
                    bestTransform = candidateTransform;
                }
            }
            if (bestTransform != null) diagnostic = null;
            return bestTransform;
        }

        private static bool TryGetTerminalPlacementDelta(
            LinkageSchemeLibraryRouteTemplateRoute sourceRoute,
            LinkageSchemeLibraryRouteTemplateMember sourceTerminal,
            LinkageSchemeLibraryRouteTemplateMember adjacentMiddle,
            LinkageTerminalAssembly targetTerminal,
            LinkageSchemeLibraryRigidTransform rigidTransform,
            out Point3d interfaceAnchor,
            out Vector3d placementDelta)
        {
            interfaceAnchor = ResolveTemplateInterfacePoint(sourceRoute,
                sourceTerminal.MemberKey, adjacentMiddle.MemberKey,
                rigidTransform);
            placementDelta = null;
            var sourceFixedPoints = sourceTerminal.CenterlinePoints?.Select(
                point => TransformPoint(rigidTransform, point)).ToList();
            var targetMember = ResolveTargetTerminalMember(sourceTerminal,
                targetTerminal?.Members, rigidTransform);
            var sourceProjection = ProjectPointToPolyline(interfaceAnchor,
                sourceFixedPoints)?.Point;
            var targetProjection = ProjectPointToPolyline(sourceProjection,
                targetMember?.FixedBusbar?.PointList)?.Point;
            if (sourceProjection == null || targetProjection == null)
            {
                return false;
            }
            placementDelta = new Vector3d(sourceProjection,
                targetProjection);
            return true;
        }
    }
}
