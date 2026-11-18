using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using RouteLinkBar.Algorithm.Routing;

namespace RouteLinkBar.Algorithm.Geometry
{
    /// <summary>限制实际固定端搭接投影；仅构造测量端面，不改变完整固定实体。</summary>
    internal static class LinkageRouteFixedTerminalEndLapValidator
    {
        private const double Precision = LinkageRouteGeometryTolerances.PointCoincidence;
        private const double Acceptance = LinkageRouteGeometryTolerances.FinalAcceptanceComparison;

        internal static bool TryValidatePair(LinkageTerminalMember fixedMember,
            Point3d oppositeAnchor, PlanarBendBusbarSetting contactSetting,
            bool contactUseStart, out string failureReason)
        {
            failureReason = null;
            if (!TryGetFixedBoundary(fixedMember, oppositeAnchor, out var fixedBoundary) ||
                contactSetting == null || contactSetting.BusBarWidth <= Precision ||
                !LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(
                    fixedBoundary, true, contactSetting, contactUseStart,
                    contactSetting.BusBarWidth, out var contactFootprint))
            {
                failureReason = "固定排末端搭接限制无法解析允许端点或实际搭接区域。";
                return false;
            }

            return TryValidateFootprint(fixedBoundary, contactFootprint,
                contactSetting.BusBarWidth, out failureReason);
        }

        private static bool TryValidateFootprint(PlanarBendBusbarSetting fixedBoundary,
            LinkageRouteLapFootprint contactFootprint, double actualContactWidth, out string failureReason)
        {
            failureReason = null;
            var allowedEndpoint = fixedBoundary.PointList[0];
            var inwardDirection = new Vector3d(allowedEndpoint, fixedBoundary.PointList[1]).Normalized;
            var widthDirection = fixedBoundary.WidthVector.Normalized;
            var normal = inwardDirection.Cross(widthDirection).Normalized;
            var contactCoordinates = contactFootprint.Corners
                .Select(corner => new Vector3d(allowedEndpoint, corner).Dot(inwardDirection)).ToList();
            var allowedLength = Math.Min(actualContactWidth,
                allowedEndpoint.DistanceTo(fixedBoundary.PointList[1]));
            if (contactCoordinates.Min() < -Acceptance ||
                contactCoordinates.Max() > allowedLength + Acceptance ||
                contactFootprint.Corners.Any(corner =>
                    Math.Abs(new Vector3d(allowedEndpoint, corner).Dot(normal)) > Acceptance ||
                    Math.Abs(new Vector3d(allowedEndpoint, corner).Dot(widthDirection)) >
                        fixedBoundary.BusBarWidth / 2d + Acceptance))
            {
                failureReason = string.Format(CultureInfo.InvariantCulture,
                    "实际搭接区域距固定排允许端点 {0:F3}～{1:F3} mm，超出末端允许范围 0～{2:F3} mm（实际搭接排宽 {3:F3} mm）。",
                    contactCoordinates.Min(), contactCoordinates.Max(), allowedLength, actualContactWidth);
                return false;
            }
            return true;
        }

        internal static bool TryValidateConversion(LinkageMultiGroupRouteRequest request,
            LinkageRouteGroupRequest group, LinkageRouteCenterlineConversionResult conversion,
            out string failureReason)
        {
            failureReason = null;
            if (request == null || !request.RequireFixedTerminalEndLap) return true;
            if (group == null || conversion == null)
            {
                failureReason = "固定排末端搭接校验缺少逻辑组或实际实体。";
                return false;
            }
            return TryValidateTerminal(group.StartTerminal, group.EndTerminal?.RouteAnchor,
                       conversion, true, out failureReason) &&
                   TryValidateTerminal(group.EndTerminal, group.StartTerminal?.RouteAnchor,
                       conversion, false, out failureReason);
        }

