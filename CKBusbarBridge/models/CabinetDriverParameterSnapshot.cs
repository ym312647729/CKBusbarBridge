using System;

namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 面对面柜体驱动界面参数的不可变快照。
    /// 驱动成功或打开界面反读成功后写入，二次驱动用它对照当前界面找出脏字段。
    /// </summary>
    internal sealed class CabinetDriverParameterSnapshot
    {
        internal CabinetDriverParameterSnapshot(
            CabinetGeneralParameterSnapshot general,
            CabinetSideParameterSnapshot aCabinet,
            CabinetSideParameterSnapshot bCabinet)
        {
            General = general ?? throw new ArgumentNullException(nameof(general));
            ACabinet = aCabinet ?? throw new ArgumentNullException(nameof(aCabinet));
            BCabinet = bCabinet ?? throw new ArgumentNullException(nameof(bCabinet));
        }

        internal CabinetGeneralParameterSnapshot General { get; }

        internal CabinetSideParameterSnapshot ACabinet { get; }

        internal CabinetSideParameterSnapshot BCabinet { get; }

        /// <summary>
        /// 从当前界面参数复制全部可编辑字段和派生字段。
        /// </summary>
        internal static CabinetDriverParameterSnapshot Capture(CabinetDriverParameters parameters)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            return new CabinetDriverParameterSnapshot(
                CabinetGeneralParameterSnapshot.Capture(parameters.General),
                CabinetSideParameterSnapshot.Capture(parameters.ACabinet),
                CabinetSideParameterSnapshot.Capture(parameters.BCabinet));
        }
    }

    internal sealed class CabinetGeneralParameterSnapshot
    {
        internal double FloorHeight { get; set; }

        internal double CabinetSpacing { get; set; }

        internal double CabinetEccentricity { get; set; }

        internal double ParameterB { get; set; }

        internal double PhaseSpacing { get; set; }
        internal double InsulationDistance { get; set; }

        internal string BusbarSpec { get; set; }

        internal int SinglePhaseRootCount { get; set; }

        internal string ACabinetExposedBusbarPhaseSequence { get; set; }

        internal string BCabinetExposedBusbarPhaseSequence { get; set; }

        internal string BusbarMaterial { get; set; }

        internal double InsulatorHeight { get; set; }

        internal static CabinetGeneralParameterSnapshot Capture(CabinetGeneralParameters general)
        {
            if (general == null)
            {
                throw new ArgumentNullException(nameof(general));
            }

            return new CabinetGeneralParameterSnapshot
            {
                FloorHeight = general.FloorHeight,
                CabinetSpacing = general.CabinetSpacing,
                CabinetEccentricity = general.CabinetEccentricity,
                ParameterB = general.ParameterB,
                PhaseSpacing = general.PhaseSpacing,
                InsulationDistance = general.InsulationDistance,
                BusbarSpec = general.BusbarSpec,
                SinglePhaseRootCount = general.SinglePhaseRootCount,
                ACabinetExposedBusbarPhaseSequence =
                    general.ACabinetExposedBusbarPhaseSequence,
                BCabinetExposedBusbarPhaseSequence =
                    general.BCabinetExposedBusbarPhaseSequence,
                BusbarMaterial = general.BusbarMaterial,
                InsulatorHeight = general.InsulatorHeight
            };
        }
    }

    internal sealed class CabinetSideParameterSnapshot
    {
        internal double CabinetHeight { get; set; }

        internal double CabinetDepth { get; set; }

        internal double CabinetWidth { get; set; }

        internal double GroundHeight { get; set; }

        internal CabinetHoleParameterSnapshot FrontHole { get; set; }

        internal CabinetHoleParameterSnapshot BackHole { get; set; }

        internal static CabinetSideParameterSnapshot Capture(CabinetSideParameters side)
        {
            if (side == null)
            {
                throw new ArgumentNullException(nameof(side));
            }

            return new CabinetSideParameterSnapshot
            {
                CabinetHeight = side.CabinetHeight,
                CabinetDepth = side.CabinetDepth,
                CabinetWidth = side.CabinetWidth,
                GroundHeight = side.GroundHeight,
                FrontHole = CabinetHoleParameterSnapshot.Capture(side.FrontHole),
                BackHole = CabinetHoleParameterSnapshot.Capture(side.BackHole)
            };
        }
    }

    internal sealed class CabinetHoleParameterSnapshot
    {
        internal bool IsEnabled { get; set; }

        internal double ToCabinetEdgeDistance { get; set; }

        internal double FixedHoleSpacing { get; set; }

        internal double L1 { get; set; }

        internal double L2 { get; set; }

        internal double L3 { get; set; }

        internal double BusbarCenterToCabinetEdgeDistance { get; set; }

        internal double BaseHeight { get; set; }

        internal double ExtensionBaseHeight { get; set; }

        internal double BaseOpeningHeight { get; set; }

        internal double InsulatorPosition { get; set; }

        internal static CabinetHoleParameterSnapshot Capture(CabinetHoleParameters hole)
        {
            if (hole == null)
            {
                throw new ArgumentNullException(nameof(hole));
            }

            return new CabinetHoleParameterSnapshot
            {
                IsEnabled = hole.IsEnabled,
                ToCabinetEdgeDistance = hole.ToCabinetEdgeDistance,
                FixedHoleSpacing = hole.FixedHoleSpacing,
                L1 = hole.L1,
                L2 = hole.L2,
                L3 = hole.L3,
                BusbarCenterToCabinetEdgeDistance = hole.BusbarCenterToCabinetEdgeDistance,
                BaseHeight = hole.BaseHeight,
                ExtensionBaseHeight = hole.ExtensionBaseHeight,
                BaseOpeningHeight = hole.BaseOpeningHeight,
                InsulatorPosition = hole.InsulatorPosition
            };
        }
    }
}
