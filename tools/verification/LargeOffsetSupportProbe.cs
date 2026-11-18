using System;
using System.Collections.Generic;
using System.Linq;
using GeometRi;
using CKBusbarBridge.Prototype;
using CKBusbarBridgePreview;

// 与支撑规划源码一起编译为 UnitTestProject1，复用已有友元权限；纯数值回归不连接 SW 或 Worker。
internal static class LargeOffsetSupportProbe
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static BridgeDesignSample Fixture(string scenario, int direction, int frameCount, double length = 800, bool omitC = false)
    {
        var provider = new PrototypeBusbarBridgeDesignProvider();
        var objects = new List<BridgeDesignObject>(); var routes = new List<BridgeDesignRoute>();
        for (var index = 0; index < frameCount; index++)
        {
            var frame = provider.Create("frame-" + index, "大偏移连接基架" + index, DesignObjectKind.Frame,
                0, direction * index * 1000, 3000, "大偏移Y向连接基架", length, 1000, height: 500);
            var values = frame.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
            values[DesignField.Direction] = direction * 90;
            frame = frame.WithValues(values).WithInsulatorStack(3, 8, 210, 204.5);
            objects.Add(frame);
            for (var slot = 0; slot < 3; slot++) objects.Add(provider.CreateInsulator(frame, slot + 1, (slot - 1) * 210));
        }
        foreach (var phase in (omitC ? new[] { "A", "B" } : new[] { "A", "B", "C" }))
        {
            var phaseIndex = phase[0] - 'A'; var stagger = (phaseIndex - 1) * 210d; var transverse = (1 - phaseIndex) * 210d;
            for (var member = 0; member < 4; member++)
            {
                var local = new[] { new[] { -3000d + stagger, transverse, 3000d + member * 16 },
                    new[] { 3000d + stagger, transverse, 3000d + member * 16 } };
                var world = local.Select(point => new[] { -direction * point[1], direction * point[0], point[2] }).ToArray();
                var ids = new[] { phase + member + "-start", phase + member + "-end" };
                for (var pointIndex = 0; pointIndex < 2; pointIndex++)
                    objects.Add(provider.Create(ids[pointIndex], ids[pointIndex], DesignObjectKind.FixedRoutePoint,
                        world[pointIndex][0], world[pointIndex][1], world[pointIndex][2], "实际中间排", phase: phase));
                var native = new PlanarBendBusbarSetting { IsCenter = true, StartBusbarVector = new Vector3d(0, 1, 0),
                    WidthVector = new Vector3d(-direction, 0, 0), BusBarWidth = 80, BusBarThickness = 8,
                    BarBendingRadius = 10, GroupKey = phase + "-" + member,
                    PointList = world.Select(point => new Point3d(point[0], point[2], point[1])).ToList() };
                routes.Add(new BridgeDesignRoute("route-" + phase + member, phase, ids).WithPhysicalGeometry(80, 8).WithNativePreview(native, 0, 0));
            }
        }
        return new BridgeDesignSample("large-support", new BridgeDesignSource(scenario, "纯数值支撑算例", 80, 8, "本项目", true),
            objects, routes, Array.Empty<BridgeDesignIssue>()).WithCoordinates(BridgeCoordinateFrame.Facing(0, 0), scenario);
    }
    private static void Check(string scenario, int direction, int frames)
    {
        var sample = Fixture(scenario, direction, frames); var planner = new BridgeLargeOffsetSupportPlanner();
        var planned = planner.Apply(sample, sample.Objects);
        foreach (var frame in planned.Where(BridgeLargeOffsetSupportPlanner.IsConnector))
        {
            var members = planned.Where(node => node.ParentId == frame.Id && node.Kind == DesignObjectKind.Insulator).ToArray();
            var active = members.Where(node => !node.IsSuppressedInsulator).ToArray();
            Require(members.Length == 9 && active.Length == 3, "模板兼容位保留九个，实际只能启用三个绝缘子。");
            Require(active.Select(node => node.InsulatorGroupId).Distinct().Count() == 3, "每相必须有独立支架组。");
            Require(active.Select(node => node.X).Distinct().Count() == 3, "三相支架必须沿连接架错开。");
            Require(active.All(node => node.MountPlan.IsFloorStanding && node.MountPlan.BodyHeights.Count(height => height > 0) == 1), "每个支架只启用一个本体。");
            Require(active.Select(node => node.MountPlan.Phases[node.MountGroupIndex]).OrderBy(phase => phase).SequenceEqual(new[] { "A", "B", "C" }), "相序不可重复或遗漏。");
            var input = new BridgeDraftGenerationPlanFactory().FrameInput(frame, planned);
            Require(input.InsulatorCount == 3 && input.InsulatorPositions.Count == 3, "预览与参数导出的安装位必须一致。");
        }
        var repeated = planner.Apply(sample, planned);
        Require(planned.Select(node => node.Id).SequenceEqual(repeated.Select(node => node.Id)), "重复计算不能追加或交换支架实例。");
    }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }
    private static int Main()
    {
        foreach (var scenario in new[] { "FaceToFace", "SideToSideBase2" })
            foreach (var direction in new[] { -1, 1 }) { Check(scenario, direction, 1); Check(scenario, direction, 2); }
        var narrow = Fixture("FaceToFace", 1, 1, 200);
        Reject(() => new BridgeLargeOffsetSupportPlanner().Apply(narrow, narrow.Objects), "安装长度不足不能挤压三相错位。");
        var missing = Fixture("FaceToFace", 1, 1, omitC: true);
        Reject(() => new BridgeLargeOffsetSupportPlanner().Apply(missing, missing.Objects), "缺相不能保留旧三相支架。");
        Console.WriteLine("大偏移单相支撑回归通过。");
        return 0;
    }
}
