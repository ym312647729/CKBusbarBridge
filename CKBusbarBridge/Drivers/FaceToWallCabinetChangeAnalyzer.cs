using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 面对墙柜体二次驱动的脏分组。面对墙只有一个逻辑基座，
    /// 因此基座和绝缘子使用布尔标记，柜体开孔仍按柜前、柜后局部处理。
    /// </summary>
    internal sealed class FaceToWallCabinetChangeSet
    {
        internal FaceToWallCabinetChangeSet()
        {
            CabinetHoles = new HashSet<string>(StringComparer.Ordinal);
        }

        internal bool HasGeneralGlobals { get; set; }
        internal bool HasInsulationDistanceMetadata { get; set; }
        internal bool HasPhaseSwitchMetadata { get; set; }
        internal bool HasInstallationMetadata { get; set; }
        internal bool HasCabinetBody { get; set; }
        internal bool HasWallBushing { get; set; }
        internal bool HasExtensionBusbar { get; set; }
        internal bool HasInsulator { get; set; }
        internal bool HasBase { get; set; }
        internal HashSet<string> CabinetHoles { get; }

        internal bool IsEmpty => !HasGeneralGlobals
            && !HasInsulationDistanceMetadata
            && !HasPhaseSwitchMetadata
            && !HasInstallationMetadata
            && !HasCabinetBody
            && !HasWallBushing
            && !HasExtensionBusbar
            && !HasInsulator
            && !HasBase
            && CabinetHoles.Count == 0;
    }

    /// <summary>
    /// 对照面对墙参数快照，按照已确认的六个子驱动边界生成脏标记。
    /// </summary>
    internal static class FaceToWallCabinetChangeAnalyzer
    {
        internal const string Front = "柜前";
        internal const string Rear = "柜后";

        internal static FaceToWallCabinetChangeSet Compare(
            FaceToWallCabinetParameterSnapshot snapshot,
            FaceToWallCabinetDriverParameters current)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (current == null)
            {
                throw new ArgumentNullException(nameof(current));
            }

            var result = new FaceToWallCabinetChangeSet();
            result.HasInstallationMetadata = snapshot.InstallationMetadata != new FaceToWallInstallationMetadata().Encode(current);
            CompareGeneral(snapshot.General, current.General, result);
            CompareCabinet(snapshot.Cabinet, current.Cabinet, result);
            return result;
        }

        private static void CompareGeneral(
            FaceToWallGeneralParameterSnapshot previous,
            FaceToWallGeneralParameters current,
            FaceToWallCabinetChangeSet result)
        {
            var busbarSpecChanged = !SameText(previous.BusbarSpec, current.BusbarSpec);
            result.HasPhaseSwitchMetadata = previous.IsPhaseSwitched != current.IsPhaseSwitched;
            if (!SameText(previous.BusbarMaterial, current.BusbarMaterial))
                result.HasExtensionBusbar = true;
            if (!SameNumber(previous.InsulationDistance, current.InsulationDistance))
                result.HasInsulationDistanceMetadata = true;
            if (!SameNumber(previous.CabinetToWallDistance, current.CabinetToWallDistance)
                || !SameNumber(previous.FloorToBeamDistance, current.FloorToBeamDistance)
                || !SameNumber(previous.WallHoleEdgeGroundHeight, current.WallHoleEdgeGroundHeight)
                || !SameNumber(
                    previous.WallEntryBusbarCenterToHoleBottomDistance,
                    current.WallEntryBusbarCenterToHoleBottomDistance)
                || !SameNumber(previous.WallHoleHeight, current.WallHoleHeight)
                || !SameNumber(previous.F1, current.F1)
                || !SameNumber(previous.F2, current.F2)
                || !SameNumber(previous.F3, current.F3)
                || !SameNumber(previous.J, current.J)
                || !SameNumber(
                    previous.WallBushingTerminalToWallDistance,
                    current.WallBushingTerminalToWallDistance)
                || !SameNumber(previous.WallHoleWidth, current.WallHoleWidth)
                || !SameNumber(previous.CabinetGroundHeight, current.CabinetGroundHeight)
                || !SameNumber(previous.BuildingHeight, current.BuildingHeight)
                || !SameNumber(previous.WallCabinetEccentricity, current.WallCabinetEccentricity)
                || busbarSpecChanged)
            {
                result.HasGeneralGlobals = true;
            }

            var phaseSpacingChanged = !SameNumber(
                previous.ExposedBusbarPhaseSpacing,
                current.ExposedBusbarPhaseSpacing);
            var rootCountChanged = previous.SinglePhaseRootCount != current.SinglePhaseRootCount;
            if (busbarSpecChanged || phaseSpacingChanged || rootCountChanged)
            {
                result.HasExtensionBusbar = true;
                result.HasInsulator = true;
            }

            if (!SameNumber(previous.InsulatorHeight, current.InsulatorHeight))
            {
                result.HasInsulator = true;
            }

            result.HasWallBushing = TerminalChanged(
                previous.WallBushingTerminal,
                current.WallBushingTerminal);
        }

        private static void CompareCabinet(
            FaceToWallCabinetSideParameterSnapshot previous,
            FaceToWallCabinetSideParameters current,
            FaceToWallCabinetChangeSet result)
        {
            if (!SameNumber(previous.CabinetHeight, current.CabinetHeight)
                || !SameNumber(previous.CabinetDepth, current.CabinetDepth)
                || !SameNumber(previous.CabinetWidth, current.CabinetWidth))
            {
                result.HasCabinetBody = true;
            }

            CompareHole(previous.FrontHole, current.FrontHole, Front, result);
            CompareHole(previous.BackHole, current.BackHole, Rear, result);
            if (!SameNumber(previous.CabinetWidth, current.CabinetWidth)
                && (current.FrontHole?.IsEnabled == true || current.BackHole?.IsEnabled == true))
            {
                result.HasBase = true;
                result.HasInsulator = true;
            }
        }

        private static void CompareHole(
            FaceToWallCabinetHoleParameterSnapshot previous,
            FaceToWallCabinetHoleParameters current,
            string position,
            FaceToWallCabinetChangeSet result)
        {
            if (previous.IsEnabled != current.IsEnabled)
            {
                result.CabinetHoles.Add(position);
                result.HasBase = true;
                result.HasInsulator = true;
            }

            // 未启用位置没有实体基座和绝缘子，派生值变化无需触发模型驱动。
            if (!current.IsEnabled)
            {
                return;
            }

            if (!SameNumber(
                    previous.BaseToCabinetEdgeDistance,
                    current.BaseToCabinetEdgeDistance)
                || !SameNumber(
                    previous.BusbarCenterToCabinetEdgeDistance,
                    current.BusbarCenterToCabinetEdgeDistance))
            {
                result.CabinetHoles.Add(position);
            }

            var fixedOrL1OrL2Changed = !SameNumber(
                    previous.FixedHoleSpacing,
                    current.FixedHoleSpacing)
                || !SameNumber(previous.L1, current.L1)
                || !SameNumber(previous.L2, current.L2);
            var l3Changed = !SameNumber(previous.L3, current.L3);
            if (fixedOrL1OrL2Changed || l3Changed)
            {
                result.CabinetHoles.Add(position);
                result.HasBase = true;
            }

            var baseHeightChanged = !SameNumber(previous.BaseHeight, current.BaseHeight)
                || !SameNumber(previous.ExtensionBaseHeight, current.ExtensionBaseHeight);
            if (baseHeightChanged
                || !SameNumber(previous.BaseOpeningHeight, current.BaseOpeningHeight))
            {
                result.HasBase = true;
            }

            if (baseHeightChanged
                || !SameNumber(previous.InsulatorPosition, current.InsulatorPosition))
            {
                // 增高基座高度可能改变绝缘子宿主或宿主内局部定位。
                result.HasInsulator = true;
            }
        }

        private static bool TerminalChanged(
            FaceToWallBushingTerminalParameterSnapshot previous,
            FaceToWallBushingTerminalParameters current)
        {
            return !SameNumber(previous.Diameter, current.Diameter)
                || !SameNumber(previous.C1, current.C1)
                || !SameNumber(previous.C2, current.C2)
                || !SameNumber(previous.C3, current.C3)
                || !SameNumber(previous.C4, current.C4)
                || !SameNumber(previous.C5, current.C5)
                || !SameNumber(previous.C6, current.C6)
                || !SameNumber(previous.C7, current.C7)
                || !SameNumber(previous.C8, current.C8)
                || !SameNumber(previous.C9, current.C9)
                || !SameNumber(previous.C10, current.C10);
        }

        internal static bool SameNumber(double left, double right)
        {
            return Math.Round(left, 3, MidpointRounding.AwayFromZero)
                == Math.Round(right, 3, MidpointRounding.AwayFromZero);
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
