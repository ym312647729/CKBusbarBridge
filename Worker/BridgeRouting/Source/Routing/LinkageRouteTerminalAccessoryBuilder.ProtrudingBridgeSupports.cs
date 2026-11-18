using System;
using System.Collections.Generic;
using System.Linq;
using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteTerminalAccessoryBuilder
    {
        private const string StartBridgeLapGapSupportKeyPrefix = "start-bridge-lap-gap:";
        private sealed class StartBridgeLapSurface
        {
            internal PlanarBendBusbarSetting Bridge, Following;
            internal int MemberIndex;
        }

        private sealed class StartBridgeGapOccupant
        {
            internal LinkageSupportProjectionOccupant Projection;
            internal ThicknessInterval Thickness;
            internal bool IsBridge;
        }

        // 固定始端只占据自己的有限投影，不能遮住两根连接排伸出后的夹缝。
        // 仅在连接排与其后始端搭接段的实际平直交集内补块，不填桥间其它空气。
        private static void AddProtrudingStartBridgeSupportBlocks(
            LinkageRouteCenterlineConversionResult conversion, LinkageMultiGroupRouteRequest request,
            LinkageRouteGroupRequest group)
        {
            foreach (var previousSupport in conversion.Accessories.Where(accessory =>
                accessory.Kind == LinkageRouteAccessoryKind.SupportBlock &&
                accessory.TerminalSide == LinkageRouteTerminalSide.Start &&
                accessory.SupportBlockKey?.StartsWith(StartBridgeLapGapSupportKeyPrefix, StringComparison.Ordinal) == true).ToList())
                conversion.Accessories.Remove(previousSupport);
            var bridgeLaps = GetStartBridgeLapSurfaces(conversion);
            if (bridgeLaps.Count < 2) return;
            var materialSettings = group.StartTerminal.Members.Select(member => member.FixedBusbar)
                .Concat(conversion.StackMembers.SelectMany(member => member.PlanarParts)).ToList();
            foreach (var bridgeLap in bridgeLaps)
            {
                if (!LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(
                    bridgeLap.Bridge, true, bridgeLap.Following, true,
                    bridgeLap.Following.BusBarWidth, out var jointFootprint)) continue;
                var jointOrigin = jointFootprint.Corners[0];
                var jointNormal = jointFootprint.PlaneNormal.Normalized;
                var jointLength = jointFootprint.LengthDirection.Normalized;
                var jointWidth = jointFootprint.WidthDirection.Normalized;
                var gapOccupants = new List<StartBridgeGapOccupant>();
                foreach (var otherBridge in bridgeLaps)
                    AddStartBridgeGapOccupant(gapOccupants, jointFootprint, otherBridge.Bridge, true);
                foreach (var material in materialSettings.Concat(conversion.Accessories.Select(item => item.Setting)))
                    for (var segmentIndex = 0; segmentIndex + 1 < (material?.PointList?.Count ?? 0); segmentIndex++)
                    {
                        var flatMaterial = CreateStartBridgeFlatSurface(material, segmentIndex);
                        if (flatMaterial != null)
                            AddStartBridgeGapOccupant(gapOccupants, jointFootprint, flatMaterial, false);
                    }
                foreach (var cell in LinkageSupportProjectionPartition.Partition(
                    gapOccupants.Select(occupant => occupant.Projection).ToArray()))
                {
                    var cellOccupants = cell.OccupantIndices.Select(index => gapOccupants[index]).ToList();
                    var cellBridges = cellOccupants.Where(occupant => occupant.IsBridge).ToList();
                    if (cellBridges.Count < 2) continue;
                    var bridgeMinimum = cellBridges.Min(occupant => occupant.Thickness.Minimum);
                    var bridgeMaximum = cellBridges.Max(occupant => occupant.Thickness.Maximum);
                    var residuals = GetResidualIntervals(bridgeMinimum, bridgeMaximum,
                        cellOccupants.Select(occupant => new ThicknessInterval
                        {
                            Minimum = Math.Max(bridgeMinimum, occupant.Thickness.Minimum),
                            Maximum = Math.Min(bridgeMaximum, occupant.Thickness.Maximum)
                        }).Where(interval => interval.Maximum - interval.Minimum > GeometryTolerance));
                    foreach (var residual in residuals)
                    {
                        var supportFootprint = new LinkageRouteLapFootprint
                        {
                            LengthDirection = jointLength, WidthDirection = jointWidth, PlaneNormal = jointNormal,
                            Corners = new[]
                            {
                                Translate(Translate(jointOrigin, jointLength, cell.LengthMinimum), jointWidth, cell.WidthMinimum),
                                Translate(Translate(jointOrigin, jointLength, cell.LengthMinimum), jointWidth, cell.WidthMaximum),
                                Translate(Translate(jointOrigin, jointLength, cell.LengthMaximum), jointWidth, cell.WidthMinimum),
                                Translate(Translate(jointOrigin, jointLength, cell.LengthMaximum), jointWidth, cell.WidthMaximum)
                            }
                        };
                        var support = CreateSupportBlockAccessory(supportFootprint, jointOrigin,
                            jointNormal, residual.Minimum, residual.Maximum, request, group, true, bridgeLap.MemberIndex);
                        support.SupportBlockKey = StartBridgeLapGapSupportKeyPrefix + bridgeLap.MemberIndex + ":" + conversion.Accessories.Count;
                        conversion.Accessories.Add(support);
                    }
                }
            }
        }

        private static List<StartBridgeLapSurface> GetStartBridgeLapSurfaces(LinkageRouteCenterlineConversionResult conversion)
        {
            var result = new List<StartBridgeLapSurface>();
            foreach (var accessory in conversion.Accessories.Where(item =>
                item.Kind == LinkageRouteAccessoryKind.DoubleStartHorizontalBridge &&
                item.TerminalSide == LinkageRouteTerminalSide.Start))
            {
                var following = conversion.StackMembers.FirstOrDefault(member =>
                    member.MemberIndex == accessory.RelatedGeneratedMemberIndex)?.PlanarParts.FirstOrDefault();
                AddStartBridgeLapSurface(result, accessory.Setting, 0, following, accessory.RelatedGeneratedMemberIndex);
            }
            if (!conversion.DoubleStartPhysicalBridgeSectionIndices.Contains(0)) return result;
            foreach (var member in conversion.StackMembers)
            {
                if (conversion.StartTerminalMemberIndices.Count > 0 &&
                    !conversion.StartTerminalMemberIndices.Contains(member.MemberIndex)) continue;
                var bridge = member.PlanarParts.FirstOrDefault();
                if (member.PlanarParts.Count < 2 || bridge?.PhysicalSectionIndex != 0) continue;
                AddStartBridgeLapSurface(result, bridge, bridge.PointList.Count - 2,
                    member.PlanarParts[1], member.MemberIndex);
            }
            return result;
        }

        private static void AddStartBridgeLapSurface(ICollection<StartBridgeLapSurface> surfaces,
            PlanarBendBusbarSetting bridge, int bridgeSegmentIndex, PlanarBendBusbarSetting following, int memberIndex)
        {
            var flatBridge = CreateStartBridgeFlatSurface(bridge, bridgeSegmentIndex);
            var flatFollowing = CreateStartBridgeFlatSurface(following, 0);
            if (flatBridge != null && flatFollowing != null)
                surfaces.Add(new StartBridgeLapSurface { Bridge = flatBridge, Following = flatFollowing, MemberIndex = memberIndex });
        }

        private static PlanarBendBusbarSetting CreateStartBridgeFlatSurface(PlanarBendBusbarSetting setting, int segmentIndex)
        {
            if (!LinkageRouteRoundedLapGeometry.TryGetFlatSegment(setting, segmentIndex, out var boundary, out _) ||
                boundary.OriginalLength - boundary.StartTrim - boundary.EndTrim <= GeometryTolerance) return null;
            var flat = setting.Copy();
            flat.IsCenter = true;
            flat.PointList = new List<Point3d>
            {
                Translate(boundary.Start, boundary.Direction, boundary.StartTrim),
                Translate(boundary.End, boundary.Direction, -boundary.EndTrim)
            };
            flat.StartBusbarVector = boundary.Direction.Cross(setting.WidthVector.Normalized).Normalized;
            return flat;
        }

        private static void AddStartBridgeGapOccupant(ICollection<StartBridgeGapOccupant> occupants,
            LinkageRouteLapFootprint domain, PlanarBendBusbarSetting flatMaterial, bool isBridge)
        {
            var domainSetting = new PlanarBendBusbarSetting
            {
                IsCenter = true, PointList = new List<Point3d> { domain.Corners[0], domain.Corners[2] },
                WidthVector = domain.WidthDirection, StartBusbarVector = domain.PlaneNormal,
                BusBarWidth = domain.Corners[0].DistanceTo(domain.Corners[1]), BusBarThickness = 1
            };
            // 点集在矩形中线，Corners[0]在宽边，不能直接作为中心线。
            domainSetting.PointList = domainSetting.PointList.Select(point =>
                Translate(point, domain.WidthDirection, domainSetting.BusBarWidth / 2d)).ToList();
            if (!LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(domainSetting,
                true, flatMaterial, true, domainSetting.BusBarWidth, out var occupiedFootprint)) return;
            var lengthCoordinates = occupiedFootprint.Corners.Select(corner =>
                new Vector3d(domain.Corners[0], corner).Dot(domain.LengthDirection)).ToArray();
            var widthCoordinates = occupiedFootprint.Corners.Select(corner =>
                new Vector3d(domain.Corners[0], corner).Dot(domain.WidthDirection)).ToArray();
            var thicknessCenter = new Vector3d(domain.Corners[0], flatMaterial.PointList[0]).Dot(domain.PlaneNormal);
            occupants.Add(new StartBridgeGapOccupant
            {
                IsBridge = isBridge, Thickness = CreateThicknessInterval(thicknessCenter, flatMaterial.BusBarThickness),
                Projection = new LinkageSupportProjectionOccupant
                {
                    LengthMinimum = lengthCoordinates.Min(), LengthMaximum = lengthCoordinates.Max(),
                    WidthMinimum = widthCoordinates.Min(), WidthMaximum = widthCoordinates.Max()
                }
            });
        }
    }
}
