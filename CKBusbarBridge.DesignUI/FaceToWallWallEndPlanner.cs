using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridge.AlgorithmWorker;
using CKBusbarBridge.DesignGeometry;
using GeometRi;
using Newtonsoft.Json;

namespace CKBusbarBridge.Services
{
    internal sealed class FaceToWallWallEndPlanner
    {
        private const double Tolerance = .01d;

        internal LinkageRouteWorkerSolutionDto CreateStraightEnds(LinkageRouteWorkerRequestDto request,
            LinkageRouteWorkerSolutionDto source, out string problem)
        {
            var result = CreateStraightEndsCore(request, source, -1d, out problem);
            if (result != null) return result;
            var alternate = CreateStraightEndsCore(request, source, 1d, out var alternateProblem);
            if (alternate != null) problem = null;
            else if (alternateProblem != problem) problem += "；另一贴合侧：" + alternateProblem;
            return alternate;
        }

        private LinkageRouteWorkerSolutionDto CreateStraightEndsCore(LinkageRouteWorkerRequestDto request,
            LinkageRouteWorkerSolutionDto source, double contactSide, out string problem)
        {
            problem = null;
            var rules = new FaceToWallWorkerSolutions();
            if (rules.HasStraightWallLap(request, source)) return source;
            if (request?.Groups == null || request.Groups.Count != 3 || source?.GroupSolutions == null ||
                !source.IsSuccess || source.IsCancelled || source.IsRelaxedClearanceSolution)
            {
                problem = "缺少满足原绝缘要求的完整三相候选。";
                return null;
            }
            // 改的是候选深副本中的可制造连接排；真实桩头及Worker请求中的虚拟加长均不改写。
            var result = Copy(source);
            foreach (var group in request.Groups)
            {
                var matches = result.GroupSolutions.Where(g => g?.GroupKey == group.GroupKey).ToArray();
                if (matches.Length != 1 || matches[0].StackMembers == null ||
                    matches[0].StackMembers.Count != group.StackCount || group.EndTerminal?.Members == null ||
                    group.EndTerminal.Members.Count == 0)
                {
                    problem = group.Phase + "相缺少完整叠排或墙端接口。";
                    return null;
                }
                var target = matches[0];
                var direction = Vector(group.EndTerminal.LengthDirection).Normalized;
                if (target.StackMembers.All(m => IsStraight(m?.PlanarParts?.LastOrDefault(), direction))) continue;
                foreach (var member in target.StackMembers)
                {
                    if (!AppendStraightHead(request, group, member.PlanarParts, contactSide, out problem))
                    {
                        problem = group.Phase + "相第" + (member.MemberIndex + 1) + "层：" + problem;
                        return null;
                    }
                }
                if (target.PlanarParts != null && target.PlanarParts.Count > 0 &&
                    !AppendStraightHead(request, group, target.PlanarParts, contactSide, out problem)) return null;
                CompleteCenterline(target.AxisSkeleton, group);
                CompleteCenterline(target.ManufacturableCenterline, group);
                target.PerpendicularLapCount += target.StackMembers.Count;
                result.PerpendicularLapCount += target.StackMembers.Count;
                target.TopologySignature = null;
            }
            if (!rules.HasStraightWallLap(request, result))
            {
                problem = "补齐墙端直头后仍未满足同向夹接、搭接长度或逐层层位要求。";
                return null;
            }
            if (!BusbarWorkerSolutionBusbarExtractor.TryExtractGeneratedPreview(result, out var copper, out problem)) return null;
            foreach (var phase in copper.GroupBy(b => b.GroupKey))
            {
                var identities = phase.Select((bar, index) => new { bar, key = index.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                    .ToDictionary(item => item.bar, item => item.key);
                if (!BridgeCopperClearance.EvaluateMemberStraightSegments(phase.ToArray(), b => identities[b]).IsClear)
                {
                    problem = phase.Key + "补齐墙端直头后同相铜排实体相穿。";
                    return null;
                }
            }
            var clearance = new BridgeRaisedLapGeometry().EvaluateActual(copper, b => b.GroupKey, request.InsulationClearance);
            if (!clearance.IsClear)
            {
                problem = FormattableString.Invariant($"墙端直头连接后实际相间净距{clearance.MinimumDistance:0.###}mm，不满足{request.InsulationClearance:0.###}mm。");
                return null;
            }
            foreach (var group in result.GroupSolutions)
                group.ActualCopperLength = copper.Where(b => b.GroupKey == group.GroupKey).Sum(b => b.GetDevelopedLength());
            result.ActualCopperLength = result.GroupSolutions.Sum(g => g.ActualCopperLength);
            result.MinimumClearance = clearance.MinimumDistance;
            return result;
        }

        private bool AppendStraightHead(LinkageRouteWorkerRequestDto request, LinkageRouteWorkerGroupRequestDto group,
            IList<LinkageRouteWorkerPlanarSettingDto> parts, double contactSide, out string problem)
        {
            problem = "墙端横向候选不能构成完整的直头连接。";
            var bar = parts?.LastOrDefault();
            if (bar?.PointList == null || bar.PointList.Count < 3 || !bar.IsCenter) return false;
            var terminal = group.EndTerminal;
            var direction = Vector(terminal.LengthDirection).Normalized;
            var thickness = Vector(terminal.ThicknessDirection).Normalized;
            var fixedBar = terminal.Members[0].FixedBusbar;
            var width = Vector(fixedBar.WidthVector).Normalized;
            var lap = fixedBar.EndLapWidth;
            var end = Vector(bar.PointList.Last());
            var previous = Vector(bar.PointList[bar.PointList.Count - 2]);
            var before = Vector(bar.PointList[bar.PointList.Count - 3]);
            var delta = end - previous;
            var near = terminal.Members.Average(m => Vector(m.FixedBusbar.PointList.Last()).Dot(direction));
            var across = terminal.Members.Average(m => Vector(m.FixedBusbar.PointList.Last()).Dot(width));
            // 只转换完整的正交搭接足迹；不裁短铜排、不平移整条路径，也不放宽墙端方向校验。
            if (lap <= 0 || bar.BusBarThickness <= 0 || Math.Abs(bar.BusBarWidth - lap) > Tolerance ||
                Math.Abs(bar.EndLapWidth - lap) > Tolerance || Math.Abs(bar.BusBarThickness - group.BusbarThickness) > Tolerance ||
                Math.Abs(Vector(bar.WidthVector).Normalized.Dot(direction)) < .999999d ||
                delta.Norm < lap - Tolerance || Math.Abs(delta.Normalized.Dot(width)) < .999999d ||
                Math.Abs(end.Dot(direction) + bar.BusBarWidth / 2d - near - lap) > Tolerance ||
                Math.Abs(end.Dot(width) - across - Math.Sign(delta.Dot(width)) * lap / 2d) > Tolerance ||
                (previous - before).Norm < Tolerance || Math.Abs((previous - before).Normalized.Dot(thickness)) < .999999d)
                return false;
            var shifted = previous + thickness * (bar.BusBarThickness * contactSide);
            var required = Math.Max(request.MinimumSegmentLength, 2d * (bar.BarBendingRadius + bar.BusBarThickness / 2d));
            if ((shifted - before).Dot(previous - before) <= 0 || (shifted - before).Norm < required - Tolerance)
            {
                problem = "墙端连接所需的厚度偏移会使折弯直段不足。";
                return false;
            }
            var headEnd = end + direction * (near + lap - end.Dot(direction)) + width * (across - end.Dot(width));
            var head = Copy(bar);
            head.PointList = new List<LinkageRouteWorkerCoordinateDto> { Coordinate(headEnd - direction * lap), Coordinate(headEnd) };
            head.WidthVector = Coordinate(width);
            head.StartBusbarVector = Coordinate(thickness);
            head.StartLapWidth = head.EndLapWidth = lap;
            // 横向排与新增直头沿厚度相贴，直头保留原来的墙端夹排层位。
            bar.PointList[bar.PointList.Count - 2] = Coordinate(shifted);
            bar.PointList[bar.PointList.Count - 1] = Coordinate(end + thickness * (bar.BusBarThickness * contactSide));
            parts.Add(head);
            problem = null;
            return true;
        }

        private void CompleteCenterline(IList<LinkageRouteWorkerCoordinateDto> points, LinkageRouteWorkerGroupRequestDto group)
        {
            if (points == null || points.Count == 0) return;
            var terminal = group.EndTerminal;
            var direction = Vector(terminal.LengthDirection).Normalized;
            var width = Vector(terminal.Members[0].FixedBusbar.WidthVector).Normalized;
            var point = Vector(points.Last());
            var anchor = Vector(terminal.RouteAnchor);
            point += width * ((anchor - point).Dot(width));
            points[points.Count - 1] = Coordinate(point);
            points.Add(Coordinate(point + direction * ((anchor - point).Dot(direction) + terminal.Members[0].FixedBusbar.EndLapWidth)));
        }

        private bool IsStraight(LinkageRouteWorkerPlanarSettingDto bar, Vector3d direction)
            => bar?.PointList != null && bar.PointList.Count >= 2 &&
                (Vector(bar.PointList.Last()) - Vector(bar.PointList[bar.PointList.Count - 2])).Norm > Tolerance &&
                (Vector(bar.PointList.Last()) - Vector(bar.PointList[bar.PointList.Count - 2])).Normalized.Dot(direction) > .999999d;
        private Vector3d Vector(LinkageRouteWorkerCoordinateDto p) => new Vector3d(p.X, p.Y, p.Z);
        private LinkageRouteWorkerCoordinateDto Coordinate(Vector3d p) => new LinkageRouteWorkerCoordinateDto { X = p.X, Y = p.Y, Z = p.Z };
        private T Copy<T>(T source) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(source),
            new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace });
    }
}
