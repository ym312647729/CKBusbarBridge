using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CKBusbarBridge.Models
{
    /// <summary>按已核验的柜顶安装、基座叠装及顶部开口关系，校验本次驱动后的孔口上下沿标高（mm）。</summary>
    internal sealed class CabinetOpeningElevationValidator
    {
        // 与柜体驱动参数保留一位小数的精度一致。
        internal const double Tolerance = 0.1d;

        internal double ProjectMountingElevation(string label, double height, double ground,
            double targetHeight, double targetGround, double[] placement, double[] mounting)
        {
            CabinetTop(height, ground, label);
            CabinetTop(targetHeight, targetGround, label);
            if (placement == null || placement.Length < 13 || placement.Any(value => !Finite(value))
                || mounting == null || mounting.Length < 13 || mounting.Any(value => !Finite(value))
                || Math.Abs(placement[12] - 1d) > 1e-6d || Math.Abs(mounting[12] - 1d) > 1e-6d
                || Math.Abs(placement[4] - 1d) > 1e-6d || Math.Abs(mounting[4] - 1d) > 1e-6d)
                throw new ArgumentException(label + "缺少有效的竖直组件变换或总装安装坐标系，不能校验孔口标高。");
            var currentMounting = mounting[10] * 1000d;
            // 柜体原点在名义中心，安装锚点在柜顶；关系不成立时不套用尺寸增量公式。
            if (Math.Abs(currentMounting - placement[10] * 1000d - height / 2d) > Tolerance + 1e-9d)
                throw new ArgumentException(label + "与实际柜顶安装标高不一致，不能推测驱动后的孔口标高。");
            // 保留实际总装基准，只应用本次柜高/离地配合的变化量。
            return currentMounting + targetHeight - height + targetGround - ground;
        }

        internal void Validate(CabinetDriverParameters parameters,
            IReadOnlyList<FaceToFaceBasePositionPlan> plans,
            Func<string, double> installationElevation = null)
        {
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            var openings = plans.Select(plan =>
            {
                var isA = plan.PositionName.StartsWith("A柜", StringComparison.Ordinal);
                var cabinet = isA ? parameters.ACabinet : parameters.BCabinet;
                var mounting = installationElevation == null
                    ? CabinetTop(cabinet.CabinetHeight, cabinet.GroundHeight, plan.PositionName)
                    : installationElevation(plan.PositionName);
                return Create(plan.PositionName, isA, mounting, plan.BaseModel.Height,
                    plan.ElevatedBaseModel?.Height ?? 0d,
                    Convert.ToDouble(plan.BaseModel.GlobalVariables[CabinetHoleParameters.BaseOpeningHeightVariableName],
                        CultureInfo.InvariantCulture));
            }).ToArray();
            Validate(openings);
        }

        internal void Validate(SideToSideCabinetDriverParameters parameters,
            IReadOnlyList<SideToSideBasePositionPlan> plans,
            Func<string, double> installationElevation = null,
            bool requireAlignedOpenings = true)
        {
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            var openings = plans.Select(plan =>
            {
                var cabinet = plan.IsACabinet ? parameters.ACabinet : parameters.BCabinet;
                var mounting = installationElevation == null
                    ? CabinetTop(cabinet.CabinetHeight, cabinet.GroundHeight, plan.PositionName)
                    : installationElevation(plan.PositionName);
                return Create(plan.PositionName, plan.IsACabinet, mounting,
                    plan.BaseModel.GlobalVariables["基座高"],
                    plan.ElevatedBaseModel?.GlobalVariables["高度"] ?? 0d,
                    plan.BaseModel.GlobalVariables["基座开孔高度"]);
            }).ToArray();
            Validate(openings, requireAlignedOpenings);
        }

        private double CabinetTop(double height, double ground, string position)
        {
            if (!Finite(height) || height <= 0d || !Finite(ground) || ground < 0d)
                throw new ArgumentException(position + "的柜高或离地高度无效，无法校验孔口标高。");
            return height + ground;
        }

        private Opening Create(string position, bool isA, double mounting, double body,
            double elevated, double aperture)
        {
            if (!Finite(mounting) || !Finite(body) || body <= 0d || !Finite(elevated) || elevated < 0d
                || !Finite(aperture) || aperture <= 0d || aperture > body + Tolerance)
                throw new ArgumentException(position + "的安装标高、普通/增高基座高度或开孔高度无效，无法校验孔口标高。");
            // 计划中的普通基座高度已扣除增高段；必须相加一次，不能再把界面的组合总高重复叠加。
            var top = mounting + elevated + body;
            if (!Finite(top) || !Finite(top - aperture))
                throw new ArgumentException(position + "的孔口标高无效。");
            return new Opening { Position = position, IsA = isA, Top = top, Bottom = top - aperture };
        }

        private void Validate(Opening[] openings, bool requireAlignedOpenings = true)
        {
            if (!requireAlignedOpenings)
                return;
            // 只比较实际启用的两端；单侧独立柜孔仍沿用原有生成范围。
            foreach (var a in openings.Where(item => item.IsA))
                foreach (var b in openings.Where(item => !item.IsA))
                {
                    var difference = Math.Max(Math.Abs(a.Top - b.Top), Math.Abs(a.Bottom - b.Bottom));
                    if (difference <= Tolerance + 1e-9d) continue;
                    throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                        "基座孔口安装标高不对齐，已阻止生成：{0}上沿 {1:0.###} mm、下沿 {2:0.###} mm；"
                        + "{3}上沿 {4:0.###} mm、下沿 {5:0.###} mm；最大高差 {6:0.###} mm（允许 {7:0.###} mm）。"
                        + "请调整柜高、离地高度或包含增高基座的基座安装总高。",
                        a.Position, a.Top, a.Bottom, b.Position, b.Top, b.Bottom, difference, Tolerance));
                }
        }

        private bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private sealed class Opening
        {
            internal string Position;
            internal bool IsA;
            internal double Top;
            internal double Bottom;
        }
    }
}
