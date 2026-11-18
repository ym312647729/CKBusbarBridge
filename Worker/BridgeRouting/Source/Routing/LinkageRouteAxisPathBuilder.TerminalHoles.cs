using System;
using System.Collections.Generic;
using System.Linq;
using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteAxisPathBuilder
    {
        private static IReadOnlyList<LinkageRouteTerminalPose> WithHoleCenteredPoses(
            LinkageMultiGroupRouteRequest request, LinkageRouteGroupRequest group,
            bool atStart, IReadOnlyList<LinkageRouteTerminalPose> original)
        {
            if (!LinkageRouteTerminalHoleLapValidator.HasStartHoles(group) || atStart == group.PathSearchIsReversed) return original;
            var terminal=atStart ? group.StartTerminal : group.EndTerminal;
            if (terminal.CabinetBridgeAssembly != null) return original; // 桥端仍由真实接触图落位，不虚构原固定面。
            var lapWidth=atStart ? group.GetStartLapBusbarWidth(request) : group.GetCabinetTopLapBusbarWidth(request);
            var proposals=new List<LinkageRouteTerminalPose>();
            foreach(var pose in original)
            {
                var inward=pose.RouteDirection.Normalized*(atStart ? 1d : -1d);
                var normal=terminal.ThicknessDirection.Normalized;
                var width=normal.Cross(inward);
                if(width.Norm<=GeometryTolerance) continue;
                var length=Math.Max(pose.RequiredTerminalSegmentLength,request.MinimumSegmentLength);
                var template=new PlanarBendBusbarSetting { IsCenter=true,WidthVector=width.Normalized,StartBusbarVector=normal,
                    BusBarWidth=lapWidth,BusBarThickness=group.GetBusbarThickness(request),
                    PointList=new List<Point3d> { pose.LapEndpoint.Copy(),Translate(pose.LapEndpoint,inward*length) } };
                foreach(var member in terminal.Members.Where(item=>item.Holes?.Count>0))
                for(var segment=0;segment+1<member.FixedBusbar.PointList.Count;segment++)
                foreach(var candidate in LinkageRouteTerminalHoleLapGeometry.BuildCenteredLapProposals(
                    member,segment,template,request.RotateSlotHole90Degrees,request.TerminalHoleEdgeDistance)
                    .Concat(request.RequireCenteredTerminalHoleGroups ? Array.Empty<PlanarBendBusbarSetting>() :
                        LinkageRouteTerminalHoleLapGeometry.BuildPerpendicularEdgeLapProposals(
                            member,segment,template,request.RotateSlotHole90Degrees,request.TerminalHoleEdgeDistance)))
                {
                    var newLength=candidate.PointList[0].DistanceTo(candidate.PointList[1]);
                    proposals.Add(new LinkageRouteTerminalPose { LapEndpoint=candidate.PointList[0],RouteDirection=pose.RouteDirection,
                        RequiredTerminalSegmentLength=Math.Max(pose.RequiredTerminalSegmentLength,newLength),
                        PerpendicularLapCoverageSpan=pose.PerpendicularLapCoverageSpan,IsPerpendicularLap=pose.IsPerpendicularLap });
                }
            }
            // 只改变有孔始端的搜索优先级；原允许姿态完整保留，最终静态/端头限制仍全部执行。
            return proposals.Concat(original).GroupBy(pose=>GetSignature(new[] { pose.LapEndpoint })+"|"+
                GetSignature(new[] { new Point3d(pose.RouteDirection.X,pose.RouteDirection.Y,pose.RouteDirection.Z) })+"|"+
                pose.RequiredTerminalSegmentLength.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"|"+
                pose.PerpendicularLapCoverageSpan.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"|"+
                pose.IsPerpendicularLap)
                .Select(poses=>poses.First()).ToList();
        }

        private static Point3d Translate(Point3d origin, Vector3d displacement)
            => new Point3d(origin.X+displacement.X,origin.Y+displacement.Y,origin.Z+displacement.Z);
    }
}
