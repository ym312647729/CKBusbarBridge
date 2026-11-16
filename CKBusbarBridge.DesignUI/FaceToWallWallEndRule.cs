using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridge.AlgorithmWorker;
using CKBusbarBridge.DesignGeometry;
using CKBusbarBridge.Models;
using CKBusbarBridge.Prototype;
using CKBusbarBridgePreview;
using GeometRi;

namespace CKBusbarBridge.Services
{
    internal sealed class FaceToWallWallEndRule
    {
        internal const double ClearanceMm = 150d;
        private const double Tolerance = .01d;

        internal string FindProblem(FaceToWallUpstreamGeometry input, LinkageRouteWorkerRequestDto request,
            IEnumerable<PlanarBendBusbarSetting> copper)
        {
            if (input == null || request?.Groups == null || request.Groups.Count != 3 ||
                !request.Groups.Select(group => group?.Phase).OrderBy(phase => phase)
                    .SequenceEqual(new[] { "A", "B", "C" }))
                return "墙端预留缺少真实桩头或三相Worker接口，请重新规划铜排。";
            var bars = copper?.ToArray() ?? Array.Empty<PlanarBendBusbarSetting>();
            foreach (var group in request.Groups)
            {
                var terminal = group.EndTerminal;
                if (terminal?.LengthDirection == null || terminal.Members == null || terminal.Members.Count == 0)
                    return group.Phase + "相墙端缺少有效终端，请重新规划铜排。";
                var direction = Vector(terminal.LengthDirection);
                if (!Finite(direction.X) || !Finite(direction.Y) || !Finite(direction.Z) || direction.Norm < Tolerance)
                    return group.Phase + "相墙端方向无效。";
                direction = direction.Normalized;
                var realPlanes = new List<double>();
                foreach (var member in terminal.Members)
                {
                    var fixedBar = member?.FixedBusbar;
                    if (fixedBar?.PointList == null || fixedBar.PointList.Count < 2 ||
                        fixedBar.PointList.Any(point => point == null || !Finite(point.X) || !Finite(point.Y) || !Finite(point.Z)) ||
                        !Finite(fixedBar.EndLapWidth) || fixedBar.EndLapWidth <= 0)
                        return group.Phase + "相墙端虚拟桩头无效。";
                    // 虚拟加长仅改变最后一点，其余原始点用于唯一关联真实桩头，不按距离猜测。
                    var matches = input.WallConductors.SelectMany(source => MatchRealEnds(source.Shape, fixedBar)).ToArray();
                    if (matches.Length != 1)
                        return group.Phase + "相虚拟终端未唯一对应真实桩头，请重新读取并规划铜排。";
                    var real = matches[0];
                    var virtualEnd = Vector(fixedBar.PointList.Last());
                    var expected = new Vector3d(real.X, real.Y, real.Z) - direction * (fixedBar.EndLapWidth + ClearanceMm);
                    if ((virtualEnd - expected).Norm > Tolerance)
                        return group.Phase + "相墙端方案未按搭接长度+150mm虚拟加长，请重新规划铜排。";
                    realPlanes.Add(Project(real, direction));
                }
                if (realPlanes.Max() - realPlanes.Min() > Tolerance)
                    return group.Phase + "相真实桩头端面不共面，无法保证逐根端面净距150mm。";
                var target = realPlanes[0] - ClearanceMm;
                var width = Vector(terminal.Members[0].FixedBusbar.WidthVector).Normalized;
                var across = terminal.Members.Average(member => Vector(member.FixedBusbar.PointList.Last()).Dot(width));
                var thickness = Vector(terminal.ThicknessDirection).Normalized;
                var levels = terminal.Members.Select(member => Vector(member.FixedBusbar.PointList.Last()).Dot(thickness)).ToArray();
                var phaseBars = bars.Where(bar => Phase(bar.GroupKey) == group.Phase).ToArray();
                // 中间排也可能止于同一桥向平面，只认对准套管板横向中心且处于夹排层位范围的直头。
                var ends = phaseBars.Where(bar => bar.PointList != null && bar.PointList.Count >= 2 &&
                    Math.Abs(Project(bar.PointList.Last(), direction) - target) <= Tolerance &&
                    Math.Abs(Project(bar.PointList.Last(), width) - across) <= Tolerance &&
                    Project(bar.PointList.Last(), thickness) >= levels.Min() - group.BusbarThickness - Tolerance &&
                    Project(bar.PointList.Last(), thickness) <= levels.Max() + group.BusbarThickness + Tolerance).ToArray();
                if (ends.Length != group.StackCount || phaseBars.Any(bar => !WithinWallPlane(bar, direction, target)))
                    return group.Phase + "相铜排实际端面净距不满足150mm，请重新规划铜排。";
                foreach (var bar in ends)
                {
                    var tangent = new Vector3d(bar.PointList[bar.PointList.Count - 2], bar.PointList.Last());
                    if (!bar.IsCenter || tangent.Norm < Tolerance || tangent.Normalized.Dot(direction) < .999999d ||
                        bar.WidthVector == null || !Finite(bar.WidthVector.Norm) || bar.WidthVector.Norm < Tolerance ||
                        Math.Abs(bar.WidthVector.Normalized.Dot(direction)) > .000001d)
                        return group.Phase + "相铜排末段方向不能形成桥向150mm端面净距。";
                }
            }
            return null;
        }

