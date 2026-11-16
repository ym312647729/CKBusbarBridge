using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridgePreview;
using GeometRi;

namespace CKBusbarBridge.Prototype
{
    // Business interfaces are compared by phase/member and original drive geometry, never by mesh bounds.
    internal static class BridgeParameterDraftUpstreamVerification
    {
        internal static void Require(BridgeUpstreamSnapshot preview, BridgeUpstreamSnapshot actual)
        {
            if (preview == null || actual == null || preview.Scenario != actual.Scenario ||
                (preview.SideToSide == null) != (actual.SideToSide == null) || (preview.FaceToWall == null) != (actual.FaceToWall == null))
                throw new InvalidOperationException("实际柜体场景与冻结的参数预览不一致。");
            var materials = new BridgeDraftGenerationPlanFactory();
            if (materials.ReadMaterial(preview) != materials.ReadMaterial(actual)) Fail("铜排材质");
            var expectedEndpoints = preview.Endpoints.OrderBy(endpoint => endpoint.Side).ThenBy(endpoint => endpoint.Phase).ThenBy(endpoint => endpoint.Member).ToArray();
            var actualEndpoints = actual.Endpoints.OrderBy(endpoint => endpoint.Side).ThenBy(endpoint => endpoint.Phase).ThenBy(endpoint => endpoint.Member).ToArray();
            if (expectedEndpoints.Length != actualEndpoints.Length) Fail("固定端排数量");
            for (var index = 0; index < expectedEndpoints.Length; index++)
            {
                var expected = expectedEndpoints[index]; var observed = actualEndpoints[index];
                if (expected.Side != observed.Side || expected.Phase != observed.Phase || expected.Member != observed.Member) Fail("固定端排身份");
                Point(expected.Interface, observed.Interface, "端排连接点");
                Points(expected.Points, observed.Points, "端排中心线");
                Shape(expected.CopyNativePreview(), observed.CopyNativePreview());
            }
            if (preview.SideToSide != null)
            {
                var expected = preview.SideToSide; var observed = actual.SideToSide;
                Coordinates(expected.Coordinates, observed.Coordinates);
                Cabinet(expected.Copper.A, observed.Copper.A); Cabinet(expected.Copper.B, observed.Copper.B);
                if (expected.Copper.FixedPhaseChange != observed.Copper.FixedPhaseChange || expected.Copper.RootCount != observed.Copper.RootCount) Fail("侧对侧换相或根数");
                Number(expected.Copper.Width, observed.Copper.Width, "铜排宽度"); Number(expected.Copper.Thickness, observed.Copper.Thickness, "铜排厚度");
                Number(expected.Copper.Clearance, observed.Copper.Clearance, "电气间隙"); Number(expected.Copper.PhaseSpacing, observed.Copper.PhaseSpacing, "相间距");
                Number(expected.Copper.CabinetEccentricity, observed.Copper.CabinetEccentricity, "柜间偏心");
                Number(expected.FrameInsulatorBodyHeight, observed.FrameInsulatorBodyHeight, "架内绝缘子高度");
            }
            if (preview.FaceToWall != null)
            {
                var expected = preview.FaceToWall; var observed = actual.FaceToWall;
                Coordinates(expected.Coordinates, observed.Coordinates); Cabinet(expected.Cabinet, observed.Cabinet);
                Points(expected.WallOpeningCorners.Select(Values), observed.WallOpeningCorners.Select(Values), "墙洞角点");
                Extensions(expected.WallConductors, observed.WallConductors);
                if (expected.RootCount != observed.RootCount || expected.IsPhaseSwitched != observed.IsPhaseSwitched || expected.Position != observed.Position) Fail("面对墙逻辑端位");
                Number(expected.Clearance, observed.Clearance, "电气间隙"); Number(expected.OverlapLength, observed.OverlapLength, "搭接长度");
                Number(expected.WallCabinetEccentricity, observed.WallCabinetEccentricity, "墙柜偏心");
            }
        }
        private static void Cabinet(SideToSideCabinetGeometry expected, SideToSideCabinetGeometry observed)
        {
            if (expected == null || observed == null || expected.PhaseSequence != observed.PhaseSequence) Fail("柜端相序");
            Point(Values(expected.Opening), Values(observed.Opening), "柜端孔口");
            Number(expected.OpeningHeight, observed.OpeningHeight, "孔口高度"); Number(expected.BaseWidth, observed.BaseWidth, "基座宽度");
            Number(expected.InsulatorWithBracketHeight, observed.InsulatorWithBracketHeight, "绝缘子及支架高度");
            if ((expected.ElevatedOrdinaryJoint == null) != (observed.ElevatedOrdinaryJoint == null)) Fail("增高基座叠装链");
            if (expected.ElevatedOrdinaryJoint != null) Point(Values(expected.ElevatedOrdinaryJoint), Values(observed.ElevatedOrdinaryJoint), "叠装连接点");
            Connections(expected.OrdinaryConnections, observed.OrdinaryConnections); Connections(expected.ElevatedConnections, observed.ElevatedConnections);
            Extensions(expected.Extensions, observed.Extensions);
        }
        private static void Connections(IEnumerable<SideToSideConnectionGeometry> expected, IEnumerable<SideToSideConnectionGeometry> observed)
        {
            var first = (expected ?? Array.Empty<SideToSideConnectionGeometry>()).OrderBy(item => item.Center.Z).ThenBy(item => item.Center.Y).ToArray();
            var second = (observed ?? Array.Empty<SideToSideConnectionGeometry>()).OrderBy(item => item.Center.Z).ThenBy(item => item.Center.Y).ToArray();
            if (first.Length != second.Length) Fail("绝缘子连接端位数量");
            for (var index = 0; index < first.Length; index++)
            { Point(Values(first[index].Center), Values(second[index].Center), "绝缘子连接圆心"); Direction(Values(first[index].Normal), Values(second[index].Normal)); Number(first[index].Radius, second[index].Radius, "连接圆半径"); }
        }
        private static void Extensions(IEnumerable<SideToSideExtensionGeometry> expected, IEnumerable<SideToSideExtensionGeometry> observed)
        {
            Func<IEnumerable<SideToSideExtensionGeometry>, SideToSideExtensionGeometry[]> ordered = items => items.OrderBy(item => item.Phase).ThenBy(item => item.TerminalEnd.Z).ThenBy(item => item.TerminalEnd.Y).ThenBy(item => item.TerminalEnd.X).ToArray();
            var first = ordered(expected); var second = ordered(observed);
            if (first.Length != second.Length) Fail("固定排数量");
            for (var index = 0; index < first.Length; index++)
            {
                if (first[index].Phase != second[index].Phase) Fail("固定排相别");
                Shape(first[index].Shape, second[index].Shape);
                Point(Values(first[index].TerminalStart), Values(second[index].TerminalStart), "固定排末段起点");
                Point(Values(first[index].TerminalEnd), Values(second[index].TerminalEnd), "固定排末段终点");
            }
        }
        private static void Shape(PlanarBendBusbarSetting expected, PlanarBendBusbarSetting observed)
        {
            if (expected == null || observed == null) Fail("固定排加工参数");
            Points(expected.PointList.Select(Values), observed.PointList.Select(Values), "固定排中心线");
            Number(expected.BusBarWidth, observed.BusBarWidth, "固定排宽度"); Number(expected.BusBarThickness, observed.BusBarThickness, "固定排厚度");
            Number(expected.StartLapWidth, observed.StartLapWidth, "始端搭接"); Number(expected.EndLapWidth, observed.EndLapWidth, "末端搭接");
            Direction(Values(expected.WidthVector), Values(observed.WidthVector));
        }
        private static void Coordinates(BridgeCoordinateFrame expected, BridgeCoordinateFrame observed)
        { Point(expected.Origin, observed.Origin, "原点"); Direction(expected.Along, observed.Along); Direction(expected.Across, observed.Across); Direction(expected.Up, observed.Up); }
        private static void Points(IEnumerable<IEnumerable<double>> expected, IEnumerable<IEnumerable<double>> observed, string label)
        {
            var first = expected.Select(point => point.ToArray()).ToArray(); var second = observed.Select(point => point.ToArray()).ToArray();
            if (first.Length != second.Length) Fail(label + "拓扑");
            for (var index = 0; index < first.Length; index++) Point(first[index], second[index], label);
        }
        private static void Point(IEnumerable<double> expected, IEnumerable<double> observed, string label)
        {
            var first = expected.Select(value => (float)value).ToArray(); var second = observed.Select(value => (float)value).ToArray();
            if (first.Length != 3 || second.Length != 3) Fail(label);
            if (first.Concat(second).Any(value => float.IsNaN(value) || float.IsInfinity(value))) Fail(label);
            var dx = first[0] - second[0]; var dy = first[1] - second[1]; var dz = first[2] - second[2];
            if ((float)Math.Sqrt(dx * dx + dy * dy + dz * dz) > SinglePrecision.Tolerance) Fail(label);
        }
        private static void Direction(IEnumerable<double> expected, IEnumerable<double> observed)
        {
            var first = expected.Select(value => (float)value).ToArray(); var second = observed.Select(value => (float)value).ToArray();
            if (first.Length != 3 || second.Length != 3 || first.Concat(second).Any(value => float.IsNaN(value) || float.IsInfinity(value))) Fail("连接方向");
            var dot = first.Zip(second, (left, right) => left * right).Sum();
            var norm = (float)Math.Sqrt(first.Sum(value => value * value) * second.Sum(value => value * value));
            if (!(norm > 0f) || (float)(Math.Acos(Math.Max(-1f, Math.Min(1f, dot / norm))) * 180f / (float)Math.PI) > SinglePrecision.Tolerance) Fail("连接方向");
        }
        private static void Number(double expected, double observed, string label)
        { var delta = (float)expected - (float)observed; if (float.IsNaN(delta) || float.IsInfinity(delta) || Math.Abs(delta) > SinglePrecision.Tolerance) Fail(label); }
        private static double[] Values(Point3d point) => new[] { point.X, point.Y, point.Z };
        private static double[] Values(Vector3d vector) => new[] { vector.X, vector.Y, vector.Z };
        private static void Fail(string label) { throw new InvalidOperationException("实际模型与冻结预览的" + label + "不一致，已停止正式生成。"); }
    }
}
