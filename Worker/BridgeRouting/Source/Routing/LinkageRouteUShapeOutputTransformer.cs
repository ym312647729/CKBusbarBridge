using GeometRi;
using System;
using System.Collections.Generic;
using System.Linq;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;

namespace RouteLinkBar.Algorithm.Routing
{
    /// <summary>
    /// 按实际平直长度处理M/U：长中段保留，短M尝试同锚点转L，短U拆为真实满搭接LL。
    /// 识别、转换和发布终检采用同一口径，转换只提交完整副本。
    /// </summary>
    internal static partial class LinkageRouteUShapeOutputTransformer
    {
        private const double GeometryTolerance =
            LinkageRouteGeometryTolerances.PointCoincidence;

        internal static bool TryReplaceUShapes(
            LinkageRouteCenterlineConversionResult conversionResult,
            out bool geometryChanged,
            out string failureReason)
        {
            return TryReplaceUShapesWithOffsetDirection(conversionResult, 1,
                out geometryChanged, out failureReason);
        }

        // Preserve the normal entry point and compare the two physical contact
        // sides on separate conversion copies.
        internal static bool TryReplaceUShapesWithOffsetDirection(
            LinkageRouteCenterlineConversionResult conversionResult,
            int contactOffsetSign,
            out bool geometryChanged,
            out string failureReason)
        {
            geometryChanged = false;
            failureReason = null;
            if (contactOffsetSign != -1 && contactOffsetSign != 1)
            {
                failureReason = "U 型诊断的接触侧必须为正侧或反侧。";
                return false;
            }
            if (conversionResult?.CenterlineParts == null ||
                conversionResult.CenterlineParts.Count == 0 ||
                conversionResult.StackMembers == null ||
                conversionResult.StackMembers.Count == 0)
            {
                failureReason = "U 型替代缺少完整的中心线或叠排实体。";
                return false;
            }

            if (conversionResult.StackMembers.Any(stackMember =>
                    stackMember?.PlanarParts == null ||
                    stackMember.PlanarParts.Count == 0))
            {
                failureReason = "U 型替代前存在缺少物理分段的叠排成员。";
                return false;
            }

            var transformedCenterlineParts = new List<PlanarBendBusbarSetting>();
            if (!TryTransformPartSequence(
                    conversionResult.CenterlineParts,
                    transformedCenterlineParts,
                    contactOffsetSign,
                    out var centerlineSectionExpansionCounts,
                    out var centerlineChanged,
                    out failureReason))
            {
                return false;
            }

            var transformedMembers = new List<LinkageStackedRouteMember>();
            var expansionCountsByMember = new List<IList<int>>();
            foreach (var stackMember in conversionResult.StackMembers)
            {
                var transformedMemberParts =
                    new List<PlanarBendBusbarSetting>();
                if (!TryTransformPartSequence(
                        stackMember.PlanarParts,
                        transformedMemberParts,
                        contactOffsetSign,
                        out var memberSectionExpansionCounts,
                        out var memberChanged,
                        out failureReason))
                {
                    failureReason = "第 " + (stackMember.MemberIndex + 1) +
                        " 层叠排 U 型替代失败：" + failureReason;
                    return false;
                }

                transformedMembers.Add(new LinkageStackedRouteMember
                {
                    MemberIndex = stackMember.MemberIndex,
                    CenterlineOffset = stackMember.CenterlineOffset,
                    PlanarParts = transformedMemberParts
                });
                expansionCountsByMember.Add(memberSectionExpansionCounts);
                geometryChanged |= memberChanged;
            }

            geometryChanged |= centerlineChanged;
            if (!geometryChanged)
            {
                return true;
            }

            // G trial: downstream thickness offsets must not detach the selected
            // fixed-end line. Use the existing rigid-I chain propagation, not a
            // second per-interface displacement solver; commit only complete copies.
            if (!TryRestoreSelectedTerminalLines(conversionResult.CenterlineParts,
                    transformedCenterlineParts, true, true,
                    conversionResult.DoubleStartPhysicalBridgeSectionIndices, out failureReason))
                return false;
            var firstPhysicalIdentity = conversionResult.CenterlineParts.First().PhysicalSectionIndex;
            var lastPhysicalIdentity = conversionResult.CenterlineParts.Last().PhysicalSectionIndex;
            for (var restoreMemberIndex = 0; restoreMemberIndex < transformedMembers.Count; restoreMemberIndex++)
            {
                var sourceSequence = conversionResult.StackMembers[restoreMemberIndex].PlanarParts;
                if (!TryRestoreSelectedTerminalLines(sourceSequence,
                        transformedMembers[restoreMemberIndex].PlanarParts,
                        sourceSequence.First().PhysicalSectionIndex == firstPhysicalIdentity,
                        sourceSequence.Last().PhysicalSectionIndex == lastPhysicalIdentity,
                        conversionResult.DoubleStartPhysicalBridgeSectionIndices, out failureReason))
                    return false;
            }

            var originalToExpandedSectionIndex = new Dictionary<int, int>();
            var expansionCountByOriginalSection = new Dictionary<int, int>();
            var expandedSectionIndex = 0;
            for (var originalSectionIndex = 0;
                originalSectionIndex < centerlineSectionExpansionCounts.Count;
                originalSectionIndex++)
            {
                var originalIdentity = conversionResult.CenterlineParts[originalSectionIndex].PhysicalSectionIndex;
                if (originalIdentity < 0) originalIdentity = originalSectionIndex;
                if (originalToExpandedSectionIndex.ContainsKey(originalIdentity))
                { failureReason = "U型拆分前的全局物理段身份重复。"; return false; }
                originalToExpandedSectionIndex[originalIdentity] = expandedSectionIndex;
                expansionCountByOriginalSection[originalIdentity] = centerlineSectionExpansionCounts[originalSectionIndex];
                expandedSectionIndex +=
                    centerlineSectionExpansionCounts[originalSectionIndex];
            }

            if (!TryAssignExpandedSectionIndices(conversionResult.CenterlineParts, transformedCenterlineParts,
                centerlineSectionExpansionCounts, originalToExpandedSectionIndex, expansionCountByOriginalSection,
                conversionResult.CenterlineParts.Count, out failureReason)) return false;
            for (var memberIndex = 0; memberIndex < transformedMembers.Count; memberIndex++)
            {
                if (!TryAssignExpandedSectionIndices(conversionResult.StackMembers[memberIndex].PlanarParts,
                    transformedMembers[memberIndex].PlanarParts, expansionCountsByMember[memberIndex],
                    originalToExpandedSectionIndex, expansionCountByOriginalSection,
                    conversionResult.CenterlineParts.Count, out failureReason)) return false;
            }
            var remappedBridgeSectionIndices = conversionResult
                .DoubleStartPhysicalBridgeSectionIndices
                .Where(originalToExpandedSectionIndex.ContainsKey)
                .Select(originalSectionIndex =>
                    originalToExpandedSectionIndex[originalSectionIndex])
                .ToList();

            conversionResult.CenterlineParts.Clear();
            foreach (var transformedCenterlinePart in transformedCenterlineParts)
            {
                conversionResult.CenterlineParts.Add(transformedCenterlinePart);
            }

            conversionResult.StackMembers.Clear();
            foreach (var transformedMember in transformedMembers)
            {
                conversionResult.StackMembers.Add(transformedMember);
            }

            conversionResult.DoubleStartPhysicalBridgeSectionIndices.Clear();
            foreach (var remappedBridgeSectionIndex in
                remappedBridgeSectionIndices)
            {
                conversionResult.DoubleStartPhysicalBridgeSectionIndices.Add(
                    remappedBridgeSectionIndex);
            }

            conversionResult.USplitContactSide = contactOffsetSign;
            return true;
        }

