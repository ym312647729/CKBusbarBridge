using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Routing.Algorithms;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace RouteLinkBar.Algorithm.Routing
{
    /// <summary>
    /// 独立历史路径重建：不依赖普通算法搜索方案池，直接由命中方案的历史中间排
    /// 中心线在当前固定端之间重建中间排。
    ///
    /// 历史库仍然只提供"方向/拓扑提案"，实际中心线转换、搭接落位、静态限制、
    /// 叠排实体化和端部输出处理全部由 <see cref="LinkageRouteCandidateFactory"/>
    /// 负责；本文件只负责把历史提案调整成"当前可制造中心线"并逐个候选实体化。
    /// </summary>
    public static partial class LinkageSchemeLibraryRouteRebuilder
    {
        /// <summary>历史中心线闭合容差，单位毫米。</summary>
        private const double HistoricalCenterlineClosureTolerance = 0.5d;

        /// <summary>历史中心线最小可制造段长下限，避免求出的段长过小。</summary>
        private const double HistoricalCenterlineFallbackSegmentLength = 40d;

        /// <summary>
        /// 独立重建：每个路径组都由历史中心线在当前固定端之间重新生成中间排，
        /// 任一路径组失败则整套候选不发布。
        /// </summary>
        public static LinkageRouteSolution RebuildFromHistoricalPath(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageSchemeLibraryMatchResult matchResult,
            LinkageSchemeLibraryRouteTemplate routeTemplate,
            CancellationToken cancellationToken,
            bool allowExpensiveRepair = true)
        {
            var solution = CreateSchemeLibrarySolutionShell(routeRequest, matchResult);
            if (routeRequest == null || matchResult == null || routeTemplate == null ||
                routeTemplate.Routes == null || routeTemplate.Routes.Count == 0)
            {
                AddFailure(solution, "方案库路径模板或当前固定端请求为空。");
                return solution;
            }

            var rebuiltRouteCount = 0;
            foreach (var routeTemplateRoute in routeTemplate.Routes)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    AddFailure(solution, "方案库路径重建已取消。");
                    break;
                }

                var routeRebuild = RebuildHistoricalRoute(
                    routeRequest,
                    matchResult,
                    routeTemplateRoute,
                    cancellationToken,
                    allowExpensiveRepair);
                if (!routeRebuild.IsSuccess || routeRebuild.GroupSolution == null)
                {
                    AddFailure(solution, routeRebuild.FailureMessage);
                    // 继续尝试其余路径组，保留已经通过完整实体化校验的
                    // 历史中间排。Worker 后续可在跨组最终复核通过时，把
                    // 这些不改方向的历史组嵌回普通合法方案，避免一个历史
                    // 组的局部附件冲突吞掉整套候选。
                    continue;
                }

                solution.GroupSolutions.Add(routeRebuild.GroupSolution);
                solution.ActualCopperLength += routeRebuild.GroupSolution.ActualCopperLength;
                solution.PerpendicularLapCount += routeRebuild.PerpendicularLapCount;
                rebuiltRouteCount++;
            }

            if (solution.Diagnostics.Count > 0)
            {
                solution.IsSuccess = false;
                solution.MinimumClearance = double.NaN;
                return solution;
            }

            solution.IsSuccess = rebuiltRouteCount == routeTemplate.Routes.Count &&
                rebuiltRouteCount > 0;
            if (solution.IsSuccess &&
                !LinkageMultiGroupRouteEngine.TryValidateSchemeLibraryOutput(
                    routeRequest,
                    solution,
                    out var finalOutputFailureReason))
            {
                solution.IsSuccess = false;
                AddFailure(solution, "方案库重算完整输出复核失败：" +
                    (finalOutputFailureReason ?? "未提供原因"));
            }
            solution.MinimumClearance = solution.IsSuccess
                ? routeRequest.InsulationClearance
                : double.NaN;
            return solution;
        }

        /// <summary>
        /// 逐路径组诊断：输出历史成员、搭接界面、历史方向序列、当前端点、
        /// 历史可制造化各变体的实体化结果。纯数据，供虚拟验证使用。
        /// </summary>
        public static IReadOnlyList<string> DescribeHistoricalPathRebuild(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageSchemeLibraryMatchResult matchResult,
            LinkageSchemeLibraryRouteTemplate routeTemplate,
            CancellationToken cancellationToken)
        {
            var lines = new List<string>();
            if (routeRequest == null || matchResult == null || routeTemplate == null ||
                routeTemplate.Routes == null)
            {
                lines.Add("（历史路径诊断为空的输入）");
                return lines;
            }

            for (var routeIndex = 0; routeIndex < routeTemplate.Routes.Count; routeIndex++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var routeTemplateRoute = routeTemplate.Routes[routeIndex];
                lines.Add("### 路径 " + (routeIndex + 1) + "：" +
                    (routeTemplateRoute?.RouteKey ?? "(无键)"));
                try
                {
                    lines.AddRange(DescribeHistoricalRoute(
                        routeRequest,
                        matchResult,
                        routeTemplateRoute,
                        cancellationToken));
                }
                catch (Exception exception)
                {
                    lines.Add("- 诊断异常：" + exception.Message);
                }
            }

            return lines;
        }

        private sealed class HistoricalRouteRebuildResult
        {
            public bool IsSuccess { get; set; }
            public string FailureMessage { get; set; }
            public LinkageGroupRouteSolution GroupSolution { get; set; }
            public int PerpendicularLapCount { get; set; }
            public List<string> Lines { get; } = new List<string>();
        }

        /// <summary>
        /// 逐路径诊断：跑一次同样的重建流程，输出历史成员、方向序列、
        /// 当前端点、各变体的实体化结果。
        /// </summary>
        private static IEnumerable<string> DescribeHistoricalRoute(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageSchemeLibraryMatchResult matchResult,
            LinkageSchemeLibraryRouteTemplateRoute routeTemplateRoute,
            CancellationToken cancellationToken)
        {
            var rebuildResult = RebuildHistoricalRoute(
                routeRequest,
                matchResult,
                routeTemplateRoute,
                cancellationToken);
            var lines = new List<string>(rebuildResult.Lines);
            lines.Add("- 结论：" + (rebuildResult.IsSuccess
                ? "成功（拓扑=" + (rebuildResult.GroupSolution?.TopologySignature ?? string.Empty) + "）"
                : "失败：" + (rebuildResult.FailureMessage ?? string.Empty)));
            return lines;
        }

        private sealed class HistoricalCenterlineVariant
        {
            public string Name { get; set; }
            public IList<Point3d> Centerline { get; set; }
        }

        /// <summary>
        /// 逐中间排成员输出原始（变换后）点列、界面点与自身方向，用于判断历史
        /// 点列里的回折是真实形状还是成员之间的伪连接段。
        /// </summary>
        private static IEnumerable<string> DescribeMiddleMembers(
            LinkageSchemeLibraryRouteTemplateRoute routeTemplateRoute,
            IList<LinkageSchemeLibraryRouteTemplateMember> middleMembers,
            LinkageSchemeLibraryRigidTransform routeTransform)
        {
            var lines = new List<string>();
            var startTemplateMember = routeTemplateRoute?.OrderedMembers
                ?.FirstOrDefault(member => member != null &&
                    member.PositionType == StartPositionType);
            var cabinetTemplateMember = routeTemplateRoute?.OrderedMembers
                ?.LastOrDefault(member => member != null &&
                    member.PositionType == CabinetTopPositionType);
            var previousTemplateMemberKey = startTemplateMember?.MemberKey;
            for (var memberIndex = 0; memberIndex < middleMembers.Count; memberIndex++)
            {
                var middleMember = middleMembers[memberIndex];
                var transformedPoints = (middleMember?.CenterlinePoints ??
                        new List<LinkageSchemeLibraryVector>())
                    .Select(point => TransformPoint(routeTransform, point))
                    .Where(point => point != null)
                    .ToList();
                var nextTemplateMemberKey = memberIndex + 1 < middleMembers.Count
                    ? middleMembers[memberIndex + 1]?.MemberKey
                    : cabinetTemplateMember?.MemberKey;
                var previousInterface = ResolveTemplateInterfacePoint(
                    routeTemplateRoute,
                    previousTemplateMemberKey,
                    middleMember?.MemberKey,
                    routeTransform);
                var nextInterface = ResolveTemplateInterfacePoint(
                    routeTemplateRoute,
                    middleMember?.MemberKey,
                    nextTemplateMemberKey,
                    routeTransform);
                var startProjection = ProjectPointToPolyline(previousInterface, transformedPoints);
                var endProjection = ProjectPointToPolyline(nextInterface, transformedPoints);
                lines.Add("- 中间排成员[" + memberIndex + "] 键=" +
                    (middleMember?.MemberKey ?? "(空)") +
                    "，形状=" + (middleMember?.Shape ?? "(空)") +
                    "，" + middleMember?.BusbarWidth.ToString("F1") + "×" +
                    middleMember?.BusbarThickness.ToString("F1") +
                    "，原始点=" + FormatPoints(transformedPoints) +
                    "，自身方向=" + FormatDirections(BuildReducedPathDirections(transformedPoints)));
                lines.Add("  - 上一界面=" + FormatPoint(previousInterface) +
                    "（投影段=" + (startProjection?.SegmentIndex.ToString() ?? "-") +
                    "，沿程=" + (startProjection == null
                        ? "-"
                        : startProjection.DistanceAlong.ToString("F1", CultureInfo.InvariantCulture)) +
                    "），下一界面=" + FormatPoint(nextInterface) +
                    "（投影段=" + (endProjection?.SegmentIndex.ToString() ?? "-") +
                    "，沿程=" + (endProjection == null
                        ? "-"
                        : endProjection.DistanceAlong.ToString("F1", CultureInfo.InvariantCulture)) +
                    "），需反向=" + (startProjection != null && endProjection != null &&
                        startProjection.DistanceAlong > endProjection.DistanceAlong));
                previousTemplateMemberKey = middleMember?.MemberKey;
            }
            return lines;
        }

        /// <summary>
        /// 端部直腿加长：末端/首端直腿必须容纳"所搭接固定排真实排宽 + 单排折弯余量"，
        /// 不足时把差值沿该端直腿方向补足，并把同一轴向、同向的最近中间段等量缩短
        /// （总位移不变、两端锚点不动）。没有可吸收的同轴向段时返回 false。
        /// </summary>
        private static bool TryExtendEndSegment(
            IList<Point3d> centerline,
            double requiredEndLength,
            double minimumSegmentLength,
            bool extendStartEnd,
            out List<Point3d> extendedCenterline)
        {
            extendedCenterline = null;
            var points = RemoveCoincidentPoints(centerline);
            if (points.Count < 3 || requiredEndLength <= 0d)
            {
                return false;
            }

            var endIndex = extendStartEnd ? 1 : points.Count - 1;
            var cornerIndex = extendStartEnd ? 1 : points.Count - 2;
            var endDirection = new Vector3d(points[cornerIndex], points[endIndex]);
            if (endDirection.Norm <= 1e-8d)
            {
                return false;
            }

            endDirection = endDirection.Normalized;
            var currentEndLength = points[cornerIndex].DistanceTo(points[endIndex]);
            var extraLength = requiredEndLength - currentEndLength;
            if (extraLength <= 1e-6d)
            {
                return false;
            }

            // 需要一段与端腿同轴向、同向的中间段来吸收这 extraLength。
            var absorbIndex = -1;
            for (var index = cornerIndex; index >= 2; index--)
            {
                var segmentVector = new Vector3d(points[index - 1], points[index]);
                if (segmentVector.Norm <= 1e-8d ||
                    segmentVector.Normalized.Dot(endDirection) < 0.999999d)
                {
                    continue;
                }

                if (segmentVector.Norm - extraLength + 1e-6d < minimumSegmentLength)
                {
                    continue;
                }

                absorbIndex = index;
                break;
            }
            if (absorbIndex < 0)
            {
                return false;
            }

            var shiftedPoints = points.Select(CopyPoint).ToList();
            for (var index = absorbIndex; index <= cornerIndex; index++)
            {
                shiftedPoints[index] = new Point3d(
                    points[index].X - endDirection.X * extraLength,
                    points[index].Y - endDirection.Y * extraLength,
                    points[index].Z - endDirection.Z * extraLength);
            }

            for (var index = 1; index < shiftedPoints.Count; index++)
            {
                if (shiftedPoints[index - 1].DistanceTo(shiftedPoints[index]) + 1e-6d <
                    minimumSegmentLength)
                {
                    return false;
                }
            }

            extendedCenterline = shiftedPoints;
            return true;
        }

        /// <summary>
        /// 给阶梯里每个变体补一份"端腿加长"版本，用于修掉"端部直腿不足
        /// 所搭接排宽 + 单排折弯余量"这一类失败。
        /// </summary>
        private static void AddEndLegExtendedVariants(
            LinkageMultiGroupRouteRequest routeRequest,
            IList<HistoricalCenterlineVariant> variants,
            IList<string> diagnosticLines)
        {
            if (variants == null || variants.Count == 0)
            {
                return;
            }

            var minimumSegmentLength = Math.Max(
                1d,
                routeRequest?.MinimumSegmentLength ?? HistoricalCenterlineFallbackSegmentLength);
            var requiredEndLegLength = ResolveRequiredEndLegLength(routeRequest);
            var extendedVariants = new List<HistoricalCenterlineVariant>();
            foreach (var variant in variants)
            {
                for (var endIndex = 0; endIndex < 2; endIndex++)
                {
                    if (!TryExtendEndSegment(
                            variant.Centerline,
                            requiredEndLegLength,
                            minimumSegmentLength,
                            endIndex == 1,
                            out var extendedCenterline))
                    {
                        continue;
                    }

                    var endLabel = endIndex == 1 ? "始端" : "柜顶端";
                    diagnosticLines.Add("- 端腿加长【" + variant.Name + " / " + endLabel +
                        "】端腿下限=" + requiredEndLegLength.ToString(
                            "F1", CultureInfo.InvariantCulture));
                    extendedVariants.Add(new HistoricalCenterlineVariant
                    {
                        Name = variant.Name + "+端腿加长(" + endLabel + ")",
                        Centerline = extendedCenterline
                    });
                }
            }
            foreach (var extendedVariant in extendedVariants)
            {
                variants.Add(extendedVariant);
            }
        }

        /// <summary>
        /// 端腿下限拟合变体：把端腿下限（所搭接固定排真实排宽 + 单排折弯余量）直接
        /// 作为拟合的段长下限，让段长重解阶段就满足"末端垂直搭接端部线段"要求，
        /// 不再依赖事后加长（事后加长需要同轴向段吸收，很多折线不存在这样的段）。
        /// </summary>
        private static void AddEndLegMinimumFitVariants(
            IList<HistoricalCenterlineVariant> variants,
            LinkageMultiGroupRouteRequest routeRequest,
            Point3d startPoint,
            Point3d endPoint,
            IList<Vector3d> historicalDirectionSequence,
            IList<double> baseLengths,
            IList<string> diagnosticLines,
            double endLegExtra = 0d)
        {
            if (startPoint == null || endPoint == null ||
                historicalDirectionSequence == null ||
                historicalDirectionSequence.Count == 0)
            {
                return;
            }

            var endLegMinimum = Math.Max(
                1d,
                ResolveRequiredEndLegLength(routeRequest) + Math.Max(0d, endLegExtra));
            var minimumSegmentLength = Math.Max(
                1d,
                routeRequest?.MinimumSegmentLength ?? HistoricalCenterlineFallbackSegmentLength);
            if (endLegMinimum <= minimumSegmentLength + 1e-6d)
            {
                return;
            }

            var targetDisplacement = new Vector3d(startPoint, endPoint);
            // 历史只定用哪些轴向、各用几次；轴向顺序（含轮换）与正负号由当前位移决定。
            // 轮换会改变折点位置，从而改变与端部附件的相对位置，用来避开局部重叠。
            var orientedSequence = SnapDirectionsToAxis(
                OrientDirectionsToDisplacement(
                    historicalDirectionSequence,
                    targetDisplacement));
            var fittingModes = new List<Tuple<string, Point3d, Point3d, IList<Vector3d>>>();
            for (var rotation = 0; rotation < Math.Max(1, orientedSequence.Count); rotation++)
            {
                var rotated = RotateDirections(orientedSequence, rotation);
                if (rotated.Count == 0)
                {
                    continue;
                }

                fittingModes.Add(new Tuple<string, Point3d, Point3d, IList<Vector3d>>(
                    "端腿下限-位移定向-序" + rotation,
                    startPoint,
                    endPoint,
                    rotated));
                fittingModes.Add(new Tuple<string, Point3d, Point3d, IList<Vector3d>>(
                    "端腿下限-柜顶端对齐-序" + rotation,
                    endPoint,
                    startPoint,
                    rotated.AsEnumerable().Reverse().ToList()));
            }
            foreach (var fittingMode in fittingModes)
            {
                var fitStartPoint = fittingMode.Item2;
                var fitEndPoint = fittingMode.Item3;
                var reducedDirections = ReduceDirections(fittingMode.Item4);
                if (reducedDirections.Count == 0)
                {
                    continue;
                }

                // 工艺口径：端腿行程不足时，端部搭接点可沿固定端允许区间滑移，
                // 换出足够行程（规范第 277、290 条：整张有效厚面矩形均可作搭接位置）。
                EnlargeEndLegTravel(
                    reducedDirections,
                    fitStartPoint,
                    fitEndPoint,
                    endLegMinimum,
                    out var adjustedStartPoint,
                    out var adjustedEndPoint);
                if (adjustedStartPoint.DistanceTo(fitStartPoint) > 1e-6d ||
                    adjustedEndPoint.DistanceTo(fitEndPoint) > 1e-6d)
                {
                    diagnosticLines.Add("- 端腿下限【" + fittingMode.Item1 +
                        "】端部搭接点沿固定端滑移：始端 " +
                        adjustedStartPoint.DistanceTo(fitStartPoint).ToString(
                            "F1", CultureInfo.InvariantCulture) +
                        " mm，柜顶端 " +
                        adjustedEndPoint.DistanceTo(fitEndPoint).ToString(
                            "F1", CultureInfo.InvariantCulture) + " mm");
                }
                fitStartPoint = adjustedStartPoint;
                fitEndPoint = adjustedEndPoint;
                var fitDisplacement = new Vector3d(fitStartPoint, fitEndPoint);

                if (!TryFitDirectionalLengthsWithEndMinima(
                        reducedDirections,
                        ResolveFittedBaseLengths(
                            reducedDirections.Count,
                            baseLengths,
                            endLegMinimum),
                        fitDisplacement,
                        minimumSegmentLength,
                        endLegMinimum,
                        out var fittedLengths))
                {
                    diagnosticLines.Add("- 端腿下限【" + fittingMode.Item1 +
                        "】失败：端腿下限=" + endLegMinimum.ToString(
                            "F1", CultureInfo.InvariantCulture) + "，方向集=" +
                        FormatDirections(reducedDirections));
                    continue;
                }

                var fittedCenterline = CreateCenterlineFromSegments(
                    fitStartPoint,
                    reducedDirections,
                    fittedLengths);
                // 段长校验用通用最小段长：端腿下限已在拟合内部按逐段下限保证，
                // 中间段仍按最小段长，不能拿端腿下限去卡中间段。
                if (!IsCenterlineClosed(
                        fittedCenterline,
                        fitEndPoint,
                        minimumSegmentLength))
                {
                    diagnosticLines.Add("- 端腿下限【" + fittingMode.Item1 +
                        "】失败：端点或段长不满足");
                    continue;
                }

                var isCabinetAnchored = fittingMode.Item1.IndexOf(
                    "柜顶端对齐",
                    StringComparison.Ordinal) >= 0;
                diagnosticLines.Add("- 端腿下限【" + fittingMode.Item1 + "】端腿下限=" +
                    endLegMinimum.ToString("F1", CultureInfo.InvariantCulture) +
                    "，段长=" + string.Join("/", fittedLengths.Select(length =>
                        length.ToString("F1", CultureInfo.InvariantCulture))));
                variants.Add(new HistoricalCenterlineVariant
                {
                    Name = fittingMode.Item1,
                    Centerline = isCabinetAnchored
                        ? fittedCenterline.AsEnumerable().Reverse().Select(CopyPoint).ToList()
                        : fittedCenterline
                });
            }
        }

        /// <summary>
        /// 逐段下限拟合：首、末段固定为端腿下限（所搭接固定排真实排宽 + 单排折弯余量），
        /// 中间段按最小段长拟合残差。比"所有段都用端腿下限"宽松，能保住中间段的短位移
        /// 分量由端部/中间正常段承担。
        /// </summary>
        private static bool TryFitDirectionalLengthsWithEndMinima(
            IList<Vector3d> directions,
            IList<double> baseLengths,
            Vector3d targetDisplacement,
            double minimumSegmentLength,
            double endSegmentMinimumLength,
            out IList<double> fittedLengths)
        {
            fittedLengths = null;
            if (directions == null || directions.Count == 0)
            {
                return false;
            }

            var endMinimum = Math.Max(minimumSegmentLength, endSegmentMinimumLength);
            if (directions.Count <= 2)
            {
                return TryFitDirectionalLengths(
                    directions,
                    baseLengths,
                    targetDisplacement,
                    endMinimum,
                    out fittedLengths);
            }

            // 先按最小段长整体拟合；只有端腿结果低于端腿下限时才把该端固定到下限，
            // 再重解其余段去闭合残差（其余段保留全部行程，不能把端腿也钉在下限上，
            // 否则中间段无力吸收剩下的轴向位移）。
            if (!TryFitDirectionalLengths(
                    directions,
                    baseLengths,
                    targetDisplacement,
                    minimumSegmentLength,
                    out var initialLengths))
            {
                return false;
            }

            var lastIndex = directions.Count - 1;
            var clampedLengths = new double[directions.Count];
            var fixedFlags = new bool[directions.Count];
            for (var index = 0; index < directions.Count; index++)
            {
                clampedLengths[index] = initialLengths[index];
            }
            if (clampedLengths[0] + 1e-6d < endMinimum)
            {
                clampedLengths[0] = endMinimum;
                fixedFlags[0] = true;
            }
            if (clampedLengths[lastIndex] + 1e-6d < endMinimum)
            {
                clampedLengths[lastIndex] = endMinimum;
                fixedFlags[lastIndex] = true;
            }
            if (!fixedFlags[0] && !fixedFlags[lastIndex])
            {
                fittedLengths = initialLengths;
                return true;
            }

            var residual = new Vector3d(targetDisplacement.X, targetDisplacement.Y, targetDisplacement.Z);
            var freeDirections = new List<Vector3d>();
            var freeBaseLengths = new List<double>();
            var freeIndices = new List<int>();
            for (var index = 0; index < directions.Count; index++)
            {
                if (fixedFlags[index])
                {
                    residual = new Vector3d(
                        residual.X - directions[index].X * clampedLengths[index],
                        residual.Y - directions[index].Y * clampedLengths[index],
                        residual.Z - directions[index].Z * clampedLengths[index]);
                    continue;
                }

                freeDirections.Add(directions[index]);
                freeBaseLengths.Add(baseLengths != null && baseLengths.Count > 0
                    ? baseLengths[index % baseLengths.Count]
                    : minimumSegmentLength);
                freeIndices.Add(index);
            }
            if (freeDirections.Count == 0 ||
                !TryFitDirectionalLengths(
                    freeDirections,
                    freeBaseLengths,
                    residual,
                    minimumSegmentLength,
                    out var freeLengths))
            {
                return false;
            }

            for (var index = 0; index < freeIndices.Count; index++)
            {
                clampedLengths[freeIndices[index]] = freeLengths[index];
            }
            fittedLengths = clampedLengths;
            return true;
        }

        /// <summary>
        /// 端腿行程补足：首段/末段轴向可用位移小于端腿下限时，把该端搭接点沿固定端
        /// 允许区间滑移，使端腿方向上的行程至少达到端腿下限。滑移量由工厂按
        /// "完整排宽必须投影在固定端有效排宽范围内"复核，越界即失败。
        /// </summary>
        private static void EnlargeEndLegTravel(
            IList<Vector3d> directions,
            Point3d startPoint,
            Point3d endPoint,
            double endLegMinimum,
            out Point3d adjustedStartPoint,
            out Point3d adjustedEndPoint)
        {
            adjustedStartPoint = CopyPoint(startPoint);
            adjustedEndPoint = CopyPoint(endPoint);
            if (directions == null || directions.Count == 0 ||
                adjustedStartPoint == null || adjustedEndPoint == null)
            {
                return;
            }

            var firstAxis = directions[0];
            var firstComponent = new Vector3d(adjustedStartPoint, adjustedEndPoint)
                .Dot(firstAxis);
            if (Math.Abs(firstComponent) < endLegMinimum)
            {
                var firstDeficit = endLegMinimum - Math.Abs(firstComponent);
                var firstSign = firstComponent >= 0d ? 1d : -1d;
                adjustedStartPoint = new Point3d(
                    adjustedStartPoint.X - firstAxis.X * firstSign * firstDeficit,
                    adjustedStartPoint.Y - firstAxis.Y * firstSign * firstDeficit,
                    adjustedStartPoint.Z - firstAxis.Z * firstSign * firstDeficit);
            }

            var lastAxis = directions[directions.Count - 1];
            var lastComponent = new Vector3d(adjustedStartPoint, adjustedEndPoint)
                .Dot(lastAxis);
            if (Math.Abs(lastComponent) < endLegMinimum)
            {
                var lastDeficit = endLegMinimum - Math.Abs(lastComponent);
                var lastSign = lastComponent >= 0d ? 1d : -1d;
                adjustedEndPoint = new Point3d(
                    adjustedEndPoint.X + lastAxis.X * lastSign * lastDeficit,
                    adjustedEndPoint.Y + lastAxis.Y * lastSign * lastDeficit,
                    adjustedEndPoint.Z + lastAxis.Z * lastSign * lastDeficit);
            }
        }

        /// <summary>
        /// 从实体化诊断文字里解析"正体积自相交"的两组长方体，算出最深违规轴与重叠深度。
        /// 诊断格式：中心=(x,y,z)，半长宽厚=(a,b,c)……与……重叠。
        /// </summary>
        private static bool TryParseViolationSeparation(
            LinkageRouteCandidate candidate,
            double thickness,
            out Vector3d separationAxis,
            out double violationDepth)
        {
            separationAxis = null;
            violationDepth = 0d;
            var diagnostics = new List<string>();
            if (!string.IsNullOrWhiteSpace(candidate?.Diagnostic))
            {
                diagnostics.Add(candidate.Diagnostic);
            }
            if (candidate?.PlanarConversion?.Diagnostics != null &&
                candidate.PlanarConversion.Diagnostics.Count > 0)
            {
                diagnostics.AddRange(candidate.PlanarConversion.Diagnostics);
            }

            var centers = new List<double[]>();
            var halfSizes = new List<double[]>();
            foreach (var diagnostic in diagnostics)
            {
                if (string.IsNullOrWhiteSpace(diagnostic) ||
                    diagnostic.IndexOf("自相交", StringComparison.Ordinal) < 0 &&
                    diagnostic.IndexOf("重叠", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                var matches = System.Text.RegularExpressions.Regex.Matches(
                    diagnostic,
                    @"中心=\(([-0-9.]+),([-0-9.]+),([-0-9.]+)\)，半长宽厚=\(([-0-9.]+),([-0-9.]+),([-0-9.]+)\)");
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var center = TryParseTriple(match, 1);
                    var halfSize = TryParseTriple(match, 4);
                    if (center == null || halfSize == null)
                    {
                        continue;
                    }

                    centers.Add(center);
                    halfSizes.Add(halfSize);
                }
                if (centers.Count >= 2)
                {
                    break;
                }
            }

            if (centers.Count < 2)
            {
                return false;
            }

            var candidateCenters = centers;
            var candidateHalfSizes = halfSizes;
            var axisIndex = -1;
            var maximumOverlap = 0d;
            for (var index = 0; index < 3; index++)
            {
                var overlap = candidateHalfSizes[0][index] + candidateHalfSizes[1][index] -
                    Math.Abs(candidateCenters[0][index] - candidateCenters[1][index]);
                if (overlap > maximumOverlap)
                {
                    maximumOverlap = overlap;
                    axisIndex = index;
                }
            }
            if (axisIndex < 0)
            {
                return false;
            }

            var sign = candidateCenters[0][axisIndex] >= candidateCenters[1][axisIndex] ? 1d : -1d;
            separationAxis = new Vector3d(
                axisIndex == 0 ? sign : 0d,
                axisIndex == 1 ? sign : 0d,
                axisIndex == 2 ? sign : 0d);
            // 让开量按规范第 24 条取违规深度，并留四分之一排厚的安全余量。
            violationDepth = maximumOverlap + Math.Max(1d, thickness) * 0.25d;
            return true;
        }

        private static double[] TryParseTriple(
            System.Text.RegularExpressions.Match match,
            int groupIndex)
        {
            var values = new double[3];
            for (var index = 0; index < 3; index++)
            {
                if (!double.TryParse(
                        match.Groups[groupIndex + index].Value,
                        System.Globalization.NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out values[index]))
                {
                    return null;
                }
            }
            return values;
        }

        /// <summary>
        /// 局部让让：只把违规段所在的一段折线沿分离方向平移，平移量落在**两侧最近的
        /// 同轴向段**上（该两段各自伸长/缩短 violationDepth），两端锚点与其余段不动。
        /// 这是"按最深违规的轴向分离方向精确延长最近同轴段"的落地形式。
        /// </summary>
        private static List<IList<Point3d>> BuildLocalSeparationVariants(
            IList<Point3d> centerline,
            Vector3d separationAxis,
            double violationDepth,
            double minimumSegmentLength)
        {
            var variants = new List<IList<Point3d>>();
            var points = RemoveCoincidentPoints(centerline);
            if (points.Count < 4 || separationAxis == null || violationDepth <= 1e-6d)
            {
                return variants;
            }

            var pointCount = points.Count;
            for (var leftBoundary = 1; leftBoundary < pointCount - 2; leftBoundary++)
            {
                var leftSegment = new Vector3d(
                    points[leftBoundary - 1],
                    points[leftBoundary]);
                if (leftSegment.Norm <= 1e-8d ||
                    Math.Abs(leftSegment.Normalized.Dot(separationAxis)) < 0.999999d)
                {
                    continue;
                }

                for (var rightBoundary = leftBoundary + 1;
                    rightBoundary < pointCount - 1;
                    rightBoundary++)
                {
                    var rightSegment = new Vector3d(
                        points[rightBoundary],
                        points[rightBoundary + 1]);
                    if (rightSegment.Norm <= 1e-8d ||
                        Math.Abs(rightSegment.Normalized.Dot(separationAxis)) < 0.999999d)
                    {
                        continue;
                    }

                    var shiftedPoints = points.Select(CopyPoint).ToList();
                    for (var index = leftBoundary; index <= rightBoundary; index++)
                    {
                        shiftedPoints[index] = new Point3d(
                            points[index].X + separationAxis.X * violationDepth,
                            points[index].Y + separationAxis.Y * violationDepth,
                            points[index].Z + separationAxis.Z * violationDepth);
                    }

                    var isValid = true;
                    for (var index = 1; index < shiftedPoints.Count; index++)
                    {
                        if (shiftedPoints[index - 1].DistanceTo(
                                shiftedPoints[index]) + 1e-6d < minimumSegmentLength)
                        {
                            isValid = false;
                            break;
                        }
                    }
                    if (isValid)
                    {
                        variants.Add(shiftedPoints);
                    }
                }
            }
            return variants;
        }

        /// <summary>按给定偏移量轮换方向序列（历史只定轴向集合，顺序可轮换）。</summary>
        private static IList<Vector3d> RotateDirections(
            IList<Vector3d> directions,
            int rotation)
        {
            if (directions == null || directions.Count == 0)
            {
                return new List<Vector3d>();
            }

            var offset = ((rotation % directions.Count) + directions.Count) % directions.Count;
            return directions.Skip(offset).Concat(directions.Take(offset)).ToList();
        }

        /// <summary>端部直腿最小长度：所搭接固定排真实排宽 + 单排折弯余量。</summary>
        private static double ResolveRequiredEndLegLength(
            LinkageMultiGroupRouteRequest routeRequest)
        {
            var fixedBusbarWidth = Math.Max(
                Math.Max(1d, routeRequest.DefaultBusbarWidth),
                Math.Max(1d, routeRequest.DefaultBusbarThickness));
            var bendAllowance = Math.Max(0d, routeRequest.BendAllowance);
            return fixedBusbarWidth + bendAllowance;
        }

        private static HistoricalRouteRebuildResult RebuildHistoricalRoute(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageSchemeLibraryMatchResult matchResult,
            LinkageSchemeLibraryRouteTemplateRoute routeTemplateRoute,
            CancellationToken cancellationToken,
            bool allowExpensiveRepair = true)
        {
            var result = new HistoricalRouteRebuildResult();
            var routeKey = routeTemplateRoute?.RouteKey ?? string.Empty;
            if (!TryResolveTargetGroup(
                routeRequest,
                matchResult,
                routeTemplateRoute,
                out var targetGroupRequest,
                out var resolveDiagnostic))
            {
                result.FailureMessage = resolveDiagnostic;
                result.Lines.Add("- 目标组解析失败：" + resolveDiagnostic);
                return result;
            }

            var middleMembers = (routeTemplateRoute.OrderedMembers ??
                    new List<LinkageSchemeLibraryRouteTemplateMember>())
                .Where(member => member != null && member.PositionType == MiddlePositionType)
                .ToList();
            if (middleMembers.Count == 0)
            {
                result.FailureMessage = "方案库路径没有中间排成员：" + routeKey;
                result.Lines.Add("- " + result.FailureMessage);
                return result;
            }

            var routeTransform = BuildRouteCandidateToTargetTransform(
                routeTemplateRoute,
                targetGroupRequest,
                matchResult);
            result.Lines.AddRange(DescribeMiddleMembers(
                routeTemplateRoute,
                middleMembers,
                routeTransform));
            var historicalDirectionSequence = BuildHistoricalDirectionSequence(
                routeTemplateRoute,
                middleMembers,
                routeTransform);
            var historicalPath = BuildHistoricalDirectionCenterline(
                routeTemplateRoute,
                middleMembers,
                routeTransform);
            var terminalSeed = BuildCurrentTerminalConstrainedCenterline(
                routeTemplateRoute,
                middleMembers,
                targetGroupRequest,
                routeTransform);
            result.Lines.Add("- 当前目标组：" + targetGroupRequest.GroupKey +
                "，历史方向段=" + BuildReducedPathDirections(historicalPath).Count +
                "，端点种子点=" + terminalSeed.Count);
            result.Lines.Add("- 历史中心线：" + FormatPoints(historicalPath));
            result.Lines.Add("- 当前端点种子：" + FormatPoints(terminalSeed));
            result.Lines.Add("- 历史方向序列：" + FormatDirections(historicalDirectionSequence));

            if (terminalSeed.Count < 2)
            {
                result.FailureMessage = "方案库路径无法在当前固定端之间形成中心线：" + routeKey;
                result.Lines.Add("- " + result.FailureMessage);
                return result;
            }

            if (!allowExpensiveRepair)
            {
                result.Lines.Add("- 轻量模式：已有可兜底的普通方案池，跳过端腿阶梯与偏移/局部让让修复。");
            }
            var variants = BuildHistoricalCenterlineVariants(
                routeRequest,
                targetGroupRequest,
                terminalSeed,
                historicalPath,
                historicalDirectionSequence,
                result.Lines,
                cancellationToken,
                allowExpensiveRepair);
            AddEndLegExtendedVariants(routeRequest, variants, result.Lines);
            var candidateDiagnostics = new List<string>();
            foreach (var variant in variants)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    result.FailureMessage = "方案库路径重建已取消。";
                    return result;
                }

                var candidate = LinkageRouteCandidateFactory
                    .BuildSchemeLibraryCandidateFromCenterline(
                        routeRequest,
                        targetGroupRequest,
                        variant.Centerline,
                        cancellationToken);
                var isValid = candidate?.IsValid == true &&
                    candidate.PlanarConversion?.CenterlineParts.Count > 0 &&
                    candidate.PlanarConversion.StackMembers.Count > 0;
                result.Lines.Add("- 变体【" + variant.Name + "】" +
                    (isValid
                        ? "成功：段=" + candidate.PlanarConversion.CenterlineParts.Count +
                          "，叠排件=" + candidate.PlanarConversion.StackMembers.Count +
                          "，总长=" + candidate.PlanarConversion.ActualCopperLength
                              .ToString("F1", CultureInfo.InvariantCulture)
                        : "失败：" + DescribeCandidateFailure(candidate)));
                if (!isValid)
                {
                    candidateDiagnostics.Add(variant.Name + "：" +
                        DescribeCandidateFailure(candidate));
                    continue;
                }

                var groupSolution = candidate.ToGroupSolution();
                groupSolution.TopologySignature = "LIBRARY_HISTORICAL:" + routeKey +
                    ":" + variant.Name;
                result.GroupSolution = groupSolution;
                result.PerpendicularLapCount = candidate.PerpendicularLapCount > 0
                    ? candidate.PerpendicularLapCount
                    : Math.Max(0, candidate.PlanarConversion.CenterlineParts.Count - 1);
                result.IsSuccess = true;
                return result;
            }

            var retryableVariants = variants
                .Where(variant => variant != null &&
                    (variant.Name.IndexOf("拟合", StringComparison.Ordinal) >= 0 ||
                     variant.Name.IndexOf("对齐", StringComparison.Ordinal) >= 0 ||
                     variant.Name.IndexOf("落位", StringComparison.Ordinal) >= 0 ||
                     variant.Name.IndexOf("端腿加长", StringComparison.Ordinal) >= 0 ||
                     variant.Name.IndexOf("端腿下限", StringComparison.Ordinal) >= 0 ||
                     variant.Name.IndexOf("轴骨架", StringComparison.Ordinal) >= 0))
                // 已拟合成功的变体（端腿下限/拟合/落位）优先做偏移修复，轴骨架放最后：
                // 骨架数量大，排在前面会把尝试预算吃光。
                .OrderBy(variant => variant.Name.IndexOf("轴骨架", StringComparison.Ordinal) >= 0 ? 1 : 0)
                .ToList();
            if (allowExpensiveRepair && retryableVariants.Count > 0 &&
                TryRebuildWithLateralOffsets(
                    routeRequest,
                    targetGroupRequest,
                    retryableVariants,
                    routeKey,
                    result.Lines,
                    cancellationToken,
                    out var offsetCandidate))
            {
                var offsetGroupSolution = offsetCandidate.ToGroupSolution();
                offsetGroupSolution.TopologySignature =
                    "LIBRARY_HISTORICAL_OFFSET:" + routeKey;
                result.GroupSolution = offsetGroupSolution;
                result.PerpendicularLapCount = offsetCandidate.PerpendicularLapCount > 0
                    ? offsetCandidate.PerpendicularLapCount
                    : Math.Max(0, offsetCandidate.PlanarConversion.CenterlineParts.Count - 1);
                result.IsSuccess = true;
                return result;
            }

            result.FailureMessage = "方案库历史路径无法在当前固定端生成可制造中间排：" +
                routeKey +
                (candidateDiagnostics.Count == 0
                    ? string.Empty
                    : "；" + string.Join("；", candidateDiagnostics.Take(3)));
            return result;
        }

        /// <summary>偏移修复的总实体化次数上限，避免候选多时代价失控。</summary>
        private const int HistoricalOffsetRepairAttemptLimit = 120;

        /// <summary>
        /// 偏移修复：直接重建失败时改路径再实体化，仍失败才判定该候选失败。</br>
        /// 优先沿中心线**首段/末段自身方向**整体滑动（等于把端部搭接点沿固定排
        /// 排长方向挪位，搭接仍然成立，用来绕开端部禁区与 SupportBlock）；其次沿
        /// 实际轴向平移一个"排厚 + 半排宽"量级，用来让开包围盒与相邻固定排。
        /// </summary>
        private static bool TryRebuildWithLateralOffsets(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest targetGroupRequest,
            IList<HistoricalCenterlineVariant> variants,
            string routeKey,
            IList<string> diagnosticLines,
            CancellationToken cancellationToken,
            out LinkageRouteCandidate candidate)
        {
            candidate = null;
            var slideStep = Math.Max(1d, routeRequest.DefaultBusbarThickness) * 2d;
            var axisStep = Math.Max(1d, routeRequest.DefaultBusbarThickness);
            var attemptCount = 0;
            foreach (var variant in variants)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                // 每个基准变体独立预算：避免前面变体把全局预算吃光，
                // 导致后面的变体（含曾经可直接成功的）连一次修复机会都没有。
                var variantAttemptBase = attemptCount;

                // 先按"最深违规的轴向分离方向"做局部让让：只把违规所在折线平移
                // violationDepth，平移量由两侧最近同轴向段吸收，两端锚点不动。
                var probeCandidate = LinkageRouteCandidateFactory
                    .BuildSchemeLibraryCandidateFromCenterline(
                        routeRequest,
                        targetGroupRequest,
                        variant.Centerline,
                        cancellationToken);
                if (probeCandidate?.IsValid == true &&
                    probeCandidate.PlanarConversion?.CenterlineParts.Count > 0 &&
                    probeCandidate.PlanarConversion.StackMembers.Count > 0)
                {
                    candidate = probeCandidate;
                    return true;
                }

                if (TryParseViolationSeparation(
                        probeCandidate,
                        routeRequest.DefaultBusbarThickness,
                        out var separationAxis,
                        out var violationDepth))
                {
                    var separationVariants = BuildLocalSeparationVariants(
                        variant.Centerline,
                        separationAxis,
                        violationDepth,
                        Math.Max(
                            1d,
                            routeRequest.MinimumSegmentLength));
                    var separationIndex = 0;
                    foreach (var separationVariant in separationVariants)
                    {
                        separationIndex++;
                        if (cancellationToken.IsCancellationRequested ||
                            attemptCount - variantAttemptBase >= HistoricalOffsetRepairAttemptLimit)
                        {
                            diagnosticLines.Add("- 局部让让停止：已尝试 " + attemptCount + " 次");
                            return false;
                        }

                        attemptCount++;
                        var separationCandidate = LinkageRouteCandidateFactory
                            .BuildSchemeLibraryCandidateFromCenterline(
                                routeRequest,
                                targetGroupRequest,
                                separationVariant,
                                cancellationToken);
                        var isSeparationValid = separationCandidate?.IsValid == true &&
                            separationCandidate.PlanarConversion?.CenterlineParts.Count > 0 &&
                            separationCandidate.PlanarConversion.StackMembers.Count > 0;
                        diagnosticLines.Add("- 局部让让【" + variant.Name + " #" +
                            separationIndex + "，深度=" + violationDepth.ToString(
                                "F2", CultureInfo.InvariantCulture) + "】" +
                            (isSeparationValid
                                ? "成功"
                                : "失败：" + DescribeCandidateFailure(separationCandidate)));
                        if (!isSeparationValid)
                        {
                            continue;
                        }

                        candidate = separationCandidate;
                        diagnosticLines.Add("- 局部让让命中：路径=" + routeKey +
                            "，基准=" + variant.Name + "，深度=" + violationDepth.ToString(
                                "F2", CultureInfo.InvariantCulture));
                        return true;
                    }
                }

                var sortedSegments = BuildReducedPathSegments(variant.Centerline);
                var offsetDeltas = new List<Tuple<string, Vector3d>>();
                if (sortedSegments.Count > 0)
                {
                    var firstDirection = sortedSegments[0].Direction;
                    var lastDirection = sortedSegments[sortedSegments.Count - 1].Direction;
                    offsetDeltas.Add(Tuple.Create(
                        "首段方向+",
                        new Vector3d(
                            firstDirection.X * slideStep,
                            firstDirection.Y * slideStep,
                            firstDirection.Z * slideStep)));
                    offsetDeltas.Add(Tuple.Create(
                        "首段方向-",
                        new Vector3d(
                            -firstDirection.X * slideStep,
                            -firstDirection.Y * slideStep,
                            -firstDirection.Z * slideStep)));
                    if (Math.Abs(firstDirection.Dot(lastDirection)) < 0.999999d)
                    {
                        offsetDeltas.Add(Tuple.Create(
                            "末段方向+",
                            new Vector3d(
                                lastDirection.X * slideStep,
                                lastDirection.Y * slideStep,
                                lastDirection.Z * slideStep)));
                        offsetDeltas.Add(Tuple.Create(
                            "末段方向-",
                            new Vector3d(
                                -lastDirection.X * slideStep,
                                -lastDirection.Y * slideStep,
                                -lastDirection.Z * slideStep)));
                    }
                }

                // 规范第 430 条的分级临界采样：T/4、T/2、3T/4、T（T=生成排厚）。
                // 端部附件的冲突常在数毫米量级，固定大步长会整段跑到禁区或包围盒外。
                var gradedMagnitudes = new[]
                {
                    axisStep * 0.25d,
                    axisStep * 0.5d,
                    axisStep * 0.75d,
                    axisStep
                };
                foreach (var gradedMagnitude in gradedMagnitudes)
                {
                    offsetDeltas.Add(Tuple.Create(
                        "+X(" + gradedMagnitude.ToString("F1", CultureInfo.InvariantCulture) + ")",
                        new Vector3d(gradedMagnitude, 0d, 0d)));
                    offsetDeltas.Add(Tuple.Create(
                        "-X(" + gradedMagnitude.ToString("F1", CultureInfo.InvariantCulture) + ")",
                        new Vector3d(-gradedMagnitude, 0d, 0d)));
                    offsetDeltas.Add(Tuple.Create(
                        "+Y(" + gradedMagnitude.ToString("F1", CultureInfo.InvariantCulture) + ")",
                        new Vector3d(0d, gradedMagnitude, 0d)));
                    offsetDeltas.Add(Tuple.Create(
                        "-Y(" + gradedMagnitude.ToString("F1", CultureInfo.InvariantCulture) + ")",
                        new Vector3d(0d, -gradedMagnitude, 0d)));
                    offsetDeltas.Add(Tuple.Create(
                        "+Z(" + gradedMagnitude.ToString("F1", CultureInfo.InvariantCulture) + ")",
                        new Vector3d(0d, 0d, gradedMagnitude)));
                    offsetDeltas.Add(Tuple.Create(
                        "-Z(" + gradedMagnitude.ToString("F1", CultureInfo.InvariantCulture) + ")",
                        new Vector3d(0d, 0d, -gradedMagnitude)));
                }
                foreach (var offsetDelta in offsetDeltas)
                {
                    if (cancellationToken.IsCancellationRequested ||
                        attemptCount - variantAttemptBase >= HistoricalOffsetRepairAttemptLimit)
                    {
                        diagnosticLines.Add("- 偏移修复停止：已尝试 " + attemptCount + " 次");
                        return false;
                    }

                    attemptCount++;
                    var shiftedCenterline = variant.Centerline
                        .Select(point => new Point3d(
                            point.X + offsetDelta.Item2.X,
                            point.Y + offsetDelta.Item2.Y,
                            point.Z + offsetDelta.Item2.Z))
                        .ToList();
                    var shiftedCandidate = LinkageRouteCandidateFactory
                        .BuildSchemeLibraryCandidateFromCenterline(
                            routeRequest,
                            targetGroupRequest,
                            shiftedCenterline,
                            cancellationToken);
                    var isValid = shiftedCandidate?.IsValid == true &&
                        shiftedCandidate.PlanarConversion?.CenterlineParts.Count > 0 &&
                        shiftedCandidate.PlanarConversion.StackMembers.Count > 0;
                    diagnosticLines.Add("- 偏移修复【" + variant.Name + " / " +
                        offsetDelta.Item1 + "】" +
                        (isValid ? "成功" : "失败：" + DescribeCandidateFailure(shiftedCandidate)));
                    if (!isValid)
                    {
                        continue;
                    }

                    candidate = shiftedCandidate;
                    diagnosticLines.Add("- 偏移修复命中：路径=" + routeKey +
                        "，基准=" + variant.Name + "，偏移=" + offsetDelta.Item1);
                    return true;
                }
            }

            return false;
        }

        private static string DescribeCandidateFailure(LinkageRouteCandidate candidate)
        {
            if (candidate == null)
            {
                return "候选为空。";
            }

            var diagnostics = new List<string>();
            if (!string.IsNullOrWhiteSpace(candidate.Diagnostic))
            {
                diagnostics.Add(candidate.Diagnostic);
            }
            if (candidate.PlanarConversion?.Diagnostics != null)
            {
                diagnostics.AddRange(candidate.PlanarConversion.Diagnostics.Where(item =>
                    !string.IsNullOrWhiteSpace(item)));
            }
            return diagnostics.Count == 0
                ? "无诊断信息（校验未通过）。"
                : string.Join("；", diagnostics.Distinct().Take(2));
        }

        /// <summary>
        /// 生成"历史中心线 → 当前可制造中心线"的有序变体，从最接近历史（原样）
        /// 开始，逐步施加回折消去、方向归一和段长重解。
        /// </summary>
        private static List<HistoricalCenterlineVariant> BuildHistoricalCenterlineVariants(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest targetGroupRequest,
            IList<Point3d> terminalSeed,
            IList<Point3d> historicalPath,
            IList<Vector3d> historicalDirectionSequence,
            IList<string> diagnosticLines,
            CancellationToken cancellationToken,
            bool allowExpensiveRepair = true)
        {
            var variants = new List<HistoricalCenterlineVariant>();
            var seed = RemoveCoincidentPoints(terminalSeed);

            variants.Add(new HistoricalCenterlineVariant
            {
                Name = "历史原样",
                Centerline = seed.Select(CopyPoint).ToList()
            });

            var deReversed = RemovePathReversals(seed);
            if (!IsSameCenterline(deReversed, seed))
            {
                variants.Add(new HistoricalCenterlineVariant
                {
                    Name = "消去回折",
                    Centerline = deReversed
                });
            }

            // 历史各中间排成员之间会留下一个搭接厚度的连接段（本例 10 mm），
            // 远小于最小段长；把这类短段折点合并后历史形状才可能被实体化。
            var minimumSeedSegmentLength = Math.Max(
                1d,
                routeRequest?.MinimumSegmentLength ?? HistoricalCenterlineFallbackSegmentLength);
            var mergedSeed = RemoveShortSegments(seed, minimumSeedSegmentLength);
            if (!IsSameCenterline(mergedSeed, seed))
            {
                variants.Add(new HistoricalCenterlineVariant
                {
                    Name = "压缩短段",
                    Centerline = mergedSeed
                });
                var mergedDeReversed = RemovePathReversals(mergedSeed);
                if (!IsSameCenterline(mergedDeReversed, mergedSeed))
                {
                    variants.Add(new HistoricalCenterlineVariant
                    {
                        Name = "压缩短段+消去回折",
                        Centerline = mergedDeReversed
                    });
                }
            }

            var startPoint = seed.FirstOrDefault(point => point != null);
            var endPoint = seed.LastOrDefault(point => point != null);
            var seedSegments = BuildReducedPathSegments(seed);
            var historicalSegments = BuildReducedPathSegments(historicalPath);
            var baseLengths = ResolveBaseLengths(seedSegments, historicalSegments);
            diagnosticLines.Add("- 端点：" + FormatPoint(startPoint) + " → " +
                FormatPoint(endPoint) + "，当前段长=" +
                string.Join("/", seedSegments.Select(segment => segment.Length.ToString(
                    "F1", CultureInfo.InvariantCulture))) +
                "，历史段长=" + string.Join("/", historicalSegments.Select(segment =>
                    segment.Length.ToString("F1", CultureInfo.InvariantCulture))));

            AddDirectionalFitVariants(
                variants,
                routeRequest,
                startPoint,
                endPoint,
                historicalDirectionSequence,
                baseLengths,
                diagnosticLines);
            // 工艺口径：先把柜顶端按历史与柜顶的搭接位置对齐，沿历史路径往始端走，
            // 最后用实际始端位置做调整。段长符号由实际坐标求解，历史方向只定轴向顺序。
            AddCabinetAnchoredFitVariants(
                variants,
                routeRequest,
                startPoint,
                endPoint,
                historicalDirectionSequence,
                baseLengths,
                diagnosticLines);
            AddTrimmedEndFitVariants(
                variants,
                routeRequest,
                startPoint,
                endPoint,
                historicalDirectionSequence,
                baseLengths,
                diagnosticLines);
            AddEndLegMinimumFitVariants(
                variants,
                routeRequest,
                startPoint,
                endPoint,
                historicalDirectionSequence,
                baseLengths,
                diagnosticLines);
            // 端腿长度阶梯：端腿加长会把中间段沿该端轴向整体挪开，
            // 用来避开端部 SupportBlock 附件（按 0.5T 步进）。
            var ladderStep = Math.Max(1d, routeRequest.DefaultBusbarThickness) * 0.5d;
            for (var ladderIndex = 1; ladderIndex <= (allowExpensiveRepair ? 20 : 4); ladderIndex++)
            {
                AddEndLegMinimumFitVariants(
                    variants,
                    routeRequest,
                    startPoint,
                    endPoint,
                    historicalDirectionSequence,
                    baseLengths,
                    diagnosticLines,
                    ladderStep * ladderIndex);
            }
            if (allowExpensiveRepair)
            {
                AddAxisSkeletonVariants(
                    variants,
                    routeRequest,
                    targetGroupRequest,
                    historicalDirectionSequence,
                    diagnosticLines,
                    cancellationToken);
            }
            return variants;
        }

        /// <summary>
        /// 端部落位变体：把小于最小段长的位移分量交给端部搭接在固定端**允许区间**
        /// 内自行落位（规范第 277、290 条：整张有效厚面矩形均可作为搭接位置，
        /// 不得固定为中心搭接），中间排只承担剩下的分量。落位是否越界、完整排宽
        /// 是否投影在固定端有效排宽内，仍由工厂按规范校验，这里不放宽任何校验。
        /// </summary>
        private static void AddTrimmedEndFitVariants(
            IList<HistoricalCenterlineVariant> variants,
            LinkageMultiGroupRouteRequest routeRequest,
            Point3d startPoint,
            Point3d endPoint,
            IList<Vector3d> historicalDirectionSequence,
            IList<double> baseLengths,
            IList<string> diagnosticLines)
        {
            if (startPoint == null || endPoint == null ||
                historicalDirectionSequence == null ||
                historicalDirectionSequence.Count == 0)
            {
                return;
            }

            var minimumSegmentLength = Math.Max(
                1d,
                routeRequest?.MinimumSegmentLength ?? HistoricalCenterlineFallbackSegmentLength);
            var trimmedEndPoint = TrimSmallDisplacementComponents(
                startPoint,
                endPoint,
                minimumSegmentLength);
            if (trimmedEndPoint.DistanceTo(endPoint) <= 1e-6d)
            {
                return;
            }

            var trimmedDisplacement = new Vector3d(startPoint, trimmedEndPoint);
            diagnosticLines.Add("- 端部落位：剔除小于最小段长的分量后位移=" +
                FormatVector(trimmedDisplacement) + "（原位移=" +
                FormatVector(new Vector3d(startPoint, endPoint)) + "，差=" +
                trimmedEndPoint.DistanceTo(endPoint).ToString(
                    "F1", CultureInfo.InvariantCulture) + " mm 交由端部搭接落位）");
            var reversedDirections = historicalDirectionSequence.Reverse().ToList();
            var fittingModes = new List<Tuple<string, Point3d, Point3d, IList<Vector3d>>>
            {
                new Tuple<string, Point3d, Point3d, IList<Vector3d>>(
                    "端部落位",
                    startPoint,
                    trimmedEndPoint,
                    historicalDirectionSequence),
                new Tuple<string, Point3d, Point3d, IList<Vector3d>>(
                    "端部落位-轴向归一",
                    startPoint,
                    trimmedEndPoint,
                    SnapDirectionsToAxis(historicalDirectionSequence)),
                new Tuple<string, Point3d, Point3d, IList<Vector3d>>(
                    "端部落位-柜顶端对齐",
                    trimmedEndPoint,
                    startPoint,
                    reversedDirections)
            };
            foreach (var fittingMode in fittingModes)
            {
                var fitStartPoint = fittingMode.Item2;
                var fitEndPoint = fittingMode.Item3;
                var fitDisplacement = new Vector3d(fitStartPoint, fitEndPoint);
                var reducedDirections = ReduceDirections(
                    OrientDirectionsToDisplacement(fittingMode.Item4, fitDisplacement));
                if (reducedDirections.Count == 0)
                {
                    continue;
                }

                if (!TryFitDirectionalLengths(
                        reducedDirections,
                        ResolveFittedBaseLengths(
                            reducedDirections.Count,
                            baseLengths,
                            minimumSegmentLength),
                        fitDisplacement,
                        minimumSegmentLength,
                        out var fittedLengths))
                {
                    diagnosticLines.Add("- 端部落位【" + fittingMode.Item1 +
                        "】失败：方向集=" + FormatDirections(reducedDirections));
                    continue;
                }

                var fittedCenterline = CreateCenterlineFromSegments(
                    fitStartPoint,
                    reducedDirections,
                    fittedLengths);
                if (!IsCenterlineClosed(
                        fittedCenterline,
                        fitEndPoint,
                        minimumSegmentLength))
                {
                    diagnosticLines.Add("- 端部落位【" + fittingMode.Item1 +
                        "】失败：端点或最小段长不满足");
                    continue;
                }

                var orderedCenterline = fittingMode.Item1.EndsWith(
                    "柜顶端对齐",
                    StringComparison.Ordinal)
                    ? fittedCenterline.AsEnumerable().Reverse().Select(CopyPoint).ToList()
                    : fittedCenterline;
                diagnosticLines.Add("- 端部落位【" + fittingMode.Item1 + "】方向=" +
                    FormatDirections(reducedDirections) + "，段长=" +
                    string.Join("/", fittedLengths.Select(length => length.ToString(
                        "F1", CultureInfo.InvariantCulture))));
                variants.Add(new HistoricalCenterlineVariant
                {
                    Name = fittingMode.Item1,
                    Centerline = orderedCenterline
                });
            }
        }

        /// <summary>
        /// 把位移中绝对值小于最小段长的分量置 0：该分量改由端部搭接在固定端允许
        /// 区间内落位承担（规范第 277、290 条），中间排不再为它强制多出短段。
        /// </summary>
        private static Point3d TrimSmallDisplacementComponents(
            Point3d startPoint,
            Point3d endPoint,
            double minimumSegmentLength)
        {
            if (startPoint == null || endPoint == null)
            {
                return endPoint;
            }

            var displacement = new Vector3d(startPoint, endPoint);
            return new Point3d(
                Math.Abs(displacement.X) < minimumSegmentLength
                    ? startPoint.X
                    : endPoint.X,
                Math.Abs(displacement.Y) < minimumSegmentLength
                    ? startPoint.Y
                    : endPoint.Y,
                Math.Abs(displacement.Z) < minimumSegmentLength
                    ? startPoint.Z
                    : endPoint.Z);
        }

        /// <summary>
        /// 柜顶端对齐变体：以柜顶搭接点为起点、历史轴向顺序取反，沿路径走向始端，
        /// 让"实际始端位置"这一段承担调整量。位移与符号全部由当前实际坐标求解，
        /// 历史方向只提供轴向顺序；最后把中心线翻回 始端→柜顶 顺序交给工厂。
        /// </summary>
        private static void AddCabinetAnchoredFitVariants(
            IList<HistoricalCenterlineVariant> variants,
            LinkageMultiGroupRouteRequest routeRequest,
            Point3d startPoint,
            Point3d endPoint,
            IList<Vector3d> historicalDirectionSequence,
            IList<double> baseLengths,
            IList<string> diagnosticLines)
        {
            if (startPoint == null || endPoint == null ||
                historicalDirectionSequence == null ||
                historicalDirectionSequence.Count == 0)
            {
                return;
            }

            var reversedDirections = historicalDirectionSequence.Reverse().ToList();
            var targetDisplacement = new Vector3d(endPoint, startPoint);
            var minimumSegmentLength = Math.Max(
                1d,
                routeRequest?.MinimumSegmentLength ?? HistoricalCenterlineFallbackSegmentLength);
            foreach (var directionSet in new List<Tuple<string, IList<Vector3d>>>
            {
                new Tuple<string, IList<Vector3d>>(
                    "柜顶端对齐-历史方向",
                    reversedDirections),
                new Tuple<string, IList<Vector3d>>(
                    "柜顶端对齐-轴向归一",
                    SnapDirectionsToAxis(reversedDirections)),
                new Tuple<string, IList<Vector3d>>(
                    "柜顶端对齐-按位移定向",
                    OrientDirectionsToDisplacement(
                        reversedDirections,
                        targetDisplacement)),
                new Tuple<string, IList<Vector3d>>(
                    "柜顶端对齐-按位移定向-轴向归一",
                    SnapDirectionsToAxis(OrientDirectionsToDisplacement(
                        reversedDirections,
                        targetDisplacement)))
            })
            {
                var reducedDirections = ReduceDirections(directionSet.Item2);
                if (reducedDirections.Count == 0)
                {
                    continue;
                }

                if (!TryFitDirectionalLengths(
                        reducedDirections,
                        ResolveFittedBaseLengths(
                            reducedDirections.Count,
                            baseLengths,
                            minimumSegmentLength),
                        targetDisplacement,
                        minimumSegmentLength,
                        out var fittedLengths))
                {
                    var closureDirections = AppendClosureDirections(
                        reducedDirections,
                        endPoint,
                        startPoint);
                    if (closureDirections.Count == reducedDirections.Count ||
                        !TryFitDirectionalLengths(
                            closureDirections,
                            ResolveFittedBaseLengths(
                                closureDirections.Count,
                                baseLengths,
                                minimumSegmentLength),
                            targetDisplacement,
                            minimumSegmentLength,
                            out fittedLengths))
                    {
                        diagnosticLines.Add("- 柜顶端对齐【" + directionSet.Item1 +
                            "】失败：方向集=" + FormatDirections(reducedDirections) +
                            "，位移=" + FormatVector(targetDisplacement));
                        continue;
                    }

                    reducedDirections = closureDirections;
                }

                var cabinetAnchoredCenterline = CreateCenterlineFromSegments(
                    endPoint,
                    reducedDirections,
                    fittedLengths);
                if (!IsCenterlineClosed(
                        cabinetAnchoredCenterline,
                        startPoint,
                        minimumSegmentLength))
                {
                    diagnosticLines.Add("- 柜顶端对齐【" + directionSet.Item1 +
                        "】失败：端点或最小段长不满足，方向集=" +
                        FormatDirections(reducedDirections));
                    continue;
                }

                // 翻回 始端→柜顶 顺序：工厂的输入约定是始端在前。
                var orderedCenterline = cabinetAnchoredCenterline.AsEnumerable()
                    .Reverse()
                    .Select(CopyPoint)
                    .ToList();
                diagnosticLines.Add("- 柜顶端对齐【" + directionSet.Item1 + "】方向=" +
                    FormatDirections(reducedDirections) + "，段长=" +
                    string.Join("/", fittedLengths.Select(length => length.ToString(
                        "F1", CultureInfo.InvariantCulture))));
                variants.Add(new HistoricalCenterlineVariant
                {
                    Name = directionSet.Item1,
                    Centerline = orderedCenterline
                });
            }
        }

        /// <summary>每组按历史轴向相似度排序后最多尝试的轴骨架数量。</summary>
        private const int HistoricalAxisSkeletonVariantLimit = 24;

        /// <summary>
        /// 用当前固定端的合法轴骨架补足候选：包围盒、端部禁区、SupportBlock 与
        /// 搭接落位这些由当前场景决定的限制只有轴骨架枚举能保证，历史方向只负责
        /// 排名。这样"按历史形状生成"不再依赖普通算法是否恰好搜到相似方案。
        /// </summary>
        private static void AddAxisSkeletonVariants(
            IList<HistoricalCenterlineVariant> variants,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest targetGroupRequest,
            IList<Vector3d> historicalDirectionSequence,
            IList<string> diagnosticLines,
            CancellationToken cancellationToken)
        {
            if (targetGroupRequest == null || historicalDirectionSequence == null ||
                historicalDirectionSequence.Count == 0 || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var rankedSkeletons = new List<Tuple<double, IList<Point3d>>>();
            try
            {
                foreach (var axisSkeleton in LinkageRouteAxisPathBuilder.BuildCandidates(
                    routeRequest,
                    targetGroupRequest,
                    LinkageRouteTraversalDirection.Forward,
                    cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (axisSkeleton == null || axisSkeleton.Count < 2)
                    {
                        continue;
                    }

                    rankedSkeletons.Add(Tuple.Create(
                        ScoreHistoricalPathPreference(
                            historicalDirectionSequence,
                            axisSkeleton),
                        axisSkeleton));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            diagnosticLines.Add("- 轴骨架枚举=" + rankedSkeletons.Count + " 条，按历史方向相似度取前 " +
                Math.Min(HistoricalAxisSkeletonVariantLimit, rankedSkeletons.Count) + " 条");
            var addedSkeletonCount = 0;
            foreach (var rankedSkeleton in rankedSkeletons
                .OrderBy(item => item.Item1)
                .Take(HistoricalAxisSkeletonVariantLimit))
            {
                variants.Add(new HistoricalCenterlineVariant
                {
                    Name = "轴骨架#" + (addedSkeletonCount + 1) + "（差异=" +
                        rankedSkeleton.Item1.ToString("F1", CultureInfo.InvariantCulture) + "）",
                    Centerline = RemoveCoincidentPoints(
                        rankedSkeleton.Item2.Select(CopyPoint).ToList())
                });
                addedSkeletonCount++;
            }
        }

        private static void AddDirectionalFitVariants(
            IList<HistoricalCenterlineVariant> variants,
            LinkageMultiGroupRouteRequest routeRequest,
            Point3d startPoint,
            Point3d endPoint,
            IList<Vector3d> historicalDirectionSequence,
            IList<double> baseLengths,
            IList<string> diagnosticLines)
        {
            if (startPoint == null || endPoint == null ||
                historicalDirectionSequence == null ||
                historicalDirectionSequence.Count == 0)
            {
                return;
            }

            var targetDisplacement = new Vector3d(startPoint, endPoint);
            var minimumSegmentLength = Math.Max(
                1d,
                routeRequest?.MinimumSegmentLength ?? HistoricalCenterlineFallbackSegmentLength);
            // 工艺口径：历史只提供"轴向顺序"，每一轴的正负号必须按当前实际位移重新
            // 定向（"根据实际整体匹配坐标及位置去确定方向"），否则历史方向的符号会
            // 让段长求解直接不成立（例：当前要 +Y，历史只给 −Y）。
            var directionSets = new List<Tuple<string, IList<Vector3d>>>
            {
                new Tuple<string, IList<Vector3d>>(
                    "历史方向",
                    historicalDirectionSequence),
                new Tuple<string, IList<Vector3d>>(
                    "轴向归一",
                    SnapDirectionsToAxis(historicalDirectionSequence)),
                new Tuple<string, IList<Vector3d>>(
                    "按位移定向",
                    OrientDirectionsToDisplacement(
                        historicalDirectionSequence,
                        targetDisplacement)),
                new Tuple<string, IList<Vector3d>>(
                    "按位移定向-轴向归一",
                    SnapDirectionsToAxis(OrientDirectionsToDisplacement(
                        historicalDirectionSequence,
                        targetDisplacement)))
            };
            foreach (var directionSet in directionSets)
            {
                var reducedDirections = ReduceDirections(directionSet.Item2);
                if (reducedDirections.Count == 0)
                {
                    continue;
                }

                var fittedBaseLengths = ResolveFittedBaseLengths(
                    reducedDirections.Count,
                    baseLengths,
                    minimumSegmentLength);
                if (TryFitDirectionalLengths(
                        reducedDirections,
                        fittedBaseLengths,
                        targetDisplacement,
                        minimumSegmentLength,
                        out var fittedLengths))
                {
                    var fittedCenterline = CreateCenterlineFromSegments(
                        startPoint,
                        reducedDirections,
                        fittedLengths);
                    if (IsCenterlineClosed(fittedCenterline, endPoint, minimumSegmentLength))
                    {
                        diagnosticLines.Add("- 方向拟合【" + directionSet.Item1 + "】方向=" +
                            FormatDirections(reducedDirections) + "，段长=" +
                            string.Join("/", fittedLengths.Select(length => length.ToString(
                                "F1", CultureInfo.InvariantCulture))));
                        variants.Add(new HistoricalCenterlineVariant
                        {
                            Name = "方向拟合-" + directionSet.Item1,
                            Centerline = fittedCenterline
                        });
                        continue;
                    }
                }

                // 方向集无法跨越当前位移时，插入轴向补偿段后再闭合。
                var closureDirections = AppendClosureDirections(
                    reducedDirections,
                    startPoint,
                    endPoint);
                if (closureDirections.Count != reducedDirections.Count &&
                    TryFitDirectionalLengths(
                        closureDirections,
                        ResolveFittedBaseLengths(
                            closureDirections.Count,
                            baseLengths,
                            minimumSegmentLength),
                        targetDisplacement,
                        minimumSegmentLength,
                        out var closureLengths))
                {
                    var closureCenterline = CreateCenterlineFromSegments(
                        startPoint,
                        closureDirections,
                        closureLengths);
                    if (IsCenterlineClosed(closureCenterline, endPoint, minimumSegmentLength))
                    {
                        diagnosticLines.Add("- 补偿闭合【" + directionSet.Item1 + "】方向=" +
                            FormatDirections(closureDirections) + "，段长=" +
                            string.Join("/", closureLengths.Select(length => length.ToString(
                                "F1", CultureInfo.InvariantCulture))));
                        variants.Add(new HistoricalCenterlineVariant
                        {
                            Name = "补偿闭合-" + directionSet.Item1,
                            Centerline = closureCenterline
                        });
                        continue;
                    }
                }

                diagnosticLines.Add("- 方向拟合【" + directionSet.Item1 +
                    "】失败：方向集=" + FormatDirections(reducedDirections) +
                    "，位移=" + FormatVector(targetDisplacement));
            }
        }

        /// <summary>
        /// 删除"出去又原路返回"的折点：相邻两段方向相反时，折点只是历史记录的
        /// 往返痕迹，删掉折点得到净位移段。
        /// </summary>
        private static List<Point3d> RemovePathReversals(IList<Point3d> points)
        {
            var result = RemoveCoincidentPoints(points);
            var changed = true;
            while (changed && result.Count >= 3)
            {
                changed = false;
                for (var pointIndex = 1; pointIndex + 1 < result.Count; pointIndex++)
                {
                    var firstVector = new Vector3d(
                        result[pointIndex - 1],
                        result[pointIndex]);
                    var secondVector = new Vector3d(
                        result[pointIndex],
                        result[pointIndex + 1]);
                    if (firstVector.Norm <= 1e-8d || secondVector.Norm <= 1e-8d)
                    {
                        continue;
                    }

                    if (firstVector.Normalized.Dot(secondVector.Normalized) >=
                        -1e-6d)
                    {
                        continue;
                    }

                    result.RemoveAt(pointIndex);
                    changed = true;
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// 折叠过短的线段：反复移除会形成最短线段的中间折点，直到所有线段都不小于
        /// 最小段长或只剩两端点。历史成员之间的搭接连接段就靠这一步消去。
        /// </summary>
        private static List<Point3d> RemoveShortSegments(
            IList<Point3d> points,
            double minimumSegmentLength)
        {
            var result = RemoveCoincidentPoints(points);
            while (result.Count > 2)
            {
                var shortestIndex = -1;
                var shortestLength = double.MaxValue;
                for (var pointIndex = 1; pointIndex < result.Count; pointIndex++)
                {
                    var segmentLength = result[pointIndex - 1].DistanceTo(result[pointIndex]);
                    if (segmentLength < shortestLength)
                    {
                        shortestLength = segmentLength;
                        shortestIndex = pointIndex;
                    }
                }

                if (shortestIndex < 0 || shortestLength + 1e-6d >= minimumSegmentLength)
                {
                    break;
                }

                // 删掉该短段靠中间的那个折点：首尾锚点必须保留。
                var removableIndex = shortestIndex < result.Count - 1
                    ? shortestIndex
                    : shortestIndex - 1;
                if (removableIndex <= 0 || removableIndex >= result.Count - 1)
                {
                    break;
                }

                result.RemoveAt(removableIndex);
            }
            return result;
        }

        private static IList<double> ResolveBaseLengths(
            IList<DirectionalPathSegment> seedSegments,
            IList<DirectionalPathSegment> historicalSegments)
        {
            var lengths = new List<double>();
            if (historicalSegments != null && historicalSegments.Count > 0)
            {
                lengths.AddRange(historicalSegments.Select(segment => segment.Length));
            }

            if (lengths.Count == 0 && seedSegments != null)
            {
                lengths.AddRange(seedSegments.Select(segment => segment.Length));
            }

            if (lengths.Count == 0)
            {
                lengths.Add(HistoricalCenterlineFallbackSegmentLength);
            }

            return lengths;
        }

        private static IList<double> ResolveFittedBaseLengths(
            int directionCount,
            IList<double> baseLengths,
            double minimumSegmentLength)
        {
            var lengths = new List<double>();
            for (var index = 0; index < directionCount; index++)
            {
                var baseLength = baseLengths != null && baseLengths.Count > 0
                    ? baseLengths[index % baseLengths.Count]
                    : minimumSegmentLength;
                lengths.Add(Math.Max(minimumSegmentLength, baseLength));
            }
            return lengths;
        }

        private static IList<Vector3d> ReduceDirections(IList<Vector3d> directions)
        {
            var reduced = new List<Vector3d>();
            foreach (var direction in directions ?? new List<Vector3d>())
            {
                if (direction == null || direction.Norm <= 1e-8d)
                {
                    continue;
                }

                var normalized = direction.Normalized;
                if (reduced.Count > 0 &&
                    reduced[reduced.Count - 1].Dot(normalized) >= 0.999999d)
                {
                    continue;
                }

                // 相邻反向对是历史记录的往返痕迹，直接消去。
                if (reduced.Count > 0 &&
                    reduced[reduced.Count - 1].Dot(normalized) <= -0.999999d)
                {
                    reduced.RemoveAt(reduced.Count - 1);
                    continue;
                }

                reduced.Add(normalized);
            }
            return reduced;
        }

        /// <summary>
        /// 按当前实际位移给历史轴向重新定符号：只保留"轴向"，方向正负由实际坐标决定。
        /// </summary>
        private static IList<Vector3d> OrientDirectionsToDisplacement(
            IList<Vector3d> directions,
            Vector3d displacement)
        {
            var oriented = new List<Vector3d>();
            foreach (var direction in directions ?? new List<Vector3d>())
            {
                if (direction == null || direction.Norm <= 1e-8d)
                {
                    continue;
                }

                var normalized = direction.Normalized;
                if (displacement == null || displacement.Norm <= 1e-8d)
                {
                    oriented.Add(normalized);
                    continue;
                }

                var projection = normalized.Dot(displacement);
                if (Math.Abs(projection) <= 1e-8d)
                {
                    oriented.Add(normalized);
                    continue;
                }

                oriented.Add(projection < 0d ? Negate(normalized) : normalized);
            }
            return oriented;
        }

        private static IList<Vector3d> SnapDirectionsToAxis(IList<Vector3d> directions)
        {
            var snapped = new List<Vector3d>();
            foreach (var direction in directions ?? new List<Vector3d>())
            {
                if (direction == null || direction.Norm <= 1e-8d)
                {
                    continue;
                }

                var normalized = direction.Normalized;
                var axisIndex = 0;
                var axisSign = 1d;
                var axisMagnitude = Math.Abs(normalized.X);
                if (Math.Abs(normalized.Y) > axisMagnitude)
                {
                    axisIndex = 1;
                    axisMagnitude = Math.Abs(normalized.Y);
                }
                if (Math.Abs(normalized.Z) > axisMagnitude)
                {
                    axisIndex = 2;
                    axisMagnitude = Math.Abs(normalized.Z);
                }
                if (axisIndex == 0)
                {
                    axisSign = normalized.X < 0d ? -1d : 1d;
                }
                else if (axisIndex == 1)
                {
                    axisSign = normalized.Y < 0d ? -1d : 1d;
                }
                else
                {
                    axisSign = normalized.Z < 0d ? -1d : 1d;
                }

                snapped.Add(new Vector3d(
                    axisIndex == 0 ? axisSign : 0d,
                    axisIndex == 1 ? axisSign : 0d,
                    axisIndex == 2 ? axisSign : 0d));
            }
            return snapped;
        }

        /// <summary>
        /// 方向集无法跨越当前位移时，在最后一个方向前插入轴向补偿段，使残余位移
        /// 可被吸收。最多插入三段（三个轴向），插入位置尽量靠后以保留历史形状。
        /// </summary>
        private static IList<Vector3d> AppendClosureDirections(
            IList<Vector3d> directions,
            Point3d startPoint,
            Point3d endPoint)
        {
            var closureDirections = new List<Vector3d>(directions ?? new List<Vector3d>());
            if (startPoint == null || endPoint == null)
            {
                return closureDirections;
            }

            if (closureDirections.Count == 0)
            {
                closureDirections.Add(new Vector3d(0d, 0d, 1d));
            }

            var displacement = new Vector3d(startPoint, endPoint);
            var axes = new List<Tuple<Vector3d, double>>
            {
                Tuple.Create(new Vector3d(0d, 0d, 1d), Math.Abs(displacement.Z)),
                Tuple.Create(new Vector3d(1d, 0d, 0d), Math.Abs(displacement.X)),
                Tuple.Create(new Vector3d(0d, 1d, 0d), Math.Abs(displacement.Y))
            };
            axes.Sort((first, second) => second.Item2.CompareTo(first.Item2));
            var insertionIndex = Math.Max(0, closureDirections.Count - 1);
            foreach (var axis in axes)
            {
                if (axis.Item2 <= 1e-6d)
                {
                    continue;
                }

                if (closureDirections.Any(direction =>
                    Math.Abs(direction.Dot(axis.Item1)) > 0.99d))
                {
                    continue;
                }

                var sign = axis.Item1.Dot(displacement) < 0d ? -1d : 1d;
                closureDirections.Insert(insertionIndex, new Vector3d(
                    axis.Item1.X * sign,
                    axis.Item1.Y * sign,
                    axis.Item1.Z * sign));
            }

            return closureDirections;
        }

        private static IList<Point3d> CreateCenterlineFromSegments(
            Point3d startPoint,
            IList<Vector3d> directions,
            IList<double> lengths)
        {
            var points = new List<Point3d> { CopyPoint(startPoint) };
            var currentPoint = CopyPoint(startPoint);
            for (var directionIndex = 0; directionIndex < directions.Count; directionIndex++)
            {
                var direction = directions[directionIndex];
                var length = directionIndex < lengths.Count
                    ? lengths[directionIndex]
                    : 0d;
                currentPoint = new Point3d(
                    currentPoint.X + direction.X * length,
                    currentPoint.Y + direction.Y * length,
                    currentPoint.Z + direction.Z * length);
                points.Add(currentPoint);
            }
            return points;
        }

        private static bool IsCenterlineClosed(
            IList<Point3d> centerline,
            Point3d endPoint,
            double minimumSegmentLength)
        {
            if (centerline == null || centerline.Count < 2 || endPoint == null)
            {
                return false;
            }

            if (centerline[centerline.Count - 1].DistanceTo(endPoint) >
                HistoricalCenterlineClosureTolerance)
            {
                return false;
            }

            for (var pointIndex = 1; pointIndex < centerline.Count; pointIndex++)
            {
                if (centerline[pointIndex - 1].DistanceTo(centerline[pointIndex]) + 1e-6d <
                    Math.Max(0d, minimumSegmentLength))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsSameCenterline(IList<Point3d> first, IList<Point3d> second)
        {
            if (first == null || second == null || first.Count != second.Count)
            {
                return false;
            }

            for (var pointIndex = 0; pointIndex < first.Count; pointIndex++)
            {
                if (first[pointIndex] == null || second[pointIndex] == null ||
                    first[pointIndex].DistanceTo(second[pointIndex]) > 1e-6d)
                {
                    return false;
                }
            }

            return true;
        }

        private static string FormatPoint(Point3d point)
        {
            return point == null
                ? "(空)"
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "({0:0.0},{1:0.0},{2:0.0})",
                    point.X,
                    point.Y,
                    point.Z);
        }

        private static string FormatVector(Vector3d vector)
        {
            return vector == null
                ? "(空)"
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "({0:0.0},{1:0.0},{2:0.0})",
                    vector.X,
                    vector.Y,
                    vector.Z);
        }

        private static string FormatPoints(IList<Point3d> points)
        {
            return points == null || points.Count == 0
                ? "(空)"
                : string.Join("→", points.Select(FormatPoint));
        }

        private static string FormatDirections(IList<Vector3d> directions)
        {
            return directions == null || directions.Count == 0
                ? "(空)"
                : string.Join(" ", directions.Select(FormatVector));
        }
    }
}
