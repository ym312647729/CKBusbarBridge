using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace CKBusbarBridge.Drivers
{
    /// <summary>装配体引用配置中原生尺寸方程的目标实例和表达式。</summary>
    internal sealed class ParentDimensionDependency
    {
        [JsonProperty("target", Required = Required.Always)]
        internal string Target { get; }

        [JsonProperty("family", Required = Required.Always)]
        internal string Family { get; }

        [JsonProperty("configuration", Required = Required.Always)]
        internal string Configuration { get; }

        [JsonProperty("dimension", Required = Required.Always)]
        internal string Dimension { get; }

        [JsonProperty("expression", Required = Required.Always)]
        internal string Expression { get; }

        [JsonProperty("inputs", DefaultValueHandling = DefaultValueHandling.Ignore)]
        internal ParentDimensionInput[] Inputs { get; }

        [JsonConstructor]
        internal ParentDimensionDependency(string target, string family, string configuration, string dimension, string expression,
            ParentDimensionInput[] inputs = null)
        {
            if (!ValidAddress(target)
                || string.IsNullOrWhiteSpace(family) || string.IsNullOrWhiteSpace(configuration)
                || string.IsNullOrWhiteSpace(dimension) || dimension.Split('@').Length != 2
                || string.IsNullOrWhiteSpace(expression) || inputs?.Any(input => input == null) == true)
                throw new InvalidOperationException("上级尺寸驱动记录缺少有效的原生实例、引用配置或方程。");

            Target = target;
            Family = family;
            Configuration = configuration;
            Dimension = dimension;
            Expression = expression.Trim();
            Inputs = inputs ?? Array.Empty<ParentDimensionInput>();
        }

        internal string Key => Target + "|" + Dimension;

        internal string SourceExpression => RewriteInputs(input => input.Target + "|" + input.Configuration + "|" + input.Dimension);

        internal string RewriteInputs(Func<ParentDimensionInput, string> value)
        {
            if (Inputs.Length == 0) return Expression;
            var byToken = Inputs.ToDictionary(input => input.Token, StringComparer.Ordinal);
            return Regex.Replace(Expression, "\"([^\"]+)\"", match => byToken.TryGetValue(match.Groups[1].Value, out var input)
                ? "\"" + value(input) + "\"" : match.Value);
        }

        internal bool TryRewriteObservedInputs(string observed, Func<ParentDimensionInput, string> resolve,
            Func<string, bool> isCurrentReference, out string updated)
        {
            var aliases = Inputs.ToDictionary(input => input.Token, resolve, StringComparer.Ordinal);
            updated = RewriteInputs(input => aliases[input.Token]);
            var valid = true;
            var actual = Regex.Replace(observed, "\"([^\"]+)\"", match =>
            {
                var token = match.Groups[1].Value;
                if (!TryReadEquation("\"" + token + "\"=0", out _, out var reference, out _)) return match.Value;
                var live = isCurrentReference(reference);
                var candidates = aliases.Where(pair => string.Equals(token, pair.Value, StringComparison.OrdinalIgnoreCase) ||
                    !live && KnownAlias(token, pair.Key, pair.Value)).ToArray();
                if (candidates.Length == 1) return "\"" + candidates[0].Value + "\"";
                valid = false;
                return match.Value;
            });
            return valid && string.Equals(actual.Trim(), updated, StringComparison.Ordinal);
        }

        private static bool KnownAlias(string token, string original, string current)
        {
            var parts = token.Split('@');
            var before = original.Split('@');
            var after = current.Split('@');
            return parts.Length == before.Length && parts.Length == after.Length && parts.Select((part, index) =>
                string.Equals(part, before[index], StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, after[index], StringComparison.OrdinalIgnoreCase)).All(match => match);
        }

        internal ParentDimensionDependency ForTarget(string target) =>
            new ParentDimensionDependency(target, Family, Configuration, Dimension, Expression, Inputs);

        internal static ParentDimensionDependency[] Merge(IEnumerable<ParentDimensionDependency> rules, string context)
        {
            return rules.GroupBy(rule => rule.Key, StringComparer.OrdinalIgnoreCase).Select(group =>
            {
                var rule = group.First();
                if (group.Any(other => !rule.SameSource(other)))
                    throw new InvalidOperationException("上级尺寸驱动来源冲突：" + context + "/" + rule.Key);
                return rule;
            }).OrderBy(rule => rule.Key, StringComparer.Ordinal).ToArray();
        }

        internal bool SameSource(ParentDimensionDependency other) =>
            string.Equals(Family, other.Family, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Configuration, other.Configuration, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Dimension, other.Dimension, StringComparison.OrdinalIgnoreCase)
            && string.Equals(SourceExpression, other.SourceExpression, StringComparison.Ordinal);

        internal static bool ValidAddress(string address) => !string.IsNullOrWhiteSpace(address) && address.Split('/').All(segment =>
            int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0);

        internal static string[][] OrderWrites(IEnumerable<string> targets, Func<string, IEnumerable<string>> inputs)
        {
            var remaining = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
            var layers = new List<string[]>();
            while (remaining.Count != 0)
            {
                var layer = remaining.Where(target => !inputs(target).Any(remaining.Contains))
                    .OrderBy(target => target, StringComparer.Ordinal).ToArray();
                if (layer.Length == 0)
                    throw new InvalidOperationException("上级尺寸依赖存在循环：" + string.Join("、", remaining));
                layers.Add(layer);
                remaining.ExceptWith(layer);
            }
            return layers.ToArray();
        }

        internal static bool TryReadEquation(string equation, out string dimension, out string reference, out string expression)
        {
            dimension = null;
            reference = null;
            expression = null;
            var equals = equation?.IndexOf('=') ?? -1;
            if (equals <= 0) return false;
            var left = equation.Substring(0, equals).Trim();
            if (left.Length < 2 || left[0] != '"' || left[left.Length - 1] != '"') return false;
            var fields = left.Substring(1, left.Length - 2).Split('@');
            if (fields.Length < 3 || fields.Any(string.IsNullOrWhiteSpace)) return false;
            var right = equation.Substring(equals + 1).Trim();
            if (right.Length == 0) return false;

            dimension = fields[0] + "@" + fields[1];
            reference = string.Join("@", fields.Skip(2));
            expression = right;
            return true;
        }

        internal static string ComponentReference(string instance, bool assembly)
        {
            var separator = instance?.LastIndexOf('-') ?? -1;
            if (separator <= 0 || !int.TryParse(instance.Substring(separator + 1), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var id) || id <= 0)
                return null;
            return instance.Substring(0, separator) + "<" + id.ToString(CultureInfo.InvariantCulture) + ">."
                + (assembly ? "Assembly" : "Part");
        }
    }

    internal sealed class ParentDimensionInput
    {
        [JsonProperty("token", Required = Required.Always)] internal string Token { get; }
        [JsonProperty("target", Required = Required.Always)] internal string Target { get; }
        [JsonProperty("family", Required = Required.Always)] internal string Family { get; }
        [JsonProperty("configuration", Required = Required.Always)] internal string Configuration { get; }
        [JsonProperty("dimension", Required = Required.Always)] internal string Dimension { get; }

        [JsonConstructor]
        internal ParentDimensionInput(string token, string target, string family, string configuration, string dimension)
        {
            if (string.IsNullOrWhiteSpace(token) || !ParentDimensionDependency.ValidAddress(target) || string.IsNullOrWhiteSpace(family)
                || string.IsNullOrWhiteSpace(configuration) || string.IsNullOrWhiteSpace(dimension) || dimension.Split('@').Length != 2)
                throw new InvalidOperationException("上级尺寸方程缺少实际输入组件。");
            Token = token;
            Target = target;
            Family = family;
            Configuration = configuration;
            Dimension = dimension;
        }
    }

    internal static class ParentDimensionStorage
    {
        private const string ManifestPrefix = "DQDIM1:";
        // CAD 单项属性有长度限制；固定大小的纯 ASCII 文本可原样写入和反读。
        private const int ChunkLength = 240;

        internal static string Manifest(int count) => ManifestPrefix + count.ToString(CultureInfo.InvariantCulture);

        internal static int Count(string manifest)
        {
            if (manifest?.StartsWith(ManifestPrefix, StringComparison.Ordinal) != true) return 0;
            if (!int.TryParse(manifest.Substring(ManifestPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0)
                throw new InvalidOperationException("上级尺寸驱动记录分段索引无效。");
            return count;
        }

        internal static string[] Encode(string value)
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            return Enumerable.Range(0, (encoded.Length + ChunkLength - 1) / ChunkLength)
                .Select(index => encoded.Substring(index * ChunkLength, Math.Min(ChunkLength, encoded.Length - index * ChunkLength)))
                .ToArray();
        }

        internal static string Decode(string manifest, Func<int, string> readChunk)
        {
            var count = Count(manifest);
            if (count == 0) return manifest;
            var encoded = new StringBuilder();
            for (var index = 0; index < count; index++)
            {
                var chunk = readChunk(index);
                if (string.IsNullOrWhiteSpace(chunk) || chunk.Length > ChunkLength || (index < count - 1 && chunk.Length != ChunkLength))
                    throw new InvalidOperationException("上级尺寸驱动记录分段缺失或不完整：" + index);
                encoded.Append(chunk);
            }
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded.ToString()));
        }
    }
}
