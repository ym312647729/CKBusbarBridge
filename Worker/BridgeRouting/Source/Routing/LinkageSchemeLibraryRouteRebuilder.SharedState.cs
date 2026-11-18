using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GeometRi;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;

namespace RouteLinkBar.Algorithm.Routing
{
    public static partial class LinkageSchemeLibraryRouteRebuilder
    {
        private sealed class HistoricalPhysicalSource
        {
            internal string Key, GroupKey;
            internal int SourceIndex;
            internal bool IsFixed, IsLockedBridge;
            internal PlanarBendBusbarSetting Setting;
            internal LinkageSchemeLibraryRouteTemplateMember OriginalMember;
        }

        private sealed class HistoricalPhysicalContact
        {
            internal HistoricalPhysicalSource First, Second;
            internal int FirstSegment, SecondSegment;
            internal IReadOnlyList<LinkageSchemeLibraryVector> Region;
            internal string PatchSuffix = "";
            internal string Key => "historical-interface:" + First.Key + ":" + FirstSegment +
                "/" + Second.Key + ":" + SecondSegment + PatchSuffix;
        }

        private sealed class HistoricalPhysicalNetwork
        {
            internal readonly Dictionary<string, HistoricalPhysicalSource> Sources =
                new Dictionary<string, HistoricalPhysicalSource>(StringComparer.Ordinal);
            internal readonly List<HistoricalPhysicalContact> Contacts = new List<HistoricalPhysicalContact>();
        }

