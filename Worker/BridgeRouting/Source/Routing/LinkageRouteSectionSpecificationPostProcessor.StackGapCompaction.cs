using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteSectionSpecificationPostProcessor
    {
        // Only actual physical shortages may consume existing local inter-layer gaps.
        private sealed class CompactionLayer
        {
            internal SectionMemberSetting Source;
            internal int AdjacentSegment;
            internal double Coordinate;
            internal double ExtensionProjection;
        }

        private static bool TryCompactStackForPhysicalRoundedLap(
            LinkageRouteCenterlineConversionResult conversion, LinkageMultiGroupRouteRequest request,
            LinkageRouteGroupRequest group, Func<bool> validateAdjustedGeometry, out string failureReason)
        {
            failureReason = null;
            if (conversion == null || request == null || validateAdjustedGeometry == null ||
                (!request.AllowBendGeometricLimit && !request.AllowConstrainedBendAllowanceReduction) ||
                !TryCollectRoundedLapRequirements(conversion, group, out var state,
                    out var requirements, out _)) return false;
            var shortages = requirements.Where(item => !item.IsFixed && item.SectionIndex >= 0 &&
                GetRoundedExtension(item, request, true) > LinkageRouteGeometryTolerances.FinalAcceptanceComparison &&
                state.SettingsBySectionIndex[item.SectionIndex].Count >= 2).ToList();
            if (shortages.Count == 0) return false;
            var snapshot = conversion.StackMembers.SelectMany(member => member.PlanarParts).Distinct()
                .ToDictionary(part => part, part => part.Copy());
            var originalDiagnosticCount = conversion.Diagnostics.Count;

            foreach (var shortage in shortages)
            {
                // A rejected previous boundary may have moved these same settings. Layer
                // coordinates and trial placement must always share the original baseline.
                RestoreCompactionTrial(conversion, snapshot, originalDiagnosticCount);
                if (conversion.DoubleStartPhysicalBridgeSectionIndices.Contains(shortage.SectionIndex) ||
                    !TryCreateCompactionLayers(state.SettingsBySectionIndex[shortage.SectionIndex], shortage,
                        out var layers, out var compressionAxis)) continue;
                // The kth actual layer remains fixed. Only its contiguous inner neighbours pack toward it.
                // k=1 is a single member; later k values use successively more available physical gaps.
                for (var fixedLayerIndex = 1; fixedLayerIndex < layers.Count; fixedLayerIndex++)
                {
                    RestoreCompactionTrial(conversion, snapshot, originalDiagnosticCount);
                    var targetCoordinates = new double[fixedLayerIndex + 1];
                    targetCoordinates[fixedLayerIndex] = layers[fixedLayerIndex].Coordinate;
                    for (var layerIndex = fixedLayerIndex - 1; layerIndex >= 0; layerIndex--)
                        targetCoordinates[layerIndex] = targetCoordinates[layerIndex + 1] -
                            (layers[layerIndex].Source.Setting.BusBarThickness +
                             layers[layerIndex + 1].Source.Setting.BusBarThickness) / 2d;
                    var availableCompression = targetCoordinates[0] - layers[0].Coordinate;
                    var requiredCompression = GetRoundedExtension(shortage, request, true) *
                        layers[0].ExtensionProjection;
                    if (availableCompression <= GeometryTolerance ||
                        availableCompression / layers[0].ExtensionProjection +
                            LinkageRouteGeometryTolerances.FinalAcceptanceComparison <
                        GetRoundedExtension(shortage, request, true)) continue;

                    // Only two critical positions, never a spacing grid: first the exact physical
                    // shortage with the minimum outward carry needed to preserve layer order;
                    // then the existing full-contact boundary if other interfaces require it.
                    var minimalCoordinates = layers.Take(fixedLayerIndex + 1).Select(layer => layer.Coordinate).ToArray();
                    minimalCoordinates[0] += Math.Min(requiredCompression, availableCompression);
                    for (var layerIndex = 1; layerIndex < fixedLayerIndex; layerIndex++)
                        minimalCoordinates[layerIndex] = Math.Max(minimalCoordinates[layerIndex],
                            minimalCoordinates[layerIndex - 1] + (layers[layerIndex - 1].Source.Setting.BusBarThickness +
                                layers[layerIndex].Source.Setting.BusBarThickness) / 2d);
                    var coordinateTrials = new List<double[]> { minimalCoordinates };
                    if (Enumerable.Range(0, fixedLayerIndex).Any(index =>
                        Math.Abs(minimalCoordinates[index] - targetCoordinates[index]) > GeometryTolerance))
                        coordinateTrials.Add(targetCoordinates);
                    foreach (var trialCoordinates in coordinateTrials)
                    {
                        RestoreCompactionTrial(conversion, snapshot, originalDiagnosticCount);
                        var totalCompression = trialCoordinates[0] - layers[0].Coordinate;
                        var placementSucceeded = true;
                        // Outside first: avoid moving an inner member through a layer that has not yet moved.
                        for (var layerIndex = fixedLayerIndex - 1; layerIndex >= 0; layerIndex--)
                        {
                            var layer = layers[layerIndex];
                            var displacement = trialCoordinates[layerIndex] - layer.Coordinate;
                            if (displacement <= GeometryTolerance) continue;
                            var part = layer.Source.Setting;
                            if (!TryEnsureFinalTerminalReserveWithChain(conversion, new[] { layer.Source },
                                new SectionSpecification { Width = part.BusBarWidth, Thickness = part.BusBarThickness,
                                    StackCount = 1 }, request, group, shortage.AtStart, out failureReason,
                                displacement / layer.ExtensionProjection))
                            { placementSucceeded = false; break; }
                            var centerPoints = part.IsCenter ? part.PointList : part.GetOffsetCenterlinePointList(part.BusBarThickness / 2d);
                            if (Math.Abs(DotPoint(centerPoints[layer.AdjacentSegment], compressionAxis) -
                                    trialCoordinates[layerIndex]) > GeometryTolerance)
                            { failureReason = "实际段未到达计算的贴合边界。"; placementSucceeded = false; break; }
                        }
                        if (!placementSucceeded) continue;
                        // Compaction is retained; only its rigid carry into an anchored terminal
                        // I chain is undone. A real positive interface gap is filled at the final
                        // common spacer pass, never by moving fixed copper or skipping a gate.
                        if (!PreservesCompactionDirectionsAndAnchors(conversion, snapshot) &&
                            !TryCorrectCompactionTerminalIChains(conversion, snapshot)) continue;
                        if (!PreservesCompactionDirectionsAndAnchors(conversion, snapshot) ||
                            !TryValidateRoundedLapInterfaces(conversion, request, group, out failureReason) ||
                            !validateAdjustedGeometry()) continue;

                        RefreshRoundedCopperLength(conversion);
                        conversion.Diagnostics.Add(string.Format(CultureInfo.InvariantCulture,
                            "真实圆弧极限仍不足后，物理段{0}按原层序压紧{1}个成员的中间直段，内层厚向位移{2:F3} mm；固定锚点不动，圆弧及完整实体约束复验通过，垫块仍最后生成。",
                            shortage.SectionIndex + 1, fixedLayerIndex, totalCompression));
                        return true;
                    }
                }
            }
            RestoreCompactionTrial(conversion, snapshot, originalDiagnosticCount);
            failureReason = "可用真实间隙不足，或压紧后未同时满足两端圆弧、固定锚点与完整几何约束。";
            return false;
        }

        private static bool TryCorrectCompactionTerminalIChains(LinkageRouteCenterlineConversionResult conversion,
            IDictionary<PlanarBendBusbarSetting, PlanarBendBusbarSetting> snapshot)
        {
            var corrected = false;
            foreach (var member in conversion.StackMembers)
            foreach (var fromStart in new[] { true, false })
            {
                var terminalChain = fromStart ? member.PlanarParts.ToList() : member.PlanarParts.AsEnumerable().Reverse().ToList();
                var chainLength = terminalChain.TakeWhile(part => part.PointList.Count == 2).Count();
                if (chainLength == 0 || chainLength == terminalChain.Count) continue;
                var anchored = terminalChain[0];
                var anchorIndex = fromStart ? 0 : anchored.PointList.Count - 1;
                if (anchored.PointList[anchorIndex].DistanceTo(snapshot[anchored].PointList[anchorIndex]) <= GeometryTolerance)
                    continue;
                var rigidCarry = new Vector3d(snapshot[anchored].PointList[0], anchored.PointList[0]);
                if (rigidCarry.Norm <= GeometryTolerance) return false;
                foreach (var carried in terminalChain.Take(chainLength))
                {
                    if (conversion.DoubleStartPhysicalBridgeSectionIndices.Contains(carried.PhysicalSectionIndex)) return false;
                    for (var pointIndex = 0; pointIndex < 2; pointIndex++)
                    {
                        var pointCarry = new Vector3d(snapshot[carried].PointList[pointIndex], carried.PointList[pointIndex]);
                        if ((pointCarry - rigidCarry).Norm > GeometryTolerance) return false;
                    }
                }
                var absorber = terminalChain[chainLength];
                var absorberEnd = fromStart ? 0 : absorber.PointList.Count - 1;
                var absorberCarry = new Vector3d(snapshot[absorber].PointList[absorberEnd], absorber.PointList[absorberEnd]);
                if ((absorberCarry - rigidCarry).Norm > GeometryTolerance) return false;
                foreach (var carried in terminalChain.Take(chainLength))
                    carried.PointList = snapshot[carried].PointList.Select(point => point.Copy()).ToList();

                var boundaryI = terminalChain[chainLength - 1];
                // Preserve a finite true lap projection and its original thickness-side order.
                // A missing projection, crossed layer, or negative gap cannot be fixed by pads.
                if (!LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(boundaryI, !fromStart,
                    absorber, fromStart, Math.Min(boundaryI.BusBarWidth, absorber.BusBarWidth), out _,
                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison)) return false;
                var boundaryNormal = new Vector3d(boundaryI.PointList[0], boundaryI.PointList[1]).Normalized
                    .Cross(boundaryI.WidthVector.Normalized).Normalized;
                var originalSide = new Vector3d(snapshot[boundaryI].PointList[0], snapshot[absorber].PointList[absorberEnd]).Dot(boundaryNormal);
                var correctedSide = new Vector3d(boundaryI.PointList[0], absorber.PointList[absorberEnd]).Dot(boundaryNormal);
                if (originalSide * correctedSide <= 0d || Math.Abs(correctedSide) + GeometryTolerance <
                    (boundaryI.BusBarThickness + absorber.BusBarThickness) / 2d) return false;
                corrected = true;
            }
            if (corrected) conversion.Diagnostics.Add("局部压紧后端部I链恢复原固定端层位；保留非I压紧结果，真实正间隙留待最终补块并完整终检。");
            return corrected;
        }

        private static bool TryCreateCompactionLayers(IEnumerable<SectionMemberSetting> sectionMembers,
            RoundedLapRequirement shortage, out List<CompactionLayer> layers, out Vector3d compressionAxis)
        {
            layers = new List<CompactionLayer>(); compressionAxis = null;
            var constrained = shortage.Member;
            var constrainedPoints = constrained.IsCenter ? constrained.PointList :
                constrained.GetOffsetCenterlinePointList(constrained.BusBarThickness / 2d);
            if (constrainedPoints.Count < 3) return false;
            var adjacent = shortage.AtStart ? 1 : constrainedPoints.Count - 3;
            var adjacentDirection = new Vector3d(constrainedPoints[adjacent], constrainedPoints[adjacent + 1]).Normalized;
            var terminalDirection = shortage.AtStart ? new Vector3d(constrainedPoints[0], constrainedPoints[1]).Normalized :
                new Vector3d(constrainedPoints.Last(), constrainedPoints[constrainedPoints.Count - 2]).Normalized;
            compressionAxis = adjacentDirection.Cross(constrained.WidthVector.Normalized).Normalized;
            var projection = terminalDirection.Dot(compressionAxis);
            if (Math.Abs(projection) <= GeometryTolerance) return false;
            if (projection < 0d) compressionAxis = new Vector3d(-compressionAxis.X, -compressionAxis.Y, -compressionAxis.Z);
            var stableAxis = compressionAxis;
            var constrainedCoordinate = DotPoint(constrainedPoints[adjacent], stableAxis);
            foreach (var source in sectionMembers)
            {
                var part = source.Setting;
                var points = part.IsCenter ? part.PointList : part.GetOffsetCenterlinePointList(part.BusBarThickness / 2d);
                if (points.Count < 3) return false; // mixed rigid I hosts require a separately proved mapping
                var segment = shortage.AtStart ? 1 : points.Count - 3;
                var direction = new Vector3d(points[segment], points[segment + 1]).Normalized;
                if (Math.Abs(direction.Dot(adjacentDirection)) < 1d - GeometryTolerance ||
                    Math.Abs(part.WidthVector.Normalized.Dot(constrained.WidthVector.Normalized)) < 1d - GeometryTolerance)
                    return false;
                var along = new[] { DotPoint(points[segment], adjacentDirection), DotPoint(points[segment + 1], adjacentDirection) };
                var constrainedAlong = new[] { DotPoint(constrainedPoints[adjacent], adjacentDirection),
                    DotPoint(constrainedPoints[adjacent + 1], adjacentDirection) };
                if (Math.Min(along.Max(), constrainedAlong.Max()) - Math.Max(along.Min(), constrainedAlong.Min()) <= GeometryTolerance)
                    return false;
                var widthSeparation = Math.Abs(new Vector3d(constrainedPoints[adjacent], points[segment])
                    .Dot(constrained.WidthVector.Normalized));
                if (widthSeparation >= (part.BusBarWidth + constrained.BusBarWidth) / 2d - GeometryTolerance) return false;
                var localTerminalDirection = shortage.AtStart ? new Vector3d(points[0], points[1]).Normalized :
                    new Vector3d(points.Last(), points[points.Count - 2]).Normalized;
                var localProjection = localTerminalDirection.Dot(stableAxis);
                if (localProjection <= GeometryTolerance) return false;
                layers.Add(new CompactionLayer { Source = source, AdjacentSegment = segment,
                    Coordinate = DotPoint(points[segment], stableAxis), ExtensionProjection = localProjection });
            }
            layers = layers.OrderBy(item => item.Coordinate).ToList();
            for (var index = 0; index + 1 < layers.Count; index++)
                if (layers[index + 1].Coordinate - layers[index].Coordinate <
                    (layers[index].Source.Setting.BusBarThickness + layers[index + 1].Source.Setting.BusBarThickness) / 2d - GeometryTolerance)
                    return false;
            var first = layers.FindIndex(item => ReferenceEquals(item.Source.Setting, constrained));
            if (first < 0 || first + 1 >= layers.Count) return false;
            layers = layers.Skip(first).ToList();
            return Math.Abs(layers[0].Coordinate - constrainedCoordinate) <= GeometryTolerance;
        }

        private static bool PreservesCompactionDirectionsAndAnchors(LinkageRouteCenterlineConversionResult conversion,
            IDictionary<PlanarBendBusbarSetting, PlanarBendBusbarSetting> snapshot)
        {
            foreach (var saved in snapshot)
            {
                var current = saved.Key.PointList; var original = saved.Value.PointList;
                if (current.Count != original.Count) return false;
                for (var segment = 0; segment + 1 < current.Count; segment++)
                {
                    var before = new Vector3d(original[segment], original[segment + 1]);
                    var after = new Vector3d(current[segment], current[segment + 1]);
                    if (after.Norm <= GeometryTolerance || before.Normalized.Dot(after.Normalized) < 1d - GeometryTolerance)
                        return false;
                }
            }
            foreach (var member in conversion.StackMembers.Where(item => item.PlanarParts.Count > 0))
            {
                var first = member.PlanarParts.First(); var last = member.PlanarParts.Last();
                if (first.PointList[0].DistanceTo(snapshot[first].PointList[0]) > GeometryTolerance ||
                    last.PointList.Last().DistanceTo(snapshot[last].PointList.Last()) > GeometryTolerance) return false;
            }
            return true;
        }

        private static void RestoreCompactionTrial(LinkageRouteCenterlineConversionResult conversion,
            IDictionary<PlanarBendBusbarSetting, PlanarBendBusbarSetting> snapshot, int diagnosticCount)
        {
            RestoreRoundedTrial(snapshot);
            while (conversion.Diagnostics.Count > diagnosticCount) conversion.Diagnostics.RemoveAt(conversion.Diagnostics.Count - 1);
        }

    }
}
