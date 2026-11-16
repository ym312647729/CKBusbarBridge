using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 将面对墙唯一基座参数桥接到共享基座二次事务使用的参数快照。
    /// 桥接只复用参数载体，不把面对墙的“柜前/柜后”映射伪装成面对面位置。
    /// </summary>
    internal static class FaceToWallSingleBaseParameterAdapter
    {
        internal static CabinetDriverParameters CreateBridgeParameters(
            FaceToWallCabinetDriverParameters source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var frontEnabled = source.Cabinet?.FrontHole?.IsEnabled == true;
            var rearEnabled = source.Cabinet?.BackHole?.IsEnabled == true;
            if (frontEnabled && rearEnabled)
            {
                throw new InvalidOperationException(
                    "面对墙场景只能启用一个基座位置，请保留柜前或柜后其中一个。");
            }

            var target = new CabinetDriverParameters();
            target.ACabinet.FrontHole.IsEnabled = false;
            target.ACabinet.BackHole.IsEnabled = false;
            target.BCabinet.FrontHole.IsEnabled = false;
            target.BCabinet.BackHole.IsEnabled = false;

            target.General.PhaseSpacing = source.General.ExposedBusbarPhaseSpacing;
            target.General.InsulationDistance = source.General.InsulationDistance;
            target.General.BusbarSpec = source.General.BusbarSpec;
            target.General.SinglePhaseRootCount = source.General.SinglePhaseRootCount;
            target.General.InsulatorHeight = source.General.InsulatorHeight;

            target.ACabinet.CabinetWidth = source.Cabinet.CabinetWidth;

            CopyHole(source.Cabinet.FrontHole, target.ACabinet.FrontHole);
            CopyHole(source.Cabinet.BackHole, target.ACabinet.BackHole);
            return target;
        }

        /// <summary>
        /// 共享规划器先计算基座尺寸与绝缘子计划，再把桥接位置还原成面对墙映射键和安装坐标系。
        /// </summary>
        internal static IReadOnlyList<FaceToFaceBasePositionPlan> CreatePlans(
            CabinetDriverParameterSnapshot bridgeSnapshot)
        {
            if (bridgeSnapshot == null)
            {
                throw new ArgumentNullException(nameof(bridgeSnapshot));
            }

            // 面对墙没有换相场景。桥接快照中的 A/B 默认相序即使相同，
            // 也只按不换相开孔高度下限校验。
            var plans = FaceToFaceBaseDrivePlanner
                .CreatePlansWithoutPhaseChangeOpeningHeightValidation(bridgeSnapshot)
                .Where(plan => string.Equals(
                        plan.PositionName,
                        CabinetDriverChangeAnalyzer.AFront,
                        StringComparison.Ordinal)
                    || string.Equals(
                        plan.PositionName,
                        CabinetDriverChangeAnalyzer.ABack,
                        StringComparison.Ordinal))
                .Select(ConvertPosition)
                .ToArray();
            if (plans.Length > 1)
            {
                throw new InvalidOperationException(
                    "面对墙场景只能启用一个基座位置，请重新检查柜前/柜后参数。");
            }

            return Array.AsReadOnly(plans);
        }

        private static FaceToFaceBasePositionPlan ConvertPosition(
            FaceToFaceBasePositionPlan source)
        {
            var isFront = string.Equals(
                source.PositionName,
                CabinetDriverChangeAnalyzer.AFront,
                StringComparison.Ordinal);
            return new FaceToFaceBasePositionPlan(
                isFront
                    ? FaceToWallBaseDrivePlanner.FrontPositionName
                    : FaceToWallBaseDrivePlanner.RearPositionName,
                isFront
                    ? FaceToWallBaseDrivePlanner.FrontRootCoordinateName
                    : FaceToWallBaseDrivePlanner.RearRootCoordinateName,
                ApplyFaceToWallTemplateDefault(source.BaseModel),
                ApplyFaceToWallTemplateDefault(source.ElevatedBaseModel),
                source.Insulator);
        }

        /// <summary>
        /// 面对墙模板的基座开孔高和默认增高高度与面对面模板不同，
        /// 名称归一必须按面对墙模板实际默认值判断，才能正确加/去图号后缀。
        /// </summary>
        private static FaceToFaceBaseModelPlan ApplyFaceToWallTemplateDefault(
            FaceToFaceBaseModelPlan source)
        {
            if (source == null)
            {
                return null;
            }

            var isDefault = source.Kind == FaceToFaceBaseModelKind.Base
                ? Same(source.Height, 1000)
                    && Same(source.Width, 800)
                    && Same(source.Depth, 500)
                    && Same(source.L1, 100)
                    && Same(source.L2, 250)
                    && Same(source.L3, 350)
                    && HasGlobalValue(
                        source,
                        CabinetHoleParameters.BaseOpeningHeightVariableName,
                        830)
                : Same(source.Height, 500)
                    && Same(source.Width, 800)
                    && Same(source.Depth, 500)
                    && Same(source.L1, 100)
                    && Same(source.L2, 250)
                    && Same(source.L3, 350);
            return new FaceToFaceBaseModelPlan(
                source.Kind,
                source.Height,
                source.Width,
                source.Depth,
                source.L1,
                source.L2,
                source.L3,
                isDefault,
                source.CacheKey,
                source.GlobalVariables,
                source.HasElevatedBase);
        }

        private static bool HasGlobalValue(
            FaceToFaceBaseModelPlan model,
            string variableName,
            double expected)
        {
            object value;
            return model.GlobalVariables.TryGetValue(variableName, out value)
                && Same(Convert.ToDouble(value), expected);
        }

        private static bool Same(double left, double right)
        {
            return Math.Round(left, 3, MidpointRounding.AwayFromZero)
                == Math.Round(right, 3, MidpointRounding.AwayFromZero);
        }

        private static void CopyHole(
            FaceToWallCabinetHoleParameters source,
            CabinetHoleParameters target)
        {
            if (source == null || target == null)
            {
                return;
            }

            target.IsEnabled = source.IsEnabled;
            target.ToCabinetEdgeDistance = source.BaseToCabinetEdgeDistance;
            target.BusbarCenterToCabinetEdgeDistance =
                source.BusbarCenterToCabinetEdgeDistance;
            target.FixedHoleSpacing = source.FixedHoleSpacing;
            target.L1 = source.L1;
            target.L2 = source.L2;
            target.L3 = source.L3;
            target.BaseHeight = source.BaseHeight;
            target.ExtensionBaseHeight = source.ExtensionBaseHeight;
            target.BaseOpeningHeight = source.BaseOpeningHeight;
            target.InsulatorPosition = source.InsulatorPosition;
        }
    }
}
