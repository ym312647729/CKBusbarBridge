using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteCandidateFactory
    {
        private sealed class SupportReductionFace
        {
            internal PlanarBendBusbarSetting Setting;
            internal int SegmentIndex, MemberIndex = -1, PartIndex = -1;
            internal bool IsSupport, IsFixed;
        }

        private sealed class SupportReductionProposal
        {
            internal SupportReductionFace MovingFace, HostFace;
            internal Vector3d Displacement;
            internal double TotalSupportThickness;
        }

        /// <summary>
        /// 只处理已经完整成组的输出副本。每次减少一个真实接口的垫块后，
        /// 重建其余真实间隙并执行完整候选及整套发布校验；失败不改变来源。
        /// </summary>
        internal static LinkageRouteCandidate CreateSupportReducedOutputCandidate(
            LinkageMultiGroupRouteRequest request, LinkageRouteCandidate source,
            IReadOnlyList<LinkageRouteSolid> staticSolids,
            Func<LinkageRouteCandidate, bool> validateCompleteSolution,
            CancellationToken cancellationToken)
        {
            if (!request.MinimizeSupportBlocks || source?.IsValid != true ||
                cancellationToken.IsCancellationRequested) return source;
            var retained = source;
            // 每次接受都严格减少实际垫块实体数量，因此不会来回移动或无限迭代。
            while (!cancellationToken.IsCancellationRequested)
            {
                var previousCount = SupportReductionCount(retained.PlanarConversion);
                if (previousCount == 0) break;
                LinkageRouteCandidate accepted = null;
                foreach (var proposal in BuildSupportReductionProposals(retained.PlanarConversion,
                    retained.GroupRequest, cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested) return retained;
                    var trial = CopyLinkedTrialCandidate(retained);
                    trial.SearchPriority = retained.SearchPriority;
                    trial.PlanarConversion.TerminalHoleLapGeometryAdjusted =
                        retained.PlanarConversion.TerminalHoleLapGeometryAdjusted;
                    trial.PlanarConversion.SupportReductionGeometryAdjusted = true;
                    foreach (var diagnostic in retained.PlanarConversion.Diagnostics)
                        trial.PlanarConversion.Diagnostics.Add(diagnostic);
                    var movingMember = trial.PlanarConversion.StackMembers.Single(member =>
                        member.MemberIndex == proposal.MovingFace.MemberIndex);
                    var atStart = proposal.MovingFace.SegmentIndex == 0;
                    if (!TryMoveSupportReductionChain(trial.PlanarConversion, movingMember,
                            proposal.MovingFace.PartIndex, atStart, proposal.Displacement) ||
                        !SynchronizeLinkedReference(retained.PlanarConversion, trial.PlanarConversion)) continue;
                    var movedPart = movingMember.PlanarParts[proposal.MovingFace.PartIndex];
                    var actualHost = proposal.HostFace.MemberIndex < 0 ? proposal.HostFace.Setting :
                        trial.PlanarConversion.StackMembers.Single(member => member.MemberIndex ==
                            proposal.HostFace.MemberIndex).PlanarParts[proposal.HostFace.PartIndex];
                    // 使用实际最终平直面复核，不能把局部回退/不足量偏移当作已经贴合。
                    if (!LinkageRouteTerminalHoleLapGeometry.HavePhysicalFaceContact(movedPart,
                        proposal.MovingFace.SegmentIndex, actualHost,
                        proposal.HostFace.SegmentIndex)) continue;
                    if (!LinkageRouteTerminalHoleLapValidator.TryRebuildSupports(
                        trial.PlanarConversion, request, trial.GroupRequest) ||
                        SupportReductionCount(trial.PlanarConversion) >= previousCount) continue;
                    // 调整及重建已完成，只省去再次改变几何的修复/构造步骤；
                    // 圆角搭接、制造、所有实体、孔边距、包围盒与绝缘门禁照常完整执行。
                    trial = ValidatePreparedCandidateCore(trial, request, trial.GroupRequest,
                        staticSolids, cancellationToken);
                    if (trial?.IsValid != true || validateCompleteSolution?.Invoke(trial) != true) continue;
                    trial.PlanarConversion.Diagnostics.Add(string.Format(CultureInfo.InvariantCulture,
                        "尽量减少垫块：成员{0}物理段{1}贴合原搭接面，接口垫块总厚{2:F6} mm，" +
                        "移动排单层厚{3:F6} mm，垫块{4}→{5}；完整候选和整套发布校验通过。",
                        movingMember.MemberIndex, movedPart.PhysicalSectionIndex,
                        proposal.TotalSupportThickness, movedPart.BusBarThickness,
                        previousCount, SupportReductionCount(trial.PlanarConversion)));
                    accepted = trial;
                    break;
                }
                if (accepted == null) break;
                retained = accepted;
            }
            return retained;
        }

        private static int SupportReductionCount(LinkageRouteCenterlineConversionResult conversion)
            => conversion.Accessories.Count(item => item?.Kind == LinkageRouteAccessoryKind.SupportBlock);

        private static IEnumerable<SupportReductionProposal> BuildSupportReductionProposals(
            LinkageRouteCenterlineConversionResult conversion, LinkageRouteGroupRequest group,
            CancellationToken cancellationToken)
        {
            var hosts = new List<SupportReductionFace>();
            // 物理始端优先；固定排只提供目标面，绝不写入或平移。
            foreach (var terminal in new[] { group.StartTerminal, group.EndTerminal })
                foreach (var terminalMember in terminal?.Members ?? new List<LinkageTerminalMember>())
                    AddSupportReductionFaces(hosts, terminalMember.FixedBusbar, isFixed: true);
            var moving = new List<SupportReductionFace>();
            foreach (var member in conversion.StackMembers)
                for (var partIndex = 0; partIndex < member.PlanarParts.Count; partIndex++)
                {
                    var part = member.PlanarParts[partIndex];
                    AddSupportReductionFaces(hosts, part, member.MemberIndex, partIndex);
                    if (conversion.DoubleStartPhysicalBridgeSectionIndices.Contains(part.PhysicalSectionIndex)) continue;
                    moving.Add(new SupportReductionFace { Setting = part, MemberIndex = member.MemberIndex,
                        PartIndex = partIndex, SegmentIndex = 0 });
                    if (part.PointList.Count > 2) moving.Add(new SupportReductionFace { Setting = part,
                        MemberIndex = member.MemberIndex, PartIndex = partIndex, SegmentIndex = part.PointList.Count - 2 });
                }
            var supports = new List<SupportReductionFace>();
            foreach (var accessory in conversion.Accessories)
                AddSupportReductionFaces(accessory.Kind == LinkageRouteAccessoryKind.SupportBlock ? supports : hosts,
                    accessory.Setting, isSupport: accessory.Kind == LinkageRouteAccessoryKind.SupportBlock);
            var proposals = new List<SupportReductionProposal>();
            foreach (var target in moving)
            {
                if (cancellationToken.IsCancellationRequested) yield break;
                // 只能沿真实接触的垫块链到原宿主，不能跨空隙找一个任意近的面。
                var queue = new Queue<Tuple<SupportReductionFace, double>>();
                var visited = new HashSet<SupportReductionFace>();
                queue.Enqueue(Tuple.Create(target, 0d));
                while (queue.Count > 0)
                {
                    if (cancellationToken.IsCancellationRequested) yield break;
                    var current = queue.Dequeue();
                    if (current.Item1.IsSupport)
                        foreach (var host in hosts.Where(face => !ReferenceEquals(face.Setting, target.Setting)))
                        {
                            if (!SupportReductionContact(current.Item1, host) ||
                                !TryCreateSupportReductionDisplacement(target, host, current.Item2, out var displacement)) continue;
                            proposals.Add(new SupportReductionProposal { MovingFace = target, HostFace = host,
                                Displacement = displacement, TotalSupportThickness = current.Item2 });
                        }
                    foreach (var support in supports)
                        if (!visited.Contains(support) && SupportReductionContact(current.Item1, support))
                        {
                            visited.Add(support);
                            queue.Enqueue(Tuple.Create(support, current.Item2 + support.Setting.BusBarThickness));
                        }
                }
            }
            foreach (var proposal in proposals.OrderBy(item => item.HostFace.IsFixed ? 0 : 1)
                .ThenBy(item => item.MovingFace.PartIndex).ThenBy(item => item.Displacement.Norm))
                yield return proposal;
        }

        private static void AddSupportReductionFaces(List<SupportReductionFace> faces,
            PlanarBendBusbarSetting setting, int memberIndex = -1, int partIndex = -1,
            bool isSupport = false, bool isFixed = false)
        {
            if (setting?.PointList == null) return;
            for (var segment = 0; segment + 1 < setting.PointList.Count; segment++)
                faces.Add(new SupportReductionFace { Setting = setting, SegmentIndex = segment,
                    MemberIndex = memberIndex, PartIndex = partIndex, IsSupport = isSupport, IsFixed = isFixed });
        }

        private static bool SupportReductionContact(SupportReductionFace first, SupportReductionFace second)
            => !ReferenceEquals(first.Setting, second.Setting) &&
                LinkageRouteTerminalHoleLapGeometry.HavePhysicalFaceContact(first.Setting, first.SegmentIndex,
                    second.Setting, second.SegmentIndex);

        private static bool IsSupportReductionStraight(PlanarBendBusbarSetting part)
        {
            var direction = new Vector3d(part.PointList[0], part.PointList[1]).Normalized;
            return part.PointList.Skip(1).All(point =>
                new Vector3d(part.PointList[0], point).Cross(direction).Norm <= GeometryTolerance);
        }

        private static bool TryCreateSupportReductionDisplacement(SupportReductionFace target,
            SupportReductionFace host, double totalSupportThickness, out Vector3d displacement)
        {
            displacement = null;
            // 厚度条件按整条接口垫块链计算，严格大于移动排单层厚度；非I排不受该条件限制。
            if (IsSupportReductionStraight(target.Setting) &&
                totalSupportThickness <= target.Setting.BusBarThickness) return false;
            if (!LinkageRouteRoundedLapGeometry.TryGetFlatSegment(target.Setting, target.SegmentIndex, out var moving, out _) ||
                !LinkageRouteRoundedLapGeometry.TryGetFlatSegment(host.Setting, host.SegmentIndex, out var fixedFace, out _)) return false;
            var normal = moving.Direction.Cross(target.Setting.WidthVector.Normalized).Normalized;
            if (normal.Cross(fixedFace.Direction.Cross(host.Setting.WidthVector.Normalized).Normalized).Norm >
                LinkageRouteGeometryTolerances.AssemblyDirection) return false;
            var separation = new Vector3d(moving.Start, fixedFace.Start).Dot(normal);
            var gap = Math.Abs(separation) - (target.Setting.BusBarThickness + host.Setting.BusBarThickness) / 2d;
            if (gap <= GeometryTolerance || Math.Abs(gap - totalSupportThickness) >
                LinkageRouteGeometryTolerances.FinalAcceptanceComparison) return false;
            displacement = LinkedScale(normal, Math.Sign(separation) * gap);
            var probeDisplacement = displacement;
            var directContactProbe = target.Setting.Copy();
            directContactProbe.PointList = directContactProbe.PointList.Select(point => new Point3d(
                point.X + probeDisplacement.X, point.Y + probeDisplacement.Y, point.Z + probeDisplacement.Z)).ToList();
            return LinkageRouteTerminalHoleLapGeometry.HavePhysicalFaceContact(directContactProbe,
                target.SegmentIndex, host.Setting, host.SegmentIndex);
        }

        private static bool TryMoveSupportReductionChain(LinkageRouteCenterlineConversionResult conversion,
            LinkageStackedRouteMember member, int partIndex, bool atStart, Vector3d displacement)
        {
            var indices = atStart ? Enumerable.Range(partIndex, member.PlanarParts.Count - partIndex) :
                Enumerable.Range(0, partIndex + 1).Reverse();
            var movedAnyPart = false;
            foreach (var chainIndex in indices)
            {
                var part = member.PlanarParts[chainIndex];
                if (conversion.DoubleStartPhysicalBridgeSectionIndices.Contains(part.PhysicalSectionIndex)) break;
                if (Math.Abs(displacement.Dot(part.WidthVector.Normalized)) > GeometryTolerance) return movedAnyPart;
                if (IsSupportReductionStraight(part))
                {
                    // I排保持形状，完整传递同一个世界位移，不改造成N排。
                    part.PointList = part.PointList.Select(point => new Point3d(point.X + displacement.X,
                        point.Y + displacement.Y, point.Z + displacement.Z)).ToList();
                    movedAnyPart = true;
                    continue;
                }
                var absorbing = part.Copy();
                if (!atStart)
                {
                    absorbing.PointList.Reverse();
                    absorbing.StartBusbarVector = new Vector3d(absorbing.PointList[0], absorbing.PointList[1])
                        .Normalized.Cross(absorbing.WidthVector.Normalized).Normalized;
                }
                var originalEnd = absorbing.PointList[0].Copy();
                var absorbingDirection = new Vector3d(absorbing.PointList[0], absorbing.PointList[1]).Normalized;
                var absorbingNormal = absorbingDirection.Cross(absorbing.WidthVector.Normalized).Normalized;
                var normalMove = displacement.Dot(absorbingNormal);
                if (Math.Abs(normalMove) > GeometryTolerance &&
                    !LinkageRouteTerminalAccessoryBuilder.TryMoveStartTerminalLapSegment(absorbing,
                        absorbingNormal, normalMove, out _, out _)) return movedAnyPart;
                var alongMove = displacement.Dot(absorbingDirection);
                if (Math.Abs(alongMove) > GeometryTolerance)
                    absorbing.PointList[0] = LinkageRouteSolidGeometry.Translate(
                        absorbing.PointList[0], absorbingDirection, alongMove);
                var actualMove = new Vector3d(originalEnd, absorbing.PointList[0]);
                if (new Vector3d(actualMove.X - displacement.X, actualMove.Y - displacement.Y,
                    actualMove.Z - displacement.Z).Norm > GeometryTolerance) return movedAnyPart;
                part.PointList = (atStart ? absorbing.PointList.AsEnumerable() : absorbing.PointList.AsEnumerable().Reverse())
                    .Select(point => point.Copy()).ToList();
                return true;
            }
            // 后段受限时保留前段已完成的位移，再由补块和完整门禁判断；
            // 没有发生位移时不能返回成功。整次试验被拒绝才丢弃该独立副本。
            return movedAnyPart;
        }
    }
}