        private static bool TryCloseSharedHistoricalNetwork(IList<DirectPlacedRoute> traversals, LinkageHistoricalStackContext context,
            double minimumLength, CancellationToken cancellationToken,
            out HistoricalPhysicalNetwork network, out string diagnostic)
        {
            network = new HistoricalPhysicalNetwork();
            diagnostic = null;
            // Reconcile proposals BEFORE any interface is moved. No branch gets
            // to overwrite another branch's incompatible placement of a source.
            foreach (var traversal in traversals)
            {
                var middleIndex = 0;
                foreach (var member in traversal.SourceRoute.OrderedMembers)
                {
                    var isFixed = member.PositionType != MiddlePositionType;
                    var setting = member.PositionType == StartPositionType ? traversal.TargetStartSetting :
                        member.PositionType == CabinetTopPositionType ? traversal.TargetEndSetting : traversal.Settings[middleIndex++];
                    if (setting == null || string.IsNullOrWhiteSpace(member.MemberKey))
                    { diagnostic = "完整搭接图缺少源排或目标固定排。"; return false; }
                    if (network.Sources.TryGetValue(member.MemberKey, out var existing))
                    {
                        if (existing.SourceIndex != member.SourceBusbarIndex || existing.GroupKey != traversal.GroupKey ||
                            existing.IsFixed != isFixed || !SameHistoricalPlacedEntity(existing.Setting, setting))
                        { diagnostic = "共享源排初始落位不一致，不能任选支路覆盖：" + member.MemberKey; return false; }
                    }
                    else network.Sources.Add(member.MemberKey, new HistoricalPhysicalSource
                    {
                        Key = member.MemberKey, SourceIndex = member.SourceBusbarIndex,
                        GroupKey = traversal.GroupKey, IsFixed = isFixed, Setting = setting.Copy(), OriginalMember = member
                    });
                }
                foreach (var sourceInterface in traversal.SourceRoute.OrderedInterfaces)
                {
                    if (!network.Sources.TryGetValue(sourceInterface.FirstMemberKey, out var first) ||
                        !network.Sources.TryGetValue(sourceInterface.SecondMemberKey, out var second) || first == second)
                    { diagnostic = "历史搭接接口的源身份不完整。"; return false; }
                    if (network.Contacts.Any(contact =>
                        contact.First == first && contact.Second == second && contact.FirstSegment == sourceInterface.FirstSegmentIndex && contact.SecondSegment == sourceInterface.SecondSegmentIndex ||
                        contact.First == second && contact.Second == first && contact.FirstSegment == sourceInterface.SecondSegmentIndex && contact.SecondSegment == sourceInterface.FirstSegmentIndex)) continue;
                    network.Contacts.Add(new HistoricalPhysicalContact
                    {
                        First = first, Second = second,
                        FirstSegment = sourceInterface.FirstSegmentIndex, SecondSegment = sourceInterface.SecondSegmentIndex
                    });
                }
            }

            foreach (var originalInterface in context.Interfaces)
            {
                if (originalInterface == null || !network.Sources.TryGetValue(originalInterface.FirstMemberKey, out var first) ||
                    !network.Sources.TryGetValue(originalInterface.SecondMemberKey, out var second) || first == second ||
                    first.GroupKey != second.GroupKey || NetworkSegment(first.Setting, originalInterface.FirstSegmentIndex) == null ||
                    NetworkSegment(second.Setting, originalInterface.SecondSegmentIndex) == null)
                { diagnostic = "全源接口缺少源实体或有效排段，不能忽略路径外接口。"; return false; }
                var existing = network.Contacts.FirstOrDefault(contact =>
                    (contact.Region == null || contact.Region.Count == 0) &&
                    (contact.First == first && contact.Second == second && contact.FirstSegment == originalInterface.FirstSegmentIndex && contact.SecondSegment == originalInterface.SecondSegmentIndex ||
                     contact.First == second && contact.Second == first && contact.FirstSegment == originalInterface.SecondSegmentIndex && contact.SecondSegment == originalInterface.FirstSegmentIndex));
                if (existing == null)
                {
                    existing = new HistoricalPhysicalContact
                    {
                        First = first, Second = second, FirstSegment = originalInterface.FirstSegmentIndex,
                        SecondSegment = originalInterface.SecondSegmentIndex, PatchSuffix = ":patch:" + network.Contacts.Count
                    };
                    network.Contacts.Add(existing);
                }
                existing.Region = originalInterface.CommonPolygon;
            }
            foreach (var bridgeKey in context.LockedBridgeMemberKeys)
            {
                if (string.IsNullOrWhiteSpace(bridgeKey) || !network.Sources.TryGetValue(bridgeKey, out var bridge) ||
                    bridge.IsFixed || bridge.Setting.PointList.Count != 2)
                { diagnostic = "确认连接排缺少合法I排源身份：" + bridgeKey; return false; }
                bridge.IsLockedBridge = true;
            }

            foreach (var fixedContact in network.Contacts.Where(contact => contact.First.IsFixed || contact.Second.IsFixed))
            {
                if (cancellationToken.IsCancellationRequested) { diagnostic = "共享历史排落位已取消。"; return false; }
                if (HasNetworkContact(fixedContact)) continue;
                var movable = fixedContact.First.IsFixed ? fixedContact.Second : fixedContact.First;
                var fixedSource = fixedContact.First.IsFixed ? fixedContact.First : fixedContact.Second;
                if (movable.IsFixed) { diagnostic = "固定端之间存在无法由生成排修正的接口。"; return false; }
                // Confirmed bridges remain locked even at a fixed-terminal interface.
                // Preserve an original spacer gap for final physical materialization.
                if (movable.IsLockedBridge) continue;
                var proposed = new List<PlanarBendBusbarSetting> { movable.Setting };
                var terminal = new LinkageTerminalAssembly
                {
                    Members = new List<LinkageTerminalMember> { new LinkageTerminalMember { FixedBusbar = fixedSource.Setting } }
                };
                if (!TryFitHistoricalTerminal(proposed, 0, terminal, out _)) continue;
                if (movable.Setting.PointList.Count == 2)
                {
                    // 横向位移仍传递整条I链；排长伸缩另验全部原接口与占位。
                    TryApplyNetworkIAdjustment(network, movable, fixedSource, proposed[0], minimumLength, cancellationToken);
                }
                else if (PreservesFixedNetworkContacts(network, movable, proposed[0])) movable.Setting = proposed[0];
            }

            // One source state, one ordered pass. A later bent-leg adjustment
            // can expose a positive earlier gap; final materialization must fill
            // it, rather than oscillating the same shared leg between branches.
            foreach (var contact in network.Contacts.Where(item => !item.First.IsFixed && !item.Second.IsFixed))
            {
                if (cancellationToken.IsCancellationRequested) { diagnostic = "共享历史排落位已取消。"; return false; }
                if (HasNetworkContact(contact)) continue;
                if (TryFitNetworkBentEnd(network, contact.First, contact.Second, minimumLength) ||
                    TryFitNetworkBentEnd(network, contact.Second, contact.First, minimumLength)) continue;
                if (TryMoveNetworkIChain(network, contact.Second, contact.First, minimumLength, cancellationToken)) continue;
                TryMoveNetworkIChain(network, contact.First, contact.Second, minimumLength, cancellationToken);
            }
            if (cancellationToken.IsCancellationRequested) { diagnostic = "共享历史排落位已取消。"; return false; }
            FitNetworkIProjectedBounds(network, minimumLength);
            ExtendNetworkGapBoundaryEndpoints(network);
            foreach (var traversal in traversals)
            {
                var middleSources = traversal.SourceRoute.OrderedMembers.Where(member => member.PositionType == MiddlePositionType).ToList();
                for (var index = 0; index < middleSources.Count; index++)
                    traversal.Settings[index] = network.Sources[middleSources[index].MemberKey].Setting;
            }
            return true;
        }

