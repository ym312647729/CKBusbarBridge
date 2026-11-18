using System;
using System.Collections.Generic;
using System.Linq;
using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;

namespace RouteLinkBar.Algorithm.Routing
{
    // 真实柜顶实体和外伸路由面分开保存。绝不能用外伸面替代原固定实体终检。
    internal sealed class LinkageCabinetBridgeAssembly
    {
        internal LinkageTerminalAssembly OriginalTerminal;
        internal List<LinkageRouteAccessory> Accessories = new List<LinkageRouteAccessory>();
        internal double LapWidth;
        internal int GeneratedStackCount;
    }

    internal static class LinkageRouteCabinetBridgeBuilder
    {
        private const double Precision = 1e-6;
        private const double Acceptance = LinkageRouteGeometryTolerances.FinalAcceptanceComparison;
        private sealed class MemberBox
        {
            internal PlanarBendBusbarSetting Setting;
            internal double W0, W1, T0, T1, L0, L1;
        }

        internal static LinkageMultiGroupRouteRequest Prepare(LinkageMultiGroupRouteRequest request)
        {
            if (request?.Groups == null) return request;
            var groups = new List<LinkageRouteGroupRequest>();
            var changed = false;
            foreach (var group in request.Groups)
            {
                var terminal = CreateTerminal(request, group);
                groups.Add(terminal == null ? group : group.WithCabinetTerminal(terminal));
                changed |= terminal != null;
            }
            return changed ? request.WithCabinetGroups(groups) : request;
        }

        internal static int ConnectionCount(int gaps, int generatedCount) =>
            Math.Max(Math.Max(1, generatedCount - 1), Math.Min(gaps, generatedCount + 1));

