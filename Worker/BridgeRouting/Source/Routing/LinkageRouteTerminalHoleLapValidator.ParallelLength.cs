using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteTerminalHoleLapValidator
    {
        // 与孔是否存在无关；只量物理始端自由端的实际平直重合，不把伸到固定排外的长度算进去。
        private static List<Violation> FindParallelLengthViolations(LinkageMultiGroupRouteRequest request,
            LinkageRouteGroupRequest group, LinkageRouteCenterlineConversionResult conversion)
        {
            var violations = new List<Violation>();
            if (request == null || group == null || conversion == null) return violations;
            var required = group.GetPhysicalStartParallelLapLength(request, !group.PathSearchIsReversed);
            var fixedMembers = PhysicalStart(group)?.Members;
            if (required <= Precision || fixedMembers == null) return violations;
            var supportFaces = new List<Face>();
            foreach (var accessory in conversion.Accessories.Where(item => item.Kind == LinkageRouteAccessoryKind.SupportBlock))
                AddFaces(supportFaces, accessory.Setting, true);
            foreach (var stack in conversion.StackMembers)
            {
                var terminalPart = group.PathSearchIsReversed ? stack.PlanarParts.LastOrDefault() : stack.PlanarParts.FirstOrDefault();
                if (terminalPart?.PointList == null) continue;
                var targetIndex = group.PathSearchIsReversed ? terminalPart.PointList.Count - 2 : 0;
                if (!LinkageRouteRoundedLapGeometry.TryGetFlatSegment(terminalPart, targetIndex, out var targetFlat, out _)) continue;
                var candidates = new List<Tuple<Violation, double>>();
                foreach (var member in fixedMembers.Where(item => item?.FixedBusbar?.PointList != null))
                for (var sourceIndex = 0; sourceIndex + 1 < member.FixedBusbar.PointList.Count; sourceIndex++)
                {
                    if (!LinkageRouteRoundedLapGeometry.TryGetFlatSegment(member.FixedBusbar, sourceIndex, out var sourceFlat, out _) ||
                        sourceFlat.Direction.Cross(targetFlat.Direction).Norm > LinkageRouteGeometryTolerances.AssemblyDirection) continue;
                    var pending = new Queue<Face>();
                    var visited = new HashSet<Face>();
                    pending.Enqueue(new Face { Setting = member.FixedBusbar, SegmentIndex = sourceIndex });
                    var connected = false;
                    while (pending.Count > 0 && !connected)
                    {
                        var current = pending.Dequeue();
                        connected = LinkageRouteTerminalHoleLapGeometry.HavePhysicalFaceContact(
                            current.Setting, current.SegmentIndex, terminalPart, targetIndex);
                        foreach (var support in supportFaces)
                            if (!visited.Contains(support) && LinkageRouteTerminalHoleLapGeometry.HavePhysicalFaceContact(
                                current.Setting, current.SegmentIndex, support.Setting, support.SegmentIndex))
                            { visited.Add(support); pending.Enqueue(support); }
                    }
                    if (!connected) continue;
                    var sourceDistances = new[] {
                        new Vector3d(targetFlat.Start, sourceFlat.Start).Dot(targetFlat.Direction) +
                            sourceFlat.Direction.Dot(targetFlat.Direction) * sourceFlat.StartTrim,
                        new Vector3d(targetFlat.Start, sourceFlat.End).Dot(targetFlat.Direction) -
                            sourceFlat.Direction.Dot(targetFlat.Direction) * sourceFlat.EndTrim };
                    var sourceMinimum = sourceDistances.Min();
                    var sourceMaximum = sourceDistances.Max();
                    var targetMinimum = targetFlat.StartTrim;
                    var targetMaximum = targetFlat.OriginalLength - targetFlat.EndTrim;
                    var overlap = Math.Max(0d, Math.Min(sourceMaximum, targetMaximum) - Math.Max(sourceMinimum, targetMinimum));
                    var evaluation = new LinkageRouteTerminalHoleLapGeometry.Evaluation {
                        IsValid = overlap + LinkageRouteGeometryTolerances.FinalAcceptanceComparison >= required,
                        FailureReason = string.Format(CultureInfo.InvariantCulture,
                            "始端水平实际搭接长度 {0:F6} mm，不足始端搭接排设置宽度 {1:F6} mm。", overlap, required),
                        CenteringDisplacements = new[] { new Vector3d(0d,0d,0d) },
                        RepairDisplacements = new[] { targetFlat.Direction * (sourceMinimum - targetMinimum),
                            targetFlat.Direction * (sourceMaximum - targetMaximum) }.OrderBy(vector => vector.Norm).ToArray()
                    };
                    if (!evaluation.IsValid)
                    {
                        // 只有固定排在该自由切边外确实还有可用长度时，延长才有意义。
                        if (!group.PathSearchIsReversed && Math.Min(sourceMaximum, targetMaximum) - sourceMinimum +
                                LinkageRouteGeometryTolerances.FinalAcceptanceComparison >= required)
                            evaluation.StartCutExtension = Math.Max(0d, required - overlap + Math.Max(0d, targetMinimum - sourceMaximum));
                        if (group.PathSearchIsReversed && sourceMaximum - Math.Max(sourceMinimum, targetMinimum) +
                                LinkageRouteGeometryTolerances.FinalAcceptanceComparison >= required)
                            evaluation.EndCutExtension = Math.Max(0d, required - overlap + Math.Max(0d, sourceMinimum - targetMaximum));
                    }
                    candidates.Add(Tuple.Create(new Violation { Evaluation = evaluation, MemberKey = member.MemberKey,
                        TargetFace = new Face { Setting = terminalPart, SegmentIndex = targetIndex } }, overlap));
                }
                if (candidates.Count > 0 && candidates.All(item => !item.Item1.Evaluation.IsValid))
                    violations.Add(candidates.OrderByDescending(item => item.Item2).First().Item1);
            }
            return violations;
        }
    }
}
