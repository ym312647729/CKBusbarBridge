using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using CKBusbarBridge.Models;

namespace CKBusbarBridge.Prototype
{
    // 文件摘要只描述落盘字节。以下 DTO 来自当前引用配置及实际实体，不包含 COM 对象。
    internal sealed class BridgeModelState
    {
        public int Version { get; set; } = 1;
        public string AssemblyFileHash { get; set; }
        public Dictionary<string, string> FileHashes { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, BridgeModelInstanceState> Instances { get; set; } = new Dictionary<string, BridgeModelInstanceState>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, BridgeModelDocumentState> Documents { get; set; } = new Dictionary<string, BridgeModelDocumentState>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, BridgeModelDocumentState> Connections { get; set; } = new Dictionary<string, BridgeModelDocumentState>(StringComparer.Ordinal);
        // 安装依赖不是桥体文件所有权；完整快照记录其已核验的映射、配置及安装坐标系。
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, BaseInstallationState> Installations { get; set; }
    }

    internal sealed class BridgeModelInstanceState
    {
        public string Root { get; set; }
        public string Document { get; set; }
        public bool Suppressed { get; set; }
        public bool IsRoot { get; set; }
        public double[] Transform { get; set; }
        public List<BridgeModelGeometryValue> Geometry { get; set; } = new List<BridgeModelGeometryValue>();
    }

    internal sealed class BridgeModelDocumentState
    {
        public string Path { get; set; }
        public string Configuration { get; set; }
        public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, double> Dimensions { get; set; } = new Dictionary<string, double>(StringComparer.Ordinal);
        public List<BridgeModelGeometryValue> Geometry { get; set; } = new List<BridgeModelGeometryValue>();
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public BridgeModelConnectionState Connection { get; set; }
    }

    internal sealed class BridgeModelConnectionState
    {
        public int Type { get; set; }
        public int Alignment { get; set; }
        public bool Suppressed { get; set; }
        public int Error { get; set; }
        public bool Warning { get; set; }
        public List<BridgeModelConnectionEndpoint> Endpoints { get; set; } = new List<BridgeModelConnectionEndpoint>();

        internal bool SameDefinition(BridgeModelConnectionState other) => other != null && Type == other.Type &&
            Alignment == other.Alignment && Suppressed == other.Suppressed && Error == other.Error && Warning == other.Warning &&
            Endpoints != null && other.Endpoints != null && !Endpoints.Any(e => e == null) && !other.Endpoints.Any(e => e == null) &&
            Endpoints.Select(e => e.Identity).OrderBy(v => v, StringComparer.Ordinal)
                .SequenceEqual(other.Endpoints.Select(e => e.Identity).OrderBy(v => v, StringComparer.Ordinal));
    }

    internal sealed class BridgeModelConnectionEndpoint
    {
        // 总装自身用空实例名表示，禁止用显示名称猜测根引用。
        public string Instance { get; set; }
        public string Configuration { get; set; }
        public string Reference { get; set; }
        public string Kind { get; set; }
        public double[] WorldTransform { get; set; }
        [JsonIgnore]
        internal string Identity => Instance + "\n" + Configuration + "\n" + Reference + "\n" + Kind;
    }

    internal sealed class BridgeModelGeometryValue
    {
        public string Kind { get; set; }
        public double[] Values { get; set; }
        public double Tolerance { get; set; }
        public List<BridgeModelGeometryValue> Children { get; set; } = new List<BridgeModelGeometryValue>();
    }

    internal enum BridgeModelChangeKind { Equivalent, ExpectedChange, Conflict, Unverifiable }

    internal sealed class BridgeModelStateComparison
    {
        internal BridgeModelChangeKind Kind;
        internal readonly List<string> Differences = new List<string>();
        internal void RequireVerified()
        {
            if (Kind == BridgeModelChangeKind.Conflict || Kind == BridgeModelChangeKind.Unverifiable)
                throw new InvalidOperationException(string.Join("；", Differences));
        }
    }

    internal sealed class BridgeModelStateComparer
    {
        internal void RequireComplete(BridgeModelState state, string stage)
        {
            if (!Valid(state))
                throw new InvalidOperationException(stage + "缺少完整的实际模型业务快照，无法核验几何、引用配置与安装状态。");
        }

