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
    /// <summary>以真实接触图识别物理始端搭接排；不以孔中心锁定搭接位置。</summary>
    internal static partial class LinkageRouteTerminalHoleLapValidator
    {
        private const double Precision = LinkageRouteGeometryTolerances.PointCoincidence;
        private sealed class Face
        {
            internal PlanarBendBusbarSetting Setting;
            internal int SegmentIndex;
            internal bool IsSupport;
        }
        private sealed class Violation
        {
            internal LinkageRouteTerminalHoleLapGeometry.Evaluation Evaluation;
            internal string MemberKey;
            internal Face TargetFace;
        }

        internal static bool HasStartHoles(LinkageRouteGroupRequest group)
            => PhysicalStart(group)?.Members?.Any(member => member?.Holes?.Count > 0) == true;

        private static LinkageTerminalAssembly PhysicalStart(LinkageRouteGroupRequest group)
        {
            var terminal = group?.PathSearchIsReversed == true ? group.EndTerminal : group?.StartTerminal;
            return terminal?.CabinetBridgeAssembly?.OriginalTerminal ?? terminal;
        }

        internal static bool TryValidate(LinkageMultiGroupRouteRequest request, LinkageRouteGroupRequest group,
            LinkageRouteCenterlineConversionResult conversion, out string failureReason)
        {
            var violations = FindViolations(request, group, conversion, request?.RequireCenteredTerminalHoleGroups == true);
            failureReason = violations.Count == 0 ? null : "始端固定排“" + violations[0].MemberKey + "”：" +
                violations[0].Evaluation.FailureReason;
            return violations.Count == 0;
        }

        private static List<Violation> FindViolations(LinkageMultiGroupRouteRequest request,
            LinkageRouteGroupRequest group, LinkageRouteCenterlineConversionResult conversion, bool requireCentered,
            bool preferFreeCutMargin = false)
        {
            var result = FindParallelLengthViolations(request, group, conversion);
            if (!HasStartHoles(group)) return result;
            if (conversion == null || request == null)
            {
                result.Add(new Violation { Evaluation = new LinkageRouteTerminalHoleLapGeometry.Evaluation {
                    IsValid = false, FailureReason = "始端孔约束缺少实际实体或请求。" } });
                return result;
            }
            var faces = new List<Face>();
            foreach (var stack in conversion.StackMembers ?? new List<LinkageStackedRouteMember>())
                foreach (var part in stack?.PlanarParts ?? new List<PlanarBendBusbarSetting>()) AddFaces(faces, part, false);
            foreach (var accessory in conversion.Accessories ?? new List<LinkageRouteAccessory>())
                AddFaces(faces, accessory?.Setting, accessory?.Kind == LinkageRouteAccessoryKind.SupportBlock);
            foreach (var member in PhysicalStart(group).Members.Where(item => item?.Holes?.Count > 0))
                for (var segmentIndex = 0; segmentIndex + 1 < member.FixedBusbar.PointList.Count; segmentIndex++)
                {
                    var source = new Face { Setting = member.FixedBusbar, SegmentIndex = segmentIndex };
                    var visited = new HashSet<Face>();
                    var pending = new Queue<Face>();
                    pending.Enqueue(source);
                    while (pending.Count > 0)
                    {
                        var current = pending.Dequeue();
                        foreach (var next in faces)
                        {
                            if (visited.Contains(next) || !LinkageRouteTerminalHoleLapGeometry.HavePhysicalFaceContact(
                                    current.Setting, current.SegmentIndex, next.Setting, next.SegmentIndex)) continue;
                            visited.Add(next);
                            if (next.IsSupport) { pending.Enqueue(next); continue; }
                            var evaluated = LinkageRouteTerminalHoleLapGeometry.Evaluate(member, segmentIndex,
                                next.Setting, next.SegmentIndex, request.RotateSlotHole90Degrees, request.TerminalHoleEdgeDistance);
                            var preferredExtension = preferFreeCutMargin && (group.PathSearchIsReversed
                                ? evaluated.PreferredEndCutExtension : evaluated.PreferredStartCutExtension) > Precision;
                            if (preferredExtension)
                            {
                                evaluated.StartCutExtension = evaluated.PreferredStartCutExtension;
                                evaluated.EndCutExtension = evaluated.PreferredEndCutExtension;
                            }
                            if (!evaluated.IsValid || (requireCentered && !evaluated.IsHoleGroupCentered) || preferredExtension)
                            {
                                if (evaluated.IsValid) evaluated.FailureReason = "参与搭接的孔组尚未在排宽方向居中。";
                                result.Add(new Violation { Evaluation = evaluated, MemberKey = member.MemberKey, TargetFace = next });
                            }
                        }
                    }
                }
            // 无真实接触、断链、桥和垫块覆盖仍必须由完整搭接门禁拒绝。
            return result;
        }

        private static void AddFaces(List<Face> faces, PlanarBendBusbarSetting setting, bool support)
        {
            if (setting?.PointList == null) return;
            for (var index = 0; index + 1 < setting.PointList.Count; index++)
                faces.Add(new Face { Setting = setting, SegmentIndex = index, IsSupport = support });
        }

        /// <summary>所有试移按端部链传播，I保持刚体；每个试验必须完整终检，失败恢复原实体及附件。</summary>
        internal static bool TryRepair(LinkageMultiGroupRouteRequest request, LinkageRouteGroupRequest group,
            LinkageRouteCenterlineConversionResult conversion, Func<bool> completeTrialValidation,
            out string failureReason, CancellationToken cancellationToken = default(CancellationToken))
        {
            var initiallyValid = TryValidate(request, group, conversion, out failureReason);
            if (initiallyValid && FindViolations(request, group, conversion, true, true).Count == 0) return true;
            if (completeTrialValidation == null) return false;
            var original = Capture(conversion);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var trialCount = 0;
            try
            {
                if (TryRepairRecursive(request, group, conversion, completeTrialValidation, visited,
                        ref trialCount, 0, true, cancellationToken, true))
                {
                    RefreshLength(conversion);
                    failureReason = null;
                    return true;
                }
                original();
                visited.Clear();
                trialCount = 0;
                // 优先余量失败只撤销本次试验，仍可按严格设置值及原居中规则通过。
                if (!cancellationToken.IsCancellationRequested &&
                    TryRepairRecursive(request, group, conversion, completeTrialValidation, visited,
                        ref trialCount, 0, true, cancellationToken))
                {
                    RefreshLength(conversion);
                    failureReason = null;
                    return true;
                }
                original();
                if (request?.RequireCenteredTerminalHoleGroups != true && !cancellationToken.IsCancellationRequested)
                {
                    if (initiallyValid) return true;
                    visited.Clear();
                    trialCount = 0;
                    if (TryRepairRecursive(request, group, conversion, completeTrialValidation, visited,
                            ref trialCount, 0, false, cancellationToken))
                    {
                        RefreshLength(conversion);
                        failureReason = null;
                        return true;
                    }
                    original();
                }
                failureReason = failureReason ?? "始端孔组居中及孔边距的有限落位试验未通过完整几何校验。";
                return false;
            }
            catch
            {
                original();
                throw;
            }
        }

        private static bool TryRepairRecursive(LinkageMultiGroupRouteRequest request, LinkageRouteGroupRequest group,
            LinkageRouteCenterlineConversionResult conversion, Func<bool> validate, HashSet<string> visited,
            ref int trialCount, int depth, bool requireCentered, CancellationToken cancellationToken,
            bool preferFreeCutMargin = false)
        {
            if (cancellationToken.IsCancellationRequested) return false;
            var violations = FindViolations(request, group, conversion, requireCentered, preferFreeCutMargin);
            if (violations.Count == 0) return validate();
            // 有限修复尝试不改变工艺门禁：预算耗尽仍保留拒绝，不发布未验实体。
            if (depth >= 4 || trialCount >= 64) return false;
            var restore = Capture(conversion);
            var firstViolation = violations[0];
            var displacements = (requireCentered
                ? firstViolation.Evaluation.CenteringDisplacements.Concat(firstViolation.Evaluation.RepairDisplacements)
                : firstViolation.Evaluation.RepairDisplacements.Concat(firstViolation.Evaluation.CenteringDisplacements))
                .Concat(new[] { new Vector3d(0d, 0d, 0d) }).ToList();
            // 先伸长固定端自由切边，再试原有整链位移；同时尝试“居中+延长”。
            foreach (var extendFreeCut in new[] { true, false })
            foreach (var displacement in displacements)
            {
                if (trialCount >= 64) break;
                if (cancellationToken.IsCancellationRequested) { restore(); return false; }
                restore();
                var extended = extendFreeCut && TryExtendTerminalCut(conversion, group.PathSearchIsReversed, firstViolation);
                if (extendFreeCut && !extended) continue;
                if (displacement.Norm <= Precision && !extended) continue;
                trialCount++;
                if (displacement.Norm > Precision && !TryMoveTerminalChains(conversion, group.PathSearchIsReversed, displacement)) continue;
                conversion.TerminalHoleLapGeometryAdjusted = true;
                if (!TryRebuildSupports(conversion, request, group)) continue;
                var signature = string.Join(";", conversion.StackMembers.SelectMany(stack => stack.PlanarParts)
                    .Concat(conversion.Accessories.Select(item => item.Setting)).SelectMany(setting => setting.PointList)
                    .Select(point => string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R}", point.X, point.Y, point.Z)));
                if (!visited.Add(signature)) continue;
                if (TryRepairRecursive(request, group, conversion, validate, visited, ref trialCount, depth + 1, requireCentered, cancellationToken, preferFreeCutMargin))
                    return true;
            }
            restore();
            return false;
        }

        private static bool TryExtendTerminalCut(LinkageRouteCenterlineConversionResult conversion,
            bool fromEnd, Violation violation)
        {
            // 只能延长物理始端一侧的自由端，不能延长中间接口、另一端或原固定排。
            var amount = fromEnd ? violation.Evaluation.EndCutExtension : violation.Evaluation.StartCutExtension;
            if (amount <= Precision || double.IsInfinity(amount) || double.IsNaN(amount)) return false;
            var terminalParts = conversion.StackMembers.Select(stack => fromEnd
                ? stack.PlanarParts.LastOrDefault() : stack.PlanarParts.FirstOrDefault()).Where(part => part != null).ToList();
            if (!terminalParts.Contains(violation.TargetFace.Setting)) return false;
            var requiredSegment = fromEnd ? violation.TargetFace.Setting.PointList.Count - 2 : 0;
            if (violation.TargetFace.SegmentIndex != requiredSegment) return false;
            foreach (var part in terminalParts)
            {
                var freeIndex = fromEnd ? part.PointList.Count - 1 : 0;
                var adjacentIndex = fromEnd ? freeIndex - 1 : 1;
                var outward = new Vector3d(part.PointList[adjacentIndex], part.PointList[freeIndex]);
                if (outward.Norm <= Precision) return false;
                part.PointList[freeIndex] = Translate(part.PointList[freeIndex], outward.Normalized * amount);
            }
            return true;
        }

        private static bool TryMoveTerminalChains(LinkageRouteCenterlineConversionResult conversion,
            bool fromEnd, Vector3d displacement)
        {
            foreach (var stack in conversion.StackMembers)
            {
                var parts = fromEnd ? stack.PlanarParts.Reverse() : stack.PlanarParts;
                foreach (var part in parts)
                {
                    var moved = part.Copy();
                    if (fromEnd) moved.PointList.Reverse();
                    var absorbed = TryAbsorbAtFreeEnd(moved, displacement);
                    if (!absorbed) moved.PointList = moved.PointList.Select(point => Translate(point, displacement)).ToList();
                    if (fromEnd) moved.PointList.Reverse();
                    part.PointList = moved.PointList;
                    if (absorbed) break;
                }
            }
            var side = fromEnd ? LinkageRouteTerminalSide.End : LinkageRouteTerminalSide.Start;
            foreach (var accessory in conversion.Accessories.Where(item => item.Kind != LinkageRouteAccessoryKind.SupportBlock &&
                item.TerminalSide == side))
                accessory.Setting.PointList = accessory.Setting.PointList.Select(point => Translate(point, displacement)).ToList();
            return true;
        }

        private static bool TryAbsorbAtFreeEnd(PlanarBendBusbarSetting part, Vector3d displacement)
        {
            if (part.PointList.Count <= 2) return false; // I整体移动并把同一位移交给下一根。
            var direction = new Vector3d(part.PointList[0], part.PointList[1]).Normalized;
            if (Enumerable.Range(1, part.PointList.Count - 1).All(index =>
                new Vector3d(part.PointList[0], part.PointList[index]).Cross(direction).Norm <= Precision))
                return false; // 含共线中间点的I仍必须保持形状整体移动。
            var width = part.WidthVector.Normalized;
            if (Math.Abs(displacement.Dot(width)) > Precision) return false; // 出平面必须保持形状整体移动。
            var normal = direction.Cross(width).Normalized;
            var offsets = new double[part.PointList.Count - 1];
            var sign = part.StartBusbarVector.Normalized.Dot(normal) < 0d ? -1d : 1d;
            offsets[0] = displacement.Dot(normal) * sign;
            if (!LinkageRouteStackedCenterlineOffsetter.TryOffset(part, offsets, true, out var points, out _)) return false;
            points[0] = Translate(part.PointList[0], displacement);
            part.PointList = points;
            return true;
        }

        private static Point3d Translate(Point3d point, Vector3d vector)
            => new Point3d(point.X + vector.X, point.Y + vector.Y, point.Z + vector.Z);

        internal static bool TryRebuildSupports(LinkageRouteCenterlineConversionResult conversion,
            LinkageMultiGroupRouteRequest request, LinkageRouteGroupRequest group)
        {
            if (!LinkageRouteTerminalAccessoryBuilder.TryRebuildHoleShiftTerminalSupports(conversion, request, group, out _)) return false;
            if (conversion.CenterlineParts.Count > 1)
            {
                foreach (var support in conversion.Accessories.Where(item => item.Kind == LinkageRouteAccessoryKind.SupportBlock &&
                    item.SupportBlockKey?.StartsWith("boundary:", StringComparison.Ordinal) == true).ToList()) conversion.Accessories.Remove(support);
                if (!LinkageRouteSectionSpecificationPostProcessor.TryCreateProcessingStateFromExistingGeometry(conversion, out var state, out _) ||
                    !LinkageRouteSectionSpecificationPostProcessor.TryAddInternalSupportBlocks(conversion, request, group, state, out _) ||
                    !LinkageRouteSectionSpecificationPostProcessor.TryTrimInternalSupportBlocksAroundPhysicalIntersections(conversion, out _, out _)) return false;
            }
            LinkageRouteSupportBlockLayers.SplitOversized(conversion);
            return true;
        }

        private static Action Capture(LinkageRouteCenterlineConversionResult conversion)
        {
            var accessories = conversion.Accessories.ToList();
            var settings = conversion.StackMembers.SelectMany(stack => stack.PlanarParts)
                .Concat(conversion.CenterlineParts).Concat(accessories.Select(item => item.Setting)).Where(item => item != null).Distinct()
                .ToDictionary(setting => setting, setting => setting.Copy());
            var diagnostics = conversion.Diagnostics.ToList();
            var previousLength = conversion.ActualCopperLength;
            var previousAdjusted = conversion.TerminalHoleLapGeometryAdjusted;
            return () => {
                foreach (var item in settings) { item.Key.PointList = item.Value.PointList.Select(point => point.Copy()).ToList();
                    item.Key.BendAllowance = item.Value.BendAllowance; }
                conversion.Accessories.Clear();
                foreach (var accessory in accessories) conversion.Accessories.Add(accessory);
                conversion.Diagnostics.Clear();
                foreach (var diagnostic in diagnostics) conversion.Diagnostics.Add(diagnostic);
                conversion.ActualCopperLength = previousLength;
                conversion.TerminalHoleLapGeometryAdjusted = previousAdjusted;
            };
        }

        private static void RefreshLength(LinkageRouteCenterlineConversionResult conversion)
            => conversion.ActualCopperLength = conversion.StackMembers.SelectMany(stack => stack.PlanarParts)
                .Sum(part => part.GetDevelopedLength()) + LinkageRouteCopperLength.GetBridgeLength(conversion.Accessories);
    }
}