        private static LinkageTerminalAssembly CreateTerminal(LinkageMultiGroupRouteRequest request,
            LinkageRouteGroupRequest group)
        {
            var original = group?.EndTerminal;
            if (original?.CabinetBridgeAssembly != null || original?.Members?.Count < 2) return null;
            var members = original.Members;
            if (original.ThicknessDirection == null || members.Any(member =>
                member?.FixedBusbar?.PointList?.Count != 2 || member.ForbiddenHorizontalExitDirection == null ||
                member.FixedBusbar.WidthVector == null || member.FixedBusbar.StartBusbarVector == null)) return null;
            var thicknessAxis = original.ThicknessDirection.Normalized;
            var freeAxis = -members[0].ForbiddenHorizontalExitDirection.Normalized;
            var widthAxis = thicknessAxis.Cross(-freeAxis).Normalized;
            var boxes = members.Select(member => Box(member.FixedBusbar, widthAxis, thicknessAxis, freeAxis)).ToList();
            // 横向分列以实际宽向区间区分，厚向层数不会被误判成列数。
            var columns = new List<List<MemberBox>>();
            foreach (var box in boxes.OrderBy(item => item.W0))
            {
                var column = columns.FirstOrDefault(items =>
                    Math.Abs(items[0].W0 - box.W0) < Precision && Math.Abs(items[0].W1 - box.W1) < Precision);
                if (column == null) columns.Add(new List<MemberBox> { box });
                else column.Add(box);
            }
            if (columns.Count == 1) return null;
            // 仅互相分离的并排固定排触发。交错宽向成员继续走既有规则。
            if (columns.Zip(columns.Skip(1), (a, b) => b[0].W0 - a[0].W1).Any(gap => gap < -Precision))
                return null;
            if (members.Any(member =>
                    member.ForbiddenHorizontalExitDirection.Normalized.Dot(-freeAxis) < 1-Precision ||
                    member.FixedBusbar.StartBusbarVector.Normalized.Cross(thicknessAxis).Norm > Precision ||
                    member.FixedBusbar.WidthVector.Normalized.Cross(widthAxis).Norm > Precision ||
                    new Vector3d(member.FixedBusbar.PointList[0], member.FixedBusbar.PointList[1]).Normalized.Cross(freeAxis).Norm > Precision))
                throw new ArgumentException("多列柜顶连接排要求固定直排的排长、排宽、排厚方向一致。");
            var referenceLayers = columns[0].OrderBy(item => item.T0).ToList();
            foreach (var column in columns)
            {
                var layers = column.OrderBy(item => item.T0).ToList();
                if (layers.Count != referenceLayers.Count || layers.Where((item, index) =>
                    Math.Abs(item.T0-referenceLayers[index].T0) > Precision ||
                    Math.Abs(item.T1-referenceLayers[index].T1) > Precision).Any())
                    throw new ArgumentException("多列柜顶各列的厚向层位必须对应；不能用平均厚度代替逐根位置。");
            }
            var width = group.GetCabinetTopLapBusbarWidth(request);
            var generatedThickness = group.GetCabinetTopLapBusbarThickness(request);
            var freeEnd = boxes.Min(item => item.L1);
            if (boxes.Any(item => Math.Abs(item.L1 - freeEnd) > Precision) ||
                boxes.Max(item => item.L0) > freeEnd-width+Precision)
                throw new ArgumentException("多列柜顶须有共同末端，且插入直段不得短于柜顶搭接排排宽。");
            var gapIntervals = new List<Tuple<double, double>>();
            for (var layer = 1; layer < referenceLayers.Count; layer++)
            {
                var gap = referenceLayers[layer].T0-referenceLayers[layer-1].T1;
                if (gap < -Precision) throw new ArgumentException("柜顶固定排厚向实体互相穿透。");
                if (gap > Precision) gapIntervals.Add(Tuple.Create(referenceLayers[layer-1].T1, referenceLayers[layer].T0));
            }
            var count = ConnectionCount(gapIntervals.Count, group.GetCabinetTopLapStackCount(request));
            var bridgeIntervals = new List<Tuple<double, double>>();
            foreach (var gap in gapIntervals)
            {
                // 外伸部分仍须容纳生成排。薄固定排夹在两根桥之间时，不能把
                // 小于生成厚度的层间空隙强行当成一个可用生成层。
                if (bridgeIntervals.Count >= count) break;
                if (bridgeIntervals.Count == 0 || gap.Item1-bridgeIntervals.Last().Item2 >= generatedThickness-Precision)
                    bridgeIntervals.Add(gap);
            }
            var exteriorThickness = generatedThickness;
            var low = referenceLayers.First().T0;
            var high = referenceLayers.Last().T1;
            var outerIndex = 0;
            while (bridgeIntervals.Count < count)
            {
                // 优先贴合两侧外厚面；再外扩时预留真实生成排厚度，插入段余隙随后补块。
                if (outerIndex % 2 == 0)
                {
                    if (bridgeIntervals.Count > 0)
                        low = Math.Min(low, bridgeIntervals.Min(bridge => bridge.Item1)-generatedThickness);
                    bridgeIntervals.Add(Tuple.Create(low-exteriorThickness, low));
                    low -= exteriorThickness;
                }
                else
                {
                    if (bridgeIntervals.Count > 0)
                        high = Math.Max(high, bridgeIntervals.Max(bridge => bridge.Item2)+generatedThickness);
                    bridgeIntervals.Add(Tuple.Create(high, high+exteriorThickness));
                    high += exteriorThickness;
                }
                outerIndex++;
            }
            bridgeIntervals = bridgeIntervals.OrderBy(item => item.Item1).ToList();
            var assembly = new LinkageCabinetBridgeAssembly { OriginalTerminal = original, LapWidth = width,
                GeneratedStackCount = group.GetCabinetTopLapStackCount(request) };
            var w0 = boxes.Min(item => item.W0);
            var w1 = boxes.Max(item => item.W1);
            var exposed = new LinkageTerminalAssembly
            {
                LengthDirection = -freeAxis,
                ThicknessDirection = thicknessAxis,
                IsMaterializedRoutingBridge = true,
                CabinetBridgeAssembly = assembly,
                Members = new List<LinkageTerminalMember>(),
                RouteAnchor = Point(widthAxis, (w0+w1)/2, thicknessAxis,
                    bridgeIntervals.Average(item => (item.Item1+item.Item2)/2), freeAxis, freeEnd+width),
                PreferredPerpendicularLapReferencePoints = new List<Point3d>()
            };
            for (var index = 0; index < bridgeIntervals.Count; index++)
            {
                var interval = bridgeIntervals[index];
                var bridge = Setting(widthAxis, thicknessAxis, freeAxis, w0, w1,
                    interval.Item1, interval.Item2, freeEnd-width, freeEnd+width);
                bridge.StartLapWidth = width;
                bridge.EndLapWidth = width;
                bridge.GroupKey = group.GroupKey;
                assembly.Accessories.Add(new LinkageRouteAccessory
                {
                    Kind = LinkageRouteAccessoryKind.CabinetTopConnectionBridge,
                    TerminalSide = LinkageRouteTerminalSide.End,
                    RelatedGeneratedMemberIndex = -1,
                    SupportBlockKey = "CABINET_BRIDGE_"+(index+1), Setting = bridge
                });
                exposed.Members.Add(new LinkageTerminalMember
                {
                    MemberKey = "CABINET_BRIDGE_"+(index+1),
                    ForbiddenHorizontalExitDirection = -freeAxis,
                    FixedBusbar = Setting(widthAxis, thicknessAxis, freeAxis, w0, w1,
                        interval.Item1, interval.Item2, freeEnd, freeEnd+width)
                });
                foreach (var edge in new[] { w0+width/2, w1-width/2 })
                    exposed.PreferredPerpendicularLapReferencePoints.Add(Point(widthAxis, edge, thicknessAxis,
                        (interval.Item1+interval.Item2)/2, freeAxis, freeEnd+width/2));
            }
            // 原始固定排、已占用夹缝及外侧新增桥排形成厚向有序区间；逐列补齐每个真实正间隙。
            foreach (var column in columns)
            {
                var occupied = column.Select(item => Tuple.Create(item.T0, item.T1))
                    .Concat(bridgeIntervals).OrderBy(item => item.Item1).ToList();
                var boundary = occupied[0].Item2;
                foreach (var interval in occupied.Skip(1))
                {
                    var remaining = interval.Item1-boundary;
                    var padLimit = bridgeIntervals.Where(bridge =>
                            Math.Abs(bridge.Item2-boundary) < Precision ||
                            Math.Abs(bridge.Item1-interval.Item1) < Precision)
                        .Select(bridge => bridge.Item2-bridge.Item1).Concat(new[] { generatedThickness }).Min();
                    while (remaining > Precision)
                    {
                        var padThickness = Math.Min(remaining, padLimit);
                        assembly.Accessories.Add(new LinkageRouteAccessory
                        {
                            Kind = LinkageRouteAccessoryKind.SupportBlock,
                            TerminalSide = LinkageRouteTerminalSide.End,
                            RelatedGeneratedMemberIndex = -1,
                            SupportBlockKey = "CABINET_GAP_"+assembly.Accessories.Count,
                            Setting = Setting(widthAxis, thicknessAxis, freeAxis, column[0].W0, column[0].W1,
                                boundary, boundary+padThickness, freeEnd-width, freeEnd)
                        });
                        boundary += padThickness;
                        remaining = interval.Item1-boundary;
                    }
                    boundary = Math.Max(boundary, interval.Item2);
                }
            }
            return exposed;
        }