        private static void ExtendNetworkGapBoundaryEndpoints(HistoricalPhysicalNetwork network)
        {
            // Collect outward-only free-end extensions by physical source. A later
            // interface must not shorten an endpoint already covering another lap.
            // This does not move thickness layers or independently re-solve I chains.
            var boundaryProposals = new Dictionary<HistoricalPhysicalSource, PlanarBendBusbarSetting>();
            foreach (var gapContact in network.Contacts.Where(item => item.Region == null || item.Region.Count == 0))
            {
                var firstSlice = NetworkSegment(gapContact.First.Setting, gapContact.FirstSegment);
                var secondSlice = NetworkSegment(gapContact.Second.Setting, gapContact.SecondSegment);
                if (firstSlice == null || secondSlice == null ||
                    !LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(firstSlice, true, secondSlice, true,
                        Math.Max(firstSlice.BusBarWidth, secondSlice.BusBarWidth), out var projectedPatch)) continue;
                var planeDistance = Math.Abs(new Vector3d(firstSlice.PointList[0], secondSlice.PointList[0])
                    .Dot(projectedPatch.PlaneNormal));
                if (planeDistance - (firstSlice.BusBarThickness + secondSlice.BusBarThickness) / 2 <=
                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison) continue;

                foreach (var boundaryBinding in new[]
                {
                    Tuple.Create(gapContact.First, gapContact.FirstSegment, secondSlice),
                    Tuple.Create(gapContact.Second, gapContact.SecondSegment, firstSlice)
                })
                {
                    var boundarySource = boundaryBinding.Item1;
                    var boundarySegment = boundaryBinding.Item2;
                    if (boundarySource.IsFixed || boundarySource.IsLockedBridge || boundarySource.Setting.PointList.Count <= 2 ||
                        boundarySegment != 0 && boundarySegment != boundarySource.Setting.PointList.Count - 2) continue;
                    if (!boundaryProposals.TryGetValue(boundarySource, out var boundaryProposal))
                    {
                        boundaryProposal = boundarySource.Setting.Copy();
                        boundaryProposals.Add(boundarySource, boundaryProposal);
                    }
                    ExtendHistoricalInterfaceEnd(boundaryProposal, boundarySegment == 0, boundaryBinding.Item3, true);
                }
            }
            foreach (var boundaryProposal in boundaryProposals)
                if (PreservesFixedNetworkContacts(network, boundaryProposal.Key, boundaryProposal.Value, checkStationarySources: true))
                    boundaryProposal.Key.Setting = boundaryProposal.Value;
        }

        private static bool TryFitNetworkBentEnd(HistoricalPhysicalNetwork network,
            HistoricalPhysicalSource movable, HistoricalPhysicalSource anchor, double minimumLength)
        {
            if (movable.IsFixed || movable.IsLockedBridge || movable.Setting.PointList.Count < 3 ||
                !TryFitHistoricalMovableEnd(movable.Setting, anchor.Setting, minimumLength, out var fitted) ||
                !PreservesFixedNetworkContacts(network, movable, fitted, checkStationarySources: true)) return false;
            movable.Setting = fitted;
            return true;
        }

        private static bool TryMoveNetworkIChain(HistoricalPhysicalNetwork network,
            HistoricalPhysicalSource follower, HistoricalPhysicalSource anchor, double minimumLength,
            CancellationToken cancellationToken)
        {
            if (follower.IsFixed || follower.IsLockedBridge || follower.Setting.PointList.Count != 2 ||
                !TryFitHistoricalStraightFollower(follower.Setting, anchor.Setting, minimumLength, out var fitted)) return false;
            return TryApplyNetworkIAdjustment(network, follower, anchor, fitted, minimumLength, cancellationToken);
        }

        private static bool TryApplyNetworkITranslation(HistoricalPhysicalNetwork network,
            HistoricalPhysicalSource follower, HistoricalPhysicalSource anchor, PlanarBendBusbarSetting fitted,
            double minimumLength, CancellationToken cancellationToken)
        {
            if (follower.IsFixed || follower.IsLockedBridge || follower.Setting.PointList.Count != 2 ||
                fitted?.PointList?.Count != 2) return false;
            var displacement = new Vector3d(follower.Setting.PointList[0], fitted.PointList[0]);
            // An I-chain propagates a rigid translation, not a different fitted
            // displacement or a length change solved independently at each edge.
            if ((new Vector3d(follower.Setting.PointList[1], fitted.PointList[1]) - displacement).Norm > DirectPlacementTolerance ||
                displacement.Norm <= DirectPlacementTolerance || !PreservesFixedNetworkContacts(network, follower, fitted)) return false;
            var pending = new Queue<HistoricalPhysicalSource>();
            var visited = new HashSet<HistoricalPhysicalSource> { anchor };
            pending.Enqueue(follower);
            var movedAny = false;
            while (pending.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested) return false;
                var current = pending.Dequeue();
                if (!visited.Add(current) || current.IsFixed || current.IsLockedBridge) continue;
                if (current.Setting.PointList.Count != 2) continue;
                var translated = current.Setting.Copy();
                TranslateHistoricalPart(translated, displacement);
                if (!PreservesFixedNetworkContacts(network, current, translated)) continue;
                current.Setting = translated;
                movedAny = true;
                foreach (var incident in network.Contacts.Where(item => item.First == current || item.Second == current))
                {
                    var neighbor = incident.First == current ? incident.Second : incident.First;
                    if (visited.Contains(neighbor) || neighbor.IsFixed || neighbor.IsLockedBridge) continue;
                    if (neighbor.Setting.PointList.Count == 2) pending.Enqueue(neighbor);
                    else TryFitNetworkBentEnd(network, neighbor, current, minimumLength);
                }
            }
            return movedAny;
        }

