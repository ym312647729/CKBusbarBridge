using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteCandidateFactory
    {
        private sealed class LinkedTrialLapInterface
        {
            internal PlanarBendBusbarSetting Target, Neighbor;
            internal bool TargetStart, NeighborStart;
        }
        // Geometry descriptions share the lifetime of the production one-sided box.
        // No string parsing, protocol fields, independent interface algorithm or cache key.
        private static readonly ConditionalWeakTable<LinkageRouteSolid,LinkedTrialLapInterface>
            LinkedTrialLapInterfaces = new ConditionalWeakTable<LinkageRouteSolid,LinkedTrialLapInterface>();

        private static LinkageRouteCandidate TryRepairCriticalLapForLinkedTrial(LinkageRouteCandidate failed,
            LinkageMultiGroupRouteRequest request,IReadOnlyList<LinkageRouteSolid> staticSolids,CancellationToken cancel)
        {
            if (failed?.FailureStage != LinkageRouteFailureStage.Clearance || failed.PlanarConversion?.IsValid != true ||
                cancel.IsCancellationRequested) return null;
            var ordinaryCopper = BuildActualGeneratedMemberSolids(failed,request);
            foreach (var generated in ordinaryCopper)
            foreach (var other in staticSolids.Where(solid=>solid.SolidRole!=LinkageRouteSolidRole.LapClearanceEnvelope &&
                         solid.ClearanceGroupKey!=generated.ClearanceGroupKey))
                if (LinkageRouteSolidDistance.GetMinimumDistance(generated,other) +
                        LinkageRouteGeometryTolerances.FinalAcceptanceComparison < request.InsulationClearance) return null;

            foreach (var envelope in failed.ActualClearanceSolids.Where(solid=>solid.SolidRole==LinkageRouteSolidRole.LapClearanceEnvelope))
            {
                if (!LinkedTrialLapInterfaces.TryGetValue(envelope,out var contact)) continue;
                foreach (var obstacle in staticSolids.Where(solid=>solid.SolidRole!=LinkageRouteSolidRole.LapClearanceEnvelope &&
                             solid.ClearanceGroupKey!=envelope.ClearanceGroupKey))
                {
                    if (!LinkageRouteSolidDistance.HasPositiveVolumeIntersection(envelope,obstacle,
                            LinkageRouteGeometryTolerances.FinalAcceptanceComparison)) continue;
                    var normal=envelope.ThicknessDirection.Normalized;
                    var relative=new Vector3d(envelope.Center,obstacle.Center);
                    var gap=relative.Dot(normal)-LinkedSupport(obstacle,normal)+request.LapInsulationClearance/2d;
                    var deficit=request.LapInsulationClearance-gap;
                    if (deficit<=GeometryTolerance || deficit>Math.Min(contact.Target.BusBarThickness,
                            contact.Neighbor.BusBarThickness)+GeometryTolerance) continue;
                    var contactMember=failed.PlanarConversion.StackMembers.FirstOrDefault(member=>member.PlanarParts.Contains(contact.Target));
                    var contactIndex=contactMember?.PlanarParts.IndexOf(contact.Target)??-1;
                    if (contactIndex<0 || failed.PlanarConversion.DoubleStartPhysicalBridgeSectionIndices.Contains(
                            contact.Target.PhysicalSectionIndex)) continue;
                    // Internal boundaries are movable; selected fixed-end anchors are not.
                    if ((contact.TargetStart && contactIndex==0) || (!contact.TargetStart && contactIndex==contactMember.PlanarParts.Count-1)) continue;
                    var axes=new[]{envelope.LengthDirection.Normalized,envelope.WidthDirection.Normalized};
                    var tangents=axes.SelectMany(axis=>new[]{axis,new Vector3d(-axis.X,-axis.Y,-axis.Z)})
                        .Select(axis=>new { Axis=axis, Distance=LinkedSupport(envelope,axis)+LinkedSupport(obstacle,axis)-relative.Dot(axis) })
                        .Where(item=>item.Distance>GeometryTolerance).OrderBy(item=>item.Distance).ToList();
                    var movements=new List<Tuple<Vector3d,bool>>();
                    if (tangents.Count>0) movements.Add(Tuple.Create(LinkedScale(tangents[0].Axis,-tangents[0].Distance),false));
                    var targetSolid=CreateBoundarySolidForLapEnvelope(contact.Target,contact.TargetStart,failed.GroupRequest);
                    var neighborSolid=CreateBoundarySolidForLapEnvelope(contact.Neighbor,contact.NeighborStart,failed.GroupRequest);
                    var separation=new Vector3d(neighborSolid.Center,targetSolid.Center).Dot(normal);
                    movements.Add(Tuple.Create(LinkedScale(normal,-2d*separation),true));
                    foreach (var movement in movements)
                    {
                        if (cancel.IsCancellationRequested) return null;
                        var trial=CopyLinkedTrialCandidate(failed);
                        var trialMember=trial.PlanarConversion.StackMembers.Single(member=>member.MemberIndex==contactMember.MemberIndex);
                        if (!TryMoveLinkedTrialBoundary(trial.PlanarConversion,trialMember,contactIndex,contact.TargetStart,
                                movement.Item1)) continue;
                        if (!movement.Item2)
                        {
                            var otherMember=failed.PlanarConversion.StackMembers.FirstOrDefault(member=>member.PlanarParts.Contains(contact.Neighbor));
                            if (otherMember==null) continue;
                            if (!TryMoveLinkedTrialBoundary(trial.PlanarConversion,
                                    trial.PlanarConversion.StackMembers.Single(member=>member.MemberIndex==otherMember.MemberIndex),
                                    otherMember.PlanarParts.IndexOf(contact.Neighbor),contact.NeighborStart,movement.Item1)) continue;
                        }
                        if (!SynchronizeLinkedReference(failed.PlanarConversion,trial.PlanarConversion)) continue;
                        LinkageRouteSectionSpecificationPostProcessor.TrySeparateInternalPhysicalLapGaps(trial.PlanarConversion,
                            request,trial.GroupRequest,out _,out _);
                        if (LinkageRouteSectionSpecificationPostProcessor.TryCreateProcessingStateFromExistingGeometry(
                                trial.PlanarConversion,out var state,out _))
                            if (!LinkageRouteSectionSpecificationPostProcessor.TryAddInternalSupportBlocks(trial.PlanarConversion,
                                    request,trial.GroupRequest,state,out _)) continue;
                        trial=ValidatePreparedCandidateCore(trial,request,trial.GroupRequest,staticSolids,cancel);
                        if (trial == null) return null; // Cooperative cancellation is not a failed geometry proposal.
                        if (!trial.IsValid) continue;
                        trial.PlanarConversion.Diagnostics.Add(movement.Item2 ? "临界搭接真实上下换侧后完整候选门禁通过。" :
                            "临界搭接切向移位并联动相邻接口后完整候选门禁通过。");
                        return trial;
                    }
                    // One eligible interface, at most two independent proposals; no recursion.
                    return null;
                }
            }
            return null;
        }

        private static double LinkedSupport(LinkageRouteSolid solid,Vector3d axis) =>
            Math.Abs(solid.LengthDirection.Dot(axis))*solid.HalfLength+
            Math.Abs(solid.WidthDirection.Dot(axis))*solid.HalfWidth+
            Math.Abs(solid.ThicknessDirection.Dot(axis))*solid.HalfThickness;
        private static Vector3d LinkedScale(Vector3d axis,double distance) => new Vector3d(axis.X*distance,axis.Y*distance,axis.Z*distance);

        private static LinkageRouteCandidate CopyLinkedTrialCandidate(LinkageRouteCandidate source)
        {
            var copy=CreateCandidateShell(source.GroupRequest,source.AxisSkeleton.Select(point=>point.Copy()).ToList(),
                source.ManufacturableCenterline.Select(point=>point.Copy()).ToList(),source.IsConvertedShape,
                source.TopologySignature,source.TraversalDirection);
            copy.IdentitySignature=(source.IdentitySignature??string.Empty)+"|LINKED-LAP-TRIAL";
            var from=source.PlanarConversion;
            copy.PlanarConversion=new LinkageRouteCenterlineConversionResult { IsValid=from.IsValid,
                StartStackPlacement=from.StartStackPlacement,EndStackPlacement=from.EndStackPlacement,
                ActualCopperLength=from.ActualCopperLength,UsesSectionSpecificBusbarGeometry=from.UsesSectionSpecificBusbarGeometry,
                SupportReductionGeometryAdjusted=from.SupportReductionGeometryAdjusted,
                USplitContactSide=from.USplitContactSide,USplitWasAttempted=from.USplitWasAttempted,
                OriginalCenterlineSnapshot=from.OriginalCenterlineSnapshot };
            foreach(var part in from.CenterlineParts) copy.PlanarConversion.CenterlineParts.Add(part.Copy());
            var group=source.ToGroupSolution();
            foreach(var member in group.StackMembers) copy.PlanarConversion.StackMembers.Add(member);
            foreach(var accessory in group.Accessories) copy.PlanarConversion.Accessories.Add(accessory);
            foreach(var index in from.StartTerminalMemberIndices) copy.PlanarConversion.StartTerminalMemberIndices.Add(index);
            foreach(var index in from.EndTerminalMemberIndices) copy.PlanarConversion.EndTerminalMemberIndices.Add(index);
            foreach(var index in from.DoubleStartPhysicalBridgeSectionIndices) copy.PlanarConversion.DoubleStartPhysicalBridgeSectionIndices.Add(index);
            return copy;
        }

        private static bool TryMoveLinkedTrialBoundary(LinkageRouteCenterlineConversionResult conversion,
            LinkageStackedRouteMember member,int index,bool atStart,Vector3d delta)
        {
            var part=member.PlanarParts[index];
            if (conversion.DoubleStartPhysicalBridgeSectionIndices.Contains(part.PhysicalSectionIndex)) return false;
            var endIndex=atStart?0:part.PointList.Count-1;
            var direction=new Vector3d(part.PointList[atStart?0:endIndex-1],part.PointList[atStart?1:endIndex]).Normalized;
            if (Math.Abs(delta.Dot(part.WidthVector.Normalized))>GeometryTolerance) return false;
            if (Math.Abs(delta.Dot(direction))>=delta.Norm-GeometryTolerance && part.PointList.Count>2)
            {
                part.PointList[endIndex]=LinkageRouteSolidGeometry.Translate(part.PointList[endIndex],delta.Normalized,delta.Norm);
                return true;
            }
            var indices=atStart?Enumerable.Range(index,member.PlanarParts.Count-index).ToList():Enumerable.Range(0,index+1).Reverse().ToList();
            var chain=new LinkageStackedRouteMember { MemberIndex=member.MemberIndex };
            foreach(var chainIndex in indices)
            {
                if (conversion.DoubleStartPhysicalBridgeSectionIndices.Contains(member.PlanarParts[chainIndex].PhysicalSectionIndex)) break;
                var chainPart=member.PlanarParts[chainIndex].Copy();
                if (!atStart)
                {
                    chainPart.PointList.Reverse();
                    chainPart.StartBusbarVector=new Vector3d(chainPart.PointList[0],chainPart.PointList[1]).Normalized.Cross(chainPart.WidthVector.Normalized).Normalized;
                }
                chain.PlanarParts.Add(chainPart);
            }
            if (!LinkageRouteTerminalAccessoryBuilder.TryMoveStartTerminalLapChain(conversion,chain,0,delta.Normalized,delta.Norm,out _)) return false;
            for(var item=0;item<chain.PlanarParts.Count;item++)
                member.PlanarParts[indices[item]].PointList=(atStart?chain.PlanarParts[item].PointList.AsEnumerable():chain.PlanarParts[item].PointList.AsEnumerable().Reverse())
                    .Select(point=>point.Copy()).ToList();
            return true;
        }

        private static bool SynchronizeLinkedReference(LinkageRouteCenterlineConversionResult before,LinkageRouteCenterlineConversionResult after)
        {
            foreach(var reference in after.CenterlineParts)
            {
                var original=before.StackMembers.SelectMany(member=>member.PlanarParts).FirstOrDefault(part=>part.PhysicalSectionIndex==reference.PhysicalSectionIndex);
                if(original==null) return false;
                var memberIndex=before.StackMembers.First(member=>member.PlanarParts.Contains(original)).MemberIndex;
                var changed=after.StackMembers.Single(member=>member.MemberIndex==memberIndex).PlanarParts
                    .Single(part=>part.PhysicalSectionIndex==reference.PhysicalSectionIndex);
                if(original.PointList.Count!=changed.PointList.Count || reference.PointList.Count!=changed.PointList.Count) return false;
                for(var point=0;point<reference.PointList.Count;point++)
                {
                    var delta=new Vector3d(original.PointList[point],changed.PointList[point]);
                    reference.PointList[point]=new Point3d(reference.PointList[point].X+delta.X,reference.PointList[point].Y+delta.Y,reference.PointList[point].Z+delta.Z);
                }
            }
            return true;
        }
    }
}
