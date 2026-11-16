using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;

namespace CKBusbarBridge.Drivers
{
    internal sealed class SideToSideCabinetChangeSet
    {
        internal SideToSideCabinetChangeSet()
        {
            CabinetHoles = new HashSet<string>(StringComparer.Ordinal);
            HostPositions = new HashSet<string>(StringComparer.Ordinal);
        }

        internal bool HasRootGlobals { get; set; }
        internal bool HasInsulationDistanceMetadata { get; set; }
        internal bool HasACabinetBody { get; set; }
        internal bool HasBCabinetBody { get; set; }
        internal bool HasBaseKindChange { get; set; }
        internal bool HasExtensionBusbar { get; set; }
        internal bool HasInsulatorModels { get; set; }
        internal bool HasBaseLifecycle { get; set; }
        internal HashSet<string> CabinetHoles { get; }
        internal HashSet<string> HostPositions { get; }

        internal bool IsEmpty => !HasRootGlobals
            && !HasInsulationDistanceMetadata
            && !HasACabinetBody
            && !HasBCabinetBody
            && !HasBaseKindChange
            && !HasExtensionBusbar
            && !HasInsulatorModels
            && !HasBaseLifecycle
            && CabinetHoles.Count == 0
            && HostPositions.Count == 0;
    }

    internal static class SideToSideCabinetChangeAnalyzer
    {
        internal const string AFront = "A柜前";
        internal const string ABack = "A柜后";
        internal const string BFront = "B柜前";
        internal const string BBack = "B柜后";

        internal static SideToSideCabinetChangeSet Compare(
            SideToSideCabinetParameterSnapshot snapshot,
            SideToSideCabinetDriverParameters current)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            if (current == null)
            {
                throw new ArgumentNullException(nameof(current));
            }

            var changes = new SideToSideCabinetChangeSet();
            CompareGeneral(snapshot.General, current.General, changes);
            CompareCabinet(
                snapshot.ACabinet,
                current.ACabinet,
                "A柜",
                changes);
            CompareCabinet(
                snapshot.BCabinet,
                current.BCabinet,
                "B柜",
                changes);

            if (changes.HasBaseKindChange)
            {
                changes.HasBaseLifecycle = true;
            }

            return changes;
        }

        private static void CompareGeneral(
            SideToSideGeneralParameterSnapshot previous,
            SideToSideGeneralParameters current,
            SideToSideCabinetChangeSet changes)
        {
            if (!SameNumber(previous.InsulationDistance, current.InsulationDistance))
                changes.HasInsulationDistanceMetadata = true;
            if (!SameNumber(previous.FloorHeight, current.FloorHeight)
                || !SameNumber(previous.ParameterB, current.ParameterB)
                || !SameNumber(previous.CabinetSpacing, current.CabinetSpacing)
                || !SameNumber(
                    previous.CabinetEccentricity,
                    current.CabinetEccentricity))
            {
                changes.HasRootGlobals = true;
            }

            var specificationChanged = !SameText(previous.BusbarSpec, current.BusbarSpec);
            var spacingChanged = !SameNumber(
                previous.ExposedBusbarPhaseSpacing,
                current.ExposedBusbarPhaseSpacing);
            var rootCountChanged = previous.SinglePhaseRootCount
                != current.SinglePhaseRootCount;
            var phaseChanged = !SameText(
                    previous.ACabinetExposedBusbarPhaseSequence,
                    current.ACabinetExposedBusbarPhaseSequence)
                || !SameText(
                    previous.BCabinetExposedBusbarPhaseSequence,
                    current.BCabinetExposedBusbarPhaseSequence);
            var materialChanged = !SameText(
                previous.BusbarMaterial,
                current.BusbarMaterial);

            if (specificationChanged
                || spacingChanged
                || rootCountChanged
                || phaseChanged
                || materialChanged)
            {
                changes.HasExtensionBusbar = true;
            }

            if (specificationChanged
                || spacingChanged
                || rootCountChanged
                || !SameNumber(previous.InsulatorHeight, current.InsulatorHeight))
            {
                changes.HasInsulatorModels = true;
            }

            changes.HasBaseKindChange = previous.BaseKind != current.BaseKind;
            if (changes.HasBaseKindChange || SideToSideSchemeRules.HasOffset(previous.CabinetEccentricity) !=
                    SideToSideSchemeRules.HasOffset(current.CabinetEccentricity))
                changes.HasRootGlobals = true;
        }