        internal BridgeModelStateComparison Compare(BridgeModelState before, BridgeModelState after,
            ISet<string> allowedDocuments = null, bool allowRootPlacement = false)
        {
            var result = new BridgeModelStateComparison();
            if (before != null && ReferenceEquals(before, after)) return result;
            if (!Valid(before) || !Valid(after))
            {
                result.Kind = BridgeModelChangeKind.Unverifiable;
                result.Differences.Add("缺少完整的实际模型业务快照，无法核验几何、引用配置与安装状态。");
                return result;
            }
            CompareInstances(before, after, allowedDocuments, allowRootPlacement, result);
            foreach (var name in before.Connections.Keys.Union(after.Connections.Keys))
            {
                if (!before.Connections.TryGetValue(name, out var oldConnection) || !after.Connections.TryGetValue(name, out var currentConnection))
                    Conflict(result, name + "总装安装配合新增或缺失");
                else CompareDocument(name, oldConnection, currentConnection, result);
            }
            foreach (var key in before.Documents.Keys.Union(after.Documents.Keys, StringComparer.OrdinalIgnoreCase))
            {
                var hasOld = before.Documents.TryGetValue(key, out var old);
                var hasCurrent = after.Documents.TryGetValue(key, out var current);
                if (!hasOld || !hasCurrent)
                {
                    if (Allowed(old ?? current, allowedDocuments)) Expected(result);
                    else Conflict(result, key + "引用文档新增或缺失");
                    continue;
                }
                if (Allowed(current, allowedDocuments)) { Expected(result); continue; }
                CompareDocument(key, old, current, result, before, after);
            }
            return result;
        }

        private bool Valid(BridgeModelState state) => state?.Version == 1 && state.Instances?.Count > 0 &&
            state.Documents?.Count > 0 && state.Connections != null && state.Connections.Values.All(ValidDocument) &&
            state.Instances.Values.All(i => i != null && !string.IsNullOrWhiteSpace(i.Root) &&
                !string.IsNullOrWhiteSpace(i.Document) && i.Geometry != null && i.Geometry.All(ValidGeometry) &&
                (i.Suppressed || new BridgeTransformComparison().IsValid(i.Transform))) &&
            state.Documents.Values.All(ValidDocument);

        private bool ValidDocument(BridgeModelDocumentState d) => d != null && !string.IsNullOrWhiteSpace(d.Path) &&
                !string.IsNullOrWhiteSpace(d.Configuration) && d.Properties != null && d.Dimensions != null &&
                d.Dimensions.Values.All(Finite) && d.Geometry != null && d.Geometry.All(ValidGeometry) &&
                (!d.Path.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase) || d.Geometry.Count > 0);

        private bool ValidGeometry(BridgeModelGeometryValue g) => g != null && !string.IsNullOrWhiteSpace(g.Kind) &&
            Finite(g.Values, 1) && Finite(g.Tolerance) && g.Tolerance > 0 && g.Tolerance <= .00001 &&
            g.Children != null && g.Children.All(ValidGeometry);

        private void CompareInstances(BridgeModelState before, BridgeModelState after, ISet<string> allowed,
            bool allowRootPlacement, BridgeModelStateComparison result)
        {
            foreach (var key in before.Instances.Keys.Union(after.Instances.Keys, StringComparer.OrdinalIgnoreCase))
            {
                before.Instances.TryGetValue(key, out var old); after.Instances.TryGetValue(key, out var current);
                var item = old ?? current;
                var document = (old != null ? before : after).Documents.TryGetValue(item.Document, out var d) ? d : null;
                if (Allowed(document, allowed)) { Expected(result); continue; }
                if (old == null || current == null || old.Document != current.Document || old.Root != current.Root ||
                    old.Suppressed != current.Suppressed || old.IsRoot != current.IsRoot)
                { Conflict(result, key + "组件身份、配置、归属或压缩状态变化"); continue; }
                if (!old.Suppressed && !SameTransform(old.Transform, current.Transform))
                {
                    if (allowRootPlacement && old.IsRoot) Expected(result);
                    else Conflict(result, key + "实际安装变换变化");
                }
                if (!GeometryEqual(old.Geometry, current.Geometry)) Conflict(result, key + "装配上下文中的实际实体变化（含装配体切除）");
            }
        }

