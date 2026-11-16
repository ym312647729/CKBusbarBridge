using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridge.Prototype;

namespace CKBusbarBridge.Drivers
{
    internal sealed class FrameAngleMateReference
    {
        internal string Instance;
        internal int Type;
        internal string Feature;
        internal bool Resolved;
        internal double[] Geometry = Array.Empty<double>();
        internal string Key => Instance + "/" + Type + "/" + Feature;
        public override string ToString() => Key + "/可读=" + Resolved + "/参数=["
            + string.Join(",", Geometry.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";
    }

    internal sealed class FrameAngleMateState
    {
        internal string Name;
        internal string Type;
        internal int Alignment;
        internal bool Suppressed;
        internal int Error;
        internal bool Warning;
        internal FrameAngleMateReference[] References = Array.Empty<FrameAngleMateReference>();
        internal Dictionary<string, double> Dimensions = new Dictionary<string, double>(StringComparer.Ordinal);
        public override string ToString() => Name + "/类型=" + Type + "/对齐=" + Alignment + "/压缩=" + Suppressed
            + "/错误码=" + Error + "/警告=" + Warning + "/引用={" + string.Join(";", References.Select(r => r.ToString()))
            + "}/尺寸={" + string.Join(";", Dimensions.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key + "=" + p.Value.ToString("R", CultureInfo.InvariantCulture))) + "}";
    }

    internal sealed class FrameAngleInstanceState
    {
        internal string Owner;
        internal int Id;
        internal string Name;
        internal string Path;
        internal string Configuration;
        internal bool Fixed;
        internal int Constrained;
        internal double[] Transform;
        internal FrameAngleMateState[] Mates = Array.Empty<FrameAngleMateState>();
        public override string ToString() => "所属装配=" + Owner + "；实例=" + Name + "；ID=" + Id + "；文件=" + Path
            + "；配置=" + Configuration + "；固定=" + Fixed + "；约束状态=" + Constrained
            + "；变换=[" + string.Join(",", (Transform ?? Array.Empty<double>()).Select(v => v.ToString("R", CultureInfo.InvariantCulture)))
            + "]；配合数=" + Mates.Length + "；配合={" + string.Join(";", Mates.Select(m => m.ToString())) + "}";
    }

    internal sealed class FrameAngleIndependenceResult
    {
        internal readonly List<string> Diagnostics = new List<string>();
        internal readonly List<string> Failures = new List<string>();
        internal void Check(bool passed, string name, string detail)
        {
            Diagnostics.Add(name + "；通过=" + passed + "；" + detail);
            if (!passed) Failures.Add(name + "：" + detail);
        }
        internal void RequireVerified()
        {
            if (Failures.Count != 0) throw new InvalidOperationException("角钢使之独立后校验失败：" + string.Join("；", Failures));
        }
    }

    internal sealed class FrameAngleIndependenceValidator
    {
        internal FrameAngleIndependenceResult Validate(FrameAngleInstanceState before, FrameAngleInstanceState after,
            FrameAngleInstanceState diagonalBefore, FrameAngleInstanceState diagonalAfter, string newPath)
        {
            var result = new FrameAngleIndependenceResult();
            CheckInstance(result, "目标角钢", before, after, newPath);
            CheckInstance(result, "对角角钢", diagonalBefore, diagonalAfter, diagonalBefore?.Path);
            return result;
        }

        internal FrameAngleIndependenceResult ValidateInstance(string label,
            FrameAngleInstanceState before, FrameAngleInstanceState after, string expectedPath, bool preserveExistingMateErrors = false)
        {
            var result = new FrameAngleIndependenceResult();
            CheckInstance(result, label, before, after, expectedPath, preserveExistingMateErrors);
            return result;
        }

        private void CheckInstance(FrameAngleIndependenceResult result, string label, FrameAngleInstanceState before,
            FrameAngleInstanceState after, string expectedPath, bool preserveExistingMateErrors = false)
        {
            result.Check(before != null && after != null, label + "实例解析", "原ID=" + before?.Id + "；现ID=" + after?.Id);
            if (before == null || after == null) return;
            result.Check(before.Id == after.Id && BridgeGenerationFileBackup.Same(before.Owner, after.Owner),
                label + "实例身份", "原所属=" + before.Owner + "/" + before.Id + "；现所属=" + after.Owner + "/" + after.Id);
            result.Check(BridgeGenerationFileBackup.Same(after.Path, expectedPath), label + "文件引用", "期望=" + expectedPath + "；实际=" + after.Path);
            result.Check(before.Configuration == after.Configuration, label + "原生默认配置", "期望=" + before.Configuration + "；实际=" + after.Configuration);
            var transform = new BridgeTransformComparison().Compare(before.Transform, after.Transform);
            var tolerance = BridgeDesignPrecision.NumericTolerance;
            result.Check(transform.Valid && transform.TranslationMm <= tolerance && transform.RotationDegrees <= tolerance &&
                transform.Scale <= tolerance, label + "安装变换", transform.Valid ? FormattableString.Invariant(
                    $"平移差={transform.TranslationMm:0.0} mm，旋转差={transform.RotationDegrees:0.0}°，缩放差={transform.Scale:0.0}") : transform.ToString());
            result.Check(before.Fixed == after.Fixed, label + "固定状态", "原=" + before.Fixed + "；现=" + after.Fixed);
            result.Diagnostics.Add(label + "约束状态；原=" + before.Constrained + "；现=" + after.Constrained);
            CheckMates(result, label, before.Mates, after.Mates, preserveExistingMateErrors);
        }