        private static bool PreservesFixedNetworkContacts(HistoricalPhysicalNetwork network,
            HistoricalPhysicalSource movable, PlanarBendBusbarSetting proposal, bool checkStationarySources = false)
        {
            foreach (var contact in network.Contacts.Where(item =>
                item.First == movable && item.Second.IsFixed || item.Second == movable && item.First.IsFixed))
            {
                if (!HasNetworkContact(contact)) continue;
                if (!HasNetworkContact(contact, movable, proposal)) return false;
            }
            // A bent-end adjustment does not propagate displacement to other sources.
            // Its full proposed solid must therefore respect every stationary source,
            // not just fixed ends. Positive residual gaps can still be padded later.
            // I-chain translations retain their separate propagation/temporary-gap rule.
            foreach (var protectedSource in network.Sources.Values.Where(source => source != movable &&
                (checkStationarySources || source.IsFixed || source.IsLockedBridge || source.Setting.PointList.Count == 2 && network.Contacts.Any(contact =>
                    contact.First == source && contact.Second.IsFixed || contact.Second == source && contact.First.IsFixed))))
            {
                for (var proposalSegment = 0; proposalSegment + 1 < proposal.PointList.Count; proposalSegment++)
                for (var lockedSegment = 0; lockedSegment + 1 < protectedSource.Setting.PointList.Count; lockedSegment++)
                {
                    var proposedSlice = NetworkSegment(proposal, proposalSegment);
                    var lockedSlice = NetworkSegment(protectedSource.Setting, lockedSegment);
                    if (proposedSlice == null || lockedSlice == null) return false;
                    var proposedSolid = LinkageRouteSolidGeometry.CreateSegmentSolid(movable.GroupKey,
                        proposedSlice.PointList[0], proposedSlice.PointList[1], proposedSlice.WidthVector, proposedSlice.StartBusbarVector,
                        proposedSlice.BusBarWidth, proposedSlice.BusBarThickness, proposalSegment, movable.GroupKey, LinkageRouteSolidRole.GeneratedBusbar);
                    var lockedSolid = LinkageRouteSolidGeometry.CreateSegmentSolid(protectedSource.GroupKey,
                        lockedSlice.PointList[0], lockedSlice.PointList[1], lockedSlice.WidthVector, lockedSlice.StartBusbarVector,
                        lockedSlice.BusBarWidth, lockedSlice.BusBarThickness, lockedSegment, protectedSource.GroupKey, LinkageRouteSolidRole.GeneratedBusbar);
                    if (LinkageRouteSolidDistance.HasPositiveVolumeIntersection(proposedSolid, lockedSolid,
                        LinkageRouteGeometryTolerances.FinalAcceptanceComparison)) return false;
                }
            }
            return true;
        }

        private static PlanarBendBusbarSetting NetworkSegment(PlanarBendBusbarSetting source, int segment)
        {
            if (source?.PointList == null || segment < 0 || segment + 1 >= source.PointList.Count) return null;
            var result = source.Copy();
            result.PointList = new List<Point3d> { CopyPoint(source.PointList[segment]), CopyPoint(source.PointList[segment + 1]) };
            var segmentDirection = new Vector3d(result.PointList[0], result.PointList[1]);
            if (segmentDirection.Norm <= DirectPlacementTolerance) return null;
            result.StartBusbarVector = segmentDirection.Cross(source.WidthVector).Normalized;
            result.StartLapWidth = result.EndLapWidth = 0;
            return result;
        }

        private static bool HasNetworkContact(HistoricalPhysicalContact contact,
            HistoricalPhysicalSource replaced = null, PlanarBendBusbarSetting replacement = null)
        {
            var first = NetworkSegment(contact.First == replaced ? replacement : contact.First.Setting, contact.FirstSegment);
            var second = NetworkSegment(contact.Second == replaced ? replacement : contact.Second.Setting, contact.SecondSegment);
            return first != null && second != null && HasHistoricalLap(first, true, second, true,
                LinkageRouteGeometryTolerances.FinalAcceptanceComparison) &&
                (contact.Region?.Count > 0
                    ? LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(first, true, second, true,
                        Math.Max(first.BusBarWidth, second.BusBarWidth), out var regionFootprint) &&
                        TryRestrictHistoricalContactRegion(contact, regionFootprint, replaced, replacement)
                    : PreservesOriginalDirectContactPatch(contact, first, second));
        }

