using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace CKBusbarBridge.Models
{
    internal sealed class BaseModelIdentity : IEquatable<BaseModelIdentity>
    {
        private BaseModelIdentity(
            string scenarioKey,
            string templateKey,
            string value,
            string baseValue,
            bool isTemplateDefault)
        {
            ScenarioKey = RequireText(scenarioKey, nameof(scenarioKey));
            TemplateKey = RequireText(templateKey, nameof(templateKey));
            Value = RequireText(value, nameof(value));
            BaseValue = RequireText(baseValue, nameof(baseValue));
            IsTemplateDefault = isTemplateDefault;
            ComparisonKey = Encode(ScenarioKey)
                + Encode(TemplateKey)
                + Encode(Value);
        }

        internal string ScenarioKey { get; }

        internal string TemplateKey { get; }

        internal string Value { get; }

        internal string BaseValue { get; }

        internal bool IsTemplateDefault { get; }

        internal string ComparisonKey { get; }

        internal static BaseModelIdentity Create(
            string scenarioKey,
            string templateKey,
            IEnumerable<KeyValuePair<string, object>> baseState,
            IEnumerable<KeyValuePair<string, object>> hostedState,
            bool isTemplateDefault)
        {
            var normalizedBase = NormalizeState(baseState);
            var normalizedHosted = NormalizeState(hostedState);
            return new BaseModelIdentity(
                scenarioKey,
                templateKey,
                normalizedBase + "|Hosted=" + normalizedHosted,
                normalizedBase,
                isTemplateDefault);
        }

        internal static BaseModelIdentity FromCanonical(
            string scenarioKey,
            string templateKey,
            string value,
            string baseValue,
            bool isTemplateDefault)
        {
            return new BaseModelIdentity(
                scenarioKey,
                templateKey,
                value,
                baseValue,
                isTemplateDefault);
        }

        public bool Equals(BaseModelIdentity other)
        {
            return !ReferenceEquals(other, null)
                && StringComparer.Ordinal.Equals(
                    ComparisonKey,
                    other.ComparisonKey);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as BaseModelIdentity);
        }

        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(ComparisonKey);
        }

        private static string NormalizeState(
            IEnumerable<KeyValuePair<string, object>> state)
        {
            var normalized = (state ?? Enumerable.Empty<KeyValuePair<string, object>>())
                .Select(pair => new KeyValuePair<string, string>(
                    RequireText(pair.Key, "state key"),
                    NormalizeValue(pair.Value)))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToArray();

            for (var index = 1; index < normalized.Length; index++)
            {
                if (StringComparer.Ordinal.Equals(
                        normalized[index - 1].Key,
                        normalized[index].Key))
                {
                    throw new ArgumentException(
                        "模型身份状态包含重复键：" + normalized[index].Key,
                        nameof(state));
                }
            }

            return string.Concat(normalized.Select(pair =>
                Encode(pair.Key) + Encode(pair.Value)));
        }

        private static string NormalizeValue(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (value is double doubleValue)
            {
                return Math.Round(
                        doubleValue,
                        3,
                        MidpointRounding.AwayFromZero)
                    .ToString("0.000", CultureInfo.InvariantCulture);
            }

            if (value is float floatValue)
            {
                return Math.Round(
                        floatValue,
                        3,
                        MidpointRounding.AwayFromZero)
                    .ToString("0.000", CultureInfo.InvariantCulture);
            }

            if (value is decimal decimalValue)
            {
                return decimal.Round(
                        decimalValue,
                        3,
                        MidpointRounding.AwayFromZero)
                    .ToString("0.000", CultureInfo.InvariantCulture);
            }

            if (value is bool boolValue)
            {
                return boolValue.ToString(CultureInfo.InvariantCulture);
            }

            if (value is byte
                || value is sbyte
                || value is short
                || value is ushort
                || value is int
                || value is uint
                || value is long
                || value is ulong)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            if (value is IFormattable formattable)
            {
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            }

            return value.ToString();
        }

        private static string Encode(string value)
        {
            var text = value ?? string.Empty;
            return text.Length.ToString(CultureInfo.InvariantCulture)
                + ":"
                + text;
        }

        private static string RequireText(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("值不能为空。", parameterName);
            }

            return value;
        }
    }

    internal sealed class BaseModelTarget
    {
        internal BaseModelTarget(
            string positionName,
            string modelRoleKey,
            BaseModelIdentity identity,
            object context)
        {
            PositionName = RequireText(positionName, nameof(positionName));
            ModelRoleKey = RequireText(modelRoleKey, nameof(modelRoleKey));
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            Context = context;
        }

        internal string PositionName { get; }

        internal string ModelRoleKey { get; }

        internal BaseModelIdentity Identity { get; }

        internal object Context { get; }

        internal string PositionRoleKey => PositionName + "|" + ModelRoleKey;

        private static string RequireText(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("值不能为空。", parameterName);
            }

            return value;
        }
    }

    internal sealed class BaseModelFirstGenerationGroup
    {
        internal BaseModelFirstGenerationGroup(
            string targetKey,
            BaseModelIdentity identity,
            BaseModelTarget representative,
            IEnumerable<BaseModelTarget> targets)
        {
            TargetKey = string.IsNullOrWhiteSpace(targetKey)
                ? throw new ArgumentException("目标模型键不能为空。", nameof(targetKey))
                : targetKey;
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            Representative = representative
                ?? throw new ArgumentNullException(nameof(representative));
            Targets = Array.AsReadOnly((targets
                ?? throw new ArgumentNullException(nameof(targets))).ToArray());
            if (Targets.Count == 0)
            {
                throw new ArgumentException("首次生成分组不能为空。", nameof(targets));
            }
        }

        internal string TargetKey { get; }

        internal BaseModelIdentity Identity { get; }

        internal BaseModelTarget Representative { get; }

        internal IReadOnlyList<BaseModelTarget> Targets { get; }
    }

    internal sealed class BaseModelFirstGenerationPlan
    {
        private readonly IReadOnlyDictionary<string, string> _targetKeys;

        internal BaseModelFirstGenerationPlan(
            IEnumerable<BaseModelFirstGenerationGroup> groups,
            IDictionary<string, string> targetKeys)
        {
            Groups = Array.AsReadOnly((groups
                ?? throw new ArgumentNullException(nameof(groups))).ToArray());
            _targetKeys = new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(
                    targetKeys ?? throw new ArgumentNullException(nameof(targetKeys)),
                    StringComparer.Ordinal));
        }

        internal IReadOnlyList<BaseModelFirstGenerationGroup> Groups { get; }

        internal string ResolveTargetKey(string positionName, string modelRoleKey)
        {
            if (string.IsNullOrWhiteSpace(positionName))
            {
                throw new ArgumentException("位置名称不能为空。", nameof(positionName));
            }

            if (string.IsNullOrWhiteSpace(modelRoleKey))
            {
                throw new ArgumentException("模型角色不能为空。", nameof(modelRoleKey));
            }

            var positionRoleKey = positionName + "|" + modelRoleKey;
            if (!_targetKeys.TryGetValue(positionRoleKey, out var targetKey))
            {
                throw new KeyNotFoundException(
                    "首次生成计划中不存在目标：" + positionRoleKey);
            }

            return targetKey;
        }
    }
}