        private void CheckMates(FrameAngleIndependenceResult result, string label, FrameAngleMateState[] before,
            FrameAngleMateState[] after, bool preserveExistingMateErrors)
        {
            result.Check(before.Length == after.Length, label + "配合数量", "原=" + before.Length + "；现=" + after.Length);
            var unique = before.Select(m => m.Name).Distinct().Count() == before.Length && after.Select(m => m.Name).Distinct().Count() == after.Length;
            result.Check(unique, label + "配合身份唯一", "原=" + string.Join(",", before.Select(m => m.Name)) + "；现=" + string.Join(",", after.Select(m => m.Name)));
            if (!unique) return;
            foreach (var name in before.Select(m => m.Name).Union(after.Select(m => m.Name)).OrderBy(n => n, StringComparer.Ordinal))
            {
                var a = before.SingleOrDefault(m => m.Name == name); var b = after.SingleOrDefault(m => m.Name == name);
                result.Check(a != null && b != null, label + "配合保留/" + name, "原存在=" + (a != null) + "；现存在=" + (b != null));
                if (a == null || b == null) continue;
                result.Check(a.Type == b.Type && a.Alignment == b.Alignment && a.Suppressed == b.Suppressed,
                    label + "配合定义/" + name, "原=" + a + "；现=" + b);
                var existingError = preserveExistingMateErrors && a.Error != 0 && !a.Warning;
                result.Check((b.Suppressed || b.Error == 0 || b.Warning || existingError) && a.Error == b.Error && a.Warning == b.Warning,
                    label + "配合求解/" + name, "原错误码=" + a.Error + "/警告=" + a.Warning + "；现错误码=" + b.Error + "/警告=" + b.Warning);
                result.Check(ReferencesMatch(a, b, existingError), label + "配合引用/" + name, "原=" + a + "；现=" + b);
                result.Check(a.Dimensions.Count == b.Dimensions.Count && a.Dimensions.All(p => b.Dimensions.TryGetValue(p.Key, out var value)
                    && Math.Abs(p.Value - value) <= BridgeDesignPrecision.NumericTolerance), label + "配合尺寸/" + name, "原=" + a + "；现=" + b);
            }
        }

        private bool ReferencesMatch(FrameAngleMateState before, FrameAngleMateState after, bool existingError)
        {
            if (before.References.Length != after.References.Length) return false;
            var used = new bool[after.References.Length];
            foreach (var a in before.References)
            {
                var match = Enumerable.Range(0, after.References.Length).Where(index => !used[index]).Where(index =>
                {
                    var b = after.References[index];
                    return b.Key == a.Key && b.Resolved == a.Resolved && (after.Suppressed || b.Resolved || existingError)
                        && ((before.Suppressed && after.Suppressed) || GeometryMatches(a.Geometry, b.Geometry));
                }).DefaultIfEmpty(-1).First();
                if (match < 0) return false;
                used[match] = true;
            }
            return true;
        }

        private static bool GeometryMatches(double[] before, double[] after)
        {
            if (before.Length == 0 && after.Length == 0) return true;
            if (before.Length != 8 || after.Length != 8 || before.Concat(after).Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                return false;
            // EntityParams 依次为总装位置、方向向量和两个半径。长度转为 mm，方向差转为度。
            var tolerance = BridgeDesignPrecision.NumericTolerance;
            var position = BridgeDesignPrecision.PositionDistance(before.Take(3).ToArray(), after.Take(3).ToArray()) * 1000d;
            if (position > tolerance || Math.Abs(before[6] - after[6]) * 1000d > tolerance ||
                Math.Abs(before[7] - after[7]) * 1000d > tolerance) return false;
            var vector = Enumerable.Range(3, 3).ToArray();
            var a = Math.Sqrt(vector.Sum(i => before[i] * before[i]));
            var b = Math.Sqrt(vector.Sum(i => after[i] * after[i]));
            if (a == 0d || b == 0d) return a == b;
            var cosine = vector.Sum(i => before[i] * after[i]) / (a * b);
            return Math.Acos(Math.Max(-1d, Math.Min(1d, cosine))) * 180d / Math.PI <= tolerance;
        }
    }
}
