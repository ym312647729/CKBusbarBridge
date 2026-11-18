using GeometRi;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RouteLinkBar.Algorithm.Contracts;

namespace RouteLinkBar.Algorithm.Routing
{
    /// <summary>
    /// 按物理始端到柜顶方向，把最终中心线归一为与坐标、长度和斜率无关的方向拓扑。
    /// </summary>
    public static class LinkageRouteTopologySignature
    {
        private const double AbsoluteDirectionTolerance = 1e-8d;
        private const double RelativeDirectionTolerance = 1e-9d;
        private const double RepresentativeLengthTolerance = 1e-8d;

        public static string CreateCenterlineSignature(
            IEnumerable<Point3d> physicalStartToCabinetCenterline)
        {
            var centerlinePointList = (physicalStartToCabinetCenterline ??
                    Enumerable.Empty<Point3d>())
                .Where(centerlinePoint => centerlinePoint != null)
                .ToList();
            var orderedDirectionCategories = new List<string>();
            for (var centerlinePointIndex = 1;
                centerlinePointIndex < centerlinePointList.Count;
                centerlinePointIndex++)
            {
                var segmentDirectionCategory = CreateSegmentDirectionCategory(
                    centerlinePointList[centerlinePointIndex - 1],
                    centerlinePointList[centerlinePointIndex]);
                if (string.IsNullOrEmpty(segmentDirectionCategory) ||
                    string.Equals(
                        orderedDirectionCategories.LastOrDefault(),
                        segmentDirectionCategory,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                orderedDirectionCategories.Add(segmentDirectionCategory);
            }

            return string.Join(">", orderedDirectionCategories);
        }

        public static string CreateSolutionSignature(
            LinkageRouteSolution routeSolution)
        {
            if (routeSolution == null)
            {
                return string.Empty;
            }

            return CreateSolutionSignature(routeSolution.GroupSolutions
                .Where(groupRouteSolution => groupRouteSolution != null)
                .Select(groupRouteSolution => new KeyValuePair<string,
                    IEnumerable<Point3d>>(
                        groupRouteSolution.GroupKey,
                        ResolveFinalCenterline(groupRouteSolution))));
        }

        public static string CreateStableGeometrySignature(
            LinkageRouteSolution routeSolution)
        {
            if (routeSolution == null)
            {
                return string.Empty;
            }

            var groupCenterlines = routeSolution.GroupSolutions
                .Where(groupRouteSolution => groupRouteSolution != null)
                .Select(groupRouteSolution => new KeyValuePair<string,
                    IEnumerable<Point3d>>(
                        groupRouteSolution.GroupKey,
                        ResolveFinalCenterline(groupRouteSolution)));
            var groupGeometrySignature = CreateGroupGeometrySignature(
                groupCenterlines);
            var diagnosticGeometrySignature = string.Join("||", routeSolution.Diagnostics
                .Where(routeDiagnostic => routeDiagnostic != null)
                .OrderBy(
                    routeDiagnostic => routeDiagnostic.GroupKey ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(routeDiagnostic => routeDiagnostic.Stage)
                .Select(routeDiagnostic =>
                    EncodeText(routeDiagnostic.GroupKey) + ":" +
                    ((int)routeDiagnostic.Stage).ToString(
                        CultureInfo.InvariantCulture) + "=" +
                    CreatePointSequenceSignature(routeDiagnostic.PreviewCenterline)));

            return ((int)routeSolution.AlgorithmKind).ToString(
                    CultureInfo.InvariantCulture) +
                "|" + routeSolution.IsSuccess +
                "|G:" + groupGeometrySignature +
                "|D:" + diagnosticGeometrySignature;
        }

        public static bool IsPreferredRepresentative(
            LinkageRouteSolution candidateRepresentative,
            LinkageRouteSolution currentRepresentative)
        {
            if (candidateRepresentative == null)
            {
                return false;
            }

            if (currentRepresentative == null)
            {
                return true;
            }

            // 相同方向拓扑同时存在正常方案和60秒后的低绝缘候选时，
            // 必须始终保留满足原始绝缘要求的方案，不能被更短的降级方案替换。
            if (candidateRepresentative.IsRelaxedClearanceSolution !=
                currentRepresentative.IsRelaxedClearanceSolution)
            {
                return !candidateRepresentative.IsRelaxedClearanceSolution;
            }

            return IsPreferredRepresentative(
                candidateRepresentative.ActualCopperLength,
                candidateRepresentative.GeneratedBusbarPartCount,
                CountSolutionBends(candidateRepresentative),
                CreateStableGeometrySignature(candidateRepresentative),
                currentRepresentative);
        }

        /// <summary>
        /// 方案全部组最终中心线的折弯数（相邻非零长段方向不平行即计一次）。
        /// </summary>
        public static int CountSolutionBends(LinkageRouteSolution routeSolution)
        {
            return routeSolution?.GroupSolutions == null
                ? 0
                : routeSolution.GroupSolutions
                    .Where(groupRouteSolution => groupRouteSolution != null)
                    .Sum(groupRouteSolution => CountCenterlineBends(
                        ResolveFinalCenterline(groupRouteSolution)));
        }

        public static int CountCenterlineBends(IEnumerable<Point3d> centerline)
        {
            var directions = new List<Vector3d>();
            var points = (centerline ?? Enumerable.Empty<Point3d>())
                .Where(point => point != null)
                .ToList();
            for (var pointIndex = 1; pointIndex < points.Count; pointIndex++)
            {
                var segment = new Vector3d(points[pointIndex - 1], points[pointIndex]);
                if (segment.Norm > AbsoluteDirectionTolerance)
                    directions.Add(segment.Normalized);
            }

            var bendCount = 0;
            for (var directionIndex = 1; directionIndex < directions.Count; directionIndex++)
            {
                if (directions[directionIndex - 1].Cross(directions[directionIndex]).Norm > 1e-9d ||
                    directions[directionIndex - 1].Dot(directions[directionIndex]) < 0d)
                    bendCount++;
            }

            return bendCount;
        }

        /// <summary>
        /// 路径族签名（“不同路径”判重规则）。每组最终中心线：
        /// 1. 把每段位移按 X/Y/Z 分量拆入六个有向轴（斜段拆分量），累计各有向轴总行程；
        /// 2. 总行程小于该组错位阈值的有向轴视为小偏移，忽略；
        /// 3. 剩余有向轴集合即该组走向，不计先后顺序——先降后平与先平后降、斜切角、
        ///    台阶前后挪位都属同一走向；同一轴正反都走（绕行）时保留先走的方向，左绕/右绕不同；
        /// 4. 各组结果按组键排序拼接。全部组走向相同的方案属于同一路径族，只保留最优代表。
        /// 错位阈值由调用方按组给出（引擎取一个排距：排宽 + 绝缘距离）。
        /// </summary>
        public static string CreateRouteFamilySignature(
            LinkageRouteSolution routeSolution,
            Func<string, double> jogThresholdByGroupKey)
        {
            if (routeSolution?.GroupSolutions == null)
                return string.Empty;
            return CreateRouteFamilySignature(routeSolution.GroupSolutions
                .Where(groupRouteSolution => groupRouteSolution != null)
                .Select(groupRouteSolution => new KeyValuePair<string, IEnumerable<Point3d>>(
                    groupRouteSolution.GroupKey,
                    ResolveFinalCenterline(groupRouteSolution))),
                jogThresholdByGroupKey);
        }

        public static string CreateRouteFamilySignature(
            IEnumerable<KeyValuePair<string, IEnumerable<Point3d>>> groupCenterlines,
            Func<string, double> jogThresholdByGroupKey)
        {
            return string.Join("||", (groupCenterlines ??
                    Enumerable.Empty<KeyValuePair<string, IEnumerable<Point3d>>>())
                .OrderBy(groupCenterline => groupCenterline.Key ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(groupCenterline => groupCenterline.Key ?? string.Empty,
                    StringComparer.Ordinal)
                .Select(groupCenterline =>
                {
                    var normalizedGroupKey = groupCenterline.Key ?? string.Empty;
                    var jogThreshold = Math.Max(0d,
                        jogThresholdByGroupKey?.Invoke(normalizedGroupKey) ?? 0d);
                    return normalizedGroupKey.Length.ToString(CultureInfo.InvariantCulture) +
                        ":" + normalizedGroupKey + "=" +
                        CreateCenterlineFamilySignature(groupCenterline.Value, jogThreshold);
                }));
        }

        internal static string CreateCenterlineFamilySignature(
            IEnumerable<Point3d> physicalStartToCabinetCenterline,
            double jogThreshold)
        {
            var points = (physicalStartToCabinetCenterline ?? Enumerable.Empty<Point3d>())
                .Where(point => point != null)
                .ToList();
            // 六个有向轴（+X,-X,+Y,-Y,+Z,-Z）各自累计行程；斜段按分量拆入对应轴。
            var travel = new double[6];
            var firstOccurrence = new int[] { -1, -1, -1, -1, -1, -1 };
            var occurrence = 0;
            for (var pointIndex = 1; pointIndex < points.Count; pointIndex++)
            {
                var delta = new[]
                {
                    points[pointIndex].X - points[pointIndex - 1].X,
                    points[pointIndex].Y - points[pointIndex - 1].Y,
                    points[pointIndex].Z - points[pointIndex - 1].Z
                };
                var tolerance = Math.Max(AbsoluteDirectionTolerance,
                    delta.Max(Math.Abs) * RelativeDirectionTolerance);
                for (var axisIndex = 0; axisIndex < 3; axisIndex++)
                {
                    if (Math.Abs(delta[axisIndex]) <= tolerance)
                        continue;
                    var signedAxis = axisIndex * 2 + (delta[axisIndex] > 0d ? 0 : 1);
                    travel[signedAxis] += Math.Abs(delta[axisIndex]);
                    if (firstOccurrence[signedAxis] < 0 && travel[signedAxis] >= jogThreshold)
                        firstOccurrence[signedAxis] = occurrence++;
                }
            }

            var familyParts = new List<string>();
            for (var axisIndex = 0; axisIndex < 3; axisIndex++)
            {
                var axisName = "XYZ"[axisIndex];
                var positive = travel[axisIndex * 2] >= jogThreshold;
                var negative = travel[axisIndex * 2 + 1] >= jogThreshold;
                if (positive && negative)
                {
                    // 同轴往返（绕行）保留先走的方向：左绕与右绕是不同路径。
                    familyParts.Add(firstOccurrence[axisIndex * 2] <= firstOccurrence[axisIndex * 2 + 1]
                        ? "+" + axisName + "-" + axisName
                        : "-" + axisName + "+" + axisName);
                }
                else if (positive)
                    familyParts.Add("+" + axisName);
                else if (negative)
                    familyParts.Add("-" + axisName);
            }

            return string.Join(",", familyParts);
        }

        internal static string CreateSolutionSignature(
            IEnumerable<KeyValuePair<string, IEnumerable<Point3d>>>
                groupCenterlines)
        {
            return string.Join("||", (groupCenterlines ??
                    Enumerable.Empty<KeyValuePair<string, IEnumerable<Point3d>>>())
                .OrderBy(
                    groupCenterline => groupCenterline.Key ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(
                    groupCenterline => groupCenterline.Key ?? string.Empty,
                    StringComparer.Ordinal)
                .Select(groupCenterline =>
                {
                    var normalizedGroupKey = groupCenterline.Key ?? string.Empty;
                    return normalizedGroupKey.Length.ToString(
                            CultureInfo.InvariantCulture) +
                        ":" + normalizedGroupKey + "=" +
                        CreateCenterlineSignature(groupCenterline.Value);
                }));
        }

        internal static string CreateStableGeometrySignature(
            LinkageRouteAlgorithmKind algorithmKind,
            IEnumerable<KeyValuePair<string, IEnumerable<Point3d>>>
                groupCenterlines)
        {
            return ((int)algorithmKind).ToString(CultureInfo.InvariantCulture) +
                "|True|G:" + CreateGroupGeometrySignature(groupCenterlines) +
                "|D:";
        }

        internal static bool IsPreferredRepresentative(
            double candidateActualCopperLength,
            int candidateGeneratedBusbarPartCount,
            string candidateStableGeometrySignature,
            LinkageRouteSolution currentRepresentative)
        {
            return IsPreferredRepresentative(candidateActualCopperLength,
                candidateGeneratedBusbarPartCount, -1,
                candidateStableGeometrySignature, currentRepresentative);
        }

        /// <summary>
        /// 代表比较顺序：总长（容差内视为相等）→ 零件数 → 折弯数（更少优先；
        /// candidateBendCount 小于 0 表示不比较）→ 稳定几何签名。字典序比较，不会循环替换。
        /// </summary>
        internal static bool IsPreferredRepresentative(
            double candidateActualCopperLength,
            int candidateGeneratedBusbarPartCount,
            int candidateBendCount,
            string candidateStableGeometrySignature,
            LinkageRouteSolution currentRepresentative)
        {
            if (currentRepresentative == null)
            {
                return true;
            }

            var candidateComparableLength = GetComparableLength(
                candidateActualCopperLength);
            var currentComparableLength = GetComparableLength(
                currentRepresentative.ActualCopperLength);
            if (candidateComparableLength + RepresentativeLengthTolerance <
                currentComparableLength)
            {
                return true;
            }

            if (currentComparableLength + RepresentativeLengthTolerance <
                candidateComparableLength)
            {
                return false;
            }

            if (candidateGeneratedBusbarPartCount !=
                currentRepresentative.GeneratedBusbarPartCount)
            {
                return candidateGeneratedBusbarPartCount <
                    currentRepresentative.GeneratedBusbarPartCount;
            }

            if (candidateBendCount >= 0)
            {
                var currentBendCount = CountSolutionBends(currentRepresentative);
                if (candidateBendCount != currentBendCount)
                    return candidateBendCount < currentBendCount;
            }

            return string.CompareOrdinal(
                    candidateStableGeometrySignature ?? string.Empty,
                    CreateStableGeometrySignature(currentRepresentative)) < 0;
        }

        private static string CreateSegmentDirectionCategory(
            Point3d segmentStartPoint,
            Point3d segmentEndPoint)
        {
            var segmentDeltaX = segmentEndPoint.X - segmentStartPoint.X;
            var segmentDeltaY = segmentEndPoint.Y - segmentStartPoint.Y;
            var segmentDeltaZ = segmentEndPoint.Z - segmentStartPoint.Z;
            var maximumAbsoluteDelta = Math.Max(
                Math.Abs(segmentDeltaX),
                Math.Max(Math.Abs(segmentDeltaY), Math.Abs(segmentDeltaZ)));
            var directionTolerance = Math.Max(
                AbsoluteDirectionTolerance,
                maximumAbsoluteDelta * RelativeDirectionTolerance);
            var segmentXSign = ResolveComponentSign(
                segmentDeltaX,
                directionTolerance);
            var segmentYSign = ResolveComponentSign(
                segmentDeltaY,
                directionTolerance);
            var segmentZSign = ResolveComponentSign(
                segmentDeltaZ,
                directionTolerance);
            var nonZeroComponentCount =
                (segmentXSign == 0 ? 0 : 1) +
                (segmentYSign == 0 ? 0 : 1) +
                (segmentZSign == 0 ? 0 : 1);
            if (nonZeroComponentCount == 0)
            {
                return string.Empty;
            }

            if (nonZeroComponentCount == 1)
            {
                if (segmentXSign != 0)
                {
                    return FormatSign(segmentXSign) + "X";
                }

                if (segmentYSign != 0)
                {
                    return FormatSign(segmentYSign) + "Y";
                }

                return FormatSign(segmentZSign) + "Z";
            }

            if (nonZeroComponentCount == 2)
            {
                if (segmentZSign == 0)
                {
                    return "XY(" + FormatSign(segmentXSign) + "," +
                        FormatSign(segmentYSign) + ")";
                }

                if (segmentYSign == 0)
                {
                    return "XZ(" + FormatSign(segmentXSign) + "," +
                        FormatSign(segmentZSign) + ")";
                }

                return "YZ(" + FormatSign(segmentYSign) + "," +
                    FormatSign(segmentZSign) + ")";
            }

            // 正常可制造中心线不会出现三轴同时变化；保留独立类别，避免误并入任一平面。
            return "XYZ(" + FormatSign(segmentXSign) + "," +
                FormatSign(segmentYSign) + "," +
                FormatSign(segmentZSign) + ")";
        }

        private static IList<Point3d> ResolveFinalCenterline(
            LinkageGroupRouteSolution groupRouteSolution)
        {
            return groupRouteSolution.ManufacturableCenterline != null &&
                groupRouteSolution.ManufacturableCenterline.Count >= 2
                    ? groupRouteSolution.ManufacturableCenterline
                    : groupRouteSolution.AxisSkeleton ?? new List<Point3d>();
        }

        private static string CreatePointSequenceSignature(
            IEnumerable<Point3d> centerlinePoints)
        {
            return string.Join(";", (centerlinePoints ?? Enumerable.Empty<Point3d>())
                .Where(centerlinePoint => centerlinePoint != null)
                .Select(centerlinePoint =>
                    centerlinePoint.X.ToString("R", CultureInfo.InvariantCulture) + "," +
                    centerlinePoint.Y.ToString("R", CultureInfo.InvariantCulture) + "," +
                    centerlinePoint.Z.ToString("R", CultureInfo.InvariantCulture)));
        }

        private static string CreateGroupGeometrySignature(
            IEnumerable<KeyValuePair<string, IEnumerable<Point3d>>>
                groupCenterlines)
        {
            return string.Join("||", (groupCenterlines ??
                    Enumerable.Empty<KeyValuePair<string, IEnumerable<Point3d>>>())
                .OrderBy(
                    groupCenterline => groupCenterline.Key ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(
                    groupCenterline => groupCenterline.Key ?? string.Empty,
                    StringComparer.Ordinal)
                .Select(groupCenterline =>
                    EncodeText(groupCenterline.Key) + "=" +
                    CreatePointSequenceSignature(groupCenterline.Value)));
        }

        private static string EncodeText(string sourceText)
        {
            var normalizedSourceText = sourceText ?? string.Empty;
            return normalizedSourceText.Length.ToString(CultureInfo.InvariantCulture) +
                ":" + normalizedSourceText;
        }

        private static int ResolveComponentSign(
            double componentValue,
            double directionTolerance)
        {
            if (componentValue > directionTolerance)
            {
                return 1;
            }

            return componentValue < -directionTolerance ? -1 : 0;
        }

        private static string FormatSign(int componentSign)
        {
            return componentSign > 0 ? "+" : "-";
        }

        private static double GetComparableLength(double routeLength)
        {
            return double.IsNaN(routeLength) ? double.PositiveInfinity : routeLength;
        }
    }
}
