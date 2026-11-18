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
        internal static IEnumerable<IList<Point3d>> BuildPhysicalLayerCandidates(
            LinkageMultiGroupRouteRequest request, LinkageRouteGroupRequest group,
            IReadOnlyList<Geometry.LinkageRouteSolid> fixedSolids,
            CancellationToken cancellationToken)
        {
            if (request == null || group?.StartTerminal?.Members?.Count < 2 ||
                group.EndTerminal?.Members?.Count == 0 || !group.HasSectionSpecificBusbarSettings(request)) yield break;
            var lateralAxis = GetAxisIndex(group.StartTerminal.ThicknessDirection);
            var exitAxis = GetAxisIndex(group.StartTerminal.LengthDirection);
            var heightAxis = GetAxisIndex(group.EndTerminal.LengthDirection);
            if (new[] { lateralAxis, exitAxis, heightAxis }.Distinct().Count() != 3 ||
                GetAxisIndex(group.EndTerminal.ThicknessDirection) != exitAxis ||
                group.StartTerminal.Members.Concat(group.EndTerminal.Members).Any(member =>
                    member.FixedBusbar == null || member.FixedBusbar.PointList.Count != 2)) yield break;
            var sourcePaths = BuildActualSectionCandidates(request, group,
                LinkageRouteTraversalDirection.Forward, cancellationToken);
            var critical = BuildClearanceCriticalCoordinates(request, group, fixedSolids,
                group.GetRequiredInsulationClearance(request), cancellationToken);
            var middleWidth = group.GetBusbarWidth(request);
            var middleThickness = group.GetBusbarThickness(request);
            var startWidth = group.GetStartLapBusbarWidth(request);
            var heightDirection = Math.Sign(Component(group.EndTerminal.LengthDirection, heightAxis));
            var exitDirection = Math.Sign(Component(group.StartTerminal.LengthDirection, exitAxis));
            if (heightDirection == 0 || exitDirection == 0) yield break;
            var startEdges = group.StartTerminal.Members.SelectMany(member =>
                member.FixedBusbar.PointList.SelectMany(point => new[] {
                    GetCoordinate(point,heightAxis) - Math.Abs(Component(member.FixedBusbar.WidthVector,heightAxis))*member.FixedBusbar.BusBarWidth/2d,
                    GetCoordinate(point,heightAxis) + Math.Abs(Component(member.FixedBusbar.WidthVector,heightAxis))*member.FixedBusbar.BusBarWidth/2d })).Distinct().ToArray();
            var endCoordinates = group.EndTerminal.Members.SelectMany(member =>
                member.FixedBusbar.PointList.Select(point => GetCoordinate(point,heightAxis))).ToArray();
            var endEntryBoundary = heightDirection < 0 ? endCoordinates.Max() : endCoordinates.Min();
            var crossPlane = endEntryBoundary - heightDirection*(middleWidth + middleThickness/2d);
            var startLayers = new[] { heightDirection < 0 ? startEdges.Max() : startEdges.Min() }
                .Concat(startEdges.Select(edge => edge + heightDirection*middleWidth/2d))
                .Concat(startEdges).Distinct().ToArray();
            var anchors = sourcePaths.Where(path => path.Count >= 4).GroupBy(path => string.Join("|",new[] {
                GetCoordinate(path[0],lateralAxis), GetCoordinate(path[0],exitAxis),
                GetCoordinate(path.Last(),lateralAxis),GetCoordinate(path.Last(),exitAxis),GetCoordinate(path.Last(),heightAxis),
                GetCoordinate(path[path.Count-2],heightAxis)
            }.Select(value => value.ToString("R",System.Globalization.CultureInfo.InvariantCulture))))
                .Select(paths => paths.OrderBy(path => path.Count).First()).ToArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var proposals = new List<Tuple<int,int,IList<Point3d>>>();
            // Every geometric attachment profile gets a turn before revisiting
            // more planes for the same profile. No acceptance is inferred here.
            for (var layerIndex=0;layerIndex<startLayers.Length;layerIndex++)
            foreach (var anchor in anchors)
            {
                var startLayer=startLayers[layerIndex];
                if (cancellationToken.IsCancellationRequested) yield break;
                var first = anchor[0]; var last = anchor.Last();
                var firstExits = new[] { GetCoordinate(first,exitAxis),
                    GetCoordinate(group.StartTerminal.RouteAnchor,exitAxis)-exitDirection*startWidth }.Distinct().ToArray();
                foreach (var firstExit in firstExits)
                {
                    var crossing = new List<Point3d> {
                        LayerPoint(first,lateralAxis,exitAxis,heightAxis,GetCoordinate(first,lateralAxis),firstExit,startLayer),
                        LayerPoint(first,lateralAxis,exitAxis,heightAxis,GetCoordinate(first,lateralAxis),firstExit,crossPlane),
                        LayerPoint(first,lateralAxis,exitAxis,heightAxis,GetCoordinate(last,lateralAxis),firstExit,crossPlane),
                        LayerPoint(first,lateralAxis,exitAxis,heightAxis,GetCoordinate(last,lateralAxis),GetCoordinate(last,exitAxis),crossPlane), last.Copy() };
                    if (HasMinimumSegments(crossing,request.MinimumSegmentLength) && seen.Add(GetSignature(crossing)))
                        proposals.Add(Tuple.Create(0,layerIndex,(IList<Point3d>)crossing));
                }
                var originalRail = anchor.Skip(1).Take(anchor.Count-2).Select(point => GetCoordinate(point,exitAxis))
                    .Where(value => exitDirection*(value-GetCoordinate(last,exitAxis))>GeometryTolerance).FirstOrDefault();
                var rails = new[] { originalRail }.Concat(critical[exitAxis].Where(value =>
                    exitDirection*(value-GetCoordinate(last,exitAxis))>GeometryTolerance).OrderBy(value=>Math.Abs(value-originalRail))).Distinct();
                var returnHeight = GetCoordinate(anchor[anchor.Count-2],heightAxis);
                var railIndex=0;
                foreach (var rail in rails)
                {
                railIndex++;
                foreach (var firstExit in firstExits)
                {
                    if (cancellationToken.IsCancellationRequested) yield break;
                    var detour = new List<Point3d> {
                        LayerPoint(first,lateralAxis,exitAxis,heightAxis,GetCoordinate(first,lateralAxis),firstExit,startLayer),
                        LayerPoint(first,lateralAxis,exitAxis,heightAxis,GetCoordinate(first,lateralAxis),rail,startLayer),
                        LayerPoint(first,lateralAxis,exitAxis,heightAxis,GetCoordinate(last,lateralAxis),rail,startLayer),
                        LayerPoint(first,lateralAxis,exitAxis,heightAxis,GetCoordinate(last,lateralAxis),rail,returnHeight),
                        LayerPoint(first,lateralAxis,exitAxis,heightAxis,GetCoordinate(last,lateralAxis),GetCoordinate(last,exitAxis),returnHeight),last.Copy() };
                    if (HasMinimumSegments(detour,request.MinimumSegmentLength) && seen.Add(GetSignature(detour)))
                        proposals.Add(Tuple.Create(railIndex,layerIndex,(IList<Point3d>)detour));
                }
                }
            }
            foreach(var proposal in proposals.OrderBy(item=>item.Item1).ThenBy(item=>item.Item2))
            {
                if(cancellationToken.IsCancellationRequested) yield break;
                yield return proposal.Item3;
            }
        }

        private static double Component(Vector3d vector, int axis) => axis == 0 ? vector.X : axis == 1 ? vector.Y : vector.Z;
        private static Point3d LayerPoint(Point3d source,int lateralAxis,int exitAxis,int heightAxis,double lateral,double exit,double height)
        {
            var point=WithCoordinate(source,lateralAxis,lateral);
            point=WithCoordinate(point,exitAxis,exit);
            return WithCoordinate(point,heightAxis,height);
        }
    }
}
