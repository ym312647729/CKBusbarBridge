using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteSectionSpecificationPostProcessor
    {
        // A bounded fallback for a nested parallel joint of two bent members.
        // Shift only the two incident straight lines, using the current fixed-leg
        // layer positions as geometric proposals. This is not a new route solver.
        private static bool TryRepackLinkedParallelLapLayers(LinkageRouteCenterlineConversionResult conversion,
            LinkageMultiGroupRouteRequest request, LinkageRouteGroupRequest group,
            Func<bool> validateGeometry, out string failureReason, CancellationToken cancellationToken)
        {
            failureReason="受影响的内部平行搭接层位没有同时满足真实圆弧、闭合及完整实体门禁。";
            var packingSettings=conversion.CenterlineParts
                .Concat(conversion.StackMembers.SelectMany(member=>member.PlanarParts))
                .Concat(conversion.Accessories.Where(item=>item.Setting!=null).Select(item=>item.Setting))
                .Distinct().ToDictionary(part=>part,part=>part.Copy());
            var packingAccessories=conversion.Accessories.ToArray();
            var packingDiagnostics=conversion.Diagnostics.ToArray();
            var packingLength=conversion.ActualCopperLength;
            var packingCommitted=false;
            Action restorePacking=()=>
            {
                RestoreRoundedTrial(packingSettings);
                conversion.Accessories.Clear();
                foreach(var accessory in packingAccessories) conversion.Accessories.Add(accessory);
                conversion.Diagnostics.Clear();
                foreach(var diagnostic in packingDiagnostics) conversion.Diagnostics.Add(diagnostic);
                conversion.ActualCopperLength=packingLength;
            };
            try
            {
                if (!TryCreateProcessingStateFromExistingGeometry(conversion,out var packingState,out failureReason)) return false;
                foreach(var boundaryIndex in packingState.InternalSupportBoundaryIndices.OrderBy(index=>index))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (conversion.DoubleStartPhysicalBridgeSectionIndices.Contains(boundaryIndex) ||
                        conversion.DoubleStartPhysicalBridgeSectionIndices.Contains(boundaryIndex+1) ||
                        !packingState.SettingsBySectionIndex.TryGetValue(boundaryIndex,out var beforeLayers) ||
                        !packingState.SettingsBySectionIndex.TryGetValue(boundaryIndex+1,out var afterLayers) ||
                        beforeLayers.Count<2 || beforeLayers.Count!=afterLayers.Count) continue;
                    var layerPairs=beforeLayers.Select(before=>new {
                        Before=before.Setting,
                        After=afterLayers.SingleOrDefault(after=>after.MemberIndex==before.MemberIndex)?.Setting
                    }).ToList();
                    if (layerPairs.Any(pair=>pair.After==null || pair.Before.PointList.Count!=3 || pair.After.PointList.Count!=3 ||
                        Math.Abs(pair.Before.WidthVector.Normalized.Dot(pair.After.WidthVector.Normalized))<1d-GeometryTolerance ||
                        Math.Abs(new Vector3d(pair.Before.PointList[1],pair.Before.PointList[2]).Normalized.Dot(
                            new Vector3d(pair.After.PointList[0],pair.After.PointList[1]).Normalized))<1d-GeometryTolerance)) continue;
                    foreach(var referencePair in layerPairs)
                    foreach(var packingSign in new[]{-1d,1d})
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        restorePacking();
                        var referenceBefore=packingSettings[referencePair.Before];
                        var referenceAfter=packingSettings[referencePair.After];
                        // Earlier independent corrections can reverse one member's
                        // signed gap. It is not a new choice of interface side:
                        // all nested pairs must use the same selected physical side.
                        var referenceJointNormal=new Vector3d(referenceBefore.PointList[1],referenceBefore.PointList[2])
                            .Normalized.Cross(referenceBefore.WidthVector.Normalized).Normalized;
                        var contactSide=Math.Sign(new Vector3d(referenceBefore.PointList[2],referenceAfter.PointList[0]).Dot(referenceJointNormal));
                        if(contactSide==0) continue;
                        var fixedLegDirection=new Vector3d(referenceAfter.PointList[1],referenceAfter.PointList[2]).Normalized;
                        var fixedLegNormal=fixedLegDirection.Cross(referenceAfter.WidthVector.Normalized).Normalized;
                        var proposalReady=true;
                        foreach(var pair in layerPairs)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (ReferenceEquals(pair.Before,referencePair.Before)) continue;
                            var beforeSource=packingSettings[pair.Before];
                            var afterSource=packingSettings[pair.After];
                            var layerPitch=new Vector3d(referenceAfter.PointList[2],afterSource.PointList[2]).Dot(fixedLegNormal);
                            if (Math.Abs(new Vector3d(afterSource.PointList[1],afterSource.PointList[2]).Normalized.Dot(fixedLegDirection))<1d-GeometryTolerance ||
                                !TryOffsetLinkedPackingCorner(pair.Before,beforeSource,referenceBefore,1,
                                    fixedLegDirection,packingSign*layerPitch,out failureReason) ||
                                !TryOffsetLinkedPackingCorner(pair.After,afterSource,referenceAfter,0,
                                    fixedLegDirection,packingSign*layerPitch,out failureReason))
                            { proposalReady=false; break; }
                        }
                        if (!proposalReady) continue;
                        // A common corner height alone does not establish contact for
                        // slanted legs. Snap each real pair to its original contact side
                        // using the actual half-thickness sum, without moving its far end.
                        foreach(var pair in layerPairs)
                        {
                            var before=pair.Before;
                            var after=pair.After;
                            var jointNormal=new Vector3d(before.PointList[1],before.PointList[2]).Normalized.Cross(before.WidthVector.Normalized).Normalized;
                            var signedDistance=new Vector3d(before.PointList[2],after.PointList[0]).Dot(jointNormal);
                            var requiredDistance=contactSide*(before.BusBarThickness+after.BusBarThickness)/2d;
                            var afterNormal=new Vector3d(after.PointList[0],after.PointList[1]).Normalized.Cross(after.WidthVector.Normalized).Normalized;
                            var orientation=afterNormal.Dot(after.StartBusbarVector.Normalized)<0d?-1d:1d;
                            var projection=afterNormal.Dot(jointNormal)*orientation;
                            if(Math.Abs(projection)<1d-GeometryTolerance || !LinkageRouteStackedCenterlineOffsetter.TryOffset(after,
                                    new[]{(requiredDistance-signedDistance)/projection,0d},true,out var contactPoints,out failureReason))
                            { proposalReady=false; break; }
                            after.PointList=contactPoints;
                        }
                        if(!proposalReady || !TryValidateRoundedLapInterfaces(conversion,request,group,out failureReason)) continue;
                        cancellationToken.ThrowIfCancellationRequested();
                        if(!TryCreateProcessingStateFromExistingGeometry(conversion,out var finalState,out failureReason) ||
                            !TryAddInternalSupportBlocks(conversion,request,group,finalState,out failureReason) ||
                            !TryValidateInternalPhysicalLapClosure(conversion,false,out _,out failureReason) ||
                            !TryValidateRoundedLapInterfaces(conversion,request,group,out failureReason) ||
                            !(validateGeometry?.Invoke()??false)) continue;
                        cancellationToken.ThrowIfCancellationRequested();
                        RefreshRoundedCopperLength(conversion);
                        conversion.Diagnostics.Add("保留逐根圆弧延长，仅联动受影响内部平行直段；真实厚面贴合、实际补块及完整实体校验通过，固定端保持。");
                        packingCommitted=true;
                        return true;
                    }
                }
                return false;
            }
            finally { if(!packingCommitted) restorePacking(); }
        }

        private static bool TryOffsetLinkedPackingCorner(PlanarBendBusbarSetting target,
            PlanarBendBusbarSetting source,PlanarBendBusbarSetting reference,int segmentIndex,
            Vector3d layerDirection,double layerPitch,out string failureReason)
        {
            failureReason=null;
            // Independent miter reconstruction can leave slightly different headings
            // in one stack. Establish one actual parallel interface, rather than
            // comparing three different normals as if they were the same face.
            var jointDirection=new Vector3d(reference.PointList[segmentIndex],reference.PointList[segmentIndex+1]).Normalized;
            var normal=jointDirection.Cross(source.WidthVector.Normalized).Normalized;
            // Layer pitch is a true face-normal distance. Projecting it twice
            // would compress an oblique joint by cos(angle) and create a thin
            // cross-layer penetration even though each paired face touches.
            var layerProjection=layerDirection.Dot(normal);
            if(Math.Abs(layerProjection)<=GeometryTolerance)
            { failureReason="内部层位方向无法提供共同搭接面的厚向距离。"; return false; }
            var distance=new Vector3d(source.PointList[1],reference.PointList[1]).Dot(normal)+layerPitch*Math.Sign(layerProjection);
            var lineOrigin=new Point3d(source.PointList[1].X+normal.X*distance,
                source.PointList[1].Y+normal.Y*distance,source.PointList[1].Z+normal.Z*distance);
            var jointLine=new Line3d(lineOrigin,jointDirection);
            var fixedLeg=segmentIndex==0?new Line3d(source.PointList[1],source.PointList[2]):
                new Line3d(source.PointList[0],source.PointList[1]);
            if(!(jointLine.IntersectionWith(fixedLeg) is Point3d corner))
            { failureReason="内部直段与保持不动的邻接腿无法求交。"; return false; }
            var shifted=source.PointList.Select(point=>point.Copy()).ToList();
            shifted[1]=corner;
            var freePoint=segmentIndex==0?0:2;
            shifted[freePoint]=source.PointList[freePoint].ProjectionTo(jointLine);
            // The nonincident leg and its selected terminal/other joint remain fixed.
            var fixedPoint=segmentIndex==0?2:0;
            if(shifted.Count!=3 || shifted[fixedPoint].DistanceTo(source.PointList[fixedPoint])>GeometryTolerance)
            { failureReason="内部层位联动不能移动未受影响的远端锚点。"; return false; }
            target.PointList=shifted;
            return true;
        }
    }
}