        private sealed class ContactNode
        {
            internal PlanarBendBusbarSetting Setting;
            internal bool UseStart;
            internal bool IsSupport;
        }

        private sealed class SupportPath
        {
            internal ContactNode Node;
            internal LinkageRouteLapFootprint FixedContactFootprint;
        }

        private static bool TryValidateTerminal(LinkageTerminalAssembly terminal,
            Point3d oppositeAnchor, LinkageRouteCenterlineConversionResult conversion,
            bool useStart, out string failureReason)
        {
            failureReason = null;
            if (terminal?.IsMaterializedRoutingBridge == true) return true;
            var nodes = new List<ContactNode>();
            foreach (var stackMember in conversion.StackMembers ?? new List<LinkageStackedRouteMember>())
                foreach (var part in stackMember?.PlanarParts ?? new List<PlanarBendBusbarSetting>())
                    AddStraightFaceNodes(nodes, part, false);
            foreach (var accessory in conversion.Accessories ?? new List<LinkageRouteAccessory>())
                AddStraightFaceNodes(nodes, accessory?.Setting,
                    accessory?.Kind == LinkageRouteAccessoryKind.SupportBlock);

            foreach (var fixedMember in terminal?.Members ?? new List<LinkageTerminalMember>())
            {
                if (!TryGetFixedBoundary(fixedMember, oppositeAnchor, out var fixedBoundary))
                {
                    failureReason = "固定排末端搭接校验无法确定固定排允许端点。";
                    return false;
                }
                // 只沿真实贴合的垫块链到达铜排。厚向无关的投影不属于搭接。
                // 检查完整固定排的每一个真实面，防止在另一个直段搭接而漏验。
                for (var fixedSegmentIndex = 0; fixedSegmentIndex + 1 < fixedMember.FixedBusbar.PointList.Count;
                     fixedSegmentIndex++)
                {
                    var fixedSegment = new PlanarBendBusbarSetting {
                        PointList = new List<Point3d> { fixedMember.FixedBusbar.PointList[fixedSegmentIndex],
                            fixedMember.FixedBusbar.PointList[fixedSegmentIndex + 1] },
                        WidthVector = fixedMember.FixedBusbar.WidthVector,
                        BusBarWidth = fixedMember.FixedBusbar.BusBarWidth,
                        BusBarThickness = fixedMember.FixedBusbar.BusBarThickness };
                    var fixedNode = new ContactNode { Setting = fixedSegment, UseStart = true };
                    // 每一块贴固定排的垫块有自己的原始接触区，不能因汇入同根铜排而去重。
                    foreach (var initialContactNode in nodes)
                    {
                        if (!HavePhysicalFaceContact(fixedNode, initialContactNode, out var initialFootprint)) continue;
                        if (!initialContactNode.IsSupport)
                        {
                            if (!TryValidateFootprint(fixedBoundary, initialFootprint,
                                    initialContactNode.Setting.BusBarWidth, out failureReason))
                                return false;
                            continue;
                        }
                        var pendingSupports = new Queue<SupportPath>();
                        var visitedNodes = new HashSet<ContactNode> { initialContactNode };
                        pendingSupports.Enqueue(new SupportPath { Node = initialContactNode,
                            FixedContactFootprint = initialFootprint });
                        while (pendingSupports.Count > 0)
                        {
                            var path = pendingSupports.Dequeue();
                            foreach (var contactNode in nodes)
                            {
                                if (visitedNodes.Contains(contactNode) ||
                                    !HavePhysicalFaceContact(path.Node, contactNode, out var faceContact)) continue;
                                visitedNodes.Add(contactNode);
                                var fixedContact = path.FixedContactFootprint ?? faceContact;
                                if (contactNode.IsSupport) pendingSupports.Enqueue(new SupportPath {
                                    Node = contactNode, FixedContactFootprint = fixedContact });
                                else if (!TryValidateFootprint(fixedBoundary, fixedContact,
                                             contactNode.Setting.BusBarWidth, out failureReason))
                                {
                                    failureReason = (useStart ? "始端" : "柜顶") + "固定排“" +
                                        fixedMember.MemberKey + "”：" + failureReason;
                                    return false;
                                }
                            }
                        }
                    }
                }
            }
            // 缺失搭接、断链及垫块覆盖仍由完整连接/搭接门禁判定，不能在此豁免。
            return true;
        }

