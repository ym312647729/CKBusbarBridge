using System;
using System.Collections.Generic;
using System.Linq;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;

namespace RouteLinkBar.Algorithm.Routing
{
    public static partial class LinkageSchemeLibraryRouteRebuilder
    {
        internal static bool TryValidateHistoricalFixedClearance(LinkageMultiGroupRouteRequest request,
            out string diagnostic)
        {
            if (!LinkageRouteCandidateFactory.TryBuildFixedAndObstacleSolids(request,
                out var fixedAndObstacleSolids, out diagnostic)) return false;
            var fixedSolids = fixedAndObstacleSolids.Where(solid => !solid.IsForbiddenTerminalRegion &&
                solid.SolidRole != LinkageRouteSolidRole.InputObstacle &&
                solid.SolidRole != LinkageRouteSolidRole.ForbiddenTerminalRegion).ToList();
            for (var firstIndex = 0; firstIndex < fixedSolids.Count; firstIndex++)
            for (var secondIndex = firstIndex + 1; secondIndex < fixedSolids.Count; secondIndex++)
            {
                var firstFixed = fixedSolids[firstIndex];
                var secondFixed = fixedSolids[secondIndex];
                if (string.Equals(firstFixed.ClearanceGroupKey, secondFixed.ClearanceGroupKey,
                    StringComparison.OrdinalIgnoreCase)) continue;
                var fixedDistance = LinkageRouteSolidDistance.GetMinimumDistance(firstFixed, secondFixed);
                if (LinkageRouteSolidDistance.HasPositiveVolumeIntersection(firstFixed, secondFixed,
                        LinkageRouteGeometryTolerances.FinalAcceptanceComparison) ||
                    fixedDistance + LinkageRouteGeometryTolerances.FinalAcceptanceComparison < request.InsulationClearance)
                {
                    diagnostic = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "输入固定端 {0}/{1} 与 {2}/{3} 的净距 {4:F6}mm 不满足 {5:F6}mm 要求，或存在超差重叠；固定端不可由匹配移动。",
                        firstFixed.GroupKey, firstFixed.SolidRole, secondFixed.GroupKey, secondFixed.SolidRole,
                        fixedDistance, request.InsulationClearance);
                    return false;
                }
            }
            diagnostic = null;
            return true;
        }

        // 仅匹配历史实体：共享源节点连接的遍历属于一个物理存储分量，不代表叠排层数。
        // 不同身份即使几何同位也绝不合并；每条原始遍历保存到唯一实体列表的映射。
        private static bool TryCollectHistoricalBranchComponents(IList<DirectPlacedRoute> traversals,
            out List<DirectPlacedRoute> physicalRoutes, out string diagnostic)
        {
            physicalRoutes = new List<DirectPlacedRoute>();
            diagnostic = null;
            var pendingTraversals = traversals.ToList();
            while (pendingTraversals.Count > 0)
            {
                var componentTraversals = new List<DirectPlacedRoute> { pendingTraversals[0] };
                pendingTraversals.RemoveAt(0);
                var sourceKeys = new HashSet<Tuple<string, int>>(HistoricalSourceKeys(componentTraversals[0]));
                bool expanded;
                do
                {
                    var connected = pendingTraversals.Where(route => HistoricalSourceKeys(route)
                        .Any(sourceKeys.Contains)).ToList();
                    expanded = connected.Count > 0;
                    foreach (var route in connected)
                    {
                        pendingTraversals.Remove(route);
                        componentTraversals.Add(route);
                        sourceKeys.UnionWith(HistoricalSourceKeys(route));
                    }
                } while (expanded);
                var physicalRoute = new DirectPlacedRoute { GroupKey = componentTraversals[0].GroupKey,
                    RouteKey = componentTraversals[0].RouteKey, OutputMemberIndex = physicalRoutes.Count };
                var sourceIndices = new Dictionary<Tuple<string, int>, int>();
                foreach (var traversal in componentTraversals)
                {
                    var keys = HistoricalSourceKeys(traversal).ToList();
                    if (keys.Count != traversal.Settings.Count || keys.Any(key => string.IsNullOrWhiteSpace(key.Item1)))
                    { diagnostic = "历史支路缺少一一对应的源实体身份。"; return false; }
                    traversal.OutputMemberIndex = physicalRoute.OutputMemberIndex;
                    traversal.OutputPartIndices.Clear();
                    traversal.SharesPhysicalRoute = componentTraversals.Count > 1;
                    for (var position = 0; position < keys.Count; position++)
                    {
                        var setting = traversal.Settings[position];
                        if (!sourceIndices.TryGetValue(keys[position], out var physicalIndex))
                        {
                            physicalIndex = physicalRoute.Settings.Count;
                            sourceIndices.Add(keys[position], physicalIndex);
                            physicalRoute.Settings.Add(setting);
                        }
                        else if (!SameHistoricalPlacedEntity(setting, physicalRoute.Settings[physicalIndex]))
                        {
                            diagnostic = "共享历史铜排的多始端落位约束不一致：" + keys[position].Item1 +
                                "，路径 " + traversal.RouteKey;
                            return false;
                        }
                        traversal.OutputPartIndices.Add(physicalIndex);
                    }
                }
                physicalRoutes.Add(physicalRoute);
            }
            return true;
        }