        private static PlanarBendBusbarSetting Setting(Vector3d w, Vector3d t, Vector3d l,
            double w0, double w1, double t0, double t1, double l0, double l1) => new PlanarBendBusbarSetting
        {
            IsCenter = true, WidthVector = w, StartBusbarVector = t,
            PointList = new List<Point3d> { Point(w,(w0+w1)/2,t,(t0+t1)/2,l,l1), Point(w,(w0+w1)/2,t,(t0+t1)/2,l,l0) },
            BusBarWidth = w1-w0, BusBarThickness = t1-t0, StartLapWidth = w1-w0, EndLapWidth = w1-w0
        };
        private static Point3d Point(Vector3d w, double wc, Vector3d t, double tc, Vector3d l, double lc) =>
            new Point3d(w.X*wc+t.X*tc+l.X*lc, w.Y*wc+t.Y*tc+l.Y*lc, w.Z*wc+t.Z*tc+l.Z*lc);
        private static double Project(Point3d p, Vector3d axis) => p.X*axis.X+p.Y*axis.Y+p.Z*axis.Z;
        private static MemberBox Box(PlanarBendBusbarSetting setting, Vector3d w, Vector3d t, Vector3d l)
        {
            var points = setting.PointList.Select(p => setting.IsCenter ? p :
                new Point3d(p.X+setting.StartBusbarVector.X*setting.BusBarThickness/2,
                    p.Y+setting.StartBusbarVector.Y*setting.BusBarThickness/2,
                    p.Z+setting.StartBusbarVector.Z*setting.BusBarThickness/2)).ToList();
            return new MemberBox { Setting = setting,
                W0 = points.Min(p=>Project(p,w))-setting.BusBarWidth/2,
                W1 = points.Max(p=>Project(p,w))+setting.BusBarWidth/2,
                T0 = points.Min(p=>Project(p,t))-setting.BusBarThickness/2,
                T1 = points.Max(p=>Project(p,t))+setting.BusBarThickness/2,
                L0 = points.Min(p=>Project(p,l)), L1 = points.Max(p=>Project(p,l)) };
        }