        internal static bool TryRestoreSelectedTerminalLines(
            IList<PlanarBendBusbarSetting> originalSequence,
            IList<PlanarBendBusbarSetting> expandedSequence,
            bool restoreStart, bool restoreEnd, ISet<int> lockedSections,
            out string failureReason)
        {
            failureReason = null;
            var firstExpanded = expandedSequence.First();
            var lastExpanded = expandedSequence.Last();
            var startTarget = originalSequence.First().PointList.First();
            var endTarget = originalSequence.Last().PointList.Last();
            var startDelta = new Vector3d(firstExpanded.PointList.First(), startTarget);
            var endDelta = new Vector3d(lastExpanded.PointList.Last(), endTarget);
            var startNormal = GetBoundaryThickness(firstExpanded, true);
            var endNormal = GetBoundaryThickness(lastExpanded, false);
            if ((restoreStart && Math.Abs(startDelta.Dot(firstExpanded.WidthVector.Normalized)) > GeometryTolerance) ||
                (restoreEnd && Math.Abs(endDelta.Dot(lastExpanded.WidthVector.Normalized)) > GeometryTolerance))
            { failureReason = "拆U后固定端宽向不能由厚向联动恢复。"; return false; }
            var chain = new LinkageStackedRouteMember();
            foreach (var part in expandedSequence) chain.PlanarParts.Add(part);
            if (!LinkageRouteCenterlineConverter.TryMoveOrdinaryMemberTerminalChain(chain,
                    restoreStart ? ScaleBoundary(startNormal, startDelta.Dot(startNormal)) : new Vector3d(0,0,0),
                    restoreEnd ? ScaleBoundary(endNormal, endDelta.Dot(endNormal)) : new Vector3d(0,0,0),
                    out var movedChain, out failureReason)) return false;
            for (var partIndex = 0; partIndex < expandedSequence.Count; partIndex++)
            {
                if (lockedSections.Contains(expandedSequence[partIndex].PhysicalSectionIndex) &&
                    expandedSequence[partIndex].PointList.Zip(movedChain.PlanarParts[partIndex].PointList,
                        (before, after) => before.DistanceTo(after) > GeometryTolerance).Any(changed => changed))
                { failureReason = "拆U端部恢复不能移动锁定连接排。"; return false; }
            }
            if (restoreStart) movedChain.PlanarParts.First().PointList[0] = startTarget.Copy();
            if (restoreEnd)
            {
                var movedLast = movedChain.PlanarParts.Last();
                movedLast.PointList[movedLast.PointList.Count - 1] = endTarget.Copy();
            }
            expandedSequence.Clear();
            foreach (var part in movedChain.PlanarParts) expandedSequence.Add(part);
            return true;
        }