        private static bool PreservesOriginalDirectContactPatch(HistoricalPhysicalContact contact,
            PlanarBendBusbarSetting currentFirst, PlanarBendBusbarSetting currentSecond)
        {
            var originalSlices = new List<PlanarBendBusbarSetting>();
            foreach (var originalBinding in new[]
            { Tuple.Create(contact.First.OriginalMember, contact.FirstSegment), Tuple.Create(contact.Second.OriginalMember, contact.SecondSegment) })
            {
                var originalMember = originalBinding.Item1;
                if (originalMember?.CenterlinePoints == null || originalMember.WidthDirection == null ||
                    originalMember.CenterlinePoints.Any(point => point == null)) return false;
                var originalSlice = NetworkSegment(new PlanarBendBusbarSetting
                {
                    BusBarWidth = originalMember.BusbarWidth, BusBarThickness = originalMember.BusbarThickness,
                    WidthVector = ToVector(originalMember.WidthDirection),
                    PointList = originalMember.CenterlinePoints.Select(HistoricalEvidencePoint).ToList()
                }, originalBinding.Item2);
                if (originalSlice == null) return false;
                originalSlices.Add(originalSlice);
            }
            if (!LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(originalSlices[0], true,
                    originalSlices[1], true, Math.Max(originalSlices[0].BusBarWidth, originalSlices[1].BusBarWidth), out var originalPatch) ||
                !LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(currentFirst, true,
                    currentSecond, true, Math.Max(currentFirst.BusBarWidth, currentSecond.BusBarWidth), out var currentPatch)) return false;
            // Compare each patch in its own first-source frame. Rigid placement
            // may rotate the interface, and a larger real overlap is permitted,
            // but minimum full lap alone cannot replace the original dimensions.
            var patchTolerance = LinkageRouteGeometryTolerances.FinalAcceptanceComparison;
            return HistoricalPatchSpan(currentPatch, currentPatch.LengthDirection) + patchTolerance >=
                    HistoricalPatchSpan(originalPatch, originalPatch.LengthDirection) &&
                HistoricalPatchSpan(currentPatch, currentPatch.WidthDirection) + patchTolerance >=
                    HistoricalPatchSpan(originalPatch, originalPatch.WidthDirection);
        }

        private static double HistoricalPatchSpan(LinkageRouteLapFootprint patch, Vector3d direction)
        {
            var coordinates = patch.Corners.Select(point => new Vector3d(patch.Corners[0], point).Dot(direction)).ToList();
            return coordinates.Max() - coordinates.Min();
        }

