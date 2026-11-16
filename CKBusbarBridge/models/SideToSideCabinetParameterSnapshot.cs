using System;

namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 侧对侧柜体窗口的不可变参数快照。新模板统一使用前后孔几何，
    /// 两类基座的机械参数分别保留。
    /// </summary>
    internal sealed class SideToSideCabinetParameterSnapshot
    {
        private SideToSideCabinetParameterSnapshot(
            SideToSideGeneralParameterSnapshot general,
            SideToSideCabinetSideParameterSnapshot aCabinet,
            SideToSideCabinetSideParameterSnapshot bCabinet)
        {
            General = general ?? throw new ArgumentNullException(nameof(general));
            ACabinet = aCabinet ?? throw new ArgumentNullException(nameof(aCabinet));
            BCabinet = bCabinet ?? throw new ArgumentNullException(nameof(bCabinet));
        }

        internal SideToSideGeneralParameterSnapshot General { get; }

        internal SideToSideCabinetSideParameterSnapshot ACabinet { get; }

        internal SideToSideCabinetSideParameterSnapshot BCabinet { get; }

        internal static SideToSideCabinetParameterSnapshot Capture(
            SideToSideCabinetDriverParameters parameters)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            return new SideToSideCabinetParameterSnapshot(
                SideToSideGeneralParameterSnapshot.Capture(parameters.General),
                SideToSideCabinetSideParameterSnapshot.Capture(parameters.ACabinet),
                SideToSideCabinetSideParameterSnapshot.Capture(parameters.BCabinet));
        }

        internal SideToSideCabinetDriverParameters ToParameters()
        {
            var restored = new SideToSideCabinetDriverParameters();
            restored.General.FloorHeight = General.FloorHeight;
            restored.General.ParameterB = General.ParameterB;
            restored.General.CabinetSpacing = General.CabinetSpacing;
            restored.General.BusbarSpec = General.BusbarSpec;
            restored.General.SinglePhaseRootCount = General.SinglePhaseRootCount;
            restored.General.ExposedBusbarPhaseSpacing =
                General.ExposedBusbarPhaseSpacing;
            restored.General.InsulationDistance = General.InsulationDistance;
            restored.General.BusbarMaterial = General.BusbarMaterial;
            restored.General.InsulatorHeight = General.InsulatorHeight;
            restored.General.ACabinetExposedBusbarPhaseSequence =
                General.ACabinetExposedBusbarPhaseSequence;
            restored.General.BCabinetExposedBusbarPhaseSequence =
                General.BCabinetExposedBusbarPhaseSequence;
            restored.General.CabinetEccentricity = General.CabinetEccentricity;
            RestoreCabinet(ACabinet, restored.ACabinet);
            RestoreCabinet(BCabinet, restored.BCabinet);
            return restored;
        }

        private static void RestoreCabinet(
            SideToSideCabinetSideParameterSnapshot source,
            SideToSideCabinetParameters target)
        {
            target.CabinetHeight = source.CabinetHeight;
            target.CabinetDepth = source.CabinetDepth;
            target.CabinetWidth = source.CabinetWidth;
            target.GroundHeight = source.GroundHeight;
            RestoreHole(source.FrontHole, target.FrontHole);
            RestoreHole(source.BackHole, target.BackHole);
        }

        private static void RestoreHole(
            SideToSideCabinetHoleParameterSnapshot source,
            SideToSideCabinetHoleParameters target)
        {
            target.IsEnabled = source.IsEnabled;
            RestoreOpening(source.FrontRearOpening, target.FrontRearOpeningParameters);
            target.Base1.BaseHeight = source.Base1.BaseHeight;
            target.Base1.BaseOpeningHeight = source.Base1.BaseOpeningHeight;
            target.Base1.InsulatorPosition1 = source.Base1.InsulatorPosition1;
            target.Base1.InsulatorPosition2 = source.Base1.InsulatorPosition2;
            target.Base1.InsulatorPosition3 = source.Base1.InsulatorPosition3;
            target.Base2.BaseHeight = source.Base2.BaseHeight;
            target.Base2.BaseWidth = source.Base2.BaseWidth;
            target.Base2.BaseOpeningHeight = source.Base2.BaseOpeningHeight;
            target.Base2.ElevatedBaseHeight = source.Base2.ElevatedBaseHeight;
            target.Base2.InsulatorPosition = source.Base2.InsulatorPosition;
        }

        private static void RestoreOpening(
            SideToSideOpeningParameterSnapshot source,
            SideToSideCabinetTopOpeningParameters target)
        {
            target.ToCabinetEdgeDistance = source.ToCabinetEdgeDistance;
            target.BusbarCenterToCabinetEdgeDistance =
                source.BusbarCenterToCabinetEdgeDistance;
            target.FixedHoleSpacing = source.FixedHoleSpacing;
            target.L1 = source.L1;
            target.L2 = source.L2;
            target.L3 = source.L3;
        }
    }

    internal sealed class SideToSideGeneralParameterSnapshot
    {
        internal double FloorHeight { get; private set; }
        internal double ParameterB { get; private set; }
        internal double CabinetSpacing { get; private set; }
        internal double CabinetEccentricity { get; private set; }
        internal string BusbarSpec { get; private set; }
        internal int SinglePhaseRootCount { get; private set; }
        internal double ExposedBusbarPhaseSpacing { get; private set; }
        internal double InsulationDistance { get; private set; }
        internal string BusbarMaterial { get; private set; }
        internal double InsulatorHeight { get; private set; }
        internal string ACabinetExposedBusbarPhaseSequence { get; private set; }
        internal string BCabinetExposedBusbarPhaseSequence { get; private set; }
        internal SideToSideCabinetTopOpeningMode CabinetTopOpeningMode { get; private set; }
        internal SideToSideCabinetBaseKind BaseKind { get; private set; }

        internal static SideToSideGeneralParameterSnapshot Capture(
            SideToSideGeneralParameters source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            return new SideToSideGeneralParameterSnapshot
            {
                FloorHeight = source.FloorHeight,
                ParameterB = source.ParameterB,
                CabinetSpacing = source.CabinetSpacing,
                CabinetEccentricity = source.CabinetEccentricity,
                BusbarSpec = source.BusbarSpec,
                SinglePhaseRootCount = source.SinglePhaseRootCount,
                ExposedBusbarPhaseSpacing = source.ExposedBusbarPhaseSpacing,
                InsulationDistance = source.InsulationDistance,
                BusbarMaterial = source.BusbarMaterial,
                InsulatorHeight = source.InsulatorHeight,
                ACabinetExposedBusbarPhaseSequence =
                    source.ACabinetExposedBusbarPhaseSequence,
                BCabinetExposedBusbarPhaseSequence =
                    source.BCabinetExposedBusbarPhaseSequence,
                CabinetTopOpeningMode = source.CabinetTopOpeningMode,
                BaseKind = source.BaseKind
            };
        }
    }

    internal sealed class SideToSideCabinetSideParameterSnapshot
    {
        internal double CabinetHeight { get; private set; }
        internal double CabinetDepth { get; private set; }
        internal double CabinetWidth { get; private set; }
        internal double GroundHeight { get; private set; }
        internal SideToSideCabinetHoleParameterSnapshot FrontHole { get; private set; }
        internal SideToSideCabinetHoleParameterSnapshot BackHole { get; private set; }

        internal static SideToSideCabinetSideParameterSnapshot Capture(
            SideToSideCabinetParameters source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            return new SideToSideCabinetSideParameterSnapshot
            {
                CabinetHeight = source.CabinetHeight,
                CabinetDepth = source.CabinetDepth,
                CabinetWidth = source.CabinetWidth,
                GroundHeight = source.GroundHeight,
                FrontHole = SideToSideCabinetHoleParameterSnapshot.Capture(source.FrontHole),
                BackHole = SideToSideCabinetHoleParameterSnapshot.Capture(source.BackHole)
            };
        }
    }

    internal sealed class SideToSideCabinetHoleParameterSnapshot
    {
        internal bool IsEnabled { get; private set; }
        internal SideToSideOpeningParameterSnapshot FrontRearOpening { get; private set; }
        internal SideToSideOpeningParameterSnapshot LeftRightOpening { get; private set; }
        internal SideToSideBase1ParameterSnapshot Base1 { get; private set; }
        internal SideToSideBase2ParameterSnapshot Base2 { get; private set; }

        internal static SideToSideCabinetHoleParameterSnapshot Capture(
            SideToSideCabinetHoleParameters source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            return new SideToSideCabinetHoleParameterSnapshot
            {
                IsEnabled = source.IsEnabled,
                FrontRearOpening = SideToSideOpeningParameterSnapshot.Capture(
                    source.FrontRearOpeningParameters),
                LeftRightOpening = SideToSideOpeningParameterSnapshot.Capture(
                    source.LeftRightOpeningParameters),
                Base1 = SideToSideBase1ParameterSnapshot.Capture(source.Base1),
                Base2 = SideToSideBase2ParameterSnapshot.Capture(source.Base2)
            };
        }

        internal SideToSideOpeningParameterSnapshot GetOpening(
            SideToSideCabinetTopOpeningMode mode)
        {
            return FrontRearOpening;
        }
    }

    internal sealed class SideToSideOpeningParameterSnapshot
    {
        internal double ToCabinetEdgeDistance { get; private set; }
        internal double BusbarCenterToCabinetEdgeDistance { get; private set; }
        internal double FixedHoleSpacing { get; private set; }
        internal double L1 { get; private set; }
        internal double L2 { get; private set; }
        internal double L3 { get; private set; }

        internal static SideToSideOpeningParameterSnapshot Capture(
            SideToSideCabinetTopOpeningParameters source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            return new SideToSideOpeningParameterSnapshot
            {
                ToCabinetEdgeDistance = source.ToCabinetEdgeDistance,
                BusbarCenterToCabinetEdgeDistance =
                    source.BusbarCenterToCabinetEdgeDistance,
                FixedHoleSpacing = source.FixedHoleSpacing,
                L1 = source.L1,
                L2 = source.L2,
                L3 = source.L3
            };
        }
    }

    internal sealed class SideToSideBase1ParameterSnapshot
    {
        internal double BaseHeight { get; private set; }
        internal double BaseOpeningHeight { get; private set; }
        internal double InsulatorPosition1 { get; private set; }
        internal double InsulatorPosition2 { get; private set; }
        internal double InsulatorPosition3 { get; private set; }

        internal static SideToSideBase1ParameterSnapshot Capture(
            SideToSideBase1Parameters source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            return new SideToSideBase1ParameterSnapshot
            {
                BaseHeight = source.BaseHeight,
                BaseOpeningHeight = source.BaseOpeningHeight,
                InsulatorPosition1 = source.InsulatorPosition1,
                InsulatorPosition2 = source.InsulatorPosition2,
                InsulatorPosition3 = source.InsulatorPosition3
            };
        }
    }

    internal sealed class SideToSideBase2ParameterSnapshot
    {
        internal double BaseHeight { get; private set; }
        internal double BaseWidth { get; private set; }
        internal double BaseOpeningHeight { get; private set; }
        internal double ElevatedBaseHeight { get; private set; }
        internal double InsulatorPosition { get; private set; }

        internal static SideToSideBase2ParameterSnapshot Capture(
            SideToSideBase2Parameters source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            return new SideToSideBase2ParameterSnapshot
            {
                BaseHeight = source.BaseHeight,
                BaseWidth = source.BaseWidth,
                BaseOpeningHeight = source.BaseOpeningHeight,
                ElevatedBaseHeight = source.ElevatedBaseHeight,
                InsulatorPosition = source.InsulatorPosition
            };
        }
    }
}