        private static Vector3d GetBoundaryThickness(PlanarBendBusbarSetting part, bool atStart)
        {
            var firstDirection = new Vector3d(part.PointList[0], part.PointList[1]).Normalized;
            var orientation = firstDirection.Cross(part.WidthVector.Normalized)
                .Dot(part.StartBusbarVector.Normalized) < 0d ? -1d : 1d;
            var index = atStart ? 0 : part.PointList.Count - 2;
            return ScaleBoundary(new Vector3d(part.PointList[index], part.PointList[index+1]).Normalized
                .Cross(part.WidthVector.Normalized).Normalized, orientation);
        }

        private static Vector3d ScaleBoundary(Vector3d vector, double distance)
        { return new Vector3d(vector.X * distance, vector.Y * distance, vector.Z * distance); }

        internal static bool ContainsUShape(
            IEnumerable<PlanarBendBusbarSetting> planarSettings)
        {
            return (planarSettings ??
                    Enumerable.Empty<PlanarBendBusbarSetting>())
                .Any(planarSetting => GetReturnSplitSegmentIndices(
                    planarSetting?.PointList).Count > 0);
        }

        private static bool TryTransformPartSequence(
            IList<PlanarBendBusbarSetting> sourceParts,
            ICollection<PlanarBendBusbarSetting> transformedParts,
            int contactOffsetSign,
            out IList<int> sectionExpansionCounts,
            out bool geometryChanged,
            out string failureReason)
        {
            sectionExpansionCounts = new List<int>();
            geometryChanged = false;
            failureReason = null;
            var accumulatedDownstreamShift = new Vector3d(0d, 0d, 0d);
            foreach (var sourcePart in sourceParts)
            {
                var shiftedSourcePart = sourcePart?.Copy();
                if (shiftedSourcePart?.PointList == null ||
                    shiftedSourcePart.PointList.Count < 2)
                {
                    failureReason = "U 型替代遇到空的物理铜排。";
                    return false;
                }

                TranslateSetting(shiftedSourcePart, accumulatedDownstreamShift);
                var expandedParts = new List<PlanarBendBusbarSetting>
                {
                    shiftedSourcePart
                };
                var partExpansionCount = 1;
                var expandedPartIndex = 0;
                while (expandedPartIndex < expandedParts.Count)
                {
                    var currentPart = expandedParts[expandedPartIndex];
                    var uShapeSplitSegmentIndices =
                        GetRestrictedReturnSegmentIndices(currentPart);
                    if (uShapeSplitSegmentIndices.Count == 0)
                    {
                        expandedPartIndex++;
                        continue;
                    }

                    var normalizedRestrictedM = false;
                    foreach (var restrictedMiddleIndex in uShapeSplitSegmentIndices.OrderBy(index => index))
                    {
                        if (!HasLongerMiddleStraight(currentPart, restrictedMiddleIndex) &&
                            TryNormalizeRestrictedM(currentPart, restrictedMiddleIndex, out var normalizedMPart))
                        {
                            expandedParts[expandedPartIndex] = normalizedMPart;
                            geometryChanged = true;
                            normalizedRestrictedM = true;
                            break;
                        }
                    }
                    if (normalizedRestrictedM)
                        continue;
                    if (GetReturnSplitSegmentIndices(currentPart.PointList).Count == 0)
                    {
                        failureReason = "M段中间实际直线不大于两端之和，且同锚点转L不可行。";
                        return false;
                    }

                    if (!TrySplitAtFirstAvailableReturnSegment(
                            currentPart,
                            uShapeSplitSegmentIndices,
                            contactOffsetSign,
                            out var firstLPart,
                            out var secondLPart,
                            out var downstreamShift,
                            out failureReason))
                    {
                        return false;
                    }

                    expandedParts[expandedPartIndex] = firstLPart;
                    expandedParts.Insert(expandedPartIndex + 1, secondLPart);
                    for (var downstreamPartIndex = expandedPartIndex + 2; downstreamPartIndex < expandedParts.Count; downstreamPartIndex++)
                        TranslateSetting(expandedParts[downstreamPartIndex], downstreamShift);
                    accumulatedDownstreamShift = Add(
                        accumulatedDownstreamShift,
                        downstreamShift);
                    partExpansionCount++;
                    geometryChanged = true;
                }

                foreach (var expandedPart in expandedParts)
                {
                    transformedParts.Add(expandedPart);
                }
                sectionExpansionCounts.Add(partExpansionCount);
            }

            return true;
        }

