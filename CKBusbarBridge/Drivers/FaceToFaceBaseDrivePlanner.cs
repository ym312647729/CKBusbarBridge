using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 纯计算独立基座计划。面对面和面对墙共用公式，不打开模型、不访问文件系统或 SolidWorks。
    /// </summary>
    internal static class FaceToFaceBaseDrivePlanner
    {
        private static readonly double[] BaseTemplateDefaults =
        {
            1000, 800, 500, 100, 250, 350, 500
        };

        private static readonly double[] ElevatedBaseTemplateDefaults =
        {
            400, 800, 500, 100, 250, 350
        };

        /// <summary>
        /// 只返回已启用柜孔，顺序固定为 A柜前、A柜后、B柜前、B柜后。
        /// 面对面总装根坐标系名与位置名相同。
        /// </summary>
        internal static IReadOnlyList<FaceToFaceBasePositionPlan> CreatePlans(
            CabinetDriverParameters parameters)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            var insulatorInput = StandaloneBaseInsulatorInput.From(parameters.General);
            var candidates = new[]
                {
                    new PositionCandidate("A柜前", "A柜前", StandaloneBaseHoleInput.From(parameters.ACabinet?.FrontHole, parameters.ACabinet?.CabinetWidth ?? 0)),
                    new PositionCandidate("A柜后", "A柜后", StandaloneBaseHoleInput.From(parameters.ACabinet?.BackHole, parameters.ACabinet?.CabinetWidth ?? 0)),
                    new PositionCandidate("B柜前", "B柜前", StandaloneBaseHoleInput.From(parameters.BCabinet?.FrontHole, parameters.BCabinet?.CabinetWidth ?? 0)),
                    new PositionCandidate("B柜后", "B柜后", StandaloneBaseHoleInput.From(parameters.BCabinet?.BackHole, parameters.BCabinet?.CabinetWidth ?? 0))
                };
            ValidateBaseOpeningHeights(
                parameters.General?.ACabinetExposedBusbarPhaseSequence,
                parameters.General?.BCabinetExposedBusbarPhaseSequence,
                insulatorInput,
                parameters.General.InsulationDistance,
                candidates,
                validatePhaseChangeOpeningHeight: true);
            return CreatePlans(insulatorInput, candidates);
        }

        /// <summary>
        /// 从首次生成或反读时保存的参数快照重新计算基座计划。
        /// 当前参数和快照参数最终进入同一个公式入口，避免二次生成的旧、新身份计算发生漂移。
        /// </summary>
        internal static IReadOnlyList<FaceToFaceBasePositionPlan> CreatePlans(
            CabinetDriverParameterSnapshot snapshot)
        {
            return CreatePlans(snapshot, validatePhaseChangeOpeningHeight: true);
        }

        /// <summary>
        /// 从参数快照计算基座计划，按不换相公式校验开孔高度。
        /// 供不存在换相语义、但复用面对面基座事务的场景使用。
        /// </summary>
        internal static IReadOnlyList<FaceToFaceBasePositionPlan>
            CreatePlansWithoutPhaseChangeOpeningHeightValidation(
                CabinetDriverParameterSnapshot snapshot)
        {
            return CreatePlans(snapshot, validatePhaseChangeOpeningHeight: false);
        }

        private static IReadOnlyList<FaceToFaceBasePositionPlan> CreatePlans(
            CabinetDriverParameterSnapshot snapshot,
            bool validatePhaseChangeOpeningHeight)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var insulatorInput = CreateInsulatorInput(snapshot.General);
            var candidates = new[]
                {
                    new PositionCandidate("A柜前", "A柜前", CreateHoleInput(snapshot.ACabinet?.FrontHole, snapshot.ACabinet?.CabinetWidth ?? 0)),
                    new PositionCandidate("A柜后", "A柜后", CreateHoleInput(snapshot.ACabinet?.BackHole, snapshot.ACabinet?.CabinetWidth ?? 0)),
                    new PositionCandidate("B柜前", "B柜前", CreateHoleInput(snapshot.BCabinet?.FrontHole, snapshot.BCabinet?.CabinetWidth ?? 0)),
                    new PositionCandidate("B柜后", "B柜后", CreateHoleInput(snapshot.BCabinet?.BackHole, snapshot.BCabinet?.CabinetWidth ?? 0))
                };
            ValidateBaseOpeningHeights(
                snapshot.General?.ACabinetExposedBusbarPhaseSequence,
                snapshot.General?.BCabinetExposedBusbarPhaseSequence,
                insulatorInput,
                snapshot.General.InsulationDistance,
                candidates,
                validatePhaseChangeOpeningHeight);

            return CreatePlans(insulatorInput, candidates);
        }

        private static void ValidateBaseOpeningHeights(
            string aCabinetPhaseSequence,
            string bCabinetPhaseSequence,
            StandaloneBaseInsulatorInput insulatorInput,
            double insulationDistance,
            IEnumerable<PositionCandidate> candidates,
            bool validatePhaseChangeOpeningHeight)
        {
            var isPhaseChange =
                validatePhaseChangeOpeningHeight
                && !string.IsNullOrWhiteSpace(aCabinetPhaseSequence)
                && !string.IsNullOrWhiteSpace(bCabinetPhaseSequence)
                && string.Equals(
                    aCabinetPhaseSequence,
                    bCabinetPhaseSequence,
                    StringComparison.Ordinal);
            if (insulatorInput == null)
            {
                return;
            }

            var busbarThickness =
                FaceToFaceBusbarCenterDistanceHelper.ResolveBusbarThickness(
                    insulatorInput.BusbarSpec);
            foreach (var candidate in candidates ?? Array.Empty<PositionCandidate>())
            {
                if (candidate?.Hole == null || !candidate.Hole.IsEnabled)
                {
                    continue;
                }

                string validationMessage;
                var isValid = isPhaseChange
                    ? PhaseChangeBaseOpeningHeightRules.TryValidate(
                        candidate.Hole.BaseOpeningHeight,
                        insulatorInput.InsulatorHeight,
                        busbarThickness,
                        insulatorInput.SinglePhaseRootCount,
                        insulationDistance,
                        out validationMessage)
                    : NoPhaseChangeBaseOpeningHeightRules.TryValidate(
                        candidate.Hole.BaseOpeningHeight,
                        insulatorInput.InsulatorHeight,
                        busbarThickness,
                        insulatorInput.SinglePhaseRootCount,
                        insulationDistance,
                        out validationMessage);
                if (!isValid)
                {
                    throw new ArgumentOutOfRangeException(
                        CabinetHoleParameters.BaseOpeningHeightDisplayName,
                        candidate.Hole.BaseOpeningHeight,
                        candidate.Name + validationMessage);
                }
            }
        }

        private static IReadOnlyList<FaceToFaceBasePositionPlan> CreatePlans(
            StandaloneBaseInsulatorInput insulatorInput,
            IReadOnlyList<PositionCandidate> candidates)
        {
            var plans = new List<FaceToFaceBasePositionPlan>();
            foreach (var candidate in candidates)
            {
                if (candidate.Hole == null || !candidate.Hole.IsEnabled)
                {
                    continue;
                }

                plans.Add(CreatePositionPlan(
                    candidate.Name,
                    candidate.RootCoordinateName,
                    candidate.Hole,
                    insulatorInput));
            }

            return plans;
        }

        private static StandaloneBaseHoleInput CreateHoleInput(
            CabinetHoleParameterSnapshot hole, double cabinetWidth)
        {
            if (hole == null)
            {
                return null;
            }

            return new StandaloneBaseHoleInput
            {
                CabinetWidth = cabinetWidth,
                IsEnabled = hole.IsEnabled,
                BaseHeight = hole.BaseHeight,
                ExtensionBaseHeight = hole.ExtensionBaseHeight,
                FixedHoleSpacing = hole.FixedHoleSpacing,
                L1 = hole.L1,
                L2 = hole.L2,
                L3 = hole.L3,
                BaseOpeningHeight = hole.BaseOpeningHeight,
                InsulatorPosition = hole.InsulatorPosition
            };
        }

        private static StandaloneBaseInsulatorInput CreateInsulatorInput(
            CabinetGeneralParameterSnapshot general)
        {
            if (general == null)
            {
                return null;
            }

            return new StandaloneBaseInsulatorInput
            {
                PhaseSpacing = general.PhaseSpacing,
                InsulatorHeight = general.InsulatorHeight,
                SinglePhaseRootCount = general.SinglePhaseRootCount,
                BusbarSpec = general.BusbarSpec
            };
        }

        /// <summary>
        /// 按共享公式生成单个位置计划。面对面和面对墙都走这里，避免复制增高和绝缘子宿主计算。
        /// </summary>
        internal static FaceToFaceBasePositionPlan CreatePositionPlan(
            string positionName,
            string rootCoordinateName,
            StandaloneBaseHoleInput hole,
            StandaloneBaseInsulatorInput insulatorInput)
        {
            if (hole == null)
            {
                throw new ArgumentNullException(nameof(hole));
            }

            ValidatePositiveFinite(positionName, "基座高", hole.BaseHeight);
            ValidatePositiveFinite(positionName, "接口固定孔间距", hole.FixedHoleSpacing);
            ValidatePositiveFinite(positionName, "L1", hole.L1);
            ValidatePositiveFinite(positionName, "L2", hole.L2);
            ValidatePositiveFinite(positionName, "L3", hole.L3);
            ValidatePositiveFinite(positionName, CabinetHoleParameters.BaseOpeningHeightVariableName, hole.BaseOpeningHeight);
            ValidateNonNegativeFinite(positionName, "绝缘子定位高度", hole.InsulatorPosition);
            if (insulatorInput == null)
            {
                throw new ArgumentNullException(nameof(insulatorInput), $"{positionName}缺少柜体整体参数。");
            }

            ValidatePositiveFinite(positionName, "伸出排相间距", insulatorInput.PhaseSpacing);
            ValidatePositiveFinite(
                positionName,
                "绝缘子+支架高度",
                insulatorInput.InsulatorHeight);
            InsulatorHeightRules.ToBodyHeight(
                insulatorInput.InsulatorHeight);
            if (insulatorInput.SinglePhaseRootCount < 1 || insulatorInput.SinglePhaseRootCount > 4)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(insulatorInput.SinglePhaseRootCount),
                    insulatorInput.SinglePhaseRootCount,
                    $"{positionName}单相根数必须在 1 到 4 之间。");
            }

            var totalBaseHeight = Normalize(hole.BaseHeight);
            var needsElevatedBase = totalBaseHeight
                > CabinetHoleParameters.ExtensionBaseHeightThreshold;
            if (needsElevatedBase)
            {
                ValidatePositiveFinite(positionName, "延长基座高", hole.ExtensionBaseHeight);
            }

            var extensionHeight = needsElevatedBase
                ? Normalize(hole.ExtensionBaseHeight)
                : 0;
            var baseHeight = Normalize(totalBaseHeight - extensionHeight);
            if (baseHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(hole.BaseHeight),
                    baseHeight,
                    $"{positionName}普通基座计算高度必须大于 0。总基座高={totalBaseHeight}，延长基座高={extensionHeight}");
            }

            // 外形宽取本位置所属柜体宽，L3 只控制接口孔位。
            ValidatePositiveFinite(positionName, "所属柜体宽", hole.CabinetWidth);
            var width = Normalize(hole.CabinetWidth);
            var depth = Normalize(hole.FixedHoleSpacing + 41.5);
            ValidatePositiveFinite(positionName, "基座宽", width);
            ValidatePositiveFinite(positionName, "基座深", depth);
            // 支架长度 = 基座宽 − 20，必须保证计算结果为正。
            if (width <= 20)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(hole.CabinetWidth),
                    width,
                    $"{positionName}基座宽必须大于 20，才能计算绝缘子支架长度。");
            }

            var l1 = Normalize(hole.L1);
            var l2 = Normalize(hole.L2);
            var l3 = Normalize(hole.L3);
            var openingHeight = Normalize(hole.BaseOpeningHeight);
            var insulatorPlan = CreateInsulatorPlan(
                positionName,
                needsElevatedBase,
                extensionHeight,
                Normalize(hole.InsulatorPosition),
                width,
                insulatorInput);
            var insulatorCacheQualifier = BuildInsulatorCacheQualifier(insulatorPlan);
            var baseModel = CreateModelPlan(
                FaceToFaceBaseModelKind.Base,
                baseHeight,
                width,
                depth,
                l1,
                l2,
                l3,
                openingHeight,
                needsElevatedBase,
                insulatorPlan.TargetBaseKind == FaceToFaceBaseModelKind.Base
                    ? insulatorCacheQualifier
                    : null);
            var elevatedModel = needsElevatedBase
                ? CreateModelPlan(
                    FaceToFaceBaseModelKind.ElevatedBase,
                    extensionHeight,
                    width,
                    depth,
                    l1,
                    l2,
                    l3,
                    openingHeight,
                    null,
                    insulatorPlan.TargetBaseKind == FaceToFaceBaseModelKind.ElevatedBase
                        ? insulatorCacheQualifier
                        : null)
                : null;

            return new FaceToFaceBasePositionPlan(
                positionName,
                rootCoordinateName,
                baseModel,
                elevatedModel,
                insulatorPlan);
        }

        /// <summary>
        /// 将绝缘子的绝对定位高度转换为目标基座内的局部尺寸。
        /// 有增高基座时禁止定位高度等于增高基座高度，交界处无法唯一确定宿主。
        /// </summary>
        private static FaceToFaceBaseInsulatorPlan CreateInsulatorPlan(
            string positionName,
            bool hasElevatedBase,
            double elevatedBaseHeight,
            double absolutePlacementHeight,
            double baseWidth,
            StandaloneBaseInsulatorInput insulatorInput)
        {
            ValidateInsulatorPositionNotEqualToElevatedHeight(
                positionName,
                hasElevatedBase,
                absolutePlacementHeight,
                elevatedBaseHeight);

            var targetKind = hasElevatedBase && absolutePlacementHeight < elevatedBaseHeight
                ? FaceToFaceBaseModelKind.ElevatedBase
                : FaceToFaceBaseModelKind.Base;
            var placementHeight = targetKind == FaceToFaceBaseModelKind.ElevatedBase
                ? absolutePlacementHeight
                : absolutePlacementHeight - (hasElevatedBase ? elevatedBaseHeight : 0);
            if (placementHeight < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(absolutePlacementHeight),
                    absolutePlacementHeight,
                    "绝缘子在普通基座内的局部定位高度不能小于 0。");
            }

            var busbarThickness = FaceToFaceBusbarCenterDistanceHelper.ResolveBusbarThickness(
                insulatorInput.BusbarSpec);
            var globalVariables = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["排厚"] = Normalize(busbarThickness),
                ["相间距"] = Normalize(insulatorInput.PhaseSpacing),
                ["绝缘子高度"] = Normalize(
                    InsulatorHeightRules.ToBodyHeight(
                        insulatorInput.InsulatorHeight)),
                ["支架长度"] = Normalize(baseWidth - 20)
            };

            return new FaceToFaceBaseInsulatorPlan(
                targetKind,
                targetKind == FaceToFaceBaseModelKind.ElevatedBase
                    ? "D1@草图2"
                    : "D1@绝缘子安装坐标系草图",
                Normalize(placementHeight),
                globalVariables,
                insulatorInput.SinglePhaseRootCount,
                insulatorInput.BusbarSpec);
        }

        private static FaceToFaceBaseModelPlan CreateModelPlan(
            FaceToFaceBaseModelKind kind,
            double height,
            double width,
            double depth,
            double l1,
            double l2,
            double l3,
            double openingHeight,
            bool? hasElevatedBase,
            string cacheQualifier)
        {
            var isOrdinaryBase = kind == FaceToFaceBaseModelKind.Base;
            var values = isOrdinaryBase
                ? new[]
                {
                    Normalize(height),
                    Normalize(width),
                    Normalize(depth),
                    Normalize(l1),
                    Normalize(l2),
                    Normalize(l3),
                    Normalize(openingHeight)
                }
                : new[]
                {
                    Normalize(height),
                    Normalize(width),
                    Normalize(depth),
                    Normalize(l1),
                    Normalize(l2),
                    Normalize(l3)
                };
            var defaults = isOrdinaryBase
                ? BaseTemplateDefaults
                : ElevatedBaseTemplateDefaults;
            var isTemplateDefault = ValuesEqual(values, defaults);
            var globalVariables = new Dictionary<string, object>(
                StringComparer.OrdinalIgnoreCase)
            {
                [isOrdinaryBase ? "基座高" : "高度"] = values[0],
                [isOrdinaryBase ? "基座宽" : "宽度"] = values[1],
                [isOrdinaryBase ? "基座深" : "深度"] = values[2],
                ["L1"] = values[3],
                ["L2"] = values[4],
                ["L3"] = values[5]
            };
            if (isOrdinaryBase)
            {
                globalVariables[CabinetHoleParameters.BaseOpeningHeightVariableName] = values[6];
            }

            return new FaceToFaceBaseModelPlan(
                kind,
                values[0],
                values[1],
                values[2],
                values[3],
                values[4],
                values[5],
                isTemplateDefault,
                BuildCacheKey(kind, values, hasElevatedBase, cacheQualifier),
                globalVariables,
                hasElevatedBase);
        }

        private static string BuildCacheKey(
            FaceToFaceBaseModelKind kind,
            IReadOnlyList<double> values,
            bool? hasElevatedBase,
            string cacheQualifier)
        {
            var statePartCount = hasElevatedBase.HasValue ? 1 : 0;
            var qualifierPartCount = string.IsNullOrWhiteSpace(cacheQualifier) ? 0 : 1;
            var parts = new string[values.Count + 1 + statePartCount + qualifierPartCount];
            parts[0] = kind.ToString();
            for (var index = 0; index < values.Count; index++)
            {
                parts[index + 1] = values[index].ToString(
                    "0.000",
                    CultureInfo.InvariantCulture);
            }

            // 普通基座的侧封板状态参与复用签名，避免两种外观错误共用同一模型。
            if (hasElevatedBase.HasValue)
            {
                parts[values.Count + 1] = "HasElevatedBase=" + hasElevatedBase.Value;
            }

            // 绝缘子会修改目标基座的草图尺寸和引用树，因此必须参与该基座文件的复用签名。
            if (qualifierPartCount > 0)
            {
                parts[parts.Length - 1] = cacheQualifier;
            }

            return string.Join("|", parts);
        }

        private static string BuildInsulatorCacheQualifier(FaceToFaceBaseInsulatorPlan plan)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Insulator={0},{1:0.000},{2:0.000},{3:0.000},{4:0.000},Roots={5}",
                plan.PlacementDimensionName,
                plan.PlacementHeight,
                (double)plan.GlobalVariables["排厚"],
                (double)plan.GlobalVariables["相间距"],
                (double)plan.GlobalVariables["绝缘子高度"],
                plan.SinglePhaseRootCount);
        }

        private static bool ValuesEqual(
            IReadOnlyList<double> left,
            IReadOnlyList<double> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (var index = 0; index < left.Count; index++)
            {
                if (Normalize(left[index]) != Normalize(right[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static double Normalize(double value)
        {
            return Math.Round(value, 3, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// 有增高基座时，绝缘子定位不能落在增高与普通基座的交界面上。
        /// 面对面和面对墙共用此规则。
        /// </summary>
        internal static void ValidateInsulatorPositionNotEqualToElevatedHeight(
            string positionName,
            bool hasElevatedBase,
            double insulatorPosition,
            double elevatedBaseHeight)
        {
            if (!hasElevatedBase)
            {
                return;
            }

            var normalizedPosition = Normalize(insulatorPosition);
            var normalizedElevatedHeight = Normalize(elevatedBaseHeight);
            if (normalizedPosition == normalizedElevatedHeight)
            {
                var prefix = string.IsNullOrWhiteSpace(positionName) ? string.Empty : positionName;
                throw new ArgumentOutOfRangeException(
                    "绝缘子定位高度",
                    insulatorPosition,
                    $"{prefix}存在增高基座时，绝缘子定位高度不能等于增高基座高度 {normalizedElevatedHeight}。");
            }
        }

        private static void ValidatePositiveFinite(
            string positionName,
            string parameterName,
            double value)
        {
            if (double.IsNaN(value)
                || double.IsInfinity(value)
                || value <= 0)
            {
                var displayName = CabinetHoleParameters.GetParameterDisplayName(parameterName);
                throw new ArgumentOutOfRangeException(
                    displayName,
                    value,
                    $"{positionName}{displayName}必须为有限正数。");
            }
        }

        private static void ValidateNonNegativeFinite(
            string positionName,
            string parameterName,
            double value)
        {
            if (double.IsNaN(value)
                || double.IsInfinity(value)
                || value < 0)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    value,
                    $"{positionName}{parameterName}必须为有限非负数。");
            }
        }

        private sealed class PositionCandidate
        {
            internal PositionCandidate(
                string name,
                string rootCoordinateName,
                StandaloneBaseHoleInput hole)
            {
                Name = name;
                RootCoordinateName = rootCoordinateName;
                Hole = hole;
            }

            internal string Name { get; }

            internal string RootCoordinateName { get; }

            internal StandaloneBaseHoleInput Hole { get; }
        }
    }
}
