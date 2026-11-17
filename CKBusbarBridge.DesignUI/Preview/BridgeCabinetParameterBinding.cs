using System;
using System.Globalization;
using System.Reflection;
using CKBusbarBridge.Models;
using Newtonsoft.Json.Linq;

namespace CKBusbarBridge.Prototype
{
    internal static class BridgeCabinetParameterBinding
    {
        internal static bool MatchesInput(object parameters, string dimensionPath, object boundOwner, string boundProperty)
        {
            if (parameters == null || boundOwner == null || string.IsNullOrEmpty(dimensionPath) || string.IsNullOrEmpty(boundProperty)) return false;
            var parts = dimensionPath.Split('.');
            if (!string.Equals(parts[parts.Length - 1], boundProperty, StringComparison.Ordinal)) return false;
            var owner = parameters;
            for (var index = 0; owner != null && index < parts.Length - 1; index++)
                owner = owner.GetType().GetProperty(parts[index], BindingFlags.Public | BindingFlags.Instance)?.GetValue(owner, null);
            return ReferenceEquals(owner, boundOwner);
        }

        internal static object Restore(string scenario, JObject snapshot)
        {
            if (scenario == "face-to-face") return snapshot.ToObject<CabinetDriverParameters>();
            if (scenario == "side-to-side") return snapshot.ToObject<SideToSideCabinetDriverParameters>();
            if (scenario == "face-to-wall") return snapshot.ToObject<FaceToWallCabinetDriverParameters>();
            throw new InvalidOperationException("不支持的原柜体参数类型。");
        }
        internal static string Commit(object parameters, string path, string text)
        {
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || float.IsNaN(value) || float.IsInfinity(value))
                return "请输入有效的毫米数值，显示保留一位小数。";
            try
            {
                var segments = path.Split('.'); var owner = parameters;
                for (var index = 0; index < segments.Length - 1; index++)
                    owner = owner.GetType().GetProperty(segments[index], BindingFlags.Public | BindingFlags.Instance)?.GetValue(owner, null)
                        ?? throw new InvalidOperationException("原参数路径不存在：" + path);
                var property = owner.GetType().GetProperty(segments[segments.Length - 1], BindingFlags.Public | BindingFlags.Instance);
                if (property?.CanWrite != true || (property.PropertyType != typeof(float) && property.PropertyType != typeof(double)))
                    throw new InvalidOperationException("该原界面参数不可直接编辑：" + path);
                property.SetValue(owner, property.PropertyType == typeof(float) ? (object)value : (double)value, null);
                var written = Convert.ToSingle(property.GetValue(owner, null), CultureInfo.InvariantCulture);
                if (float.IsNaN(written) || float.IsInfinity(written) || Math.Abs(written - value) > SinglePrecision.Tolerance)
                    return "原参数规则未接受该尺寸，请按原界面约束修改。";
                return null;
            }
            catch (TargetInvocationException error) { return (error.InnerException ?? error).Message; }
            catch (InvalidOperationException error) { return error.Message; }
        }
        internal static string Validate(object parameters, string path, float value)
        {
            var candidate = BridgeParameterSnapshotBinding.Capture(parameters).ToObject(parameters.GetType());
            return Commit(candidate, path, value.ToString("R", CultureInfo.InvariantCulture));
        }
    }
}
