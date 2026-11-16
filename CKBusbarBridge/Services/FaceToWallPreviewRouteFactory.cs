using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridge.Prototype;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.Services
{
    // 算法分件名称用于规划；预览路由保留既有生成身份契约，二者不能直接混用。
    internal sealed class FaceToWallPreviewRouteFactory
    {
        internal List<BridgeDesignRoute> Create(FaceToWallUpstreamGeometry input,
            IReadOnlyList<AlgorithmBusbarGenerationGroup> groups, List<BridgeDesignObject> objects)
            => Create(input, groups, objects, null);

        internal List<BridgeDesignRoute> Create(FaceToWallUpstreamGeometry input,
            IReadOnlyList<AlgorithmBusbarGenerationGroup> groups, List<BridgeDesignObject> objects,
            Func<int, int, int?> copperSectionIndex)
        {
            var routes = new List<BridgeDesignRoute>();
            var part = 0;
            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                var group = groups[groupIndex];
                var identity = GenerationIdentity(group.DisplayName);
                for (var barIndex = 0; barIndex < group.Busbars.Count; barIndex++)
                {
                    var original = group.Busbars[barIndex];
                    var shape = BridgeNativePreviewCopy.Copy(original);
                    var phase = new[] { "A", "B", "C" }.FirstOrDefault(p => string.Equals(shape.GroupKey, p, StringComparison.OrdinalIgnoreCase) ||
                        shape.GroupKey?.StartsWith(p + "-", StringComparison.OrdinalIgnoreCase) == true ||
                        shape.GroupKey?.EndsWith("-" + p, StringComparison.OrdinalIgnoreCase) == true);
                    if (phase == null) throw new InvalidOperationException("面对墙铜排产物缺少明确三相身份。");
                    var ids = new List<string>();
                    for (var i = 0; i < shape.PointList.Count; i++)
                    {
                        var p = shape.PointList[i]; var id = "wall-copper-" + part + "-" + i;
                        objects.Add(new BridgeDesignObject(id, phase + "相实际加工点", DesignObjectKind.FixedRoutePoint,
                            new Dictionary<DesignField, double> { [DesignField.X] = p.X, [DesignField.Y] = p.Z, [DesignField.Z] = p.Y },
                            Array.Empty<DesignField>(), group.DisplayName == "算法中间段" || group.DisplayName == "特殊段入口桥头" ? "Worker特殊段加工几何" : "既有规则普通段加工几何", phase: phase));
                        ids.Add(id);
                    }
                    var route = new BridgeDesignRoute("wall-route-" + part, phase, ids)
                        .WithPhysicalGeometry(shape.BusBarWidth, shape.BusBarThickness,
                            FaceToFaceModelBusbarPlanner.CreateFixedInterfacePreviewSolids(shape)
                                .Select(s => s.Select(p => new[] { p[0], p[2], p[1] }).ToArray()))
                        .WithNativePreview(shape, input.Coordinates)
                        .WithGenerationGroup(identity, group.IsBridge);
                    var sectionIndex = copperSectionIndex?.Invoke(groupIndex, barIndex);
                    if (sectionIndex.HasValue) route = route.WithCopperSection(sectionIndex.Value);
                    routes.Add(route);
                    part++;
                }
            }
            return routes;
        }

        private string GenerationIdentity(string name)
        {
            // 面对墙只有一个柜端，复用现有A端身份；不扩大正式生成契约的合法值集合。
            if (name == "柜端连接段") return "A柜连接段";
            if (name == "算法始端普通段" || name == "算法中间段" || name == "特殊段入口桥头") return "中间排";
            if (name?.StartsWith("普通段", StringComparison.Ordinal) == true &&
                int.TryParse(name.Substring(3), NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index > 0)
                return "中间排";
            throw new InvalidOperationException("面对墙铜排规划分组未定义生成身份：" + (name ?? "<空>"));
        }
    }
}
