using System;

namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 面对墙柜体界面的不可变参数快照。快照保存界面联动完成后的最终值，
    /// 因此墙孔高度等参数引起的基座派生值变化也能被二次驱动准确识别。
    /// </summary>
    internal sealed class FaceToWallCabinetParameterSnapshot
    {
        private FaceToWallCabinetParameterSnapshot(
            FaceToWallGeneralParameterSnapshot general,
            FaceToWallCabinetSideParameterSnapshot cabinet)
        {
            General = general ?? throw new ArgumentNullException(nameof(general));
            Cabinet = cabinet ?? throw new ArgumentNullException(nameof(cabinet));
        }

        internal FaceToWallGeneralParameterSnapshot General { get; }

        internal FaceToWallCabinetSideParameterSnapshot Cabinet { get; }
        internal string InstallationMetadata { get; private set; }

        internal static FaceToWallCabinetParameterSnapshot Capture(
            FaceToWallCabinetDriverParameters parameters)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            return new FaceToWallCabinetParameterSnapshot(
                FaceToWallGeneralParameterSnapshot.Capture(parameters.General),
                FaceToWallCabinetSideParameterSnapshot.Capture(parameters.Cabinet))
            { InstallationMetadata = new FaceToWallInstallationMetadata().Encode(parameters) };
        }
    }

    internal sealed class FaceToWallGeneralParameterSnapshot
    {
        internal double CabinetToWallDistance { get; private set; }
        internal double FloorToBeamDistance { get; private set; }
        internal double WallHoleEdgeGroundHeight { get; private set; }
        internal double WallEntryBusbarCenterToHoleBottomDistance { get; private set; }
        internal double WallHoleHeight { get; private set; }
        internal double ExposedBusbarPhaseSpacing { get; private set; }
        internal double InsulationDistance { get; private set; }
        internal bool IsPhaseSwitched { get; private set; }
        internal string BusbarSpec { get; private set; }
        internal string BusbarMaterial { get; private set; }
        internal int SinglePhaseRootCount { get; private set; }
        internal double F1 { get; private set; }
        internal double F2 { get; private set; }
        internal double F3 { get; private set; }
        internal double J { get; private set; }
        internal double WallBushingTerminalToWallDistance { get; private set; }
        internal double WallHoleWidth { get; private set; }
        internal double CabinetGroundHeight { get; private set; }
        internal double BuildingHeight { get; private set; }
        internal double WallCabinetEccentricity { get; private set; }
        internal double InsulatorHeight { get; private set; }
        internal FaceToWallBushingTerminalParameterSnapshot WallBushingTerminal { get; private set; }

        internal static FaceToWallGeneralParameterSnapshot Capture(
            FaceToWallGeneralParameters source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            return new FaceToWallGeneralParameterSnapshot
            {
                CabinetToWallDistance = source.CabinetToWallDistance,
                IsPhaseSwitched = source.IsPhaseSwitched,
                FloorToBeamDistance = source.FloorToBeamDistance,
                WallHoleEdgeGroundHeight = source.WallHoleEdgeGroundHeight,
                WallEntryBusbarCenterToHoleBottomDistance =
                    source.WallEntryBusbarCenterToHoleBottomDistance,
                WallHoleHeight = source.WallHoleHeight,
                ExposedBusbarPhaseSpacing = source.ExposedBusbarPhaseSpacing,
                InsulationDistance = source.InsulationDistance,
                BusbarSpec = source.BusbarSpec,
                BusbarMaterial = source.BusbarMaterial,
                SinglePhaseRootCount = source.SinglePhaseRootCount,
                F1 = source.F1,
                F2 = source.F2,
                F3 = source.F3,
                J = source.J,
                WallBushingTerminalToWallDistance = source.WallBushingTerminalToWallDistance,
                WallHoleWidth = source.WallHoleWidth,
                CabinetGroundHeight = source.CabinetGroundHeight,
                BuildingHeight = source.BuildingHeight,
                WallCabinetEccentricity = source.WallCabinetEccentricity,
                InsulatorHeight = source.InsulatorHeight,
                WallBushingTerminal = FaceToWallBushingTerminalParameterSnapshot.Capture(
                    source.WallBushingTerminal)
            };
        }
    }

    internal sealed class FaceToWallBushingTerminalParameterSnapshot
    {
        internal double Diameter { get; private set; }
        internal double C1 { get; private set; }
        internal double C2 { get; private set; }
        internal double C3 { get; private set; }
        internal double C4 { get; private set; }
        internal double C5 { get; private set; }
        internal double C6 { get; private set; }
        internal double C7 { get; private set; }
        internal double C8 { get; private set; }
        internal double C9 { get; private set; }
        internal double C10 { get; private set; }

        internal static FaceToWallBushingTerminalParameterSnapshot Capture(
            FaceToWallBushingTerminalParameters source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            return new FaceToWallBushingTerminalParameterSnapshot
            {
                Diameter = source.Diameter,
                C1 = source.C1,
                C2 = source.C2,
                C3 = source.C3,
                C4 = source.C4,
                C5 = source.C5,
                C6 = source.C6,
                C7 = source.C7,
                C8 = source.C8,
                C9 = source.C9,
                C10 = source.C10
            };
        }
    }

    internal sealed class FaceToWallCabinetSideParameterSnapshot
    {
        internal double CabinetHeight { get; private set; }
        internal double CabinetDepth { get; private set; }
        internal double CabinetWidth { get; private set; }
        internal FaceToWallCabinetHoleParameterSnapshot FrontHole { get; private set; }
        internal FaceToWallCabinetHoleParameterSnapshot BackHole { get; private set; }

        internal static FaceToWallCabinetSideParameterSnapshot Capture(
            FaceToWallCabinetSideParameters source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            return new FaceToWallCabinetSideParameterSnapshot
            {
                CabinetHeight = source.CabinetHeight,
                CabinetDepth = source.CabinetDepth,
                CabinetWidth = source.CabinetWidth,
                FrontHole = FaceToWallCabinetHoleParameterSnapshot.Capture(source.FrontHole),
                BackHole = FaceToWallCabinetHoleParameterSnapshot.Capture(source.BackHole)
            };
        }
    }

    internal sealed class FaceToWallCabinetHoleParameterSnapshot
    {
        internal bool IsEnabled { get; private set; }
        internal double BaseToCabinetEdgeDistance { get; private set; }
        internal double BusbarCenterToCabinetEdgeDistance { get; private set; }
        internal double FixedHoleSpacing { get; private set; }
        internal double L1 { get; private set; }
        internal double L2 { get; private set; }
        internal double L3 { get; private set; }
        internal double InsulatorPosition { get; private set; }
        internal double BaseOpeningHeight { get; private set; }
        internal double BaseHeight { get; private set; }
        internal double ExtensionBaseHeight { get; private set; }

        internal static FaceToWallCabinetHoleParameterSnapshot Capture(
            FaceToWallCabinetHoleParameters source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            return new FaceToWallCabinetHoleParameterSnapshot
            {
                IsEnabled = source.IsEnabled,
                BaseToCabinetEdgeDistance = source.BaseToCabinetEdgeDistance,
                BusbarCenterToCabinetEdgeDistance = source.BusbarCenterToCabinetEdgeDistance,
                FixedHoleSpacing = source.FixedHoleSpacing,
                L1 = source.L1,
                L2 = source.L2,
                L3 = source.L3,
                InsulatorPosition = source.InsulatorPosition,
                BaseOpeningHeight = source.BaseOpeningHeight,
                BaseHeight = source.BaseHeight,
                ExtensionBaseHeight = source.ExtensionBaseHeight
            };
        }
    }
}
