using System;
using System.Collections.Generic;
using System.Linq;
using GeometRi;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CKBusbarBridge.Models;
using CKBusbarBridgePreview;
namespace CKBusbarBridge.Prototype
{
    // Adapts frozen, driven cache geometry to the existing planner. No model objects or identities are synthesized.
    internal static class BridgeOfflineUpstreamAdapter
    {
        internal static BridgeUpstreamSnapshot Read(JObject input, string modelPath, string configuration, string material)
        {
            if ((string)input["version"] != "original-design-upstream/1") throw new InvalidOperationException("离线柜体接口版本不支持。");
            var result = (string)input["scenario"] == "face-to-face" ? ReadFacingUpstream(input, modelPath, configuration) : ReadSideUpstream(input, modelPath, configuration);
            var fields = result.Fields.Concat(((string)input["scenario"] == "face-to-wall" ? new[] { "柜端始端" } : new[] { "A柜始端", "B柜始端" }).Select(prefix => new BridgeUpstreamField(prefix + " · 铜排材质", material, "原柜体参数")));
            return new BridgeUpstreamSnapshot(result.ModelName, result.ModelPath, result.Configuration, result.Scenario, fields, result.Endpoints, result.PreviewParts).WithSideToSide(result.SideToSide).WithFaceToWall(result.FaceToWall);
        }
        private static Point3d Point(JToken token) { var values = Numbers(token); return new Point3d(values[0],values[1],values[2]); }
        private static Vector3d Vector(JToken token) { var values = Numbers(token); return new Vector3d(values[0],values[1],values[2]); }
    private static BridgeUpstreamSnapshot ReadFacingUpstream(JObject input, string modelPath, string configuration)
    {
        if ((string)input["scenario"] != "face-to-face") throw new InvalidOperationException("该场景的原联合设计输入适配仍在迁移。");
        var rootCount = (int)input["rootCount"];
        var counts = new BridgeBusbarRootCounts(rootCount - 1);
        var endpoints = new List<BridgeUpstreamEndpoint>();
        var fields = input["fields"].Select(field => new BridgeUpstreamField((string)field["name"], (string)field["value"], (string)field["source"])).ToList();
        foreach (var side in input["sides"])
        {
            var sideName = (string)side["side"];
            var bars = side["extensions"].Select(ReadExtension).ToArray();
            counts.ValidatePhases(bars.Count(bar => bar.Phase == BusbarPhase_e.A), bars.Count(bar => bar.Phase == BusbarPhase_e.B), bars.Count(bar => bar.Phase == BusbarPhase_e.C), bars.Length, sideName);
            var connections = side["connections"].Select(connection => new InsulatorConnectionInfo { ConnectionCircleCenter = Point(connection["center"]), ConnectionCircleRadius = (float)connection["radius"], ConnectionPlane = new Plane3d(Point(connection["center"]), Vector(connection["normal"])) }).OrderBy(connection => connection.ConnectionCircleCenter.Z).ToArray();
            if (connections.Length != 3) throw new InvalidOperationException("基座预装绝缘子必须具有原三相配合面。");
            var bottom = Point(side["bottom"]); var opening = Point(side["opening"]);
            var geometry = new FaceToFaceCabinetEndpointGeometry((float)side["openingHeight"], (float)side["bodyHeight"] + 25d, 41.5d, 0d);
            var supportHeight = geometry.CalculateInsulatorConnectionHeight(bottom.Y + (float)side["baseHeight"]);
            if (Math.Abs(bottom.Y + (float)side["baseHeight"] - (float)side["openingHeight"] / 2d - opening.Y) > SinglePrecision.Tolerance) throw new InvalidOperationException("基座孔口中心与底面、基座高及孔口长度不一致。");
            var phases = new[] { BusbarPhase_e.A, BusbarPhase_e.B, BusbarPhase_e.C };
            var groups = phases.Select(phase => bars.Where(bar => bar.Phase == phase).ToArray()).ToArray();
            var order = Enumerable.Range(0, 3).OrderBy(index => groups[index].Average(bar => bar.TerminalSegment.P2.Z)).ToArray();
            for (var physical = 0; physical < 3; physical++)
            {
                var index = order[physical];
                var routes = CabinetTopExtensionBusbarReader.CreateBaseConnection(groups[index], connections[physical], sideName == "A", opening.X, supportHeight, index, rootCount, out var ordinary, frontBridgeAtB: true);
                if (routes.Count != rootCount) throw new InvalidOperationException("柜端L排生成根数与设计N不一致。");
                routes = routes.OrderBy(route => route.PointList.Last().Y).ThenBy(route => route.PointList.Last().Z).ToArray();
                for (var member = 0; member < routes.Count; member++)
                {
                    var points = routes[member].PointList; var last = points.Last();
                    var referenceLastHeight = routes.Average(route => route.PointList.Last().Y);
                    endpoints.Add(new BridgeUpstreamEndpoint(sideName, phases[index].ToString(), member, points.Select(point => new[] { point.X, point.Y, point.Z }), new[] { opening.X, ordinary.Y + last.Y - referenceLastHeight, last.Z }, routes[member]));
                }
            }
        }
        return new BridgeUpstreamSnapshot((string)input["modelName"], modelPath, configuration, "面对面", fields, endpoints);
    }

