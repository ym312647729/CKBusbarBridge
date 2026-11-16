using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 根据已采集的几何快照或坐标系配合构造基架顺序，不访问 SolidWorks COM。
    /// </summary>
    internal static class FaceToFaceFrameChainAnalyzer
    {
        internal const string BaseInstallationCoordinateName = "基座安装坐标系";
        internal const string InletCoordinate1 = "坐标系-进1";
        internal const string InletCoordinate2 = "坐标系-进2";
        internal const string OutletCoordinate1 = "坐标系-出1";
        internal const string OutletCoordinate2 = "坐标系-出2";

        internal static IReadOnlyList<FaceToFaceFrameOrderedInstance> Analyze(
            FaceToFaceFrameTopologySnapshot topology)
        {
            return Analyze(topology, false);
        }

        internal static IReadOnlyList<FaceToFaceFrameOrderedInstance> Analyze(
            FaceToFaceFrameTopologySnapshot topology,
            bool useSpatialFirstFrameSelection)
        {
            if (topology == null)
            {
                throw new ArgumentNullException(nameof(topology));
            }

            var frames = topology.Components.Where(item => item.IsFrame).ToList();
            if (frames.Count == 0)
            {
                return Array.Empty<FaceToFaceFrameOrderedInstance>();
            }

            EnsureUniqueInstanceIds(topology.Components);
            var bases = topology.Components.Where(item => item.IsOrdinaryBase).ToList();
            if (bases.Count != 1)
            {
                throw new InvalidOperationException(
                    $"基架配合链必须且只能包含一个普通基座；当前普通基座实例："
                    + (bases.Count == 0
                        ? "无"
                        : string.Join("、", bases.Select(item => item.InstanceId))));
            }

            if (useSpatialFirstFrameSelection)
            {
                return AnalyzeBySpatialOrder(frames, bases[0]);
            }

            var componentById = topology.Components.ToDictionary(
                item => item.InstanceId,
                StringComparer.OrdinalIgnoreCase);
            var edges = new List<ChainEdge>();
            foreach (var mate in topology.Mates)
            {
                if (!componentById.TryGetValue(mate.FirstComponentId, out var first)
                    || !componentById.TryGetValue(mate.SecondComponentId, out var second))
                {
                    continue;
                }

                var edge = TryNormalizeChainEdge(first, mate.FirstEntityName, second, mate.SecondEntityName);
                if (edge != null)
                {
                    edges.Add(edge);
                }
            }

            EnsureNoFrameCycle(frames, edges);

            var baseId = bases[0].InstanceId;
            var firstEdges = edges
                .Where(edge => string.Equals(edge.ParentId, baseId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (firstEdges.Count != 1)
            {
                throw new InvalidOperationException(
                    $"普通基座必须唯一连接首基架（原基座坐标系标识“{BaseInstallationCoordinateName}”仅用于诊断）；"
                    + $"当前首基架连接数={firstEdges.Count}，候选={FormatIds(firstEdges.Select(item => item.ChildId))}，"
                    + $"已识别基架={FormatIds(frames.Select(item => item.InstanceId))}。" );
            }

            var frameIds = new HashSet<string>(
                frames.Select(item => item.InstanceId),
                StringComparer.OrdinalIgnoreCase);
            var frameEdges = edges.Where(edge => frameIds.Contains(edge.ParentId)).ToList();
            var incomingByFrame = edges
                .Where(edge => frameIds.Contains(edge.ChildId))
                .GroupBy(edge => edge.ChildId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            var outgoingByFrame = frameEdges
                .GroupBy(edge => edge.ParentId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

            var multiplePredecessor = frames.FirstOrDefault(
                frame => incomingByFrame.TryGetValue(frame.InstanceId, out var incoming)
                    && incoming.Count > 1);
            if (multiplePredecessor != null)
            {
                var predecessors = incomingByFrame[multiplePredecessor.InstanceId];
                throw new InvalidOperationException(
                    $"基架“{multiplePredecessor.InstanceId}”存在多个前驱："
                    + string.Join("、", predecessors.Select(item => item.ParentId)) + "。" );
            }

            var branch = frames.FirstOrDefault(
                frame => outgoingByFrame.TryGetValue(frame.InstanceId, out var outgoing)
                    && outgoing.Count > 1);
            if (branch != null)
            {
                var successors = outgoingByFrame[branch.InstanceId];
                throw new InvalidOperationException(
                    $"基架配合链发生分叉：组件“{branch.InstanceId}”的"
                    + $"{string.Join("/", successors.Select(item => item.ParentCoordinate).Distinct())}"
                    + $"连接到 {FormatIds(successors.Select(item => item.ChildId))}。" );
            }

            var missingPredecessors = frames
                .Where(frame => !incomingByFrame.ContainsKey(frame.InstanceId))
                .Select(frame => frame.InstanceId)
                .ToList();
            if (missingPredecessors.Count > 0)
            {
                throw new InvalidOperationException(
                    "基架配合链存在无前驱的孤立基架：" + FormatIds(missingPredecessors) + "。" );
            }

            var ordered = new List<FaceToFaceFrameOrderedInstance>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var currentEdge = firstEdges[0];
            while (currentEdge != null)
            {
                if (!visited.Add(currentEdge.ChildId))
                {
                    throw new InvalidOperationException(
                        $"基架配合链检测到循环或重复访问：{currentEdge.ChildId}。" );
                }

                var component = componentById[currentEdge.ChildId];
                outgoingByFrame.TryGetValue(component.InstanceId, out var outgoing);
                var nextEdge = outgoing?.SingleOrDefault();
                if (nextEdge != null)
                {
                    var expectedOutlet = string.Equals(
                        currentEdge.ChildCoordinate,
                        InletCoordinate2,
                        StringComparison.Ordinal)
                            ? OutletCoordinate2
                            : OutletCoordinate1;
                    if (!string.Equals(nextEdge.ParentCoordinate, expectedOutlet, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"基架“{component.InstanceId}”的入口“{currentEdge.ChildCoordinate}”"
                            + $"与出口“{nextEdge.ParentCoordinate}”不属于同一进出坐标系组。" );
                    }
                }

                ordered.Add(new FaceToFaceFrameOrderedInstance(
                    component,
                    string.Equals(currentEdge.ChildCoordinate, InletCoordinate2, StringComparison.Ordinal),
                    currentEdge.ChildCoordinate,
                    nextEdge?.ParentCoordinate));
                currentEdge = nextEdge;
            }

            // 全覆盖校验阻止孤立组件被静默遗漏，也禁止回退到坐标或实例号排序。
            if (visited.Count != frames.Count)
            {
                var missing = frames
                    .Where(frame => !visited.Contains(frame.InstanceId))
                    .Select(frame => frame.InstanceId);
                throw new InvalidOperationException(
                    $"基架配合链未覆盖全部顶层基架：已遍历 {visited.Count}，已识别 {frames.Count}，"
                    + $"遗漏={FormatIds(missing)}。" );
            }

            return ordered.AsReadOnly();
        }

        private static IReadOnlyList<FaceToFaceFrameOrderedInstance> AnalyzeBySpatialOrder(
            IReadOnlyList<FaceToFaceFrameComponentSnapshot> frames,
            FaceToFaceFrameComponentSnapshot ordinaryBase)
        {
            EnsureValidBoxCenter(ordinaryBase, "普通基座");
            foreach (var frame in frames)
            {
                EnsureValidBoxCenter(frame, "基架");
            }

            // 面对墙的首个基架由主体基座与各基架的 Box 中心距离确定；
            // 基座与首基架没有配合边时，不应因为配合图缺边而阻断母排设置初始化。
            return frames
                .Select(frame => new
                {
                    Frame = frame,
                    Distance = SquaredDistance(
                        ordinaryBase.BoxCenter,
                        frame.BoxCenter)
                })
                .OrderBy(item => item.Distance)
                .ThenBy(
                    item => item.Frame.InstanceId,
                    StringComparer.OrdinalIgnoreCase)
                .Select(item => new FaceToFaceFrameOrderedInstance(
                    item.Frame,
                    false,
                    InletCoordinate1,
                    null))
                .ToList()
                .AsReadOnly();
        }

        private static void EnsureValidBoxCenter(
            FaceToFaceFrameComponentSnapshot component,
            string displayName)
        {
            if (component.BoxCenter == null ||
                component.BoxCenter.Count != 3 ||
                component.BoxCenter.Any(value =>
                    double.IsNaN(value) || double.IsInfinity(value)))
            {
                throw new InvalidOperationException(
                    "面对墙" + displayName + "缺少有效的 Box 中心：" +
                    component.InstanceId + "。 ");
            }
        }

        private static double SquaredDistance(
            IReadOnlyList<double> first,
            IReadOnlyList<double> second)
        {
            var x = first[0] - second[0];
            var y = first[1] - second[1];
            var z = first[2] - second[2];
            return (x * x) + (y * y) + (z * z);
        }

        private static ChainEdge TryNormalizeChainEdge(
            FaceToFaceFrameComponentSnapshot first,
            string firstCoordinate,
            FaceToFaceFrameComponentSnapshot second,
            string secondCoordinate)
        {
            if (first.IsOrdinaryBase && second.IsFrame)
            {
                return CreateBaseEdge(first, firstCoordinate, second, secondCoordinate);
            }

            if (second.IsOrdinaryBase && first.IsFrame)
            {
                return CreateBaseEdge(second, secondCoordinate, first, firstCoordinate);
            }

            if (first.IsFrame && second.IsFrame)
            {
                if (IsOutlet(firstCoordinate) && IsInlet(secondCoordinate))
                {
                    return new ChainEdge(
                        first.InstanceId,
                        firstCoordinate,
                        second.InstanceId,
                        secondCoordinate);
                }

                if (IsOutlet(secondCoordinate) && IsInlet(firstCoordinate))
                {
                    return new ChainEdge(
                        second.InstanceId,
                        secondCoordinate,
                        first.InstanceId,
                        firstCoordinate);
                }

                throw InvalidMate(first, firstCoordinate, second, secondCoordinate);
            }

            return null;
        }

        private static ChainEdge CreateBaseEdge(
            FaceToFaceFrameComponentSnapshot baseComponent,
            string baseCoordinate,
            FaceToFaceFrameComponentSnapshot frame,
            string frameCoordinate)
        {
            // 基座组件身份已经由顶层 RD类型=基座确认，基座内部坐标系名称不参与识别。
            // 保留实际入口用于还原基架顺序和接口组；只有普通基架再解释正反装。
            if (!IsInlet(frameCoordinate))
            {
                throw InvalidMate(baseComponent, baseCoordinate, frame, frameCoordinate);
            }

            // 配合端点在 SolidWorks 中没有固定返回顺序，这里统一转成“父级出口 → 子级入口”。
            return new ChainEdge(
                baseComponent.InstanceId,
                baseCoordinate,
                frame.InstanceId,
                frameCoordinate);
        }

        private static InvalidOperationException InvalidMate(
            FaceToFaceFrameComponentSnapshot first,
            string firstCoordinate,
            FaceToFaceFrameComponentSnapshot second,
            string secondCoordinate)
        {
            return new InvalidOperationException(
                $"基架配合坐标系无效：{first.InstanceId}:{firstCoordinate} ↔ "
                + $"{second.InstanceId}:{secondCoordinate}。" );
        }

        private static bool IsInlet(string coordinateName)
        {
            return string.Equals(coordinateName, InletCoordinate1, StringComparison.Ordinal)
                || string.Equals(coordinateName, InletCoordinate2, StringComparison.Ordinal);
        }

        private static bool IsOutlet(string coordinateName)
        {
            return string.Equals(coordinateName, OutletCoordinate1, StringComparison.Ordinal)
                || string.Equals(coordinateName, OutletCoordinate2, StringComparison.Ordinal);
        }

        private static void EnsureUniqueInstanceIds(
            IEnumerable<FaceToFaceFrameComponentSnapshot> components)
        {
            var duplicate = components
                .GroupBy(item => item.InstanceId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
            {
                throw new InvalidOperationException(
                    $"总装顶层组件实例标识重复，无法构造基架配合链：{duplicate.Key}。" );
            }
        }

        private static void EnsureNoFrameCycle(
            IReadOnlyList<FaceToFaceFrameComponentSnapshot> frames,
            IReadOnlyList<ChainEdge> edges)
        {
            var frameIds = new HashSet<string>(
                frames.Select(item => item.InstanceId),
                StringComparer.OrdinalIgnoreCase);
            var adjacency = edges
                .Where(edge => frameIds.Contains(edge.ParentId) && frameIds.Contains(edge.ChildId))
                .GroupBy(edge => edge.ParentId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(edge => edge.ChildId).ToList(),
                    StringComparer.OrdinalIgnoreCase);
            var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var stack = new List<string>();
            foreach (var frame in frames)
            {
                if (Visit(frame.InstanceId, adjacency, state, stack, out var cycle))
                {
                    throw new InvalidOperationException(
                        "基架配合链存在循环：" + string.Join(" → ", cycle) + "。" );
                }
            }
        }

        private static bool Visit(
            string componentId,
            IReadOnlyDictionary<string, List<string>> adjacency,
            IDictionary<string, int> state,
            IList<string> stack,
            out IReadOnlyList<string> cycle)
        {
            cycle = null;
            if (state.TryGetValue(componentId, out var currentState))
            {
                if (currentState != 1)
                {
                    return false;
                }

                var startIndex = stack.IndexOf(componentId);
                cycle = stack.Skip(Math.Max(0, startIndex)).Concat(new[] { componentId }).ToList();
                return true;
            }

            state[componentId] = 1;
            stack.Add(componentId);
            if (adjacency.TryGetValue(componentId, out var children))
            {
                foreach (var child in children)
                {
                    if (Visit(child, adjacency, state, stack, out cycle))
                    {
                        return true;
                    }
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[componentId] = 2;
            return false;
        }

        private static string FormatIds(IEnumerable<string> ids)
        {
            var values = ids?.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
                ?? Array.Empty<string>();
            return values.Length == 0 ? "无" : string.Join("、", values);
        }

        private sealed class ChainEdge
        {
            internal ChainEdge(
                string parentId,
                string parentCoordinate,
                string childId,
                string childCoordinate)
            {
                ParentId = parentId;
                ParentCoordinate = parentCoordinate;
                ChildId = childId;
                ChildCoordinate = childCoordinate;
            }

            internal string ParentId { get; }

            internal string ParentCoordinate { get; }

            internal string ChildId { get; }

            internal string ChildCoordinate { get; }
        }
    }
}