        private static IEnumerable<Tuple<string, int>> HistoricalSourceKeys(DirectPlacedRoute traversal) =>
            traversal.SourceRoute.OrderedMembers.Where(member => member.PositionType == MiddlePositionType)
                .Select(member => Tuple.Create(member.MemberKey, member.SourceBusbarIndex));

        private static bool SameHistoricalPlacedEntity(PlanarBendBusbarSetting current, PlanarBendBusbarSetting previous) =>
            Math.Abs(current.BusBarWidth - previous.BusBarWidth) <= DirectPlacementTolerance &&
            Math.Abs(current.BusBarThickness - previous.BusBarThickness) <= DirectPlacementTolerance &&
            (current.WidthVector - previous.WidthVector).Norm <= DirectPlacementTolerance &&
            (current.StartBusbarVector - previous.StartBusbarVector).Norm <= DirectPlacementTolerance &&
            current.PointList.Count == previous.PointList.Count &&
            current.PointList.Zip(previous.PointList, (a, b) => a.DistanceTo(b) <= DirectPlacementTolerance).All(same => same);

        internal static bool TryValidateHistoricalContactGraph(LinkageRouteGroupRequest request,
            LinkageGroupRouteSolution output, out string diagnostic)
        {
            diagnostic = null;
            var parts = output.StackMembers.SelectMany(member => member.PlanarParts)
                .Concat(output.Accessories.Select(accessory => accessory.Setting)).ToList();
            var starts = request.StartTerminal.Members.Select(member => member.FixedBusbar).ToList();
            var ends = request.EndTerminal.Members.Select(member => member.FixedBusbar).ToList();
            if (parts.Count == 0 || starts.Count == 0 || ends.Count == 0 || parts.Any(part => part == null))
            { diagnostic = "历史搭接图缺少铜排或固定端。"; return false; }
            // 从全部柜顶反向扩展，所有生成排、附件及每个始端必须连通；不把所有生成排预设为已连接。
            var reached = new HashSet<PlanarBendBusbarSetting>(ends);
            var remaining = new HashSet<PlanarBendBusbarSetting>(parts.Concat(starts));
            var generatedParts = new HashSet<PlanarBendBusbarSetting>(parts);
            var historicalBlocks = new HashSet<PlanarBendBusbarSetting>(output.Accessories
                .Where(IsHistoricalInterfaceSupportBlock).Select(accessory => accessory.Setting));
            bool HaveActualContact(PlanarBendBusbarSetting left, PlanarBendBusbarSetting right) =>
                historicalBlocks.Contains(left) || historicalBlocks.Contains(right)
                    ? TryGetHistoricalSupportBlockContact(left, right, historicalBlocks.Contains(left), historicalBlocks.Contains(right), out _, out _, out _)
                    : HasHistoricalTerminalContact(left, right);
            bool advanced;
            do
            {
                var neighbors = remaining.Where(part => reached.Any(other =>
                    (generatedParts.Contains(part) || generatedParts.Contains(other)) &&
                    HaveActualContact(part, other))).ToList();
                advanced = neighbors.Count > 0;
                foreach (var neighbor in neighbors) { remaining.Remove(neighbor); reached.Add(neighbor); }
            } while (advanced);
            if (remaining.Count > 0 || ends.Any(end => !parts.Any(part => HaveActualContact(part, end))))
            { diagnostic = "组 " + output.GroupKey + " 的历史支路存在未连通的铜排或未覆盖固定端。"; return false; }
            return true;
        }
    }
}
