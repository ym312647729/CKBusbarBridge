using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteCandidateFactory
    {
        internal static string GetCandidateSignature(
            LinkageRouteCandidate routeCandidate)
        {
            if (routeCandidate == null)
            {
                return "||";
            }

            if (routeCandidate.CachedCandidateSignature != null)
            {
                return routeCandidate.CachedCandidateSignature;
            }

            routeCandidate.CachedCandidateSignature =
                (routeCandidate.GroupRequest?.GroupKey ?? string.Empty) + "|" +
                GetCenterlineSignature(routeCandidate.ManufacturableCenterline) +
                "|" + (routeCandidate.IdentitySignature ?? string.Empty);
            return routeCandidate.CachedCandidateSignature;
        }

        private static string GetCenterlineSignature(
            IEnumerable<Point3d> centerlinePoints)
        {
            return string.Join("|", (centerlinePoints ??
                    Enumerable.Empty<Point3d>())
                .Select(point => string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:F6},{1:F6},{2:F6}",
                    point.X,
                    point.Y,
                    point.Z)));
        }

        private static string GetDoubleStartBridgeDownstreamPoolSignature(
            LinkageTerminalAssembly virtualBridgeTerminal)
        {
            if (virtualBridgeTerminal == null)
            {
                return string.Empty;
            }

            var signatureBuilder = new StringBuilder(512);
            AppendPointSignature(
                signatureBuilder,
                virtualBridgeTerminal.RouteAnchor);
            AppendVectorSignature(
                signatureBuilder,
                virtualBridgeTerminal.LengthDirection);
            AppendVectorSignature(
                signatureBuilder,
                virtualBridgeTerminal.ThicknessDirection);
            signatureBuilder.Append("|REF=").Append(
                GetCenterlineSignature(
                    virtualBridgeTerminal
                        .PreferredPerpendicularLapReferencePoints));
            foreach (var terminalMember in virtualBridgeTerminal.Members ??
                new List<LinkageTerminalMember>())
            {
                var fixedBusbar = terminalMember?.FixedBusbar;
                signatureBuilder.Append("|MEM=").Append(
                    terminalMember?.MemberKey ?? string.Empty);
                signatureBuilder.AppendFormat(
                    CultureInfo.InvariantCulture,
                    ":{0:F6},{1:F6}",
                    fixedBusbar?.BusBarWidth ?? 0d,
                    fixedBusbar?.BusBarThickness ?? 0d);
                AppendVectorSignature(
                    signatureBuilder,
                    fixedBusbar?.StartBusbarVector);
                AppendVectorSignature(
                    signatureBuilder,
                    fixedBusbar?.WidthVector);
                AppendVectorSignature(
                    signatureBuilder,
                    terminalMember?.ForbiddenHorizontalExitDirection);
                signatureBuilder.Append("|PTS=").Append(
                    GetCenterlineSignature(fixedBusbar?.PointList));
            }

            return signatureBuilder.ToString();
        }

        private static void AppendPointSignature(
            StringBuilder signatureBuilder,
            Point3d sourcePoint)
        {
            signatureBuilder.AppendFormat(
                CultureInfo.InvariantCulture,
                "|P={0:F6},{1:F6},{2:F6}",
                sourcePoint?.X ?? 0d,
                sourcePoint?.Y ?? 0d,
                sourcePoint?.Z ?? 0d);
        }

        private static void AppendVectorSignature(
            StringBuilder signatureBuilder,
            Vector3d sourceVector)
        {
            signatureBuilder.AppendFormat(
                CultureInfo.InvariantCulture,
                "|V={0:F6},{1:F6},{2:F6}",
                sourceVector?.X ?? 0d,
                sourceVector?.Y ?? 0d,
                sourceVector?.Z ?? 0d);
        }

        private static IReadOnlyList<LinkageRouteCandidate>
            SelectDiverseValidCandidates(
            IEnumerable<LinkageRouteCandidate> routeCandidates,
            int maximumCandidateCount,
            bool preserveStartPerpendicularLapVariants)
        {
            var diversityGroups = (routeCandidates ??
                    Enumerable.Empty<LinkageRouteCandidate>())
                .GroupBy(GetFinalCandidateDiversitySignature)
                .Select(candidateGroup => candidateGroup
                    .OrderBy(candidate =>
                        candidate.PlanarConversion.ActualCopperLength)
                    .ThenBy(candidate => candidate.GeneratedBusbarPartCount)
                    .ThenBy(candidate => candidate.SearchPriority)
                    .ThenBy(GetCandidateSignature, StringComparer.Ordinal)
                    .ToList())
                .OrderBy(candidateGroup =>
                    candidateGroup[0].PlanarConversion.ActualCopperLength)
                .ThenBy(candidateGroup =>
                    candidateGroup[0].GeneratedBusbarPartCount)
                .ThenBy(candidateGroup => candidateGroup[0].SearchPriority)
                .ThenBy(candidateGroup =>
                    GetFinalCandidateDiversitySignature(candidateGroup[0]),
                    StringComparer.Ordinal)
                .ToList();
            if (!preserveStartPerpendicularLapVariants)
            {
                var ordinarySelectedCandidates =
                    new List<LinkageRouteCandidate>();
                var boundedOrdinaryCandidateCount = Math.Max(
                    1,
                    maximumCandidateCount);
                for (var diversityRoundIndex = 0;
                    ordinarySelectedCandidates.Count <
                        boundedOrdinaryCandidateCount;
                    diversityRoundIndex++)
                {
                    var addedCandidateInRound = false;
                    foreach (var diversityGroup in diversityGroups)
                    {
                        if (diversityRoundIndex >= diversityGroup.Count)
                        {
                            continue;
                        }

                        ordinarySelectedCandidates.Add(
                            diversityGroup[diversityRoundIndex]);
                        addedCandidateInRound = true;
                        if (ordinarySelectedCandidates.Count >=
                            boundedOrdinaryCandidateCount)
                        {
                            break;
                        }
                    }

                    if (!addedCandidateInRound)
                    {
                        break;
                    }
                }

                return ordinarySelectedCandidates;
            }

            var selectedCandidates = new List<LinkageRouteCandidate>();
            var selectedCandidateSignatures = new HashSet<string>(
                StringComparer.Ordinal);
            var requiredFullLapCandidate = diversityGroups
                .SelectMany(candidateGroup => candidateGroup)
                .Where(routeCandidate => routeCandidate
                    .StartPerpendicularLapVariant ==
                    LinkageStartPerpendicularLapVariant.Full)
                .OrderBy(routeCandidate =>
                    routeCandidate.PlanarConversion.ActualCopperLength)
                .ThenBy(routeCandidate =>
                    routeCandidate.GeneratedBusbarPartCount)
                .ThenBy(routeCandidate => routeCandidate.SearchPriority == 1
                    ? 0
                    : 1)
                .ThenBy(routeCandidate => routeCandidate.SearchPriority)
                .ThenBy(GetCandidateSignature, StringComparer.Ordinal)
                .FirstOrDefault();
            var requiredPartialLapCandidate = diversityGroups
                .SelectMany(candidateGroup => candidateGroup)
                .Where(routeCandidate => routeCandidate
                    .StartPerpendicularLapVariant ==
                    LinkageStartPerpendicularLapVariant.Partial)
                .OrderBy(routeCandidate =>
                    routeCandidate.PlanarConversion.ActualCopperLength)
                .ThenBy(routeCandidate =>
                    routeCandidate.GeneratedBusbarPartCount)
                .ThenBy(routeCandidate => routeCandidate.SearchPriority)
                .ThenBy(GetCandidateSignature, StringComparer.Ordinal)
                .FirstOrDefault();
            var preserveBothTerminalLapVariants =
                requiredFullLapCandidate != null &&
                requiredPartialLapCandidate != null;
            var effectiveMaximumCandidateCount = preserveBothTerminalLapVariants
                ? Math.Max(2, Math.Max(1, maximumCandidateCount))
                : Math.Max(1, maximumCandidateCount);
            if (preserveBothTerminalLapVariants)
            {
                selectedCandidates.Add(requiredFullLapCandidate);
                selectedCandidates.Add(requiredPartialLapCandidate);
                selectedCandidateSignatures.Add(GetCandidateSignature(
                    requiredFullLapCandidate));
                selectedCandidateSignatures.Add(GetCandidateSignature(
                    requiredPartialLapCandidate));
            }

            if (!preserveBothTerminalLapVariants)
            {
                for (var diversityRoundIndex = 0;
                    selectedCandidates.Count < effectiveMaximumCandidateCount;
                    diversityRoundIndex++)
                {
                    var addedCandidateInRound = false;
                    foreach (var diversityGroup in diversityGroups)
                    {
                        if (diversityRoundIndex >= diversityGroup.Count)
                        {
                            continue;
                        }

                        selectedCandidates.Add(
                            diversityGroup[diversityRoundIndex]);
                        addedCandidateInRound = true;
                        if (selectedCandidates.Count >=
                            effectiveMaximumCandidateCount)
                        {
                            break;
                        }
                    }

                    if (!addedCandidateInRound)
                    {
                        break;
                    }
                }

                return selectedCandidates;
            }

            var diversityCandidateIndices = new int[diversityGroups.Count];
            var addedReservedCandidateInRound = true;
            while (selectedCandidates.Count < effectiveMaximumCandidateCount &&
                addedReservedCandidateInRound)
            {
                addedReservedCandidateInRound = false;
                for (var diversityGroupIndex = 0;
                    diversityGroupIndex < diversityGroups.Count &&
                    selectedCandidates.Count < effectiveMaximumCandidateCount;
                    diversityGroupIndex++)
                {
                    var diversityGroup = diversityGroups[diversityGroupIndex];
                    while (diversityCandidateIndices[diversityGroupIndex] <
                        diversityGroup.Count)
                    {
                        var diversityCandidate = diversityGroup[
                            diversityCandidateIndices[diversityGroupIndex]++];
                        if (!selectedCandidateSignatures.Add(
                                GetCandidateSignature(diversityCandidate)))
                        {
                            continue;
                        }

                        selectedCandidates.Add(diversityCandidate);
                        addedReservedCandidateInRound = true;
                        break;
                    }
                }
            }

            return selectedCandidates
                .OrderBy(routeCandidate =>
                    routeCandidate.PlanarConversion.ActualCopperLength)
                .ThenBy(routeCandidate =>
                    routeCandidate.GeneratedBusbarPartCount)
                .ThenBy(routeCandidate => routeCandidate.SearchPriority)
                .ThenBy(GetCandidateSignature, StringComparer.Ordinal)
                .ToList();
        }

        private static string GetFinalCandidateDiversitySignature(
            LinkageRouteCandidate routeCandidate)
        {
            if (routeCandidate == null)
            {
                return string.Empty;
            }

            if (routeCandidate.CachedFinalDiversitySignature != null)
            {
                return routeCandidate.CachedFinalDiversitySignature;
            }

            var axisNames = new List<string>();
            for (var pointIndex = 0;
                pointIndex < (routeCandidate.AxisSkeleton?.Count ?? 0) - 1;
                pointIndex++)
            {
                var axisDirection = new Vector3d(
                    routeCandidate.AxisSkeleton[pointIndex],
                    routeCandidate.AxisSkeleton[pointIndex + 1]).Normalized;
                var axisName = Math.Abs(axisDirection.X) >= 1d - 1e-6
                    ? "X"
                    : Math.Abs(axisDirection.Y) >= 1d - 1e-6 ? "Y" : "Z";
                if (axisNames.Count == 0 ||
                    axisNames[axisNames.Count - 1] != axisName)
                {
                    axisNames.Add(axisName);
                }
            }

            var firstPoint = routeCandidate.ManufacturableCenterline?
                .FirstOrDefault();
            var lastPoint = routeCandidate.ManufacturableCenterline?
                .LastOrDefault();
            var internalAxisPoints = (routeCandidate.AxisSkeleton ??
                    new List<Point3d>())
                .Skip(1)
                .Take(Math.Max(
                    0,
                    (routeCandidate.AxisSkeleton?.Count ?? 0) - 2))
                .ToList();
            var midpointX = ((firstPoint?.X ?? 0d) +
                (lastPoint?.X ?? 0d)) / 2d;
            var midpointY = ((firstPoint?.Y ?? 0d) +
                (lastPoint?.Y ?? 0d)) / 2d;
            var midpointZ = ((firstPoint?.Z ?? 0d) +
                (lastPoint?.Z ?? 0d)) / 2d;
            var layerX = internalAxisPoints.Count == 0
                ? 0
                : GetCoordinateLayerSign(
                    internalAxisPoints.Average(point => point.X) - midpointX);
            var layerY = internalAxisPoints.Count == 0
                ? 0
                : GetCoordinateLayerSign(
                    internalAxisPoints.Average(point => point.Y) - midpointY);
            var layerZ = internalAxisPoints.Count == 0
                ? 0
                : GetCoordinateLayerSign(
                    internalAxisPoints.Average(point => point.Z) - midpointZ);
            routeCandidate.CachedFinalDiversitySignature = string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|L={2},{3},{4}|S={5:F3},{6:F3},{7:F3}|" +
                "E={8:F3},{9:F3},{10:F3}",
                routeCandidate.TopologySignature ?? string.Empty,
                string.Join("-", axisNames),
                layerX,
                layerY,
                layerZ,
                firstPoint?.X ?? 0d,
                firstPoint?.Y ?? 0d,
                firstPoint?.Z ?? 0d,
                lastPoint?.X ?? 0d,
                lastPoint?.Y ?? 0d,
                lastPoint?.Z ?? 0d);
            return routeCandidate.CachedFinalDiversitySignature;
        }

        private static int GetCoordinateLayerSign(double coordinateOffset)
        {
            if (coordinateOffset < -1e-6d)
            {
                return -1;
            }

            return coordinateOffset > 1e-6d ? 1 : 0;
        }
    }
}
