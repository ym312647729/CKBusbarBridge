using CKBusbarBridge.models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 将逐相 XYZ 差值换算为单相特殊模板草图尺寸的契约。
    /// </summary>
    public interface IFaceToWallSpecialBusbarDimensionCalculator
    {
        bool IsConfigured { get; }

        IReadOnlyDictionary<string, double> Calculate(
            FaceToWallSpecialBusbarPhasePlan plan,
            BusbarGenerationPreparationResult preparationResult);
    }

    /// <summary>
    /// 未配置尺寸公式时使用的安全实现。
    /// </summary>
    public sealed class UnconfiguredFaceToWallSpecialBusbarDimensionCalculator
        : IFaceToWallSpecialBusbarDimensionCalculator
    {
        public bool IsConfigured => false;

        public IReadOnlyDictionary<string, double> Calculate(
            FaceToWallSpecialBusbarPhasePlan plan,
            BusbarGenerationPreparationResult preparationResult)
        {
            throw new InvalidOperationException("特殊铜排模板尺寸计算规则尚未配置。");
        }
    }

    /// <summary>
    /// 按单相特殊模板类型管理尺寸计算器。
    /// </summary>
    public sealed class FaceToWallSpecialBusbarDimensionCalculatorRegistry
    {
        private readonly IReadOnlyDictionary<
            FaceToWallPhaseSpecialTemplateKind,
            IFaceToWallSpecialBusbarDimensionCalculator> calculators;

        public FaceToWallSpecialBusbarDimensionCalculatorRegistry(
            IDictionary<FaceToWallPhaseSpecialTemplateKind, IFaceToWallSpecialBusbarDimensionCalculator> calculators)
        {
            if (calculators == null)
            {
                throw new ArgumentNullException(nameof(calculators));
            }

            this.calculators = new Dictionary<
                FaceToWallPhaseSpecialTemplateKind,
                IFaceToWallSpecialBusbarDimensionCalculator>(calculators);
        }

        public static FaceToWallSpecialBusbarDimensionCalculatorRegistry CreateUnconfigured()
        {
            var unconfigured = new UnconfiguredFaceToWallSpecialBusbarDimensionCalculator();
            return new FaceToWallSpecialBusbarDimensionCalculatorRegistry(
                new Dictionary<FaceToWallPhaseSpecialTemplateKind, IFaceToWallSpecialBusbarDimensionCalculator>
                {
                    { FaceToWallPhaseSpecialTemplateKind.VerticalBend, unconfigured },
                    { FaceToWallPhaseSpecialTemplateKind.MilledBar, unconfigured },
                    { FaceToWallPhaseSpecialTemplateKind.OffsetConnection, unconfigured },
                    { FaceToWallPhaseSpecialTemplateKind.StraightRaiseOnly, unconfigured }
                });
        }

        /// <summary>
        /// 创建已配置立弯、偏移连接排和铣排公式的默认注册表。
        /// </summary>
        public static FaceToWallSpecialBusbarDimensionCalculatorRegistry CreateConfigured()
        {
            return new FaceToWallSpecialBusbarDimensionCalculatorRegistry(
                new Dictionary<FaceToWallPhaseSpecialTemplateKind, IFaceToWallSpecialBusbarDimensionCalculator>
                {
                    {
                        FaceToWallPhaseSpecialTemplateKind.VerticalBend,
                        new VerticalBendFaceToWallSpecialBusbarDimensionCalculator()
                    },
                    {
                        FaceToWallPhaseSpecialTemplateKind.MilledBar,
                        new MilledBarFaceToWallSpecialBusbarDimensionCalculator()
                    },
                    {
                        FaceToWallPhaseSpecialTemplateKind.OffsetConnection,
                        new OffsetConnectionFaceToWallSpecialBusbarDimensionCalculator()
                    },
                    {
                        FaceToWallPhaseSpecialTemplateKind.StraightRaiseOnly,
                        new StraightRaiseOnlyFaceToWallSpecialBusbarDimensionCalculator()
                    }
                });
        }

        public void EnsureConfigured(IEnumerable<FaceToWallSpecialBusbarPhasePlan> plans)
        {
            if (plans == null)
            {
                throw new ArgumentNullException(nameof(plans));
            }

            var missingKinds = plans
                .Where(plan => plan != null)
                .Select(plan => plan.TemplateKind)
                .Distinct()
                .Where(kind => !calculators.TryGetValue(kind, out var calculator)
                    || calculator == null
                    || !calculator.IsConfigured)
                .OrderBy(kind => kind)
                .ToList();
            if (missingKinds.Count > 0)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "特殊铜排模板尺寸计算规则尚未配置：{0}。",
                        string.Join("、", missingKinds.Select(GetTemplateKindDisplayName))));
            }
        }

        public IReadOnlyDictionary<string, double> CalculateRequired(
            FaceToWallSpecialBusbarPhasePlan plan,
            BusbarGenerationPreparationResult preparationResult)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            if (preparationResult == null)
            {
                throw new ArgumentNullException(nameof(preparationResult));
            }

            EnsureConfigured(new[] { plan });
            var sourceDimensions = calculators[plan.TemplateKind].Calculate(plan, preparationResult);
            if (sourceDimensions == null || sourceDimensions.Count == 0)
            {
                throw new InvalidOperationException(
                    string.Format("{0}相特殊铜排尺寸计算结果为空。", plan.Phase));
            }

            var dimensions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in sourceDimensions)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    throw new InvalidOperationException(
                        string.Format("{0}相特殊铜排存在空的草图尺寸名。", plan.Phase));
                }

                if (double.IsNaN(pair.Value) || double.IsInfinity(pair.Value))
                {
                    throw new InvalidOperationException(
                        string.Format("{0}相特殊铜排尺寸“{1}”不是有限数值。", plan.Phase, pair.Key));
                }

                var dimensionName = pair.Key.Trim();
                if (dimensions.ContainsKey(dimensionName))
                {
                    throw new InvalidOperationException(
                        string.Format("{0}相特殊铜排尺寸名重复：{1}。", plan.Phase, pair.Key));
                }

                dimensions.Add(dimensionName, pair.Value);
            }

            return dimensions;
        }

        private static string GetTemplateKindDisplayName(FaceToWallPhaseSpecialTemplateKind kind)
        {
            switch (kind)
            {
                case FaceToWallPhaseSpecialTemplateKind.VerticalBend:
                    return "立弯";
                case FaceToWallPhaseSpecialTemplateKind.MilledBar:
                    return "铣排";
                case FaceToWallPhaseSpecialTemplateKind.OffsetConnection:
                    return "偏移连接排";
                case FaceToWallPhaseSpecialTemplateKind.StraightRaiseOnly:
                    return "直排仅抬高";
                default:
                    return kind.ToString();
            }
        }
    }
}