        private static void AddStraightFaceNodes(ICollection<ContactNode> nodes,
            PlanarBendBusbarSetting setting, bool isSupport)
        {
            if (setting?.PointList == null) return;
            for (var segmentIndex = 0; segmentIndex + 1 < setting.PointList.Count; segmentIndex++)
            {
                if (setting.PointList[segmentIndex].DistanceTo(setting.PointList[segmentIndex + 1]) <= Precision) continue;
                nodes.Add(new ContactNode { UseStart = true, IsSupport = isSupport,
                    Setting = new PlanarBendBusbarSetting {
                        PointList = new List<Point3d> { setting.PointList[segmentIndex], setting.PointList[segmentIndex + 1] },
                        WidthVector = setting.WidthVector, BusBarWidth = setting.BusBarWidth,
                        BusBarThickness = setting.BusBarThickness } });
            }
        }

        private static bool HavePhysicalFaceContact(ContactNode firstNode, ContactNode secondNode,
            out LinkageRouteLapFootprint footprint)
        {
            if (!LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(
                    firstNode.Setting, firstNode.UseStart, secondNode.Setting, secondNode.UseStart,
                    secondNode.Setting.BusBarWidth, out footprint)) return false;
            var firstIndex = firstNode.UseStart ? 0 : firstNode.Setting.PointList.Count - 1;
            var secondIndex = secondNode.UseStart ? 0 : secondNode.Setting.PointList.Count - 1;
            var centerDistance = Math.Abs(new Vector3d(firstNode.Setting.PointList[firstIndex],
                secondNode.Setting.PointList[secondIndex]).Dot(footprint.PlaneNormal));
            var touchingDistance = (firstNode.Setting.BusBarThickness + secondNode.Setting.BusBarThickness) / 2d;
            return Math.Abs(centerDistance - touchingDistance) <= Acceptance;
        }

        private static bool TryGetFixedBoundary(LinkageTerminalMember member, Point3d oppositeAnchor,
            out PlanarBendBusbarSetting boundary)
        {
            boundary = null;
            var fixedSetting = member?.FixedBusbar;
            var fixedPoints = fixedSetting?.PointList;
            if (fixedPoints == null || fixedPoints.Count < 2 || fixedSetting.WidthVector == null) return false;
            var forbidden = member.ForbiddenHorizontalExitDirection;
            var allowedIndex = forbidden != null && forbidden.Norm > Precision
                ? LinkageRouteTerminalPoseFactory.FindAllowedExitFixedPointIndex(fixedPoints, forbidden)
                : LinkageRouteTerminalPoseFactory.FindNearestFixedPointIndex(fixedPoints, oppositeAnchor);
            if (allowedIndex < 0) return false;
            var adjacentIndex = LinkageRouteTerminalPoseFactory.FindLapSegmentAdjacentPointIndex(
                fixedPoints, allowedIndex, oppositeAnchor);
            if (adjacentIndex < 0 || adjacentIndex >= fixedPoints.Count ||
                fixedPoints[allowedIndex].DistanceTo(fixedPoints[adjacentIndex]) <= Precision) return false;
            boundary = new PlanarBendBusbarSetting { PointList = new List<Point3d> {
                fixedPoints[allowedIndex], fixedPoints[adjacentIndex] },
                WidthVector = fixedSetting.WidthVector, BusBarWidth = fixedSetting.BusBarWidth,
                BusBarThickness = fixedSetting.BusBarThickness };
            return true;
        }
    }
}