    private static ModeledBusbar ReadExtension(JToken extension)
    {
        var free = extension["freeCopper"];
        var original = extension["originalGeometry"];
        var points = original == null ? free["points"].Select(Point).OrderBy(point => point.Y).ToList() : original["pointList"].Select(Point).ToList();
        var width = Vector(original?["widthVector"] ?? free["widthDirection"]).Normalized;
        var tangent = new Vector3d(points.First(), points.Last()).Normalized;
        var shape = new PlanarBendBusbarSetting { IsCenter = true, PointList = points, WidthVector = width, StartBusbarVector = original == null ? tangent.Cross(width).Normalized : Vector(original["startBusbarVector"]).Normalized, BusBarWidth = (float)free["widthMm"], BusBarThickness = (float)free["thicknessMm"], BarBendingRadius = Math.Max(10d, (float)free["thicknessMm"]), StartLapWidth = (float)free["widthMm"], EndLapWidth = (float)free["widthMm"], GroupKey = (string)extension["phase"] };
        var upper = points.OrderBy(point => point.Y).ToList();
        return new ModeledBusbar { SourceIdentity = (string)extension["id"], Phase = (BusbarPhase_e)Enum.Parse(typeof(BusbarPhase_e), (string)extension["phase"]), CenterlineSetting = shape, TerminalSegment = new Segment3d(upper[upper.Count - 2], upper.Last()), CenterPoint = new Point3d(points.Average(point => point.X), points.Average(point => point.Y), points.Average(point => point.Z)) };
    }