        private static bool TryMaterializeNetworkGaps(HistoricalPhysicalNetwork network,
            IList<DirectPlacedRoute> traversals, LinkageRouteSolution output, CancellationToken cancellationToken, out string diagnostic)
        {
            diagnostic = null;
            // Endpoint normalization may replace an output setting: rebind every
            // source to its actual final output address before measuring gaps.
            foreach (var traversal in traversals)
            {
                var group = output.GroupSolutions.Single(item => item.GroupKey == traversal.GroupKey);
                var member = group.StackMembers.Single(item => item.MemberIndex == traversal.OutputMemberIndex);
                var sourceKeys = HistoricalSourceKeys(traversal).ToList();
                for (var index = 0; index < sourceKeys.Count; index++)
                    network.Sources[sourceKeys[index].Item1].Setting = member.PlanarParts[traversal.OutputPartIndices[index]];
            }
            foreach (var contact in network.Contacts)
            {
                if (cancellationToken.IsCancellationRequested)
                { diagnostic = "完整接口补块已取消。"; return false; }
                if (HasNetworkContact(contact)) continue;
                var first = NetworkSegment(contact.First.Setting, contact.FirstSegment);
                var second = NetworkSegment(contact.Second.Setting, contact.SecondSegment);
                if (first == null || second == null ||
                    !LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(first, true, second, true,
                        Math.Max(first.BusBarWidth, second.BusBarWidth), out var footprint))
                { diagnostic = "原接口缺少可补块的公共搭接区域：" + contact.Key; return false; }
                if (!TryRestrictHistoricalContactRegion(contact, footprint))
                { diagnostic = "源垫块共同区域无法可靠映射到当前接口：" + contact.Key; return false; }
                var signedDistance = new Vector3d(first.PointList[0], second.PointList[0]).Dot(footprint.PlaneNormal);
                var gap = Math.Abs(signedDistance) - (first.BusBarThickness + second.BusBarThickness) / 2;
                if (gap <= LinkageRouteGeometryTolerances.FinalAcceptanceComparison)
                { diagnostic = "原接口存在重叠或未形成满搭接，不能用垫块掩盖：" + contact.Key; return false; }
                var normal = footprint.PlaneNormal * Math.Sign(signedDistance);
                var origin = footprint.Corners[0];
                var lengthCoordinates = footprint.Corners.Select(point => new Vector3d(origin, point).Dot(footprint.LengthDirection)).ToList();
                var widthCoordinates = footprint.Corners.Select(point => new Vector3d(origin, point).Dot(footprint.WidthDirection)).ToList();
                var blockWidth = widthCoordinates.Max() - widthCoordinates.Min();
                var widthCenter = (widthCoordinates.Max() + widthCoordinates.Min()) / 2;
                var thicknessLimit = Math.Min(first.BusBarThickness, second.BusBarThickness);
                if (!IsFinitePositiveHistoricalDimension(thicknessLimit) || !IsFinitePositiveHistoricalDimension(gap) ||
                    !IsFinitePositiveHistoricalDimension(blockWidth))
                { diagnostic = "接口补块尺寸不是有限正数：" + contact.Key; return false; }
                var group = output.GroupSolutions.Single(item => item.GroupKey == contact.First.GroupKey);
                var filled = 0d;
                var layerIndex = 0;
                while (gap - filled > DirectPlacementTolerance)
                {
                    if (cancellationToken.IsCancellationRequested)
                    { diagnostic = "完整接口分层补块已取消。"; return false; }
                    var layerThickness = Math.Min(thicknessLimit, gap - filled);
                    if (filled + layerThickness <= filled)
                    { diagnostic = "接口分层厚度无法推进：" + contact.Key; return false; }
                    var shift = footprint.WidthDirection * widthCenter + normal * (first.BusBarThickness / 2 + filled + layerThickness / 2);
                    var gapAccessory = new LinkageRouteAccessory
                    {
                        Kind = LinkageRouteAccessoryKind.SupportBlock, RelatedGeneratedMemberIndex = -1,
                        SupportBlockKey = contact.Key + "|layer:" + layerIndex++,
                        Setting = new PlanarBendBusbarSetting
                        {
                            GroupKey = group.GroupKey, BusBarWidth = blockWidth, BusBarThickness = layerThickness,
                            WidthVector = footprint.WidthDirection, StartBusbarVector = normal,
                            PointList = new List<Point3d>
                            {
                                MoveHistoricalPoint(origin, shift + footprint.LengthDirection * lengthCoordinates.Min()),
                                MoveHistoricalPoint(origin, shift + footprint.LengthDirection * lengthCoordinates.Max())
                            }
                        }
                    };
                    if (!TryValidateNetworkGapBlockPlacement(network, output, gapAccessory, contact.First.GroupKey, out diagnostic)) return false;
                    group.Accessories.Add(gapAccessory);
                    filled += layerThickness;
                }
            }
            return ValidateMaterializedNetworkContacts(network, output, out diagnostic);
        }

        private static bool TryValidateNetworkGapBlockPlacement(HistoricalPhysicalNetwork network, LinkageRouteSolution output,
            LinkageRouteAccessory gapAccessory, string gapGroupKey, out string diagnostic)
        {
            diagnostic = null;
            var gapSlice = NetworkSegment(gapAccessory.Setting, 0);
            var gapSolid = LinkageRouteSolidGeometry.CreateSegmentSolid(gapGroupKey,
                gapSlice.PointList[0], gapSlice.PointList[1], gapSlice.WidthVector, gapSlice.StartBusbarVector,
                gapSlice.BusBarWidth, gapSlice.BusBarThickness, 0, gapGroupKey, LinkageRouteSolidRole.GeneratedBusbar);
            var occupiedSettings = network.Sources.Values.Select(source => Tuple.Create(source.Key, source.GroupKey, source.Setting))
                .Concat(output.GroupSolutions.SelectMany(group => group.Accessories.Select(accessory =>
                    Tuple.Create(accessory.SupportBlockKey ?? "历史附件", group.GroupKey, accessory.Setting))));
            foreach (var occupiedBinding in occupiedSettings)
            {
                if (occupiedBinding.Item3?.PointList == null)
                { diagnostic = "接口补块缺少已有实体的占位证据：" + occupiedBinding.Item1; return false; }
                for (var occupiedSegment = 0; occupiedSegment + 1 < occupiedBinding.Item3.PointList.Count; occupiedSegment++)
                {
                    var occupiedSlice = NetworkSegment(occupiedBinding.Item3, occupiedSegment);
                    if (occupiedSlice == null)
                    { diagnostic = "接口补块遇到无效源排段：" + occupiedBinding.Item1; return false; }
                    var occupiedSolid = LinkageRouteSolidGeometry.CreateSegmentSolid(occupiedBinding.Item2,
                        occupiedSlice.PointList[0], occupiedSlice.PointList[1], occupiedSlice.WidthVector, occupiedSlice.StartBusbarVector,
                        occupiedSlice.BusBarWidth, occupiedSlice.BusBarThickness, occupiedSegment, occupiedBinding.Item2, LinkageRouteSolidRole.GeneratedBusbar);
                    if (!LinkageRouteSolidDistance.HasPositiveVolumeIntersection(gapSolid, occupiedSolid,
                        LinkageRouteGeometryTolerances.FinalAcceptanceComparison)) continue;
                    // Existing copper cannot silently replace an original direct
                    // interface, nor may we duplicate its solid with a new pad.
                    diagnostic = "原接口补块区域已被实体占用，不能重复补块或擅自改变搭接关系：" +
                        gapAccessory.SupportBlockKey + "；占位源=" + occupiedBinding.Item1 + "，排段=" + occupiedSegment;
                    return false;
                }
            }
            return true;
        }

