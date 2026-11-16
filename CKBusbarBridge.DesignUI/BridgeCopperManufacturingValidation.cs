using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.Prototype
{
    // 修复草稿和正式生成共用整批门禁；只计算快照，不调用SolidWorks或受保护的预览计算。
    internal static class BridgeCopperManufacturingValidation
    {
        internal const double MaximumDevelopedLengthMm = 3000.0;

        internal static IReadOnlyList<BridgeDesignIssue> Validate(BridgeDesignSample sample, bool validateProcessing = true)
        {
            var issues = new List<BridgeDesignIssue>();
            if (sample.Routes.Count == 0 && validateProcessing)
                issues.Add(Issue("铜排路线缺少完整原生加工参数。", null));
            foreach (var route in sample.Routes)
            {
                var setting = route.CopyNativePreview();
                var problems = new List<string>();
                if (setting == null) problems.Add("缺少原生加工参数");
                else if (validateProcessing) Inspect(setting, problems);
                if (problems.Count == 0) continue;
                var group = route.GenerationGroup ?? setting?.GroupKey;
                issues.Add(Issue(route.Id + "（" + group + "，" + route.Phase + "相）：" +
                    string.Join("；", problems), route.Id));
            }
            return issues;
        }

        internal static void RequireValid(BridgeDesignSample sample, bool validateProcessing = true)
        {
            var issues = Validate(sample, validateProcessing);
            if (issues.Count > 0)
                throw new InvalidOperationException("铜排加工参数或展开长度无效：" +
                    string.Join("；", issues.Select(i => i.Message)));
        }

        private static void Inspect(PlanarBendBusbarSetting setting, ICollection<string> problems)
        {
            var points = setting.PointList;
            var validPath = points != null && points.Count >= 2 && points.All(p => p != null &&
                Finite(p.X) && Finite(p.Y) && Finite(p.Z));
            if (!validPath) problems.Add("路径须包含至少两个有限三维点");
            if (!new[] { setting.BusBarWidth, setting.BusBarThickness, setting.BarBendingRadius,
                setting.VerticalBendingRadius, setting.BendAllowance }.All(Finite))
                problems.Add("规格、弯曲半径或折弯补偿含非有限数值");
            if (setting.BusBarWidth <= 0 || setting.BusBarThickness <= 0)
                problems.Add("排宽和排厚须大于 0.0 mm");
            if (setting.WidthVector == null || setting.StartBusbarVector == null ||
                !ValidVector(setting.WidthVector) || !ValidVector(setting.StartBusbarVector))
                problems.Add("排宽或排厚方向无效");
            if (validPath && points.Count > 2 && setting.BarBendingRadius <= 0)
                problems.Add("平面折弯半径须大于 0.0 mm");
            if (setting.IsVerticalBend && setting.VerticalBendingRadius <= 0)
                problems.Add("立弯半径须大于 0.0 mm");
            if (!validPath || !Finite(setting.BendAllowance)) return;
            var length = 0.0;
            for (var i = 1; i < points.Count; i++)
            {
                var dx = points[i].X - points[i - 1].X;
                var dy = points[i].Y - points[i - 1].Y;
                var dz = points[i].Z - points[i - 1].Z;
                length += Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            length += Math.Max(0, points.Count - 2) * Math.Max(0.0, setting.BendAllowance);
            if (!Finite(length)) problems.Add("展开长度不是有限数值");
            else if (length > MaximumDevelopedLengthMm + 1.0)
                problems.Add("展开长度 " + length.ToString("0.0", CultureInfo.InvariantCulture) +
                    " mm，上限 " + MaximumDevelopedLengthMm.ToString("0.0", CultureInfo.InvariantCulture) + " mm");
        }

        private static bool ValidVector(GeometRi.Vector3d vector) =>
            Finite(vector.X) && Finite(vector.Y) && Finite(vector.Z) &&
            Finite(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z) &&
            vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z > 0.0;
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static BridgeDesignIssue Issue(string message, string id) =>
            new BridgeDesignIssue(message, id, "铜排加工", false, true);
    }
}