        private static bool TrySplitAtFirstAvailableReturnSegment(
            PlanarBendBusbarSetting sourceSetting,
            IReadOnlyList<int> splitSegmentIndices,
            int contactOffsetSign,
            out PlanarBendBusbarSetting firstLSetting,
            out PlanarBendBusbarSetting secondLSetting,
            out Vector3d downstreamShift,
            out string failureReason)
        {
            firstLSetting = null;
            secondLSetting = null;
            downstreamShift = new Vector3d(0d, 0d, 0d);
            failureReason = null;
            foreach (var splitSegmentIndex in splitSegmentIndices ??
                Array.Empty<int>())
            {
                if (TrySplitAtUShape(
                        sourceSetting,
                        splitSegmentIndex,
                        contactOffsetSign,
                        out firstLSetting,
                        out secondLSetting,
                        out downstreamShift,
                        out failureReason))
                {
                    return true;
                }
            }

            if (string.IsNullOrWhiteSpace(failureReason))
            {
                failureReason = "同一铜排回折区间没有可用于水平满搭接的拆分段。";
            }

            return false;
        }

        private static bool TrySplitAtUShape(
            PlanarBendBusbarSetting sourceSetting,
            int middleSegmentIndex,
            int contactOffsetSign,
            out PlanarBendBusbarSetting firstLSetting,
            out PlanarBendBusbarSetting secondLSetting,
            out Vector3d downstreamShift,
            out string failureReason)
        {
            firstLSetting = null;
            secondLSetting = null;
            downstreamShift = new Vector3d(0d, 0d, 0d);
            failureReason = null;
            var points = sourceSetting.PointList;
            var middleSegmentVector = new Vector3d(
                points[middleSegmentIndex],
                points[middleSegmentIndex + 1]);
            if (middleSegmentVector.Norm <= GeometryTolerance ||
                sourceSetting.WidthVector == null ||
                sourceSetting.WidthVector.Norm <= GeometryTolerance ||
                sourceSetting.BusBarWidth <= GeometryTolerance ||
                sourceSetting.BusBarThickness <= GeometryTolerance)
            {
                failureReason = "U 型中间段缺少有效排规或方向。";
                return false;
            }

            var middleDirection = middleSegmentVector.Normalized;
            var bendStraightReserve = Math.Max(
                0d,
                sourceSetting.BarBendingRadius +
                sourceSetting.BusBarThickness);
            var internalLapLength = sourceSetting.BusBarWidth;
            var requiredMiddleLength =
                2d * bendStraightReserve + internalLapLength;
            if (middleSegmentVector.Norm + GeometryTolerance <
                requiredMiddleLength)
            {
                failureReason = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "U 型中间段 {0:F3} mm 无法拆成双 L：至少需要两侧折弯直线余量与 {1:F3} mm 满搭接，总计 {2:F3} mm。",
                    middleSegmentVector.Norm,
                    internalLapLength,
                    requiredMiddleLength);
                return false;
            }

