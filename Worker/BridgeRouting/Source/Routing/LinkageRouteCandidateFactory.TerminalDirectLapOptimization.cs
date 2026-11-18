using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteCandidateFactory
    {
        // Add a different terminal connection, rather than deleting a still-required I bar.
        // Scope: one uniform member, a bent absorbing part followed by two straight parts.
        // Other structures retain their original optimization neighborhoods.
        private static IEnumerable<LinkageRouteCandidate> BuildTerminalDirectLapOptimizationCandidates(
            LinkageMultiGroupRouteRequest request, LinkageRouteCandidate source,
            IReadOnlyList<LinkageRouteSolid> staticSolids, CancellationToken cancellationToken)
        {
            var conversion = source.PlanarConversion;
            var group = source.GroupRequest;
            if (conversion.StackMembers.Count != 1 || conversion.CenterlineParts.Count != 3 ||
                conversion.Accessories.Count != 0 || conversion.UsesSectionSpecificBusbarGeometry ||
                group.HasSectionSpecificBusbarSettings(request) || group.EndTerminal.Members.Count != 1 ||
                conversion.DoubleStartPhysicalBridgeSectionIndices.Count != 0)
                yield break;
            var member = conversion.StackMembers[0];
            if (member.PlanarParts.Count != 3 || member.PlanarParts[0].PointList.Count < 3 ||
                member.PlanarParts[1].PointList.Count != 2 || member.PlanarParts[2].PointList.Count != 2)
                yield break;
            var bridge = member.PlanarParts[1];
            var fixedPart = group.EndTerminal.Members[0].FixedBusbar;
            if (fixedPart?.PointList?.Count != 2 || !fixedPart.IsCenter ||
                member.PlanarParts.Any(part=>!part.IsCenter ||
                    Math.Abs(part.BusBarWidth-bridge.BusBarWidth)>GeometryTolerance ||
                    Math.Abs(part.BusBarThickness-bridge.BusBarThickness)>GeometryTolerance)) yield break;
            var fixedLength = new Vector3d(fixedPart.PointList[0], fixedPart.PointList[1]);
            if (fixedLength.Norm < bridge.BusBarWidth) yield break;
            var inward = fixedLength.Normalized;
            var bridgeDirection = new Vector3d(bridge.PointList[0], bridge.PointList[1]).Normalized;
            var fixedNormal = inward.Cross(fixedPart.WidthVector.Normalized).Normalized;
            if (Math.Abs(bridgeDirection.Dot(fixedPart.WidthVector.Normalized)) < 1d-GeometryTolerance ||
                Math.Abs(bridge.WidthVector.Normalized.Dot(inward)) < 1d-GeometryTolerance)
                yield break;
            var bridgeOrigin = bridge.PointList[0];
            var oldAlong = new Vector3d(fixedPart.PointList[0], bridgeOrigin).Dot(inward);
            var oldNormal = new Vector3d(fixedPart.PointList[0], bridgeOrigin).Dot(fixedNormal);
            var halfLapWidth = bridge.BusBarWidth/2d;
            var longitudinalTargets = new[] {
                Math.Max(halfLapWidth, Math.Min(fixedLength.Norm-halfLapWidth,oldAlong)),
                halfLapWidth, fixedLength.Norm-halfLapWidth }.Distinct().ToList();
            var contactDistance = (fixedPart.BusBarThickness+bridge.BusBarThickness)/2d;
            var placements = LinkageRouteStackPlacementSolver.ResolveTerminalPlacements(group.EndTerminal,
                1, bridge.BusBarThickness, group.StackEndSpace, group.StackMiddleSpace,
                request.ShouldUseSpacerlessTerminalLap(false));
            foreach (var alongTarget in longitudinalTargets)
            foreach (var side in new[] {-1d,1d})
            {
                if (cancellationToken.IsCancellationRequested) yield break;
                var delta = new Vector3d(
                    inward.X*(alongTarget-oldAlong)+fixedNormal.X*(side*contactDistance-oldNormal),
                    inward.Y*(alongTarget-oldAlong)+fixedNormal.Y*(side*contactDistance-oldNormal),
                    inward.Z*(alongTarget-oldAlong)+fixedNormal.Z*(side*contactDistance-oldNormal));
                var trial = CopyLinkedTrialCandidate(source);
                trial.TopologySignature = "OPT-TERMINAL-DIRECT-LAP";
                var prepared = trial.PlanarConversion;
                var changedMember = prepared.StackMembers[0];
                changedMember.PlanarParts.RemoveAt(2);
                var changedBridge = changedMember.PlanarParts[1];
                var absorbingPart = changedMember.PlanarParts[0];
                // Keep the I bar rigid; its full movement is absorbed by the adjacent bent part.
                changedBridge.PointList = changedBridge.PointList.Select(point =>
                    new Point3d(point.X+delta.X,point.Y+delta.Y,point.Z+delta.Z)).ToList();
                var endIndex = absorbingPart.PointList.Count-1;
                var requiredBridgeEnd = new Vector3d(changedBridge.PointList[0],fixedPart.PointList[0])
                    .Dot(bridgeDirection)+fixedPart.BusBarWidth/2d;
                if (requiredBridgeEnd <= GeometryTolerance) continue;
                changedBridge.PointList[1] = TranslatePoint(changedBridge.PointList[0],bridgeDirection,requiredBridgeEnd);
                var absorbingLength = new Vector3d(absorbingPart.PointList[endIndex-1],
                    absorbingPart.PointList[endIndex]).Normalized;
                var absorbingNormal = absorbingLength.Cross(absorbingPart.WidthVector.Normalized).Normalized;
                if (Math.Abs(delta.Dot(absorbingPart.WidthVector.Normalized)) > GeometryTolerance) continue;
                var reverseAbsorber = absorbingPart.Copy();
                reverseAbsorber.PointList.Reverse();
                reverseAbsorber.StartBusbarVector = new Vector3d(reverseAbsorber.PointList[0],
                    reverseAbsorber.PointList[1]).Normalized.Cross(reverseAbsorber.WidthVector.Normalized).Normalized;
                var normalDistance = delta.Dot(absorbingNormal);
                if (!LinkageRouteTerminalAccessoryBuilder.TryMoveStartTerminalLapSegment(reverseAbsorber,
                    absorbingNormal,normalDistance,out _,out _)) continue;
                absorbingPart.PointList = reverseAbsorber.PointList.AsEnumerable().Reverse().Select(point=>point.Copy()).ToList();
                var lengthDistance = delta.Dot(absorbingLength);
                absorbingPart.PointList[endIndex] = TranslatePoint(absorbingPart.PointList[endIndex],
                    absorbingLength,lengthDistance);
                if (absorbingPart.PointList[0].DistanceTo(member.PlanarParts[0].PointList[0]) > GeometryTolerance)
                    continue;
                // Rebuild reference geometry and its internal identities from the actual two parts.
                prepared.CenterlineParts.Clear();
                for (var section=0;section<changedMember.PlanarParts.Count;section++)
                {
                    var part=changedMember.PlanarParts[section];
                    part.PhysicalSectionIndex=section;part.SpecificationSectionIndex=section;
                    prepared.CenterlineParts.Add(part.Copy());
                }
                prepared.OriginalCenterlineSnapshot=null;
                var actualOffset = new Vector3d(fixedPart.PointList[0],changedBridge.PointList[0])
                    .Dot(group.EndTerminal.ThicknessDirection.Normalized);
                var placement = placements.FirstOrDefault(item=>Math.Abs(item.BaseCenterOffset-actualOffset)<=GeometryTolerance);
                if (placement==null) continue;
                prepared.EndStackPlacement=placement;
                prepared.ActualCopperLength=changedMember.PlanarParts.Sum(part=>part.GetDevelopedLength());
                var absorberEnd=absorbingPart.PointList.Last();
                var bridgeEnd=changedBridge.PointList.Last();
                var junction=TranslatePoint(absorberEnd,absorbingLength,
                    new Vector3d(absorberEnd,bridgeEnd).Dot(absorbingLength));
                var logicalEnd=TranslatePoint(bridgeEnd,absorbingNormal,
                    -new Vector3d(absorberEnd,bridgeEnd).Dot(absorbingNormal));
                var centerline=absorbingPart.PointList.Take(endIndex).Select(point=>point.Copy()).ToList();
                centerline.Add(junction);centerline.Add(logicalEnd);
                var candidate=BuildPreparedCandidate(request,group,centerline,centerline,prepared,staticSolids,
                    trial.TopologySignature,GetCenterlineSignature(centerline),source.SearchPriority,cancellationToken);
                if (candidate?.IsValid==true && candidate.PlanarConversion.ActualCopperLength+
                    GeometryTolerance < conversion.ActualCopperLength)
                    yield return candidate;
            }
        }
    }
}