        private void CompareDocument(string key, BridgeModelDocumentState old, BridgeModelDocumentState current,
            BridgeModelStateComparison result, BridgeModelState before = null, BridgeModelState after = null)
        {
            if (ReferenceEquals(old, current)) return;
            if (!string.Equals(old.Path, current.Path, StringComparison.OrdinalIgnoreCase) || old.Configuration != current.Configuration)
                Conflict(result, key + "路径或引用配置变化");
            // 旧记录允许补齐元数据；已有元数据不能丢失，配合身份和健康状态仍严格比较。
            if (old.Connection != null && !old.Connection.SameDefinition(current.Connection))
                Conflict(result, key + "总装配合引用、压缩或求解状态变化");
            foreach (var name in old.Properties.Keys.Union(current.Properties.Keys))
                if (!old.Properties.TryGetValue(name, out var a) || !current.Properties.TryGetValue(name, out var b) || a != b)
                    Conflict(result, key + "/" + name + "业务属性、特征或配合变化");
            var sameGeometry = GeometryEqual(old.Geometry, current.Geometry);
            var oldDimensions = EquationDimensions(old, before, result);
            var currentDimensions = EquationDimensions(current, after, result);
            foreach (var name in oldDimensions.Keys.Union(currentDimensions.Keys))
            {
                var hasOld = oldDimensions.TryGetValue(name, out var a);
                var hasCurrent = currentDimensions.TryGetValue(name, out var b);
                // 成型工具的显示尺寸可能在重新打开后才暴露。仅补齐显示项不代表模型改变，
                // 但必须核验同一启用特征及完整实体；已有尺寸数值改变仍然拒绝。
                if (hasOld != hasCurrent && sameGeometry && SameFormToolDisplayDimension(name, old, current)) continue;
                if (!hasOld || !hasCurrent || Math.Abs(a - b) > .01)
                    Conflict(result, key + "/" + name + "尺寸变化：" + (hasOld ? Number(a) : "缺失") + " → " + (hasCurrent ? Number(b) : "缺失"));
            }
            if (!sameGeometry) Conflict(result, key + "实际实体曲面或边界变化（含孔、切除及折弯）");
        }

        private IReadOnlyDictionary<string, double> EquationDimensions(BridgeModelDocumentState document,
            BridgeModelState state, BridgeModelStateComparison result)
        {
            if (state == null || !document.Path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase)) return document.Dimensions;
            var parts = Path.GetFileNameWithoutExtension(document.Path).Split('.');
            if (parts.Length != 5 || parts[4].Length != 4 || !parts[4].All(char.IsDigit)) return document.Dimensions;
            var suffix = "." + string.Join(".", parts.Skip(3));
            var actualParts = state.Documents.Values.Where(d => d.Path.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase))
                .GroupBy(d => Path.GetFileNameWithoutExtension(d.Path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(d => d.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    StringComparer.OrdinalIgnoreCase);
            var dimensions = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var pair in document.Dimensions)
            {
                // 另存项目后的外部方程式可能延迟展开图号。仅归一化本装配图号下已核验实物零件的名称；
                // 尺寸、草图、实例序号和父装配引用仍严格比较，实体及原驱动尺寸也由各组件独立核验。
                var name = pair.Key.StartsWith("方程式/\"", StringComparison.Ordinal) ? Regex.Replace(pair.Key,
                    @"@(?<part>[^@<>""]+)(?<instance><[1-9]\d*>)\.Part(?=@|""$)", match =>
                    {
                        var part = match.Groups["part"].Value;
                        if (!part.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || !actualParts.TryGetValue(part, out var count) || count != 1) return match.Value;
                        var template = part.Substring(0, part.Length - suffix.Length);
                        if (actualParts.ContainsKey(template)) return match.Value; // 存在真实同名文件时不能把它当作模板别名。
                        return "@" + template + match.Groups["instance"].Value + ".Part";
                    }, RegexOptions.IgnoreCase) : pair.Key;
                if (dimensions.ContainsKey(name)) Conflict(result, document.Path + "/" + name + "外部方程式目标重复，无法唯一核验");
                else dimensions.Add(name, pair.Value);
            }
            return dimensions;
        }

