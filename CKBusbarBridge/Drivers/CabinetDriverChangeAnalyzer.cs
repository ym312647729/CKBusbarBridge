using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 面对面柜体二次驱动的脏分组。一个字段可以同时点亮多个子驱动。
    /// </summary>
    internal sealed class CabinetDriverChangeSet
    {
        internal CabinetDriverChangeSet()
        {
            CabinetHoles = new HashSet<string>(StringComparer.Ordinal);
            BasePositions = new HashSet<string>(StringComparer.Ordinal);
            InsulatorPositions = new HashSet<string>(StringComparer.Ordinal);
            NewlyEnabledHoles = new HashSet<string>(StringComparer.Ordinal);
            AddElevatedPositions = new HashSet<string>(StringComparer.Ordinal);
            RemoveElevatedPositions = new HashSet<string>(StringComparer.Ordinal);
        }

        internal bool HasGeneralGlobals { get; set; }
        internal bool HasInsulationDistanceMetadata { get; set; }

        internal bool HasExtensionBusbar { get; set; }

        internal ExtensionBusbarDriveSelection ExtensionBusbarSelection { get; set; }

        internal bool HasACabinetBody { get; set; }

        internal bool HasBCabinetBody { get; set; }

        /// <summary>柜体开孔草图/特征，键为 A柜前、A柜后、B柜前、B柜后。</summary>
        internal HashSet<string> CabinetHoles { get; }

        /// <summary>独立基座外形，不含绝缘子模型。</summary>
        internal HashSet<string> BasePositions { get; }

        /// <summary>基座内绝缘子模型；支架长随基座宽/L3 变化。</summary>
        internal HashSet<string> InsulatorPositions { get; }

        /// <summary>孔从关到开：新基座事务会为该位置从模板生成并装配模型。</summary>
        internal HashSet<string> NewlyEnabledHoles { get; }

        /// <summary>总基座高跨过 1400：该位置需新增加高基座并改普通基座叠装配合。</summary>
        internal HashSet<string> AddElevatedPositions { get; }

        /// <summary>总基座高跌破 1400：该位置去掉增高基座，普通基座改回根坐标系。</summary>
        internal HashSet<string> RemoveElevatedPositions { get; }

        internal bool IsEmpty
        {
            get
            {
                return !HasGeneralGlobals
                    && !HasInsulationDistanceMetadata
                    && !HasExtensionBusbar
                    && !HasACabinetBody
                    && !HasBCabinetBody
                    && CabinetHoles.Count == 0
                    && BasePositions.Count == 0
                    && InsulatorPositions.Count == 0
                    && NewlyEnabledHoles.Count == 0
                    && AddElevatedPositions.Count == 0
                    && RemoveElevatedPositions.Count == 0;
            }
        }

        /// <summary>
        /// 新基座事务已经支持位置新增/移除和增减增高基座，因此这些变化不再整单拦截。
        /// </summary>
        internal bool HasUnsupportedStructuralChanges
        {
            get { return false; }
        }

        /// <summary>是否需要跑基座/绝缘子二次服务（含增减增高）。</summary>
        internal bool NeedsBaseOrInsulatorUpdate
        {
            get
            {
                return BasePositions.Count > 0
                    || InsulatorPositions.Count > 0
                    || AddElevatedPositions.Count > 0
                    || RemoveElevatedPositions.Count > 0;
            }
        }
    }

    /// <summary>
    /// 对照参数快照与当前界面，按「字段 → 子驱动」规则生成脏分组。
    /// L3 会同时打脏柜体开孔、基座和绝缘子（支架长度 = 基座宽 − 20）。
    /// </summary>
    internal static class CabinetDriverChangeAnalyzer
    {
        internal const string AFront = "A柜前";
        internal const string ABack = "A柜后";
        internal const string BFront = "B柜前";
        internal const string BBack = "B柜后";

        internal static CabinetDriverChangeSet Compare(
            CabinetDriverParameterSnapshot snapshot,
            CabinetDriverParameters current)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (current == null)
            {
                throw new ArgumentNullException(nameof(current));
            }

            var changeSet = new CabinetDriverChangeSet();
            CompareGeneral(snapshot.General, current.General, changeSet, current);
            CompareSide(snapshot.ACabinet, current.ACabinet, isACabinet: true, changeSet);
            CompareSide(snapshot.BCabinet, current.BCabinet, isACabinet: false, changeSet);
            return changeSet;
        }

        private static void CompareGeneral(
            CabinetGeneralParameterSnapshot previous,
            CabinetGeneralParameters current,
            CabinetDriverChangeSet changeSet,
            CabinetDriverParameters currentParameters)
        {
            if (previous == null || current == null)
            {
                return;
            }

            if (!SameNumber(previous.CabinetSpacing, current.CabinetSpacing)
                || !SameNumber(previous.FloorHeight, current.FloorHeight)
                || !SameNumber(previous.ParameterB, current.ParameterB)
                || !SameNumber(previous.CabinetEccentricity, current.CabinetEccentricity))
            {
                changeSet.HasGeneralGlobals = true;
            }

            var busbarSpecChanged = !SameText(previous.BusbarSpec, current.BusbarSpec);
            var phaseSpacingChanged = !SameNumber(previous.PhaseSpacing, current.PhaseSpacing);
            var rootCountChanged = previous.SinglePhaseRootCount != current.SinglePhaseRootCount;
            var aPhaseSequenceChanged = !SameText(
                    previous.ACabinetExposedBusbarPhaseSequence,
                    current.ACabinetExposedBusbarPhaseSequence);
            var bPhaseSequenceChanged = !SameText(
                    previous.BCabinetExposedBusbarPhaseSequence,
                    current.BCabinetExposedBusbarPhaseSequence);
            var phaseSequenceChanged = aPhaseSequenceChanged || bPhaseSequenceChanged;
            var materialChanged = !SameText(previous.BusbarMaterial, current.BusbarMaterial);
            changeSet.ExtensionBusbarSelection = new ExtensionBusbarDriveSelection(
                busbarSpecChanged || phaseSpacingChanged,
                aPhaseSequenceChanged, bPhaseSequenceChanged, materialChanged);
            var insulatorHeightChanged = !SameNumber(previous.InsulatorHeight, current.InsulatorHeight);
            if (!SameNumber(previous.InsulationDistance, current.InsulationDistance))
                changeSet.HasInsulationDistanceMetadata = true;

            if (busbarSpecChanged
                || phaseSpacingChanged
                || rootCountChanged
                || phaseSequenceChanged
                || materialChanged)
            {
                changeSet.HasExtensionBusbar = true;
            }

            // 绝缘子尺寸来自整体参数；启用孔的绝缘子都要跟着改。
            if (busbarSpecChanged || phaseSpacingChanged || rootCountChanged || insulatorHeightChanged)
            {
                MarkEnabledInsulatorPositions(currentParameters, changeSet);
            }
        }

        private static void CompareSide(
            CabinetSideParameterSnapshot previous,
            CabinetSideParameters current,
            bool isACabinet,
            CabinetDriverChangeSet changeSet)
        {
            if (previous == null || current == null)
            {
                return;
            }

            if (!SameNumber(previous.CabinetHeight, current.CabinetHeight)
                || !SameNumber(previous.CabinetDepth, current.CabinetDepth)
                || !SameNumber(previous.CabinetWidth, current.CabinetWidth)
                || !SameNumber(previous.GroundHeight, current.GroundHeight))
            {
                if (isACabinet)
                {
                    changeSet.HasACabinetBody = true;
                }
                else
                {
                    changeSet.HasBCabinetBody = true;
                }
            }

            CompareHole(
                previous.FrontHole,
                current.FrontHole,
                isACabinet ? AFront : BFront,
                changeSet);

            if (!SameNumber(previous.CabinetWidth, current.CabinetWidth))
            {
                var front = isACabinet ? AFront : BFront;
                var back = isACabinet ? ABack : BBack;
                if (current.FrontHole?.IsEnabled == true) changeSet.BasePositions.Add(front);
                if (current.BackHole?.IsEnabled == true) changeSet.BasePositions.Add(back);
                MarkIfEnabled(current.FrontHole, front, changeSet);
                MarkIfEnabled(current.BackHole, back, changeSet);
            }
            CompareHole(
                previous.BackHole,
                current.BackHole,
                isACabinet ? ABack : BBack,
                changeSet);
        }

        private static void CompareHole(
            CabinetHoleParameterSnapshot previous,
            CabinetHoleParameters current,
            string positionName,
            CabinetDriverChangeSet changeSet)
        {
            if (previous == null || current == null)
            {
                return;
            }

            var previousElevated = NeedsElevatedBase(previous.BaseHeight);
            var currentElevated = NeedsElevatedBase(current.BaseHeight);
            if (previousElevated != currentElevated)
            {
                // 增减增高是结构动作；同时把基座尺寸标脏，便于后续驱动普通基座高度与侧封板。
                if (currentElevated)
                {
                    changeSet.AddElevatedPositions.Add(positionName);
                }
                else
                {
                    changeSet.RemoveElevatedPositions.Add(positionName);
                }

                changeSet.BasePositions.Add(positionName);
            }

            if (previous.IsEnabled != current.IsEnabled)
            {
                changeSet.CabinetHoles.Add(positionName);
                // 启用时需要插入新基座，停用时需要从总装移除旧基座；两者都进入新基座事务。
                changeSet.BasePositions.Add(positionName);
                if (current.IsEnabled && !previous.IsEnabled)
                {
                    changeSet.NewlyEnabledHoles.Add(positionName);
                }
            }

            // 关闭的孔只处理柜体压缩，不再驱动基座或绝缘子。
            if (!current.IsEnabled)
            {
                return;
            }

            if (!SameNumber(previous.ToCabinetEdgeDistance, current.ToCabinetEdgeDistance)
                || !SameNumber(previous.BusbarCenterToCabinetEdgeDistance, current.BusbarCenterToCabinetEdgeDistance))
            {
                changeSet.CabinetHoles.Add(positionName);
            }

            var l1OrL2Changed = !SameNumber(previous.L1, current.L1)
                || !SameNumber(previous.L2, current.L2);
            var l3Changed = !SameNumber(previous.L3, current.L3);
            var spacingChanged = !SameNumber(previous.FixedHoleSpacing, current.FixedHoleSpacing);
            if (l1OrL2Changed || l3Changed || spacingChanged)
            {
                changeSet.CabinetHoles.Add(positionName);
                changeSet.BasePositions.Add(positionName);
            }

            if (!SameNumber(previous.BaseHeight, current.BaseHeight)
                || !SameNumber(previous.ExtensionBaseHeight, current.ExtensionBaseHeight)
                || !SameNumber(previous.BaseOpeningHeight, current.BaseOpeningHeight)
                || !SameNumber(previous.InsulatorPosition, current.InsulatorPosition))
            {
                changeSet.BasePositions.Add(positionName);
            }
        }

        private static void MarkEnabledInsulatorPositions(
            CabinetDriverParameters parameters,
            CabinetDriverChangeSet changeSet)
        {
            MarkIfEnabled(parameters.ACabinet?.FrontHole, AFront, changeSet);
            MarkIfEnabled(parameters.ACabinet?.BackHole, ABack, changeSet);
            MarkIfEnabled(parameters.BCabinet?.FrontHole, BFront, changeSet);
            MarkIfEnabled(parameters.BCabinet?.BackHole, BBack, changeSet);
        }

        private static void MarkIfEnabled(
            CabinetHoleParameters hole,
            string positionName,
            CabinetDriverChangeSet changeSet)
        {
            if (hole != null && hole.IsEnabled)
            {
                changeSet.InsulatorPositions.Add(positionName);
            }
        }

        private static bool NeedsElevatedBase(double baseHeight)
        {
            return baseHeight > CabinetHoleParameters.ExtensionBaseHeightThreshold;
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