        internal static void AddAccessories(LinkageRouteCenterlineConversionResult conversion, LinkageRouteGroupRequest group)
        {
            foreach (var terminal in new[] { group.StartTerminal, group.EndTerminal })
                foreach (var accessory in terminal.CabinetBridgeAssembly?.Accessories ?? new List<LinkageRouteAccessory>())
                    conversion.Accessories.Add(new LinkageRouteAccessory { Kind = accessory.Kind,
                        TerminalSide = ReferenceEquals(terminal, group.StartTerminal) ? LinkageRouteTerminalSide.Start : LinkageRouteTerminalSide.End,
                        RelatedGeneratedMemberIndex = accessory.RelatedGeneratedMemberIndex,
                        SupportBlockKey = accessory.SupportBlockKey, Setting = accessory.Setting.Copy() });
        }

        internal static bool TryValidate(LinkageRouteCenterlineConversionResult conversion,
            LinkageRouteGroupRequest group, out string diagnostic)
        {
            diagnostic = null;
            foreach (var terminal in new[] { group.StartTerminal, group.EndTerminal })
            {
                var assembly = terminal?.CabinetBridgeAssembly;
                if (assembly == null) continue;
                var atStart = ReferenceEquals(terminal, group.StartTerminal);
                var side = atStart ? LinkageRouteTerminalSide.Start : LinkageRouteTerminalSide.End;
                var accessories = conversion.Accessories.Where(item => item?.TerminalSide == side && item.Setting != null).ToList();
                // 锁定桥及夹缝补块必须保持原几何，后续路径压紧不得移动它们。
                foreach (var expected in assembly.Accessories)
                    if (!accessories.Any(actual => actual.Kind == expected.Kind && Identical(actual.Setting, expected.Setting)))
                    { diagnostic = "柜顶特殊连接排或夹缝补偿垫块缺失/移动。"; return false; }
                if (accessories.Count(item => item.Kind == LinkageRouteAccessoryKind.CabinetTopConnectionBridge) !=
                    assembly.Accessories.Count(item => item.Kind == LinkageRouteAccessoryKind.CabinetTopConnectionBridge))
                { diagnostic = "柜顶特殊连接排数量不符合间隙与生成叠排数量约束。"; return false; }
                var terminalIndices = atStart ? conversion.StartTerminalMemberIndices : conversion.EndTerminalMemberIndices;
                var referenceSection = atStart ? conversion.CenterlineParts.FirstOrDefault() : conversion.CenterlineParts.LastOrDefault();
                var ends = conversion.StackMembers.Where(member => terminalIndices.Count > 0
                        ? terminalIndices.Contains(member.MemberIndex)
                        : referenceSection?.PhysicalSectionIndex >= 0
                            ? (atStart ? member.PlanarParts.FirstOrDefault() : member.PlanarParts.LastOrDefault())
                                ?.PhysicalSectionIndex == referenceSection.PhysicalSectionIndex
                            : member.MemberIndex < assembly.GeneratedStackCount)
                    .Select(member => atStart ? member.PlanarParts.FirstOrDefault() : member.PlanarParts.LastOrDefault())
                    .Where(item => item != null).ToList();
                if (ends.Count != assembly.GeneratedStackCount)
                { diagnostic = "柜顶实际接通的生成末端数量与输入叠排数量不一致。"; return false; }
                var nodes = assembly.OriginalTerminal.Members.Select(item => item.FixedBusbar)
                    .Concat(accessories.Select(item => item.Setting)).Concat(ends).ToList();
                var reached = new HashSet<PlanarBendBusbarSetting>(ends);
                var queue = new Queue<PlanarBendBusbarSetting>(ends);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    foreach (var next in nodes)
                    {
                        if (reached.Contains(next) || !Contact(current, next)) continue;
                        reached.Add(next); queue.Enqueue(next);
                    }
                }
                if (assembly.OriginalTerminal.Members.Any(item => !reached.Contains(item.FixedBusbar)) ||
                    accessories.Any(item => item.Kind == LinkageRouteAccessoryKind.CabinetTopConnectionBridge && !reached.Contains(item.Setting)))
                { diagnostic = "柜顶两列固定排未全部通过实际连接排/连续补块接通。"; return false; }
                // 每个实际末端必须接到限定外伸面，不允许改为原排或插入半段。
                foreach (var end in ends)
                {
                    var connected = HasExposedContact(end, terminal.Members.Select(member => member.FixedBusbar),
                        accessories.Where(item => item.Kind == LinkageRouteAccessoryKind.SupportBlock).Select(item => item.Setting));
                    if (!connected) { diagnostic = "柜顶生成端未在特殊连接排外伸限定面完成真实满搭接。"; return false; }
                }
            }
            return true;
        }
        private static bool HasExposedContact(PlanarBendBusbarSetting end,
            IEnumerable<PlanarBendBusbarSetting> exposedFaces, IEnumerable<PlanarBendBusbarSetting> pads)
        {
            var faces = exposedFaces.ToList();
            var pending = new Queue<PlanarBendBusbarSetting>();pending.Enqueue(end);
            var reached = new HashSet<PlanarBendBusbarSetting> { end };
            var padList = pads.ToList();
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                if (faces.Any(face => Contact(current, face))) return true;
                foreach (var pad in padList)
                    if (!reached.Contains(pad) && Contact(current, pad)) { reached.Add(pad);pending.Enqueue(pad); }
            }
            return false;
        }
        private static bool Identical(PlanarBendBusbarSetting a, PlanarBendBusbarSetting b) =>
            a.PointList.Count == b.PointList.Count && Math.Abs(a.BusBarWidth-b.BusBarWidth) <= Acceptance &&
            Math.Abs(a.BusBarThickness-b.BusBarThickness) <= Acceptance &&
            a.PointList.Zip(b.PointList,(x,y)=>x.DistanceTo(y)).All(distance=>distance<=Acceptance) &&
            a.WidthVector.Normalized.Dot(b.WidthVector.Normalized) > 1-Precision &&
            a.StartBusbarVector.Normalized.Dot(b.StartBusbarVector.Normalized) > 1-Precision;
        private static bool Contact(PlanarBendBusbarSetting a, PlanarBendBusbarSetting b) =>
            LinkageRouteTerminalLapRegionValidator.TryCreateActualLapFootprint(a,false,b,false,
                Math.Min(a.BusBarWidth,b.BusBarWidth),out _,tolerance:Acceptance);
    }
}