            var secondLapStartDistance =
                (middleSegmentVector.Norm - internalLapLength) / 2d;
            var firstLapEndDistance = secondLapStartDistance +
                internalLapLength;
            var firstLapEndPoint = Translate(
                points[middleSegmentIndex],
                middleDirection,
                firstLapEndDistance);
            var secondLapStartPoint = Translate(
                points[middleSegmentIndex],
                middleDirection,
                secondLapStartDistance);

            var localThicknessDirection = middleDirection.Cross(
                sourceSetting.WidthVector.Normalized);
            if (localThicknessDirection.Norm <= GeometryTolerance)
            {
                failureReason = "U 型双 L 替代无法确定中间搭接面的排厚方向。";
                return false;
            }

            localThicknessDirection = localThicknessDirection.Normalized;
            if (sourceSetting.StartBusbarVector != null &&
                sourceSetting.StartBusbarVector.Norm > GeometryTolerance &&
                localThicknessDirection.Dot(
                    sourceSetting.StartBusbarVector.Normalized) < 0d)
            {
                localThicknessDirection = Scale(localThicknessDirection, -1d);
            }
            downstreamShift = Scale(
                localThicknessDirection,
                contactOffsetSign * sourceSetting.BusBarThickness);

            firstLSetting = sourceSetting.Copy();
            firstLSetting.PointList = points
                .Take(middleSegmentIndex + 1)
                .Select(CopyPoint)
                .Concat(new[] { firstLapEndPoint })
                .ToList();
            firstLSetting.EndLapWidth = internalLapLength;

            secondLSetting = sourceSetting.Copy();
            secondLSetting.PointList = new[] { secondLapStartPoint }
                .Concat(points.Skip(middleSegmentIndex + 1))
                .Select(CopyPoint)
                .ToList();
            TranslateSetting(secondLSetting, downstreamShift);
            secondLSetting.StartBusbarVector = CopyVector(
                localThicknessDirection);
            secondLSetting.StartLapWidth = internalLapLength;

            if (!LinkageRouteTerminalLapRegionValidator
                    .TryCreateActualLapFootprint(
                        firstLSetting,
                        false,
                        secondLSetting,
                        true,
                        internalLapLength,
                        out _,
                        false,
                        false,
                        GeometryTolerance))
            {
                failureReason = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "U 型回折拆分后的水平搭接实际投影不足一个排宽 {0:F3} mm。",
                    internalLapLength);
                firstLSetting = null;
                secondLSetting = null;
                return false;
            }

