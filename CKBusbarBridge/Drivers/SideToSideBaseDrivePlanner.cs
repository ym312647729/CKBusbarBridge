using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 侧对侧基座首次生成规划器。这里只计算和校验，不访问文件系统或 SolidWorks。
    /// </summary>
    internal static class SideToSideBaseDrivePlanner
    {
        internal const double MinimumElevatedBaseHeight = 250d;

        private const string InsulatorChildCoordinateName = "安装坐标系";
        private const string ElevatedInsulatorChildCoordinateName = "安装坐标系";

        internal static IReadOnlyList<SideToSideBasePositionPlan> CreatePlans(
            SideToSideCabinetDriverParameters parameters)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            if (parameters.General == null)
            {
                throw new ArgumentException("侧对侧整体参数不能为空。", nameof(parameters));
            }

            ValidateInsulatorSpecification(parameters.General);
            var mode = parameters.General.BaseKind;
            ValidateBaseOpeningHeights(parameters, mode);
            var plans = new List<SideToSideBasePositionPlan>();
            AddIfEnabled(plans, "A柜前", true, parameters.ACabinet?.FrontHole, parameters.ACabinet?.CabinetWidth ?? 0, mode, parameters.General);
            AddIfEnabled(plans, "A柜后", true, parameters.ACabinet?.BackHole, parameters.ACabinet?.CabinetWidth ?? 0, mode, parameters.General);
            AddIfEnabled(plans, "B柜前", false, parameters.BCabinet?.FrontHole, parameters.BCabinet?.CabinetWidth ?? 0, mode, parameters.General);
            AddIfEnabled(plans, "B柜后", false, parameters.BCabinet?.BackHole, parameters.BCabinet?.CabinetWidth ?? 0, mode, parameters.General);
            return plans.ToArray();
        }

        private static void ValidateBaseOpeningHeights(
            SideToSideCabinetDriverParameters parameters,
            SideToSideCabinetBaseKind mode)
        {
            var general = parameters.General;
            // 偏移换相由基座2内铜排完成，桥体只执行普通排布高度校验。
            // 只有无偏移换相（基座1）使用桥体换相高度下限。
            var isBridgePhaseChange = mode == SideToSideCabinetBaseKind.Base1 && !string.Equals(
                general.ACabinetExposedBusbarPhaseSequence,
                general.BCabinetExposedBusbarPhaseSequence,
                StringComparison.Ordinal);

            var busbarThickness =
                FaceToFaceBusbarCenterDistanceHelper.ResolveBusbarThickness(
                    general.BusbarSpec);
            ValidateOpeningHeight("A柜前", parameters.ACabinet?.FrontHole, mode, isBridgePhaseChange,
                general.InsulatorHeight, busbarThickness, general.SinglePhaseRootCount, general.InsulationDistance);
            ValidateOpeningHeight("A柜后", parameters.ACabinet?.BackHole, mode, isBridgePhaseChange,
                general.InsulatorHeight, busbarThickness, general.SinglePhaseRootCount, general.InsulationDistance);
            ValidateOpeningHeight("B柜前", parameters.BCabinet?.FrontHole, mode, isBridgePhaseChange,
                general.InsulatorHeight, busbarThickness, general.SinglePhaseRootCount, general.InsulationDistance);
            ValidateOpeningHeight("B柜后", parameters.BCabinet?.BackHole, mode, isBridgePhaseChange,
                general.InsulatorHeight, busbarThickness, general.SinglePhaseRootCount, general.InsulationDistance);
        }

        private static void ValidateOpeningHeight(
            string positionName,
            SideToSideCabinetHoleParameters hole,
            SideToSideCabinetBaseKind mode,
            bool isBridgePhaseChange,
            double insulatorHeight,
            double busbarThickness,
            int singlePhaseRootCount,
            double insulationDistance)
        {
            if (hole == null || !hole.IsEnabled)
            {
                return;
            }

            var openingHeight = mode == SideToSideCabinetBaseKind.Base1
                ? hole.Base1?.BaseOpeningHeight
                : hole.Base2?.BaseOpeningHeight;
            if (!openingHeight.HasValue)
            {
                throw new ArgumentException(positionName + "缺少当前基架高度参数。");
            }

            string validationMessage;
            bool isValid;
            if (isBridgePhaseChange)
            {
                isValid = PhaseChangeBaseOpeningHeightRules.TryValidate(
                    openingHeight.Value, insulatorHeight, busbarThickness,
                    singlePhaseRootCount, insulationDistance, out validationMessage);
            }
            else
            {
                isValid = NoPhaseChangeBaseOpeningHeightRules.TryValidate(
                    openingHeight.Value, insulatorHeight, busbarThickness,
                    singlePhaseRootCount, insulationDistance, out validationMessage);
            }
            if (!isValid)
            {
                throw new ArgumentOutOfRangeException(
                    CabinetHoleParameters.BaseOpeningHeightDisplayName,
                    openingHeight.Value,
                    positionName + validationMessage);
            }
        }

        private static void AddIfEnabled(
            ICollection<SideToSideBasePositionPlan> plans,
            string positionName,
            bool isACabinet,
            SideToSideCabinetHoleParameters hole,
            double cabinetWidth,
            SideToSideCabinetBaseKind mode,
            SideToSideGeneralParameters general)
        {
            if (hole == null || !hole.IsEnabled)
            {
                return;
            }

            ValidatePositiveFinite(positionName, "柜体", "柜宽", cabinetWidth);
            plans.Add(mode == SideToSideCabinetBaseKind.Base1
                ? CreateBase1Plan(positionName, isACabinet, hole, cabinetWidth, general)
                : CreateBase2Plan(positionName, isACabinet, hole, hole.Base2, cabinetWidth, general));
        }

        private static SideToSideBasePositionPlan CreateBase1Plan(
            string positionName,
            bool isACabinet,
            SideToSideCabinetHoleParameters hole,
            double cabinetWidth,
            SideToSideGeneralParameters general)
        {
            var values = hole?.Base1;
            if (values == null)
            {
                throw new ArgumentException(positionName + "的基座1参数不能为空。");
            }

            // 两排安装槽中心距为基座宽减43；深度沿柜宽方向。
            ValidatePositiveFinite(positionName, "柜顶孔", "接口固定孔间距", hole.FixedHoleSpacing);
            var baseWidth = hole.FixedHoleSpacing + 43d;
            var baseDepth = cabinetWidth;
            ValidateBaseVariables(
                positionName,
                "基座1",
                values.BaseHeight,
                baseWidth,
                baseDepth,
                hole.L1,
                hole.L2,
                hole.L3,
                values.BaseOpeningHeight);
            ValidatePosition(positionName, "基座1", "绝缘子定位1", values.InsulatorPosition1);
            ValidatePosition(positionName, "基座1", "绝缘子定位2", values.InsulatorPosition2);
            ValidatePosition(positionName, "基座1", "绝缘子定位3", values.InsulatorPosition3);
            if (values.InsulatorPosition1 >= values.InsulatorPosition2
                || values.InsulatorPosition2 >= values.InsulatorPosition3)
                throw new ArgumentException(positionName + "的绝缘子定位1、2、3必须由低到高严格递增。");
            var layout = UniversalInsulatorLayout.FromSideToSideBase1(
                baseDepth - 20d, general.ExposedBusbarPhaseSpacing);

            var model = new SideToSideBaseModelPlan(
                SideToSideBaseModelKind.Base1,
                CreateBaseVariables(
                    values.BaseHeight,
                    baseWidth,
                    baseDepth,
                    hole.L1,
                    hole.L2,
                    hole.L3,
                    values.BaseOpeningHeight),
                IsBaseTemplateDefault(
                    values.BaseHeight,
                    baseWidth,
                    baseDepth,
                    hole.L1,
                    hole.L2,
                    hole.L3,
                    values.BaseOpeningHeight));
            var cabinetSuffix = isACabinet ? "A" : "B";
            var placements = new[]
            {
                CreateBase1Insulator(
                    values.InsulatorPosition1,
                    1,
                    cabinetSuffix,
                    baseDepth,
                    general, layout),
                CreateBase1Insulator(
                    values.InsulatorPosition2,
                    2,
                    cabinetSuffix,
                    baseDepth,
                    general, layout),
                CreateBase1Insulator(
                    values.InsulatorPosition3,
                    3,
                    cabinetSuffix,
                    baseDepth,
                    general, layout)
            };

            return new SideToSideBasePositionPlan(
                positionName,
                isACabinet,
                model,
                null,
                BridgeModelReadOperations.ResolveBase1Route(positionName).ChildCoordinateName,
                placements);
        }

        private static SideToSideBasePositionPlan CreateBase2Plan(
            string positionName,
            bool isACabinet,
            SideToSideCabinetHoleParameters hole,
            SideToSideBase2Parameters values,
            double cabinetWidth,
            SideToSideGeneralParameters general)
        {
            if (values == null)
            {
                throw new ArgumentException(positionName + "的基座2参数不能为空。");
            }

            if (hole == null)
            {
                throw new ArgumentException(positionName + "的柜顶孔参数不能为空。");
            }

            // 基座2与增高基座正交安装，这两个外形尺寸都沿所属柜体宽度方向。
            var baseDepth = cabinetWidth;
            var elevatedWidth = cabinetWidth;
            var elevatedDepth = hole.FixedHoleSpacing
                + SideToSideBase2Rules.ElevatedDepthAllowance;
            ValidateBaseVariables(
                positionName,
                "基座2",
                values.BaseHeight,
                values.BaseWidth,
                baseDepth,
                hole.L1,
                hole.L2,
                hole.L3,
                values.BaseOpeningHeight);
            ValidatePositiveFinite(positionName, "增高基座", "增高基座深", elevatedDepth);
            if (!SideToSideBase2Rules.TryCalculateMinimumWidth(
                    general.ExposedBusbarPhaseSpacing,
                    general.BusbarSpec,
                    general.InsulationDistance,
                    out var minimumBaseWidth))
            {
                throw new ArgumentException(
                    positionName + "无法从铜排规格解析排宽，不能校验基座2宽。");
            }

            if (values.BaseWidth < minimumBaseWidth)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(values.BaseWidth),
                    values.BaseWidth,
                    positionName
                        + "的基座2宽不能小于"
                        + Normalize(minimumBaseWidth)
                        + "（相间距×2+排宽+2×(19.5+绝缘距离+5)）。");
            }

            ValidatePositiveFinite(positionName, "增高基座", "增高基座高度", values.ElevatedBaseHeight);
            if (values.ElevatedBaseHeight < MinimumElevatedBaseHeight)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(values.ElevatedBaseHeight),
                    values.ElevatedBaseHeight,
                    positionName + "的增高基座高度不能小于250。模型种类：基座2。");
            }

            if (values.BaseHeight <= values.ElevatedBaseHeight)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(values.BaseHeight),
                    values.BaseHeight,
                    positionName + "的组合基座高必须大于增高基座高度。模型种类：基座2。");
            }

            ValidatePosition(positionName, "增高基座", "绝缘子定位", values.InsulatorPosition);
            if (values.InsulatorPosition > values.ElevatedBaseHeight)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(values.InsulatorPosition),
                    values.InsulatorPosition,
                    positionName
                        + "的绝缘子定位不能大于增高基座高"
                        + Normalize(values.ElevatedBaseHeight)
                        + "。模型种类：增高基座。");
            }

            // 基座2模板只接收本体高度，界面值减去增高基座高度后再写入。
            var baseBodyHeight = values.BaseHeight - values.ElevatedBaseHeight;
            var baffleEdgeDistance = (values.BaseWidth - elevatedDepth) / 2d;
            var baseModel = new SideToSideBaseModelPlan(
                SideToSideBaseModelKind.Base2,
                CreateBase2Variables(
                    baseBodyHeight,
                    values.BaseWidth,
                    baseDepth,
                    hole.L1,
                    hole.L2,
                    hole.L3,
                    values.BaseOpeningHeight,
                    baffleEdgeDistance),
                IsBase2TemplateDefault(
                    baseBodyHeight,
                    values.BaseWidth,
                    baseDepth,
                    hole.L1,
                    hole.L2,
                    hole.L3,
                    values.BaseOpeningHeight,
                    baffleEdgeDistance));
            var elevatedModel = new SideToSideBaseModelPlan(
                SideToSideBaseModelKind.ElevatedBase,
                CreateElevatedVariables(
                    values.ElevatedBaseHeight,
                    elevatedWidth,
                    elevatedDepth,
                    hole.L1,
                    hole.L2,
                    hole.L3),
                IsElevatedTemplateDefault(
                    values.ElevatedBaseHeight,
                    elevatedWidth,
                    elevatedDepth,
                    hole.L1,
                    hole.L2,
                    hole.L3));
            var placement = new SideToSideInsulatorPlacementPlan(
                SideToSideBaseModelKind.ElevatedBase,
                "绝缘子安装坐标系",
                ElevatedInsulatorChildCoordinateName,
                "D1@草图2",
                values.InsulatorPosition,
                general.BusbarSpec,
                general.ExposedBusbarPhaseSpacing,
                general.InsulatorHeight,
                elevatedWidth,
                general.SinglePhaseRootCount);

            return new SideToSideBasePositionPlan(
                positionName,
                isACabinet,
                baseModel,
                elevatedModel,
                isACabinet ? "安装坐标系(A)" : "安装坐标系(B)",
                new[] { placement });
        }

        private static SideToSideInsulatorPlacementPlan CreateBase1Insulator(
            double position,
            int index,
            string cabinetSuffix,
            double bracketHostSpan,
            SideToSideGeneralParameters general,
            UniversalInsulatorLayout layout)
        {
            return new SideToSideInsulatorPlacementPlan(
                SideToSideBaseModelKind.Base1,
                "绝缘子安装坐标系" + index + "(" + cabinetSuffix + ")",
                InsulatorChildCoordinateName,
                // A/B柜分别使用模板中的草图A/草图B，
                // D1/D2/D3依次控制安装坐标系1/2/3。
                "D" + index + "@绝缘子安装坐标系草图" + cabinetSuffix,
                position,
                general.BusbarSpec,
                general.ExposedBusbarPhaseSpacing,
                general.InsulatorHeight,
                bracketHostSpan,
                general.SinglePhaseRootCount,
                layout);
        }

        private static IReadOnlyDictionary<string, double> CreateBaseVariables(
            double height,
            double width,
            double depth,
            double l1,
            double l2,
            double l3,
            double openingHeight)
        {
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["基座高"] = Normalize(height),
                ["基座宽"] = Normalize(width),
                ["基座深"] = Normalize(depth),
                ["L1"] = Normalize(l1),
                ["L2"] = Normalize(l2),
                ["L3"] = Normalize(l3),
                ["基座开孔高度"] = Normalize(openingHeight)
            };
        }

        private static IReadOnlyDictionary<string, double> CreateBase2Variables(
            double height,
            double width,
            double depth,
            double l1,
            double l2,
            double l3,
            double openingHeight,
            double baffleEdgeDistance)
        {
            var variables = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in CreateBaseVariables(height, width, depth, l1, l2, l3, openingHeight))
            {
                variables[pair.Key] = pair.Value;
            }

            variables["挡板距边距离"] = Normalize(baffleEdgeDistance);
            return variables;
        }

        private static IReadOnlyDictionary<string, double> CreateElevatedVariables(
            double height,
            double width,
            double depth,
            double l1,
            double l2,
            double l3)
        {
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["高度"] = Normalize(height),
                ["宽度"] = Normalize(width),
                ["深度"] = Normalize(depth),
                ["L1"] = Normalize(l1),
                ["L2"] = Normalize(l2),
                ["L3"] = Normalize(l3)
            };
        }

        private static bool IsBaseTemplateDefault(
            double height,
            double width,
            double depth,
            double l1,
            double l2,
            double l3,
            double openingHeight)
        {
            return Normalize(height) == 1300d
                && Normalize(width) == 800d
                && Normalize(depth) == 500d
                && Normalize(l1) == 80d
                && Normalize(l2) == 100d
                && Normalize(l3) == 180d
                && Normalize(openingHeight) == 800d;
        }

        private static bool IsBase2TemplateDefault(
            double height,
            double width,
            double depth,
            double l1,
            double l2,
            double l3,
            double openingHeight,
            double baffleEdgeDistance)
        {
            return Normalize(height) == 1000d
                && Normalize(width) == 800d
                && Normalize(depth) == 800d
                && Normalize(l1) == 100d
                && Normalize(l2) == 250d
                && Normalize(l3) == 350d
                && Normalize(openingHeight) == 500d
                && Normalize(baffleEdgeDistance) == 150d;
        }

        private static bool IsElevatedTemplateDefault(
            double height,
            double width,
            double depth,
            double l1,
            double l2,
            double l3)
        {
            return Normalize(height) == 400d
                && Normalize(width) == 800d
                && Normalize(depth) == 500d
                && Normalize(l1) == 100d
                && Normalize(l2) == 250d
                && Normalize(l3) == 300d;
        }

        private static void ValidateBaseVariables(
            string positionName,
            string modelKind,
            double height,
            double width,
            double depth,
            double l1,
            double l2,
            double l3,
            double openingHeight)
        {
            ValidatePositiveFinite(positionName, modelKind, "基座高", height);
            ValidatePositiveFinite(positionName, modelKind, "基座宽", width);
            ValidatePositiveFinite(positionName, modelKind, "基座深", depth);
            ValidatePositiveFinite(positionName, modelKind, "L1", l1);
            ValidatePositiveFinite(positionName, modelKind, "L2", l2);
            ValidatePositiveFinite(positionName, modelKind, "L3", l3);
            ValidatePositiveFinite(positionName, modelKind, "基座开孔高度", openingHeight);
        }

        private static void ValidateInsulatorSpecification(SideToSideGeneralParameters general)
        {
            if (string.IsNullOrWhiteSpace(general.BusbarSpec))
            {
                throw new ArgumentException("绝缘子驱动所需的铜排规格不能为空。");
            }

            ValidatePositiveFinite("整体参数", "绝缘子", "伸出排相间距", general.ExposedBusbarPhaseSpacing);
            ValidatePositiveFinite(
                "整体参数",
                "绝缘子",
                "绝缘子+支架高度",
                general.InsulatorHeight);
            InsulatorHeightRules.ToBodyHeight(general.InsulatorHeight);
            if (general.SinglePhaseRootCount < 1 || general.SinglePhaseRootCount > 4)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(general.SinglePhaseRootCount),
                    general.SinglePhaseRootCount,
                    "单相根数必须在1到4之间。模型种类：绝缘子。");
            }
        }

        private static void ValidatePositiveFinite(
            string positionName,
            string modelKind,
            string parameterName,
            double value)
        {
            if (value > 0d && !double.IsNaN(value) && !double.IsInfinity(value))
            {
                return;
            }

            var displayName = CabinetHoleParameters.GetParameterDisplayName(parameterName);
            throw new ArgumentOutOfRangeException(
                displayName,
                value,
                positionName + "的" + displayName + "必须为有限正数。模型种类：" + modelKind + "。");
        }

        private static void ValidatePosition(
            string positionName,
            string modelKind,
            string parameterName,
            double value)
        {
            if (value >= 0d && !double.IsNaN(value) && !double.IsInfinity(value))
            {
                return;
            }

            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                positionName + "的" + parameterName + "必须为有限非负数。模型种类：" + modelKind + "。");
        }

        private static double Normalize(double value)
        {
            return Math.Round(value, 3, MidpointRounding.AwayFromZero);
        }
    }
}