        private static Point3d HistoricalEvidencePoint(LinkageSchemeLibraryVector point) => new Point3d(point.X, point.Y, point.Z);

        private static bool IsFinitePositiveHistoricalDimension(double value) => value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);

        private static bool TryRestrictHistoricalContactRegion(HistoricalPhysicalContact contact, LinkageRouteLapFootprint footprint,
            HistoricalPhysicalSource replaced = null, PlanarBendBusbarSetting replacement = null)
        {
            if (contact.Region == null || contact.Region.Count == 0)
            {
                // A direct copper interface still requires full in-plane lap.
                // A pad may fill its thickness gap, never conceal a shortened
                // width/length by adopting the smaller projected intersection.
                var first = NetworkSegment(contact.First.Setting, contact.FirstSegment);
                var second = NetworkSegment(contact.Second.Setting, contact.SecondSegment);
                if (first == null || second == null) return false;
                var normal = footprint.PlaneNormal.Normalized;
                var signedDistance = new Vector3d(first.PointList[0], second.PointList[0]).Dot(normal);
                var translatedSecond = second.Copy();
                TranslateHistoricalPart(translatedSecond, normal *
                    (Math.Sign(signedDistance) * (first.BusBarThickness + second.BusBarThickness) / 2 - signedDistance));
                return HasHistoricalLap(first, true, translatedSecond, true,
                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison) &&
                    PreservesOriginalDirectContactPatch(contact, first, second);
            }
            if (contact.Region.Count < 3 || contact.Region.Any(point => point == null)) return false;
            var origin = footprint.Corners[0];
            var minimumLength = footprint.Corners.Min(point => new Vector3d(origin, point).Dot(footprint.LengthDirection));
            var maximumLength = footprint.Corners.Max(point => new Vector3d(origin, point).Dot(footprint.LengthDirection));
            var minimumWidth = footprint.Corners.Min(point => new Vector3d(origin, point).Dot(footprint.WidthDirection));
            var maximumWidth = footprint.Corners.Max(point => new Vector3d(origin, point).Dot(footprint.WidthDirection));
            var requiredRegionLength = 0d;
            var requiredRegionWidth = 0d;
            foreach (var binding in new[] { Tuple.Create(contact.First, contact.FirstSegment), Tuple.Create(contact.Second, contact.SecondSegment) })
            {
                var source = binding.Item1;
                var regionSetting = source == replaced ? replacement : source.Setting;
                var segment = binding.Item2;
                var originalStart = HistoricalEvidencePoint(source.OriginalMember.CenterlinePoints[segment]);
                var originalEnd = HistoricalEvidencePoint(source.OriginalMember.CenterlinePoints[segment + 1]);
                var originalLength = new Vector3d(originalStart, originalEnd);
                var originalWidth = ToVector(source.OriginalMember.WidthDirection).Normalized;
                if (originalLength.Norm <= DirectPlacementTolerance) return false;
                var anchorAtEnd = contact.Region.Average(point => new Vector3d(originalStart, HistoricalEvidencePoint(point)).Dot(originalLength.Normalized)) > originalLength.Norm / 2;
                var originalAnchor = anchorAtEnd ? originalEnd : originalStart;
                var currentAnchor = regionSetting.PointList[segment + (anchorAtEnd ? 1 : 0)];
                var currentLength = new Vector3d(regionSetting.PointList[segment], regionSetting.PointList[segment + 1]).Normalized;
                var mappedPolygon = contact.Region.Select(point => MoveHistoricalPoint(currentAnchor,
                    currentLength * new Vector3d(originalAnchor, HistoricalEvidencePoint(point)).Dot(originalLength.Normalized) +
                    regionSetting.WidthVector.Normalized * new Vector3d(originalAnchor, HistoricalEvidencePoint(point)).Dot(originalWidth))).ToList();
                var lengthCoordinates = mappedPolygon.Select(point => new Vector3d(origin, point).Dot(footprint.LengthDirection)).ToList();
                var widthCoordinates = mappedPolygon.Select(point => new Vector3d(origin, point).Dot(footprint.WidthDirection)).ToList();
                var rectangleArea = (lengthCoordinates.Max() - lengthCoordinates.Min()) * (widthCoordinates.Max() - widthCoordinates.Min());
                var polygonArea = 0d;
                for (var index = 0; index < mappedPolygon.Count; index++)
                {
                    var next = (index + 1) % mappedPolygon.Count;
                    polygonArea += lengthCoordinates[index] * widthCoordinates[next] - lengthCoordinates[next] * widthCoordinates[index];
                }
                // Do not replace a diagonal or clipped non-rectangular source
                // patch by a larger bounding rectangle. Such input stays unresolved.
                if (rectangleArea <= DirectPlacementTolerance || Math.Abs(Math.Abs(polygonArea) / 2 - rectangleArea) > 1e-6 * Math.Max(1, rectangleArea)) return false;
                requiredRegionLength = Math.Max(requiredRegionLength, lengthCoordinates.Max() - lengthCoordinates.Min());
                requiredRegionWidth = Math.Max(requiredRegionWidth, widthCoordinates.Max() - widthCoordinates.Min());
                minimumLength = Math.Max(minimumLength, lengthCoordinates.Min());
                maximumLength = Math.Min(maximumLength, lengthCoordinates.Max());
                minimumWidth = Math.Max(minimumWidth, widthCoordinates.Min());
                maximumWidth = Math.Min(maximumWidth, widthCoordinates.Max());
            }
            if (maximumLength - minimumLength <= DirectPlacementTolerance || maximumWidth - minimumWidth <= DirectPlacementTolerance) return false;
            if (maximumLength - minimumLength + LinkageRouteGeometryTolerances.FinalAcceptanceComparison < requiredRegionLength ||
                maximumWidth - minimumWidth + LinkageRouteGeometryTolerances.FinalAcceptanceComparison < requiredRegionWidth) return false;
            footprint.Corners = new[]
            {
                MoveHistoricalPoint(origin, footprint.LengthDirection * minimumLength + footprint.WidthDirection * minimumWidth),
                MoveHistoricalPoint(origin, footprint.LengthDirection * maximumLength + footprint.WidthDirection * minimumWidth),
                MoveHistoricalPoint(origin, footprint.LengthDirection * maximumLength + footprint.WidthDirection * maximumWidth),
                MoveHistoricalPoint(origin, footprint.LengthDirection * minimumLength + footprint.WidthDirection * maximumWidth)
            };
            return true;
        }

        private static bool ValidateMaterializedNetworkContacts(HistoricalPhysicalNetwork network,
            LinkageRouteSolution output, out string diagnostic)
        {
            diagnostic = null;
            foreach (var contact in network.Contacts)
            {
                if (HasNetworkContact(contact)) continue;
                var first = NetworkSegment(contact.First.Setting, contact.FirstSegment);
                var second = NetworkSegment(contact.Second.Setting, contact.SecondSegment);
                var blocks = output.GroupSolutions.Where(group => group.GroupKey == contact.First.GroupKey)
                    .SelectMany(group => group.Accessories).Where(accessory => accessory.Kind == LinkageRouteAccessoryKind.SupportBlock &&
                        (accessory.SupportBlockKey ?? "").StartsWith(contact.Key + "|layer:", StringComparison.Ordinal))
                    .Select(accessory => accessory.Setting).ToList();
                if (first == null || second == null || blocks.Count == 0 ||
                    !LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(first, true, second, true,
                        Math.Max(first.BusBarWidth, second.BusBarWidth), out var requiredPatch) ||
                    !TryRestrictHistoricalContactRegion(contact, requiredPatch))
                { diagnostic = "原始接口没有实际闭合：" + contact.Key; return false; }
                var direction = new Vector3d(first.PointList[0], second.PointList[0]);
                blocks = blocks.OrderBy(block => new Vector3d(first.PointList[0], block.PointList[0]).Dot(direction)).ToList();
                var chain = new[] { first }.Concat(blocks).Concat(new[] { second }).ToList();
                if (chain.Zip(chain.Skip(1), (left, right) =>
                    TryCreateHistoricalPatchContact(left, right, requiredPatch, out _)).Any(valid => !valid))
                { diagnostic = "原始接口垫块未填满实际间隙：" + contact.Key; return false; }
            }
            return true;
        }
    }
}