        private static void CompareCabinet(
            SideToSideCabinetSideParameterSnapshot previous,
            SideToSideCabinetParameters current,
            string cabinetName,
            SideToSideCabinetChangeSet changes)
        {
            var bodyChanged = !SameNumber(previous.CabinetHeight, current.CabinetHeight)
                || !SameNumber(previous.CabinetDepth, current.CabinetDepth)
                || !SameNumber(previous.CabinetWidth, current.CabinetWidth)
                || !SameNumber(previous.GroundHeight, current.GroundHeight);
            // 基座生命周期会按各位置的物理签名更新支架，不能先用两柜共用宽度驱动旧模型。
            if (!SameNumber(previous.CabinetWidth, current.CabinetWidth)
                && (current.FrontHole?.IsEnabled == true || current.BackHole?.IsEnabled == true))
                changes.HasBaseLifecycle = true;
            if (cabinetName == "A柜")
            {
                changes.HasACabinetBody = bodyChanged;
            }
            else
            {
                changes.HasBCabinetBody = bodyChanged;
            }

            CompareHole(
                previous.FrontHole,
                current.FrontHole,
                cabinetName + "前",
                current,
                changes);
            CompareHole(
                previous.BackHole,
                current.BackHole,
                cabinetName + "后",
                current,
                changes);
        }

        private static void CompareHole(
            SideToSideCabinetHoleParameterSnapshot previous,
            SideToSideCabinetHoleParameters current,
            string positionName,
            SideToSideCabinetParameters cabinet,
            SideToSideCabinetChangeSet changes)
        {
            if (previous.IsEnabled != current.IsEnabled)
            {
                changes.CabinetHoles.Add(positionName);
                changes.HasBaseLifecycle = true;
            }

            if (!current.IsEnabled)
            {
                return;
            }

            var oldOpening = previous.FrontRearOpening;
            var opening = current.FrontRearOpeningParameters;
            var edgeChanged = !SameNumber(
                    oldOpening.ToCabinetEdgeDistance,
                    opening.ToCabinetEdgeDistance)
                || !SameNumber(
                    oldOpening.BusbarCenterToCabinetEdgeDistance,
                    opening.BusbarCenterToCabinetEdgeDistance);
            var baseOpeningChanged = !SameNumber(
                    oldOpening.FixedHoleSpacing,
                    opening.FixedHoleSpacing)
                || !SameNumber(oldOpening.L1, opening.L1)
                || !SameNumber(oldOpening.L2, opening.L2)
                || !SameNumber(oldOpening.L3, opening.L3);
            if (edgeChanged || baseOpeningChanged)
            {
                changes.CabinetHoles.Add(positionName);
            }
            if (baseOpeningChanged)
            {
                changes.HasBaseLifecycle = true;
            }

            if (current.BaseKind == SideToSideCabinetBaseKind.Base1)
            {
                var geometryChanged = !SameNumber(
                        previous.Base1.BaseHeight,
                        current.Base1.BaseHeight)
                    || !SameNumber(
                        previous.Base1.BaseOpeningHeight,
                        current.Base1.BaseOpeningHeight);
                var placementChanged = !SameNumber(
                        previous.Base1.InsulatorPosition1,
                        current.Base1.InsulatorPosition1)
                    || !SameNumber(
                        previous.Base1.InsulatorPosition2,
                        current.Base1.InsulatorPosition2)
                    || !SameNumber(
                        previous.Base1.InsulatorPosition3,
                        current.Base1.InsulatorPosition3);
                if (geometryChanged || placementChanged)
                {
                    changes.HasBaseLifecycle = true;
                }
                if (placementChanged)
                {
                    changes.HostPositions.Add(positionName);
                }
                return;
            }

            var base2GeometryChanged = !SameNumber(
                    previous.Base2.BaseHeight,
                    current.Base2.BaseHeight)
                || !SameNumber(previous.Base2.BaseWidth, current.Base2.BaseWidth)
                || !SameNumber(
                    previous.Base2.BaseOpeningHeight,
                    current.Base2.BaseOpeningHeight)
                || !SameNumber(
                    previous.Base2.ElevatedBaseHeight,
                    current.Base2.ElevatedBaseHeight);
            var base2PlacementChanged = !SameNumber(
                previous.Base2.InsulatorPosition,
                current.Base2.InsulatorPosition);
            if (base2GeometryChanged || base2PlacementChanged)
            {
                changes.HasBaseLifecycle = true;
            }
            if (base2PlacementChanged)
            {
                changes.HostPositions.Add(positionName);
            }
        }

        internal static bool SameNumber(double left, double right)
        {
            return !double.IsNaN(left) && !double.IsNaN(right)
                && !double.IsInfinity(left) && !double.IsInfinity(right) && Math.Abs(left - right) <= 1.0;
        }

        private static bool SameText(string left, string right)
        {
            return string.Equals(
                left ?? string.Empty,
                right ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