            return true;
        }

        private static IReadOnlyList<int> GetReturnSplitSegmentIndices(
            IList<Point3d> points)
        {
            if (points == null || points.Count < 4)
            {
                return Array.Empty<int>();
            }

            var segmentDirections = new List<Vector3d>();
            for (var segmentIndex = 0;
                segmentIndex < points.Count - 1;
                segmentIndex++)
            {
                var segmentDirection = new Vector3d(
                    points[segmentIndex],
                    points[segmentIndex + 1]);
                segmentDirections.Add(segmentDirection.Norm > GeometryTolerance
                    ? segmentDirection.Normalized
                    : null);
            }

            var candidateSegments = new List<int>();
            for (var returnSegmentIndex = 2;
                returnSegmentIndex < segmentDirections.Count;
                returnSegmentIndex++)
            {
                if (segmentDirections[returnSegmentIndex] == null)
                {
                    continue;
                }

                for (var firstSegmentIndex = 0;
                    firstSegmentIndex + 1 < returnSegmentIndex;
                    firstSegmentIndex++)
                {
                    var firstSegmentDirection =
                        segmentDirections[firstSegmentIndex];
                    if (firstSegmentDirection == null ||
                        !AreOppositeParallelDirections(
                            firstSegmentDirection,
                            segmentDirections[returnSegmentIndex]))
                    {
                        continue;
                    }

                    // 两条反向平行段之间的每一段都可以成为新排的
                    // 水平搭接段；实际拆分时按长度优先尝试，避免因某个
                    // 短分段不足一个排宽而错过同一回折区间中的合法段。
                    for (var splitSegmentIndex = firstSegmentIndex + 1;
                        splitSegmentIndex < returnSegmentIndex;
                        splitSegmentIndex++)
                    {
                        candidateSegments.Add(splitSegmentIndex);
                    }

                    break;
                }
            }

            return candidateSegments
                .Distinct()
                .OrderByDescending(splitSegmentIndex =>
                    points[splitSegmentIndex].DistanceTo(
                        points[splitSegmentIndex + 1]))
                .ThenBy(splitSegmentIndex => splitSegmentIndex)
                .ToList();
        }

        private static bool AreOppositeParallelDirections(
            Vector3d firstDirection,
            Vector3d secondDirection)
        {
            if (firstDirection == null || secondDirection == null ||
                firstDirection.Norm <= GeometryTolerance ||
                secondDirection.Norm <= GeometryTolerance)
            {
                return false;
            }

            var normalizedFirstDirection = firstDirection.Normalized;
            var normalizedSecondDirection = secondDirection.Normalized;
            return normalizedFirstDirection.Cross(
                    normalizedSecondDirection).Norm <=
                    LinkageRouteGeometryTolerances.Direction &&
                normalizedFirstDirection.Dot(normalizedSecondDirection) <=
                    -1d + LinkageRouteGeometryTolerances.Direction;
        }

        private static bool TryAssignExpandedSectionIndices(
            IList<PlanarBendBusbarSetting> sourceSettings, IList<PlanarBendBusbarSetting> expandedSettings,
            IList<int> localExpansionCounts, IDictionary<int, int> globalExpandedStarts,
            IDictionary<int, int> globalExpansionCounts, int centerlineSectionCount, out string failureReason)
        {
            failureReason = null;
            var expandedCursor = 0;
            for (var localIndex = 0; localIndex < sourceSettings.Count; localIndex++)
            {
                var sourceIdentity = sourceSettings[localIndex].PhysicalSectionIndex;
                if (sourceIdentity < 0 && sourceSettings.Count == centerlineSectionCount) sourceIdentity = localIndex;
                if (!globalExpandedStarts.TryGetValue(sourceIdentity, out var expandedStart) ||
                    !globalExpansionCounts.TryGetValue(sourceIdentity, out var expectedCount) ||
                    localExpansionCounts[localIndex] != expectedCount)
                {
                    failureReason = "U型拆分的实际成员缺少唯一全局物理段身份，或同段展开数量不一致。";
                    return false;
                }
                for (var expandedOffset = 0; expandedOffset < expectedCount; expandedOffset++)
                {
                    var part = expandedSettings[expandedCursor++];
                    if (part.SpecificationSectionIndex < 0) part.SpecificationSectionIndex = sourceIdentity;
                    part.PhysicalSectionIndex = expandedStart + expandedOffset;
                }
            }
            return expandedCursor == expandedSettings.Count;
        }

        private static void TranslateSetting(
            PlanarBendBusbarSetting setting,
            Vector3d translation)
        {
            if (setting?.PointList == null ||
                translation == null ||
                translation.Norm <= GeometryTolerance)
            {
                return;
            }

            setting.PointList = setting.PointList
                .Select(point => new Point3d(
                    point.X + translation.X,
                    point.Y + translation.Y,
                    point.Z + translation.Z))
                .ToList();
        }

        private static Point3d Translate(
            Point3d point,
            Vector3d direction,
            double distance)
        {
            return new Point3d(
                point.X + direction.X * distance,
                point.Y + direction.Y * distance,
                point.Z + direction.Z * distance);
        }

        private static Point3d CopyPoint(Point3d point)
        {
            return new Point3d(point.X, point.Y, point.Z);
        }

        private static Vector3d CopyVector(Vector3d vector)
        {
            return vector == null
                ? null
                : new Vector3d(vector.X, vector.Y, vector.Z);
        }

        private static Vector3d Scale(Vector3d vector, double scalar)
        {
            return new Vector3d(
                vector.X * scalar,
                vector.Y * scalar,
                vector.Z * scalar);
        }

        private static Vector3d Add(Vector3d first, Vector3d second)
        {
            return new Vector3d(
                first.X + second.X,
                first.Y + second.Y,
                first.Z + second.Z);
        }
    }
}