    private static BridgeUpstreamSnapshot ReadSideUpstream(JObject input, string modelPath, string configuration)
    {
        var basis = input["coordinates"];
        var coordinates = new BridgeCoordinateFrame(Numbers(basis["origin"]), Numbers(basis["along"]), Numbers(basis["across"]), Numbers(basis["up"]));
        Point3d PlanningPoint(JToken point) { var draft = coordinates.ToDraft(Numbers(point)); return new Point3d(draft[0], draft[2], draft[1]); }
        Vector3d PlanningVector(JToken vector) { var draft = coordinates.ToDraftDirection(Numbers(vector)); return new Vector3d(draft[0], draft[2], draft[1]); }
        SideToSideConnectionGeometry Connection(JToken connection) => new SideToSideConnectionGeometry { Center = PlanningPoint(connection["center"]), SolidCenter = PlanningPoint(connection["solidCenter"]), Normal = PlanningVector(connection["normal"]), Radius = (float)connection["radius"] };
        var counts = new BridgeBusbarRootCounts((int)input["rootCount"] - 1);
        SideToSideCabinetGeometry Cabinet(string sideName)
        {
            var side = input["sides"].Single(row => (string)row["side"] == sideName);
            var extensions = side["extensions"].Select(ReadExtension).ToArray();
            counts.ValidatePhases(extensions.Count(bar => bar.Phase == BusbarPhase_e.A), extensions.Count(bar => bar.Phase == BusbarPhase_e.B), extensions.Count(bar => bar.Phase == BusbarPhase_e.C), extensions.Length, sideName);
            var cabinetGeometry = new SideToSideCabinetGeometry
            {
                PhaseSequence = (string)side["phaseSequence"], Opening = PlanningPoint(side["opening"]), OpeningHeight = (float)side["openingHeight"], BaseWidth = (float)side["baseWidth"], InsulatorWithBracketHeight = (float)side["insulatorWithBracketHeight"],
                ElevatedOrdinaryJoint = side["elevatedOrdinaryJoint"]?.Type == JTokenType.Array ? PlanningPoint(side["elevatedOrdinaryJoint"]) : null,
                Extensions = extensions.Select(bar => new SideToSideExtensionGeometry { Instance = bar.SourceIdentity, Phase = bar.Phase, Shape = coordinates.ToPlanning(bar.CenterlineSetting), TerminalStart = PlanningPoint(JArray.FromObject(new[] { bar.TerminalSegment.P1.X, bar.TerminalSegment.P1.Y, bar.TerminalSegment.P1.Z })), TerminalEnd = PlanningPoint(JArray.FromObject(new[] { bar.TerminalSegment.P2.X, bar.TerminalSegment.P2.Y, bar.TerminalSegment.P2.Z })) }).ToArray(),
                OrdinaryConnections = side["ordinaryConnections"].Select(Connection).ToArray(), ElevatedConnections = side["elevatedConnections"].Select(Connection).ToArray()
            };
            if ((string)input["scenario"] == "face-to-wall")
            {
                var ordered = cabinetGeometry.OrdinaryConnections.OrderByDescending(connection => connection.Center.Z).ToArray();
                if (ordered.Length != 3) throw new InvalidOperationException("面对墙柜端必须具有三个实际连接端位。");
                foreach (var extension in cabinetGeometry.Extensions)
                {
                    var matches = Enumerable.Range(0, ordered.Length).Where(index => Math.Abs(ordered[index].Center.Z - extension.TerminalEnd.Z) <= extension.Shape.BusBarWidth / 2d + SinglePrecision.Tolerance).ToArray();
                    if (matches.Length != 1) throw new InvalidOperationException("柜端伸出排未唯一对应实际绝缘子连接端位。");
                    extension.Phase = (BusbarPhase_e)(matches[0] + 1); extension.Shape.GroupKey = extension.Phase.ToString();
                }
                counts.ValidatePhases(cabinetGeometry.Extensions.Count(bar => bar.Phase == BusbarPhase_e.A), cabinetGeometry.Extensions.Count(bar => bar.Phase == BusbarPhase_e.B), cabinetGeometry.Extensions.Count(bar => bar.Phase == BusbarPhase_e.C), cabinetGeometry.Extensions.Length, "面对墙柜端逻辑端位");
            }
            return cabinetGeometry;
        }
        var objects = input["nominals"].Select(nominal =>
        {
            var pose = Numbers(nominal["pose"]); var local = Numbers(nominal["center"]); var dimensions = Numbers(nominal["dimensions"]);
            var world = Enumerable.Range(0, 3).Select(axis => pose[12 + axis] + pose[axis] * local[0] + pose[4 + axis] * local[1] + pose[8 + axis] * local[2]).ToArray();
            var center = coordinates.ToDraft(world);
            var axes = new[] { pose.Take(3).ToArray(), pose.Skip(4).Take(3).ToArray(), pose.Skip(8).Take(3).ToArray() }.Select(coordinates.ToDraftDirection).ToArray();
            if (axes.Any(axis =>
            {
                var components = axis.Select(value => (float)value).ToArray();
                var norm = (float)Math.Sqrt(components.Sum(value => value * value));
                if (!(norm > 0f) || float.IsInfinity(norm) || Math.Abs(norm - 1f) > SinglePrecision.Tolerance) return true;
                var nearestAxisCosine = components.Max(value => Math.Abs(value)) / norm;
                return (float)(Math.Acos(Math.Min(1f, nearestAxisCosine)) * 180f / (float)Math.PI) > SinglePrecision.Tolerance;
            })) throw new InvalidOperationException("上游固定接口不是原正交模板安装姿态。");
            var size = Enumerable.Range(0, 3).Select(axis => Enumerable.Range(0, 3).Sum(index => Math.Abs(axes[index][axis]) * dimensions[index])).ToArray();
            return new BridgeDesignObject((string)nominal["id"], (string)nominal["name"], (DesignObjectKind)Enum.Parse(typeof(DesignObjectKind), (string)nominal["kind"]), new Dictionary<DesignField, double> { [DesignField.X] = center[0], [DesignField.Y] = center[1], [DesignField.Z] = center[2], [DesignField.Length] = size[0], [DesignField.Width] = size[1], [DesignField.Height] = size[2] }, Array.Empty<DesignField>(), "上游固定接口（只读）");
        }).ToList();
        void FixedCopper(SideToSideExtensionGeometry bar, string id, string name)
        {
            var triangles = FaceToFaceModelBusbarPlanner.CreateFixedInterfacePreviewSolids(bar.Shape).SelectMany(s => new[] { 0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6, 0, 4, 5, 0, 5, 1, 3, 2, 6, 3, 6, 7, 0, 3, 7, 0, 7, 4, 1, 5, 6, 1, 6, 2 }.SelectMany(i => new[] { s[i][0], s[i][2], s[i][1] }));
            objects.Add(new BridgeDesignObject(id, name, DesignObjectKind.FixedBusbar, new Dictionary<DesignField, double>(), Array.Empty<DesignField>(), "实际中心线及加工规格（只读）", phase: bar.Phase == BusbarPhase_e.Unknow ? null : bar.Phase.ToString()).WithModelMesh(new BridgePreviewMesh(triangles)));
        }
        var fields = input["fields"].Select(field => new BridgeUpstreamField((string)field["name"], (string)field["value"], (string)field["source"]));
        if ((string)input["scenario"] == "face-to-wall")
        {
            var serializer = JsonSerializer.Create(BridgeWallPlanningCodec.CreateSettings());
            var bars = input["wallConductors"].Select(bar => new SideToSideExtensionGeometry { Instance = (string)bar["id"], Phase = BusbarPhase_e.Unknow, Shape = coordinates.ToPlanning(bar["shape"].ToObject<PlanarBendBusbarSetting>(serializer)), TerminalStart = PlanningPoint(bar["terminalStart"]), TerminalEnd = PlanningPoint(bar["terminalEnd"]) }).ToArray();
            var corners = input["wallOpeningCorners"].Select(PlanningPoint).ToArray();
            if (corners.Length != 4 || corners.Max(p => p.X) - corners.Min(p => p.X) > SinglePrecision.Tolerance || Math.Abs(corners.Max(p => p.Y) - corners.Min(p => p.Y) - (float)input["wallHeight"]) > SinglePrecision.Tolerance || Math.Abs(corners.Max(p => p.Z) - corners.Min(p => p.Z) - (float)input["wallWidth"]) > SinglePrecision.Tolerance) throw new InvalidOperationException("墙洞实际角点、原驱动宽高或平面方向不一致。");
            if (new[] { corners.Min(p => p.Y), corners.Max(p => p.Y) }.Any(y => new[] { corners.Min(p => p.Z), corners.Max(p => p.Z) }.Any(z => corners.Count(p => Math.Abs(p.Y - y) <= SinglePrecision.Tolerance && Math.Abs(p.Z - z) <= SinglePrecision.Tolerance) != 1))) throw new InvalidOperationException("墙洞四个角点未构成驱动矩形。");
            var installation = input["wallInstallation"];
            var cabinet = Cabinet("Cabinet");
            foreach (var bar in cabinet.Extensions) FixedCopper(bar, "source-" + bar.Instance, "柜端伸出排");
            foreach (var bar in bars.Select((bar, index) => new { bar, index })) FixedCopper(bar.bar, "wall-conductor-" + bar.index, "穿墙套管固定排");
            var wallGeometry = new FaceToWallUpstreamGeometry(coordinates, cabinet, bars, objects, corners, (string)input["position"], null, counts.GeneratedCount, (float)input["clearance"], (float)input["overlapLength"], (float)input["wallCabinetEccentricity"], null, 0d, new FaceToWallInstallationValues((float)installation["Top"], (float)installation["Bottom"], (float)installation["Left"], (float)installation["Right"], (float)installation["EntryDistance"]), (bool)input["isPhaseSwitched"]);
            return new BridgeUpstreamSnapshot((string)input["modelName"], modelPath, configuration, "面对墙", fields).WithFaceToWall(wallGeometry);
        }
        var copper = new SideToSideCopperPlanningInput { A = Cabinet("A"), B = Cabinet("B"), FixedPhaseChange = (bool)input["fixedPhaseChange"], CabinetEccentricity = (float)input["cabinetEccentricity"], PhaseSpacing = (float)input["phaseSpacing"], RootCount = counts.GeneratedCount, Width = (float)input["width"], Thickness = (float)input["thickness"], Clearance = (float)input["clearance"] };
        foreach (var cabinet in new[] { copper.A, copper.B }) foreach (var bar in cabinet.Extensions) FixedCopper(bar, "source-" + bar.Instance, "柜端伸出排");
        var geometry = new SideToSideUpstreamGeometry(coordinates, copper, objects, (float)input["frameInsulatorBodyHeight"], (string)input["position"]);
        return new BridgeUpstreamSnapshot((string)input["modelName"], modelPath, configuration, "侧对侧", fields).WithSideToSide(geometry);
    }
    private static double[] Numbers(JToken value) => value.Select(number => (double)SinglePrecision.Number(number)).ToArray();

    }
}