        private bool SameFormToolDisplayDimension(string name, BridgeModelDocumentState old, BridgeModelDocumentState current)
        {
            var parts = name.Split('@');
            if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]) ||
                string.IsNullOrWhiteSpace(parts[2]) || old.Geometry.Count == 0 || current.Geometry.Count == 0) return false;
            var feature = "特征/" + parts[1] + "/FormToolInstance";
            return old.Properties.TryGetValue(feature, out var before) && before == bool.FalseString &&
                current.Properties.TryGetValue(feature, out var after) && after == before;
        }

        // 按类型分组后做带容差的完全匹配；不依赖面/边枚举序，也不以量化哈希代替容差比较。
        private bool GeometryEqual(IReadOnlyList<BridgeModelGeometryValue> a, IReadOnlyList<BridgeModelGeometryValue> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a.Count != b.Count) return false;
            foreach (var group in a.GroupBy(g => g.Kind))
            {
                var left = group.ToArray(); var right = b.Where(g => g.Kind == group.Key).ToArray();
                if (left.Length != right.Length) return false;
                var matches = Enumerable.Repeat(-1, right.Length).ToArray();
                for (var i = 0; i < left.Length; i++)
                    if (!Match(i, left, right, matches, new bool[right.Length])) return false;
            }
            return true;
        }

        private bool Match(int i, BridgeModelGeometryValue[] a, BridgeModelGeometryValue[] b, int[] matches, bool[] seen)
        {
            for (var j = 0; j < b.Length; j++)
            {
                if (seen[j] || a[i].Values.Length != b[j].Values.Length ||
                    a[i].Values.Where((v, n) => Math.Abs(v - b[j].Values[n]) > Math.Min(a[i].Tolerance, b[j].Tolerance)).Any() ||
                    !GeometryEqual(a[i].Children, b[j].Children)) continue;
                seen[j] = true;
                if (matches[j] >= 0 && !Match(matches[j], a, b, matches, seen)) continue;
                matches[j] = i; return true;
            }
            return false;
        }

        private bool Allowed(BridgeModelDocumentState d, ISet<string> allowed) => d != null && allowed?.Contains(d.Path) == true;
        private void Expected(BridgeModelStateComparison result) { if (result.Kind == BridgeModelChangeKind.Equivalent) result.Kind = BridgeModelChangeKind.ExpectedChange; }
        private void Conflict(BridgeModelStateComparison result, string detail) { result.Kind = BridgeModelChangeKind.Conflict; result.Differences.Add(detail); }
        private string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        private bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private bool Finite(double[] values, int minimum) => values != null && values.Length >= minimum && values.All(Finite);
        internal bool SameTransform(double[] a, double[] b) => new BridgeTransformComparison().Compare(a, b).Equivalent;
    }

    internal sealed class BridgeModelStateReadPolicy
    {
        // 入口已经还原模型；摘要一致直接复用，不额外读取宿主中的配置、位置或待保存标志。
        internal bool CanReuse(string registeredHash, string currentHash) =>
            !string.IsNullOrWhiteSpace(registeredHash) &&
            string.Equals(registeredHash, currentHash, StringComparison.Ordinal);

        internal bool CanReuse(BridgeModelState baseline, string assemblyHash, IReadOnlyDictionary<string, string> hashes) =>
            baseline?.Version == 1 && CanReuse(baseline.AssemblyFileHash, assemblyHash) && baseline.FileHashes != null &&
            hashes != null && baseline.FileHashes.Count == hashes.Count &&
            hashes.All(p => baseline.FileHashes.TryGetValue(p.Key, out var oldHash) && CanReuse(oldHash, p.Value));

        internal BridgeModelState ReadIfChanged(BridgeModelState baseline, string assemblyHash,
            IReadOnlyDictionary<string, string> hashes, Func<BridgeModelState> readChanged) =>
            CanReuse(baseline, assemblyHash, hashes) ? baseline : readChanged();
    }

    internal sealed class BridgeAnalyticGeometry
    {
        internal double[] Plane(double[] data)
        {
            if (data?.Length != 6) throw new InvalidOperationException("实际平面参数无效。");
            var normal = Axis(data.Take(3).ToArray());
            return normal.Concat(new[] { normal.Select((v, i) => v * data[i + 3]).Sum() }).ToArray();
        }

        internal double[] Cylinder(double[] data)
        {
            if (data?.Length != 7) throw new InvalidOperationException("实际圆柱参数无效。");
            var axis = Axis(data.Skip(3).Take(3).ToArray());
            var projection = axis.Select((v, i) => v * data[i]).Sum();
            return data.Take(3).Select((v, i) => v - axis[i] * projection).Concat(axis).Concat(new[] { data[6] }).ToArray();
        }

        internal double[] Axis(double[] data)
        {
            var length = data == null ? 0 : Math.Sqrt(data.Sum(v => v * v));
            if (data?.Length != 3 || double.IsNaN(length) || double.IsInfinity(length) || length < 1e-12)
                throw new InvalidOperationException("实际几何方向无效。");
            var sign = data.First(v => Math.Abs(v) > 1e-12) < 0 ? -1 : 1;
            return data.Select(v => v / length * sign).ToArray();
        }
    }
}
