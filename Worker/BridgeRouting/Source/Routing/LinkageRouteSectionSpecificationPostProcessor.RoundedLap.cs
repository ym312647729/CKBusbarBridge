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
    internal static partial class LinkageRouteSectionSpecificationPostProcessor
    {
        private sealed class RoundedLapRequirement
        {
            internal PlanarBendBusbarSetting Member;
            internal bool AtStart;
            internal int SegmentIndex;
            internal IReadOnlyList<Point3d> Corners;
            internal int SectionIndex;
            internal bool IsFixed;
        }

        internal static bool TryValidateRoundedLapInterfaces(LinkageRouteCenterlineConversionResult conversion,
            LinkageMultiGroupRouteRequest request, LinkageRouteGroupRequest group, out string failureReason)
        {
            if (!TryCollectRoundedLapRequirements(conversion, group, out _, out var requirements, out failureReason))
                return false;
            foreach (var requirement in requirements)
            {
                if (!LinkageRouteRoundedLapGeometry.HasFlatSegmentLapCoverage(requirement.Member, requirement.SegmentIndex,
                        requirement.Corners, out _, out failureReason)) return false;
                if (!requirement.IsFixed && GetRoundedExtension(requirement, request,
                        request.AllowConstrainedBendAllowanceReduction) >
                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison)
                {
                    failureReason = "实际铜排搭接边至圆弧的预留小于本排采用的折弯余量。";
                    return false;
                }
            }
            return true;
        }

        // 放在局部几何调整后：只记录真正受限成员的采用值，不移动实体，也不改请求或其它成员。
        // 先完整验证再写值，避免失败路径留下部分减小的元数据。
        internal static bool TryReconcileAdjustedRoundedLapAllowances(LinkageRouteCenterlineConversionResult conversion,
            LinkageMultiGroupRouteRequest request, LinkageRouteGroupRequest group, out string failureReason)
        {
            if (!TryValidateRoundedLapInterfaces(conversion, request, group, out failureReason)) return false;
            if (!request.AllowBendGeometricLimit && !request.AllowConstrainedBendAllowanceReduction) return true;
            if (!TryCollectRoundedLapRequirements(conversion, group, out _, out var requirements, out failureReason))
                return false;
            var adjusted = new Dictionary<PlanarBendBusbarSetting, double>();
            foreach (var requirement in requirements.Where(item => !item.IsFixed && item.Member.PointList.Count >= 3))
            {
                if (!TryGetRoundedReserve(requirement, out var available, out _, out failureReason) ||
                    !LinkageRouteRoundedLapGeometry.TryResolveBendAllowance(request, requirement.Member,
                        requirement.AtStart, available, out var applied, out failureReason)) return false;
                var current = adjusted.TryGetValue(requirement.Member, out var previous) ? previous : requirement.Member.BendAllowance;
                adjusted[requirement.Member] = Math.Min(current, applied);
            }
            foreach (var adjustment in adjusted.Where(item => item.Value < item.Key.BendAllowance - GeometryTolerance))
            {
                var previous = adjustment.Key.BendAllowance;
                adjustment.Key.BendAllowance = adjustment.Value;
                conversion.Diagnostics.Add(string.Format(CultureInfo.InvariantCulture,
                    "局部调整后物理段{0}本排实际余量由{1:F3}减为{2:F3} mm；真实圆弧和搭接区满足，其它排及请求不变。",
                    adjustment.Key.PhysicalSectionIndex + 1, previous, adjustment.Value));
            }
            RefreshRoundedCopperLength(conversion);
            return true;
        }

        internal static bool TryRepairRoundedLapInterfaces(LinkageRouteCenterlineConversionResult conversion,
            LinkageMultiGroupRouteRequest request, LinkageRouteGroupRequest group,
            Func<bool> validateAdjustedGeometry, out string failureReason)
        {
            return TryRepairRoundedLapInterfacesCancelable(conversion, request, group,
                validateAdjustedGeometry, out failureReason, CancellationToken.None);
        }

        internal static bool TryRepairRoundedLapInterfacesCancelable(LinkageRouteCenterlineConversionResult conversion,
            LinkageMultiGroupRouteRequest request, LinkageRouteGroupRequest group,
            Func<bool> validateAdjustedGeometry, out string failureReason, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Func<bool> validateCancelableGeometry = () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return validateAdjustedGeometry?.Invoke() ?? true;
            };
            var linkedBaseline = conversion.StackMembers.SelectMany(member => member.PlanarParts).Distinct()
                .ToDictionary(part => part, part => part.Copy());
            if (TryRepairRoundedLapInterfacesCore(conversion, request, group,
                    validateCancelableGeometry, out failureReason, cancellationToken))
            {
                return true;
            }
            var ordinaryFailure = failureReason;
            if (TryRepairLinkedRoundedInterfacesCancelable(conversion, request, group, linkedBaseline,
                    validateCancelableGeometry, out var linkedFailure, cancellationToken))
            {
                failureReason = null; return true;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (TryCompactStackForPhysicalRoundedLap(conversion, request, group,
                    validateCancelableGeometry, out var compactionFailure))
            {
                failureReason = null; return true;
            }
            if (TrySlideInternalIInterfaceForRoundedLap(conversion, request, group,
                    validateCancelableGeometry, out var interfaceFailure))
            {
                failureReason = null; return true;
            }
            failureReason = ordinaryFailure + (string.IsNullOrEmpty(linkedFailure) ? string.Empty : "；延长后相邻接口联动：" + linkedFailure) + (string.IsNullOrEmpty(compactionFailure)
                ? string.Empty : "；局部压紧：" + compactionFailure) +
                (string.IsNullOrEmpty(interfaceFailure) ? string.Empty : "；内部接口矫正：" + interfaceFailure);
            return false;
        }

        private static bool TryRepairRoundedLapInterfacesCore(LinkageRouteCenterlineConversionResult conversion,
            LinkageMultiGroupRouteRequest request, LinkageRouteGroupRequest group,
            Func<bool> validateAdjustedGeometry, out string failureReason, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            failureReason = null;
            if (request == null) { failureReason = "真实圆弧修复缺少请求。"; return false; }
            if (request.AllowBendGeometricLimit)
                foreach (var part in conversion.StackMembers.SelectMany(member => member.PlanarParts))
                    part.BendAllowance = 0d;
            var attemptedBoundaries = new HashSet<Tuple<PlanarBendBusbarSetting, int, bool>>();
            var iterationLimit = Math.Max(4, conversion.CenterlineParts.Count * 6);
            for (var iteration = 0; iteration < iterationLimit; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryCollectRoundedLapRequirements(conversion, group, out var state, out var requirements,
                        out failureReason)) return false;
                foreach (var fixedRequirement in requirements.Where(item => item.IsFixed))
                    if (!LinkageRouteRoundedLapGeometry.HasFlatSegmentLapCoverage(fixedRequirement.Member,
                            fixedRequirement.SegmentIndex, fixedRequirement.Corners, out _, out failureReason))
                    { failureReason = "固定排圆弧侵入搭接区，不能移动固定排：" + failureReason; return false; }
                var pending = requirements.FirstOrDefault(item => !item.IsFixed &&
                    GetRoundedExtension(item, request, false) > LinkageRouteGeometryTolerances.FinalAcceptanceComparison);
                if (pending == null)
                {
                    if (!(validateAdjustedGeometry?.Invoke() ?? true))
                    {
                        if (!request.AllowConstrainedBendAllowanceReduction || request.AllowBendGeometricLimit ||
                            !TryRetractConstrainedRoundedMember(conversion, state, requirements, request, group,
                                validateAdjustedGeometry, out failureReason, attemptedBoundaries)) return false;
                        continue;
                    }
                    if (!TryValidateRoundedLapInterfaces(conversion, request, group, out failureReason)) return false;
                    RefreshRoundedCopperLength(conversion);
                    return true;
                }
                var selected = requirements.Where(item => !item.IsFixed && item.SectionIndex == pending.SectionIndex &&
                    item.AtStart == pending.AtStart).ToList();
                var snapshot = conversion.StackMembers.SelectMany(member => member.PlanarParts).Distinct()
                    .ToDictionary(part => part, part => part.Copy());
                var preferredExtension = selected.Max(item => GetRoundedExtension(item, request, false));
                if (TryExtendRoundedSection(conversion, state, pending, request, group, preferredExtension, out failureReason) &&
                    (validateAdjustedGeometry?.Invoke() ?? true)) continue;
                RestoreRoundedTrial(snapshot);
                if (!request.AllowConstrainedBendAllowanceReduction || request.AllowBendGeometricLimit)
                { failureReason = "设置折弯余量的保形联动受限：" + failureReason; return false; }
                if (!TryReduceRoundedSectionInTwoTrials(conversion, state, pending, selected, snapshot,
                        request, group, validateAdjustedGeometry, out failureReason, attemptedBoundaries)) return false;
            }
            failureReason = "真实圆弧搭接保形联动未在有限轮次内稳定，保留拒绝。";
            return false;
        }

        private static bool TryReduceRoundedSectionInTwoTrials(LinkageRouteCenterlineConversionResult conversion,
            ProcessingState state, RoundedLapRequirement pending, IReadOnlyList<RoundedLapRequirement> selected,
            IDictionary<PlanarBendBusbarSetting, PlanarBendBusbarSetting> snapshot,
            LinkageMultiGroupRouteRequest request, LinkageRouteGroupRequest group,
            Func<bool> validateAdjustedGeometry, out string failureReason,
            ISet<Tuple<PlanarBendBusbarSetting, int, bool>> attemptedBoundaries)
        {
            failureReason = "半余量和真实圆弧极限两档均未通过，保留拒绝。";
            var constrained = selected.Where(item => GetRoundedExtension(item, request, false) >
                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison)
                .GroupBy(item => item.Member).ToList();
            var constrainedBoundaries = constrained.SelectMany(items => items)
                .Select(item => Tuple.Create(item.Member, item.SegmentIndex, item.AtStart)).ToList();
            if (constrainedBoundaries.Any(attemptedBoundaries.Contains)) return false;
            foreach (var boundary in constrainedBoundaries) attemptedBoundaries.Add(boundary);
            var previousTargets = new Dictionary<PlanarBendBusbarSetting, double>();
            for (var trial = 0; trial < 2; trial++)
            {
                RestoreRoundedTrial(snapshot);
                var targets = new Dictionary<PlanarBendBusbarSetting, double>();
                foreach (var memberRequirements in constrained)
                {
                    var member = memberRequirements.Key;
                    var physicalAllowance = 0d;
                    foreach (var requirement in memberRequirements)
                    {
                        if (!TryGetRoundedReserve(requirement, out _, out var physical, out failureReason))
                        { RestoreRoundedTrial(snapshot); return false; }
                        physicalAllowance = Math.Max(physicalAllowance, physical - member.BusBarThickness / 2d);
                    }
                    var preferred = Math.Max(0d, Math.Min(request.BendAllowance, snapshot[member].BendAllowance));
                    targets[member] = Math.Min(preferred, trial == 0
                        ? Math.Max(physicalAllowance, preferred / 2d) : physicalAllowance);
                    member.BendAllowance = targets[member];
                }
                if (trial > 0 && targets.All(item => previousTargets.TryGetValue(item.Key, out var previous) &&
                        Math.Abs(previous - item.Value) <= GeometryTolerance)) break;
                previousTargets = targets;
                var extension = selected.Max(item => GetRoundedExtension(item, request, false));
                if (!TryExtendRoundedSection(conversion, state, pending, request, group, extension, out failureReason) ||
                    !(validateAdjustedGeometry?.Invoke() ?? true)) continue;
                conversion.Diagnostics.Add(trial == 0
                    ? "受限成员半余量落位通过，停止额外余量试验；其它成员及请求不变。"
                    : "受限成员真实圆弧极限落位通过，停止额外余量试验；其它成员及请求不变。");
                return true;
            }
            RestoreRoundedTrial(snapshot);
            return false;
        }

        private static bool TryExtendRoundedSection(LinkageRouteCenterlineConversionResult conversion,
            ProcessingState state, RoundedLapRequirement requirement, LinkageMultiGroupRouteRequest request,
            LinkageRouteGroupRequest group, double extension, out string failureReason)
        {
            failureReason = null;
            if (double.IsNaN(extension) || double.IsInfinity(extension))
            { failureReason = "真实圆弧预留无法计算。"; return false; }
            if (extension <= LinkageRouteGeometryTolerances.FinalAcceptanceComparison) return true;
            if (!request.AllowBendGeometricLimit && !request.AllowConstrainedBendAllowanceReduction)
            {
                // Retain the strict configured-allowance path when neither local
                // geometric-limit permission is enabled.
                var strictMembers = state.SettingsBySectionIndex[requirement.SectionIndex];
                var strictReference = strictMembers.First().Setting;
                return TryEnsureFinalTerminalReserveWithChain(conversion, strictMembers,
                    new SectionSpecification { Width = strictReference.BusBarWidth,
                        Thickness = strictReference.BusBarThickness, StackCount = strictMembers.Count },
                    request, group, requirement.AtStart, out failureReason, extension);
            }
            // Each actual member has its own arc trim and projected lap boundary.
            // Never apply the worst member's deficit to an otherwise valid layer.
            var sectionMembers = state.SettingsBySectionIndex[requirement.SectionIndex].ToList();
            var terminalBaseline = conversion.StackMembers.SelectMany(member => member.PlanarParts).Distinct()
                .ToDictionary(part => part, part => part.Copy());
            foreach (var actualMember in sectionMembers)
            {
                // A preceding member may have moved a neighbouring interface in
                // its own chain. Re-measure instead of reusing a group maximum.
                if (!TryCollectRoundedLapRequirements(conversion, group, out _,
                        out var currentRequirements, out failureReason)) return false;
                var memberExtension = currentRequirements.Where(item => !item.IsFixed &&
                        ReferenceEquals(item.Member, actualMember.Setting) &&
                        item.SectionIndex == requirement.SectionIndex && item.AtStart == requirement.AtStart)
                    .Select(item => GetRoundedExtension(item, request, false)).DefaultIfEmpty(0d).Max();
                if (memberExtension <= LinkageRouteGeometryTolerances.FinalAcceptanceComparison) continue;
                if (double.IsNaN(memberExtension) || double.IsInfinity(memberExtension))
                { failureReason = "本排真实圆弧缺口无法计算。"; return false; }
                var reference = actualMember.Setting;
                if (!TryEnsureFinalTerminalReserveWithChain(conversion, new[] { actualMember },
                        new SectionSpecification { Width = reference.BusBarWidth,
                            Thickness = reference.BusBarThickness, StackCount = 1 },
                        request, group, requirement.AtStart, out failureReason, memberExtension)) return false;
                if (!TryRestoreLinkedTrialFixedEndpoints(conversion, terminalBaseline, out failureReason)) return false;
            }
            return true;
        }

        private static void RestoreRoundedTrial(IDictionary<PlanarBendBusbarSetting, PlanarBendBusbarSetting> snapshot)
        {
            foreach (var saved in snapshot)
            {
                saved.Key.PointList = saved.Value.PointList.Select(point => point.Copy()).ToList();
                saved.Key.BendAllowance = saved.Value.BendAllowance;
            }
        }

        private static void RefreshRoundedCopperLength(LinkageRouteCenterlineConversionResult conversion)
        {
            conversion.ActualCopperLength = conversion.StackMembers.SelectMany(member => member.PlanarParts)
                .Sum(part => part.GetDevelopedLength()) + LinkageRouteCopperLength.GetBridgeLength(conversion.Accessories);
        }

        private static bool TryRetractConstrainedRoundedMember(LinkageRouteCenterlineConversionResult conversion,
            ProcessingState state, IEnumerable<RoundedLapRequirement> requirements, LinkageMultiGroupRouteRequest request,
            LinkageRouteGroupRequest group, Func<bool> validateGeometry, out string failureReason,
            ISet<Tuple<PlanarBendBusbarSetting, int, bool>> attemptedBoundaries)
        {
            failureReason = "设置余量落位的实体受限，逐排几何极限试验仍未通过完整约束。";
            foreach (var requirement in requirements.Where(item => !item.IsFixed))
            {
                if (!TryGetRoundedReserve(requirement, out var available, out var physical, out _) ||
                    requirement.Member.BendAllowance <= GeometryTolerance) continue;
                var preferred = Math.Max(0d, Math.Min(request.BendAllowance, requirement.Member.BendAllowance));
                var physicalAllowance = Math.Max(0d, physical - requirement.Member.BusBarThickness / 2d);
                if (preferred <= physicalAllowance + GeometryTolerance ||
                    available - physical <= LinkageRouteGeometryTolerances.FinalAcceptanceComparison) continue;
                if (!attemptedBoundaries.Add(Tuple.Create(requirement.Member,
                        requirement.SegmentIndex, requirement.AtStart))) continue;
                var snapshot = conversion.StackMembers.SelectMany(member => member.PlanarParts).Distinct()
                    .ToDictionary(part => part, part => part.Copy());
                // 只多做半余量和物理极限两档；若半余量已触及极限则不重复同一落位。
                foreach (var target in new[] { Math.Max(physicalAllowance, preferred / 2d), physicalAllowance }.Distinct())
                {
                    RestoreRoundedTrial(snapshot);
                    var retraction = available - Math.Max(physical,
                        requirement.Member.BusBarThickness / 2d + target);
                    if (retraction <= LinkageRouteGeometryTolerances.FinalAcceptanceComparison) continue;
                    requirement.Member.BendAllowance = target;
                    var member = state.SettingsBySectionIndex[requirement.SectionIndex]
                        .Single(item => ReferenceEquals(item.Setting, requirement.Member));
                    if (TryEnsureFinalTerminalReserveWithChain(conversion, new[] { member },
                            new SectionSpecification { Width = member.Setting.BusBarWidth,
                                Thickness = member.Setting.BusBarThickness, StackCount = 1 },
                            request, group, requirement.AtStart, out _, -retraction) &&
                        TryValidateRoundedLapInterfaces(conversion, request, group, out _) && validateGeometry())
                    {
                        conversion.Diagnostics.Add("实体约束触发单排折弯余量回退；联动吸收排余量保持原值。");
                        return true;
                    }
                }
                RestoreRoundedTrial(snapshot);
            }
            return false;
        }

        private static double GetRoundedExtension(RoundedLapRequirement requirement,
            LinkageMultiGroupRouteRequest request, bool physicalOnly)
        {
            if (!TryGetRoundedReserve(requirement, out var available, out var physical, out _)) return double.PositiveInfinity;
            var applied = request.AllowConstrainedBendAllowanceReduction
                ? Math.Max(0d, Math.Min(request.BendAllowance, requirement.Member.BendAllowance))
                : request.BendAllowance;
            var required = physicalOnly || request.AllowBendGeometricLimit ? physical :
                Math.Max(physical, requirement.Member.BusBarThickness / 2d + applied);
            return Math.Max(0d, required - available);
        }

        private static bool TryGetRoundedReserve(RoundedLapRequirement requirement, out double available,
            out double physical, out string failureReason)
        {
            available = physical = 0d;
            if (!LinkageRouteRoundedLapGeometry.TryGetFlatSegment(requirement.Member, requirement.SegmentIndex,
                    out var flat, out failureReason)) return false;
            var positions = requirement.Corners.Select(point => new Vector3d(flat.Start, point).Dot(flat.Direction)).ToList();
            available = requirement.AtStart ? flat.OriginalLength - positions.Max() : positions.Min();
            physical = requirement.AtStart ? flat.EndTrim : flat.StartTrim;
            return true;
        }

        private static bool TryCollectRoundedLapRequirements(LinkageRouteCenterlineConversionResult conversion,
            LinkageRouteGroupRequest group, out ProcessingState state,
            out List<RoundedLapRequirement> requirements, out string failureReason)
        {
            requirements = new List<RoundedLapRequirement>();
            if (!TryCreateProcessingStateFromExistingGeometry(conversion, out state, out failureReason)) return false;
            foreach (var actual in conversion.StackMembers.SelectMany(member => member.PlanarParts)
                .Concat(conversion.Accessories.Where(item => item.Setting != null).Select(item => item.Setting)))
                for (var segment = 0; segment < actual.PointList.Count - 1; segment++)
                    if (!LinkageRouteRoundedLapGeometry.TryGetFlatSegment(actual, segment, out _, out failureReason)) return false;
            var indices = state.SettingsBySectionIndex.Keys.OrderBy(index => index).ToList();
            foreach (var index in indices)
            foreach (var current in state.SettingsBySectionIndex[index])
            foreach (var atStart in new[] { true, false })
            {
                if (current.Setting.PointList.Count < 2) continue;
                var neighborIndex = index + (atStart ? -1 : 1);
                if (state.SettingsBySectionIndex.TryGetValue(neighborIndex, out var neighbors))
                {
                    foreach (var neighbor in SelectRoundedNeighbors(current.Setting, atStart,
                        neighbors.Select(item => new RoundedNeighbor { Member = item.Setting,
                            SegmentIndex = atStart ? item.Setting.PointList.Count - 2 : 0 })))
                        AddRoundedPair(requirements, current.Setting, atStart, neighbor, index, false);
                }
                else
                {
                    var terminal = atStart ? group?.StartTerminal : group?.EndTerminal;
                    var bridges = conversion.Accessories.Where(accessory =>
                        accessory.Kind == LinkageRouteAccessoryKind.DoubleStartHorizontalBridge &&
                        accessory.TerminalSide == (atStart ? LinkageRouteTerminalSide.Start : LinkageRouteTerminalSide.End))
                        .Select(accessory => accessory.Setting).Where(setting => setting != null).ToList();
                    var terminalNeighbors = bridges.Count > 0 ? bridges :
                        (terminal?.Members ?? new List<LinkageTerminalMember>()).Select(item => item.FixedBusbar).ToList();
                    foreach (var neighbor in SelectRoundedNeighbors(current.Setting, atStart, GetRoundedSegments(terminalNeighbors)))
                        AddRoundedPair(requirements, current.Setting, atStart, neighbor, index, true);
                }
            }
            foreach (var bridge in conversion.Accessories.Where(item => item.Kind == LinkageRouteAccessoryKind.DoubleStartHorizontalBridge && item.Setting != null))
            {
                var terminal = bridge.TerminalSide == LinkageRouteTerminalSide.Start ? group.StartTerminal : group.EndTerminal;
                foreach (var neighbor in SelectRoundedNeighbors(bridge.Setting, true,
                    GetRoundedSegments(terminal.Members.Select(item => item.FixedBusbar))))
                    AddRoundedPair(requirements, bridge.Setting, true, neighbor, -1, true);
            }
            // 双向遍历同一接口只保留一份实际边界约束；所有邻居的角点一起保留，
            // 不能去重时丢掉更严的一侧搭接范围，也不能重复执行同一边界的两档回退。
            requirements = requirements.GroupBy(item => new {
                    item.Member, item.SegmentIndex, item.AtStart, item.IsFixed })
                .Select(boundary => new RoundedLapRequirement {
                    Member = boundary.Key.Member, SegmentIndex = boundary.Key.SegmentIndex,
                    AtStart = boundary.Key.AtStart, IsFixed = boundary.Key.IsFixed,
                    SectionIndex = boundary.First().SectionIndex,
                    Corners = boundary.SelectMany(item => item.Corners).ToArray() }).ToList();
            return true;
        }

        private sealed class RoundedNeighbor
        {
            internal PlanarBendBusbarSetting Member;
            internal int SegmentIndex;
        }

        private static IEnumerable<RoundedNeighbor> GetRoundedSegments(IEnumerable<PlanarBendBusbarSetting> members)
        {
            foreach (var member in members.Where(item => item?.PointList?.Count >= 2))
                for (var index = 0; index < member.PointList.Count - 1; index++)
                    yield return new RoundedNeighbor { Member = member, SegmentIndex = index };
        }

        private static PlanarBendBusbarSetting RoundedSegmentProxy(RoundedNeighbor neighbor)
        {
            var member = neighbor.Member;
            var points = member.IsCenter ? member.PointList : member.GetOffsetCenterlinePointList(member.BusBarThickness / 2d);
            var proxy = member.Copy(); proxy.IsCenter = true;
            proxy.PointList = new List<Point3d> { points[neighbor.SegmentIndex].Copy(), points[neighbor.SegmentIndex + 1].Copy() };
            return proxy;
        }

        private static IEnumerable<RoundedNeighbor> SelectRoundedNeighbors(PlanarBendBusbarSetting member,
            bool atStart, IEnumerable<RoundedNeighbor> neighbors)
        {
            var points = member.IsCenter ? member.PointList : member.GetOffsetCenterlinePointList(member.BusBarThickness / 2d);
            var origin = points[atStart ? 0 : points.Count - 2];
            var end = points[atStart ? 1 : points.Count - 1];
            var normal = new Vector3d(origin, end).Normalized.Cross(member.WidthVector.Normalized).Normalized;
            var projected = neighbors.Select(neighbor => new { Neighbor = neighbor, Proxy = RoundedSegmentProxy(neighbor) })
                .Where(item => LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(member, atStart,
                    item.Proxy, true, Math.Min(member.BusBarWidth, item.Proxy.BusBarWidth), out _,
                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
                .Select(item => new { item.Neighbor, Distance = new Vector3d(origin, item.Proxy.PointList[0]).Dot(normal) }).ToList();
            foreach (var side in projected.GroupBy(item => item.Distance < 0d))
            {
                var closest = side.Min(item => Math.Abs(item.Distance));
                foreach (var item in side.Where(item => Math.Abs(item.Distance) <= closest + GeometryTolerance))
                    yield return item.Neighbor;
            }
        }

        private static void AddRoundedPair(ICollection<RoundedLapRequirement> requirements,
            PlanarBendBusbarSetting member, bool atStart, RoundedNeighbor neighborInfo,
            int sectionIndex, bool neighborIsFixed)
        {
            var neighbor = neighborInfo.Member;
            if (neighbor == null) return;
            if (!LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(member, atStart,
                    RoundedSegmentProxy(neighborInfo), true, Math.Min(member.BusBarWidth, neighbor.BusBarWidth),
                    out var footprint, LinkageRouteGeometryTolerances.FinalAcceptanceComparison)) return;
            if (member.PointList.Count >= 3)
                requirements.Add(new RoundedLapRequirement { Member = member, AtStart = atStart,
                    SegmentIndex = atStart ? 0 : member.PointList.Count - 2,
                    Corners = footprint.Corners, SectionIndex = sectionIndex, IsFixed = sectionIndex < 0 });
            if (neighbor.PointList.Count >= 3)
                requirements.Add(new RoundedLapRequirement { Member = neighbor, AtStart = neighborInfo.SegmentIndex == 0,
                    SegmentIndex = neighborInfo.SegmentIndex,
                    Corners = footprint.Corners, SectionIndex = neighbor.PhysicalSectionIndex, IsFixed = neighborIsFixed });
        }
    }
}
