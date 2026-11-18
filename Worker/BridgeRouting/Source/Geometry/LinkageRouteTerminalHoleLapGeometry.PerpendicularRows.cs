using System;
using System.Collections.Generic;
using System.Linq;
using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;

namespace RouteLinkBar.Algorithm.Geometry
{
    public static partial class LinkageRouteTerminalHoleLapGeometry
    {
        // “下面”由用户确认取不可伸出方向的反向端，与世界Z、点序及视角无关。
        // 缺少既有方向时保持原孔组偏好，不猜测或增加输入字段。
        private static bool TryGetFreeEndHoleRowOffset(LinkageTerminalMember member, int sourceIndex,
            PlanarBendBusbarSetting target, int targetIndex, out double preferredOffset)
        {
            preferredOffset=0d;
            var forbidden=member?.ForbiddenHorizontalExitDirection;
            if(forbidden==null || !Finite(forbidden.Norm) || forbidden.Norm<=Precision ||
                !LinkageRouteRoundedLapGeometry.TryGetFlatSegment(member.FixedBusbar,sourceIndex,out var source,out _) ||
                !LinkageRouteRoundedLapGeometry.TryGetFlatSegment(target,targetIndex,out var destination,out _)) return false;
            var freeAxis=-forbidden.Normalized;
            var targetWidth=target.WidthVector.Normalized;
            var targetNormal=destination.Direction.Cross(targetWidth).Normalized;
            if(Math.Abs(source.Direction.Dot(destination.Direction))>LinkageRouteGeometryTolerances.AssemblyDirection ||
                freeAxis.Cross(source.Direction).Norm>LinkageRouteGeometryTolerances.AssemblyDirection ||
                targetNormal.Cross(source.Direction.Cross(member.FixedBusbar.WidthVector.Normalized).Normalized).Norm>
                    LinkageRouteGeometryTolerances.AssemblyDirection) return false;
            Func<Point3d,Point2> project=point=>new Point2(new Vector3d(destination.Start,point).Dot(destination.Direction),
                new Vector3d(destination.Start,point).Dot(targetWidth));
            var sourceWidth=member.FixedBusbar.WidthVector.Normalized;
            var first=Move(source.Start,source.Direction,source.StartTrim);
            var last=Move(source.End,source.Direction,-source.EndTrim);
            var halfWidth=member.FixedBusbar.BusBarWidth/2d;
            var rectangle=new List<Point2> { project(Move(first,sourceWidth,-halfWidth)),
                project(Move(last,sourceWidth,-halfWidth)),project(Move(last,sourceWidth,halfWidth)),
                project(Move(first,sourceWidth,halfWidth)) };
            var samples=new List<Tuple<double,double>>();
            foreach(var hole in member.Holes ?? new List<LinkageTerminalHoleInfo>())
            {
                if(hole?.PlaneNormal!=null && hole.PlaneNormal.Norm>Precision &&
                    hole.PlaneNormal.Normalized.Cross(targetNormal).Norm>LinkageRouteGeometryTolerances.AssemblyDirection) continue;
                if(!TryGetCapsule(hole,out var arcFirst,out var arcSecond,out var radius) ||
                    !CapsuleIntersectsPolygon(project(arcFirst),project(arcSecond),radius+Acceptance,rectangle)) continue;
                // 腰孔两弧心中点也是90度旋转前后的孔心；不同孔径不改变孔心线。
                var midpoint=new Point3d((arcFirst.X+arcSecond.X)/2d,(arcFirst.Y+arcSecond.Y)/2d,(arcFirst.Z+arcSecond.Z)/2d);
                samples.Add(Tuple.Create(new Vector3d(source.Start,midpoint).Dot(freeAxis),project(midpoint).Y));
            }
            if(samples.Count==0) return false;
            var ordered=samples.OrderByDescending(sample=>sample.Item1).ToList();
            var rowCenters=new List<double>();
            for(var index=0;index<ordered.Count && rowCenters.Count<2;)
            {
                var rowRank=ordered[index].Item1;
                var rowMinimum=ordered[index].Item2;
                var rowMaximum=rowMinimum;
                do
                {
                    rowMinimum=Math.Min(rowMinimum,ordered[index].Item2);
                    rowMaximum=Math.Max(rowMaximum,ordered[index].Item2);
                    index++;
                } while(index<ordered.Count && Math.Abs(ordered[index].Item1-rowRank)<=Precision);
                rowCenters.Add((rowMinimum+rowMaximum)/2d);
            }
            preferredOffset=rowCenters.Average();
            return true;
        }
    }
}
