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
        // Bounded fallback: extend the measured actual member, then repair
        // neighbours before asking the unchanged full physical callback to accept it.
        private static bool TryRepairLinkedRoundedInterfaces(LinkageRouteCenterlineConversionResult conversion,
            LinkageMultiGroupRouteRequest request, LinkageRouteGroupRequest group,
            IDictionary<PlanarBendBusbarSetting, PlanarBendBusbarSetting> sourceBaseline,
            Func<bool> validateGeometry, out string failureReason)
        {
            return TryRepairLinkedRoundedInterfacesCancelable(conversion, request, group,
                sourceBaseline, validateGeometry, out failureReason, CancellationToken.None);
        }

        private static bool TryRepairLinkedRoundedInterfacesCancelable(LinkageRouteCenterlineConversionResult conversion,
            LinkageMultiGroupRouteRequest request, LinkageRouteGroupRequest group,
            IDictionary<PlanarBendBusbarSetting, PlanarBendBusbarSetting> sourceBaseline,
            Func<bool> validateGeometry, out string failureReason, CancellationToken cancellationToken)
        {
            // A linked attempt may update diagnostics and reference/accessory geometry,
            // not only the selected member. Rejected attempts must be observationally atomic.
            var transactionSettings = conversion.CenterlineParts
                .Concat(conversion.StackMembers.SelectMany(member => member.PlanarParts))
                .Concat(conversion.Accessories.Where(accessory => accessory.Setting != null).Select(accessory => accessory.Setting))
                .Distinct().ToDictionary(part => part, part => part.Copy());
            var transactionAccessories = conversion.Accessories.ToArray();
            var transactionDiagnostics = conversion.Diagnostics.ToArray();
            var transactionLength = conversion.ActualCopperLength;
            var committed = false;
            try
            {
                var proposalAccepted = TryRepairLinkedRoundedInterfacesCore(conversion, request, group,
                    sourceBaseline, validateGeometry, out failureReason, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                committed = proposalAccepted;
                return committed;
            }
            finally
            {
                if (!committed)
                {
                    RestoreRoundedTrial(transactionSettings);
                    foreach (var setting in transactionSettings)
                    {
                        setting.Key.StartBusbarVector = setting.Value.StartBusbarVector.Copy();
                        setting.Key.WidthVector = setting.Value.WidthVector.Copy();
                    }
                    conversion.Accessories.Clear();
                    foreach (var accessory in transactionAccessories) conversion.Accessories.Add(accessory);
                    conversion.Diagnostics.Clear();
                    foreach (var diagnostic in transactionDiagnostics) conversion.Diagnostics.Add(diagnostic);
                    conversion.ActualCopperLength = transactionLength;
                }
            }
        }

        private static bool TryRepairLinkedRoundedInterfacesCore(LinkageRouteCenterlineConversionResult conversion,
            LinkageMultiGroupRouteRequest request, LinkageRouteGroupRequest group,
            IDictionary<PlanarBendBusbarSetting, PlanarBendBusbarSetting> sourceBaseline,
            Func<bool> validateGeometry, out string failureReason, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            failureReason = null;
            if (!request.AllowBendGeometricLimit && !request.AllowConstrainedBendAllowanceReduction)
            { failureReason="额外逐成员联动未获余量/极限许可，保留原设置余量路径。"; return false; }
            var failedOrdinaryState = conversion.StackMembers.SelectMany(member => member.PlanarParts).Distinct()
                .ToDictionary(part => part, part => part.Copy());
            RestoreRoundedTrial(sourceBaseline);
            var previousDeficits = new Dictionary<Tuple<PlanarBendBusbarSetting,int,bool>,double>();
            var previousTotalDeficit = double.PositiveInfinity;
            var visitedGeometry = new HashSet<string>(StringComparer.Ordinal);
            for (var round = 0; round < 10; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryCollectRoundedLapRequirements(conversion, group, out var state,
                        out var requirements, out failureReason)) break;
                if (requirements.Any(item => item.IsFixed && !LinkageRouteRoundedLapGeometry.HasFlatSegmentLapCoverage(
                        item.Member, item.SegmentIndex, item.Corners, out _, out _)))
                { failureReason = "固定排圆弧不可移动。"; break; }
                var pending = requirements.FirstOrDefault(item => !item.IsFixed &&
                    GetRoundedExtension(item, request, request.AllowConstrainedBendAllowanceReduction) >
                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison);
                if (pending == null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TryValidateRoundedLapInterfaces(conversion, request, group, out failureReason) &&
                        TryValidateInternalPhysicalLapClosure(conversion, false, out var needsSupport, out failureReason) &&
                        (validateGeometry?.Invoke() ?? false))
                    {
                        RefreshRoundedCopperLength(conversion);
                        conversion.Diagnostics.Add("实测圆弧延长及相邻接口联动完成；固定端保持，完整实体门禁通过，垫块最后补齐。");
                        return true;
                    }
                    // Keep the measured arc improvement. If independent layer moves
                    // broke this nested parallel joint, repair both affected legs
                    // together, then require actual closure and the full callback.
                    if (TryRepackLinkedParallelLapLayers(conversion, request, group,
                            validateGeometry, out failureReason, cancellationToken)) return true;
                    break;
                }
                var extension = GetRoundedExtension(pending,request,request.AllowConstrainedBendAllowanceReduction);
                var totalDeficit = requirements.Where(item=>!item.IsFixed).Sum(item=>
                    GetRoundedExtension(item,request,request.AllowConstrainedBendAllowanceReduction));
                var boundary = Tuple.Create(pending.Member,pending.SegmentIndex,pending.AtStart);
                var geometrySignature = string.Join("|",conversion.StackMembers.SelectMany(member=>member.PlanarParts)
                    .SelectMany(part=>part.PointList).Select(point=>string.Format(CultureInfo.InvariantCulture,
                        "{0:R},{1:R},{2:R}",point.X,point.Y,point.Z)));
                if (!visitedGeometry.Add(geometrySignature) || double.IsNaN(totalDeficit) ||
                    double.IsInfinity(totalDeficit) || totalDeficit >= previousTotalDeficit-GeometryTolerance ||
                    (previousDeficits.TryGetValue(boundary,out var previousDeficit) && extension >= previousDeficit-GeometryTolerance))
                { failureReason="邻接联动未继续减少实测圆弧短差或出现周期，保留拒绝。"; break; }
                previousDeficits[boundary]=extension;
                previousTotalDeficit=totalDeficit;
                if (conversion.DoubleStartPhysicalBridgeSectionIndices.Contains(pending.SectionIndex))
                { failureReason = "圆弧缺口属于锁定连接排。"; break; }
                var selectedMember = state.SettingsBySectionIndex[pending.SectionIndex]
                    .Where(member => ReferenceEquals(member.Setting,pending.Member)).ToList();
                if (selectedMember.Count != 1 || pending.Member.PointList.Count < 3)
                { failureReason = "圆弧缺口缺少唯一可吸收折弯成员。"; break; }
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryEnsureFinalTerminalReserveWithChain(conversion,selectedMember,
                        new SectionSpecification { Width=pending.Member.BusBarWidth,Thickness=pending.Member.BusBarThickness,StackCount=1 },
                        request,group,pending.AtStart,out failureReason,extension)) break;
                if (!TryRestoreLinkedTrialFixedEndpoints(conversion,sourceBaseline,out failureReason)) break;
                LinkageRouteTerminalAccessoryBuilder.ApplyOutputTerminalBridgeLayerAlignment(conversion,group.StartTerminal);
                // One neighbour pass per outer round, not a nested ten-round solver.
                if (!TrySeparateNonAdjacentPhysicalLapSegmentsCancelable(conversion,out _,out failureReason,cancellationToken)) break;
                if (!TrySeparateInternalPhysicalLapGapsCancelable(conversion,request,group,out _,out failureReason,cancellationToken)) break;
                TryStabilizePersistentNTransitions(conversion,out _);
                if (!TryRestoreLinkedTrialFixedEndpoints(conversion,sourceBaseline,out failureReason)) break;
            }
            RestoreRoundedTrial(failedOrdinaryState);
            if (string.IsNullOrEmpty(failureReason)) failureReason="十轮内未同时满足全部相邻接口，保留原拒绝。";
            return false;
        }

        private static bool TryRestoreLinkedTrialFixedEndpoints(LinkageRouteCenterlineConversionResult conversion,
            IDictionary<PlanarBendBusbarSetting,PlanarBendBusbarSetting> baseline,out string failureReason)
        {
            failureReason=null;
            var firstPhysicalIdentity=conversion.CenterlineParts.First().PhysicalSectionIndex;
            var lastPhysicalIdentity=conversion.CenterlineParts.Last().PhysicalSectionIndex;
            foreach (var member in conversion.StackMembers)
            {
                var source=member.PlanarParts.Select(part=>baseline[part]).ToList();
                var proposed=member.PlanarParts.Select(part=>part.Copy()).ToList();
                if (!LinkageRouteUShapeOutputTransformer.TryRestoreSelectedTerminalLines(source,proposed,
                        source.First().PhysicalSectionIndex==firstPhysicalIdentity,
                        source.Last().PhysicalSectionIndex==lastPhysicalIdentity,
                        conversion.DoubleStartPhysicalBridgeSectionIndices,out failureReason)) return false;
                for (var index=0;index<proposed.Count;index++)
                    member.PlanarParts[index].PointList=proposed[index].PointList.Select(point=>point.Copy()).ToList();
            }
            return true;
        }
    }
}