        private bool WithinWallPlane(PlanarBendBusbarSetting bar, Vector3d direction, double target)
        {
            try
            {
                // 使用已核验路径构造的实体投影，横向排的宽度和非水平排的厚度都不能侵占150mm。
                var solids = new BridgeRaisedLapGeometry().CreateStructureSolids(bar, new Vector3d(0, 1, 0)) ??
                    BridgeCopperClearance.UiLongitudinalSolids(bar);
                return solids.Count > 0 && solids.All(solid =>
                    Project(solid.Center, direction) + Math.Abs(solid.LengthDirection.Dot(direction)) * solid.HalfLength +
                    Math.Abs(solid.WidthDirection.Dot(direction)) * solid.HalfWidth +
                    Math.Abs(solid.ThicknessDirection.Dot(direction)) * solid.HalfThickness <= target + Tolerance);
            }
            // 残缺或不支持的几何按预留校验失败返回，不能以中心线替代实体放行。
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        internal string FindProblem(FaceToWallUpstreamGeometry input, BridgeDesignSample sample)
        {
            if (string.IsNullOrWhiteSpace(sample.WallPlanningRecipe))
                return "面对墙方案缺少墙端预留记录，请重新规划铜排。";
            var stored = new BridgeWallPlanningCodec().DecodeWallPlanning(sample.WallPlanningRecipe);
            if (stored.IsPhaseSwitched != input.IsPhaseSwitched)
                return "面对墙“是否换相”参数已变化，请重新规划铜排后生成。";
            return FindProblem(input, stored.Request, sample.Routes.Where(route => route.IsPhysicalGeometry)
                .Select(route => route.CopyNativePreview()).Where(bar => bar != null)
                .Select(bar => input.Coordinates.ToPlanning(bar)));
        }

        private IEnumerable<Point3d> MatchRealEnds(PlanarBendBusbarSetting real, LinkageRouteWorkerPlanarSettingDto virtualBar)
        {
            if (real?.PointList == null || !real.IsCenter || real.PointList.Count != virtualBar.PointList.Count ||
                real.PointList.Any(point => point == null || !Finite(point.X) || !Finite(point.Y) || !Finite(point.Z))) yield break;
            for (var reverse = 0; reverse < 2; reverse++)
            {
                var points = reverse == 0 ? real.PointList.ToArray() : real.PointList.AsEnumerable().Reverse().ToArray();
                if (points.Take(points.Length - 1).Where((point, index) =>
                    (new Vector3d(point.X, point.Y, point.Z) - Vector(virtualBar.PointList[index])).Norm > Tolerance).Any()) continue;
                yield return points.Last();
            }
        }

        private string Phase(string key) => new[] { "A", "B", "C" }.FirstOrDefault(phase =>
            string.Equals(key, phase, StringComparison.OrdinalIgnoreCase) ||
            key?.StartsWith(phase + "-", StringComparison.OrdinalIgnoreCase) == true ||
            key?.EndsWith("-" + phase, StringComparison.OrdinalIgnoreCase) == true);
        private bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private Vector3d Vector(LinkageRouteWorkerCoordinateDto point) => new Vector3d(point.X, point.Y, point.Z);
        private double Project(Point3d point, Vector3d direction) => point.X * direction.X + point.Y * direction.Y + point.Z * direction.Z;
    }
}
