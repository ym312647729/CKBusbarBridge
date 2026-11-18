using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteAxisPathBuilder
    {
        // A proposal may remove a whole excursion rather than one corner. It
        // never edits its source or fixed terminals; all solids are rebuilt.
        internal static IReadOnlyList<IList<Point3d>> BuildDetourShortcutCenterlines(
            LinkageMultiGroupRouteRequest shortcutRequest, LinkageRouteGroupRequest shortcutGroup,
            IList<Point3d> shortcutSource, CancellationToken shortcutToken)
        {
            var shortcutProposals = new List<IList<Point3d>>();
            if (shortcutSource == null || shortcutSource.Count < 6 || !IsAxisAligned(shortcutSource))
                return shortcutProposals;
            var shortcutSourceLength = GetLength(shortcutSource);
            var shortcutSignatures = new HashSet<string>(StringComparer.Ordinal);
            for (var shortcutAxis = 0; shortcutAxis < 3; shortcutAxis++)
            for (var shortcutFirst = 1; shortcutFirst + 2 < shortcutSource.Count - 1; shortcutFirst++)
            for (var shortcutLast = shortcutFirst + 2; shortcutLast < shortcutSource.Count - 1; shortcutLast++)
            foreach (var shortcutTarget in new[] { GetCoordinate(shortcutSource[shortcutFirst], shortcutAxis),
                GetCoordinate(shortcutSource[shortcutLast], shortcutAxis) }.Distinct())
            {
                if (shortcutToken.IsCancellationRequested) return shortcutProposals;
                var projectedShortcut = shortcutSource.Select(Copy).ToList();
                for (var shortcutIndex = shortcutFirst; shortcutIndex <= shortcutLast; shortcutIndex++)
                    projectedShortcut[shortcutIndex] = WithCoordinate(projectedShortcut[shortcutIndex], shortcutAxis, shortcutTarget);
                var collapsedShortcut = CollapseShortcutRuns(projectedShortcut);
                if (collapsedShortcut.Count > shortcutSource.Count - 2 || collapsedShortcut.Count < 3 ||
                    !IsAxisAligned(collapsedShortcut)) continue;
                var shortcutVariants = new List<IList<Point3d>> { collapsedShortcut };
                foreach (var shortcutAtStart in new[] { true, false })
                    shortcutVariants = shortcutVariants.SelectMany(shortcutVariant =>
                        BuildShortcutTerminalVariants(shortcutRequest, shortcutGroup, shortcutSource,
                            shortcutVariant, shortcutAtStart)).ToList();
                foreach (var shortcutVariant in shortcutVariants)
                    if (HasMinimumSegments(shortcutVariant, shortcutRequest.MinimumSegmentLength) &&
                        GetLength(shortcutVariant) + GeometryTolerance < shortcutSourceLength &&
                        shortcutSignatures.Add(GetSignature(shortcutVariant)))
                        shortcutProposals.Add(shortcutVariant);
            }
            return shortcutProposals.OrderBy(GetLength).ThenBy(GetSignature, StringComparer.Ordinal)
                .Take(shortcutRequest.SearchOptions.MaximumClearanceOptimizationCandidateCount).ToList();
        }

        private static IList<Point3d> CollapseShortcutRuns(IList<Point3d> projectedShortcut)
        {
            var collapsedShortcut = new List<Point3d>();
            foreach (var projectedPoint in projectedShortcut)
            {
                if (collapsedShortcut.Count > 0 && collapsedShortcut.Last().DistanceTo(projectedPoint) <= GeometryTolerance) continue;
                collapsedShortcut.Add(Copy(projectedPoint));
                while (collapsedShortcut.Count >= 3)
                {
                    var lastShortcutIndex = collapsedShortcut.Count - 1;
                    var firstShortcutDirection = new Vector3d(collapsedShortcut[lastShortcutIndex - 2], collapsedShortcut[lastShortcutIndex - 1]);
                    var lastShortcutDirection = new Vector3d(collapsedShortcut[lastShortcutIndex - 1], collapsedShortcut[lastShortcutIndex]);
                    if (firstShortcutDirection.Norm > GeometryTolerance && lastShortcutDirection.Norm > GeometryTolerance &&
                        firstShortcutDirection.Normalized.Cross(lastShortcutDirection.Normalized).Norm > GeometryTolerance) break;
                    collapsedShortcut.RemoveAt(lastShortcutIndex - 1);
                    if (collapsedShortcut.Count >= 2 && collapsedShortcut.Last().DistanceTo(collapsedShortcut[collapsedShortcut.Count - 2]) <= GeometryTolerance)
                        collapsedShortcut.RemoveAt(collapsedShortcut.Count - 1);
                }
            }
            return collapsedShortcut;
        }

        private static IEnumerable<IList<Point3d>> BuildShortcutTerminalVariants(
            LinkageMultiGroupRouteRequest shortcutRequest, LinkageRouteGroupRequest shortcutGroup,
            IList<Point3d> shortcutSource, IList<Point3d> shortcutVariant, bool shortcutAtStart)
        {
            var shortcutEndpointIndex = shortcutAtStart ? 0 : shortcutVariant.Count - 1;
            var shortcutAdjacentIndex = shortcutAtStart ? 1 : shortcutVariant.Count - 2;
            var sourceEndpointIndex = shortcutAtStart ? 0 : shortcutSource.Count - 1;
            var sourceAdjacentIndex = shortcutAtStart ? 1 : shortcutSource.Count - 2;
            var originalTravel = new Vector3d(shortcutSource[sourceEndpointIndex], shortcutSource[sourceAdjacentIndex]);
            var shortcutTravel = new Vector3d(shortcutVariant[shortcutEndpointIndex], shortcutVariant[shortcutAdjacentIndex]);
            yield return shortcutVariant;
            if (shortcutTravel.Norm <= GeometryTolerance || originalTravel.Normalized.Dot(shortcutTravel.Normalized) >= -1d + GeometryTolerance) yield break;
            var shortcutTerminal = shortcutAtStart ? shortcutGroup.StartTerminal : shortcutGroup.EndTerminal;
            var shortcutWidth = shortcutAtStart ? shortcutGroup.GetStartLapBusbarWidth(shortcutRequest) : shortcutGroup.GetCabinetTopLapBusbarWidth(shortcutRequest);
            // After changing the approach side, resolve the already permitted
            // attachment edge from this route's adjacent point. The opposite
            // fixed terminal can lie on the other side of the obstacle.
            var shortcutPoses = BuildExtendedTerminalPosesForRoute(shortcutTerminal, shortcutVariant[shortcutAdjacentIndex],
                shortcutAtStart, shortcutWidth, Math.Max(shortcutRequest.MinimumSegmentLength, shortcutWidth),
                Math.Max(shortcutRequest.MinimumSegmentLength, GetCollectivePerpendicularLapSpan(shortcutTerminal, shortcutWidth)),
                shortcutRequest.MinimumSegmentLength, null,
                IsDoubleStartCollectiveParallelLapAllowed(shortcutRequest, shortcutGroup, LinkageRouteTraversalDirection.Forward, shortcutAtStart),
                IsSingleStartMiddleParallelLapRequired(shortcutRequest, shortcutGroup, LinkageRouteTraversalDirection.Forward, shortcutAtStart),
                IsPartialStartPerpendicularLapAllowed(shortcutRequest, shortcutGroup, LinkageRouteTraversalDirection.Forward, shortcutAtStart),
                0d, GetWidthSizedOverlapBoundaryReferencePoint(shortcutGroup, shortcutGroup.GetStartLapBusbarWidth(shortcutRequest), shortcutGroup.GetCabinetTopLapBusbarWidth(shortcutRequest)),
                shortcutGroup.GetShortPhysicalStartLapLength(shortcutRequest, shortcutAtStart), shortcutRequest.RequireFixedTerminalEndLap,
                shortcutGroup.GetPhysicalStartParallelLapLength(shortcutRequest, shortcutAtStart));
            var shortcutTravelAxis = GetAxisIndex(shortcutTravel);
            foreach (var shortcutPose in shortcutPoses)
            {
                var reboundShortcut = shortcutVariant.Select(Copy).ToList();
                reboundShortcut[shortcutEndpointIndex] = WithCoordinate(reboundShortcut[shortcutEndpointIndex], shortcutTravelAxis,
                    GetCoordinate(shortcutPose.LapEndpoint, shortcutTravelAxis));
                if (new Vector3d(reboundShortcut[shortcutEndpointIndex], reboundShortcut[shortcutAdjacentIndex]).Dot(shortcutTravel) > GeometryTolerance)
                    yield return reboundShortcut;
            }
        }
    }
}
