using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridge.Models;
using CKBusbarBridge.Drivers;

namespace CKBusbarBridge.Prototype
{
    // 驱动已完成但接口不符时保留现场；用类型传递策略，不能按报错文本猜测。
    internal sealed class FrameDriveInspectionRequiredException : InvalidOperationException
    {
        internal string ModelPath { get; }
        internal FrameDriveInspectionRequiredException(string modelPath, Exception validationError)
            : base(validationError.Message, validationError)
        { ModelPath = modelPath; }
    }

    // 配合链末段没有后继，反读出口可为空；这仅允许选择待核验坐标系，不能代替几何闭合校验。
    internal sealed class BridgeFrameGenerationValidator
    {
        internal void ValidateWallTemplateBatch(IEnumerable<FaceToFaceFramePositionPlan> plans,
            Action<FaceToFaceFramePositionPlan> validate)
        {
            var failures = new List<Exception>();
            foreach (var plan in plans)
            {
                try { validate(plan); }
                catch (InvalidOperationException error)
                { failures.Add(error); }
            }
            if (failures.Count > 0)
                throw new FrameDriveInspectionRequiredException("见各基架诊断",
                    new AggregateException("全部基架已生成装配，接口校验发现问题：" +
                        string.Join("；", failures.Select(e => e.Message)), failures));
        }

        internal void ValidateWallTemplateInterfaces(IReadOnlyDictionary<string, object> variables,
            Func<string, double[]> readCoordinate, string label)
        {
            var large1 = variables.ContainsKey(FaceToWallFrameDrivePlanner.UpperSealingPlateWidthVariableName);
            var large2 = variables.ContainsKey(FaceToWallFrameDrivePlanner.SideCoverPlateWidthVariableName);
            if (!large1 && !large2) return;
            if (large1 && large2) throw new InvalidOperationException(label + "混用了026/027入口驱动参数。");
            var inlet = readCoordinate("坐标系-进1");
            var outlet = readCoordinate("坐标系-出1");
            if (!ValidPoint(inlet) || !ValidPoint(outlet))
                throw new InvalidOperationException(label + "缺少有效的面对墙模板进/出坐标系。");
            var length = Convert.ToDouble(variables[FaceToWallFrameDrivePlanner.RegularFrameLengthVariableName], CultureInfo.InvariantCulture);
            var rise = large2 ? Convert.ToDouble(variables[FaceToWallFrameDrivePlanner.SideCoverPlateWidthVariableName], CultureInfo.InvariantCulture) / 2d : 0d;
            var eccentricity = large1 ? (Convert.ToDouble(variables[FaceToWallFrameDrivePlanner.UpperSealingPlateWidthVariableName], CultureInfo.InvariantCulture) -
                Convert.ToDouble(variables[FaceToWallFrameDrivePlanner.LowerSealingPlateWidthVariableName], CultureInfo.InvariantCulture)) / 2d : 0d;
            // 原模板局部轴X桥向/Y高度/Z横向；核验真实坐标差，拒绝把旧产物5mm残差推广为补偿。
            EqualPoint(outlet.Zip(inlet, (end, start) => end - start).ToArray(),
                new[] { length, rise, -eccentricity }, label + "面对墙模板进出接口与入口驱动几何");
        }

        internal void ValidateBusinessInstallation(BridgeReadbackFrame actual, FaceToFaceFramePositionPlan target)
        {
            // 普通基架正反装的接口坐标可能重合；必须同时核对实际配合所用的进出坐标系名称。
            if (actual.Physical.SourceName != target.SourceName ||
                actual.Binding.Inlet != target.InletCoordinateName || actual.Binding.Outlet != target.OutletCoordinateName)
                throw new InvalidOperationException("第" + target.PositionIndex + "段基架安装顺序或进出坐标系与草稿不一致：组件=" +
                    actual.Binding.Instance + "；实际=" + actual.Physical.SourceName + "/" + Text(actual.Binding.Inlet) + "→" + Text(actual.Binding.Outlet) +
                    "；目标=" + target.SourceName + "/" + target.InletCoordinateName + "→" + target.OutletCoordinateName);
        }

        internal string ValidateAndResolveOutlet(FaceToFaceFramePositionSnapshot actual,
            FaceToFaceFramePositionPlan target, bool isTerminal)
        {
            var differences = new List<string>();
            var model = actual.Model;
            var expected = target.ModelPlan;
            if (actual.SourceName != target.SourceName)
                differences.Add("模型名称：实际=" + actual.SourceName + "，目标=" + target.SourceName);
            if (model.TemplateIdentity != expected.TemplateIdentity)
                differences.Add("模板：实际=" + model.TemplateIdentity + "，目标=" + expected.TemplateIdentity);
            CompareDimension(differences, "宽", model.Width, expected.Width);
            CompareDimension(differences, "长", model.Length, expected.Length);
            CompareDimension(differences, "高", model.Height, expected.Height);
            if (model.InsulatorPositions.Count != expected.InsulatorPositions.Count)
                differences.Add("绝缘子套数：实际=" + model.InsulatorPositions.Count + "，目标=" + expected.InsulatorPositions.Count);
            else
                for (var i = 0; i < model.InsulatorPositions.Count; i++)
                    CompareDimension(differences, "绝缘子定位D" + (i + 1), model.InsulatorPositions[i], expected.InsulatorPositions[i]);
            if (actual.InletCoordinateName != target.InletCoordinateName)
                differences.Add("入口：实际=" + Text(actual.InletCoordinateName) + "，目标=" + target.InletCoordinateName);
            var outlet = actual.OutletCoordinateName;
            // 只处理反读器明确返回的null；非末段缺失、空字符串及明确不同的出口均拒绝。
            if (isTerminal && outlet == null) outlet = target.OutletCoordinateName;
            if (string.IsNullOrWhiteSpace(outlet) || outlet != target.OutletCoordinateName)
                differences.Add("出口：实际=" + Text(actual.OutletCoordinateName) + "，目标=" + target.OutletCoordinateName);
            if (differences.Count > 0)
                throw new InvalidOperationException(Label(actual, target) + "与草稿不一致：" + string.Join("；", differences));
            return outlet;
        }

        internal double[] ReadAndValidateOutlet(string outletName, Func<string, double[]> readCoordinate,
            double[] expected, bool isTerminal, double[] terminalAnchor, string label)
        {
            if (string.IsNullOrWhiteSpace(outletName))
                throw new InvalidOperationException(label + "缺少待核验出口名称。");
            if (readCoordinate == null) throw new ArgumentNullException(nameof(readCoordinate));
            var outlet = readCoordinate(outletName);
            // readCoordinate由SW层按组件引用配置和实际变换读取；缺失或无效坐标不得放行。
            EqualPoint(outlet, expected, label + "出口“" + outletName + "”与草稿");
            if (isTerminal) EqualPoint(outlet, terminalAnchor, label + "出口与A侧基座闭合");
            return outlet;
        }

        internal string Label(FaceToFaceFramePositionSnapshot actual, FaceToFaceFramePositionPlan target)
            => "第" + target.PositionIndex + "段“" + target.SourceName + "”（组件=" + actual.ComponentInstanceId + "）";

        internal void EqualPoint(double[] actual, double[] expected, string label)
        {
            if (!ValidPoint(actual) || !ValidPoint(expected))
                throw new InvalidOperationException(label + "坐标缺失或无效：实际=" + Point(actual) + "；目标=" + Point(expected));
            var difference = BridgeDesignPrecision.PositionDistance(actual, expected);
            if (difference > BridgeDesignPrecision.LengthComparisonToleranceMm)
            {
                var message = label + "不符：实际=" + Point(actual) + "；目标=" + Point(expected) +
                    "；位置偏差=" + Number(difference) + " mm（允许" + Number(BridgeDesignPrecision.LengthToleranceMm) + " mm）。";
                throw new InvalidOperationException(message);
            }
        }

        private void CompareDimension(List<string> differences, string label, double actual, double expected)
        {
            if (!Finite(actual) || !Finite(expected) || Math.Abs(actual - expected) > BridgeDesignPrecision.LengthComparisonToleranceMm)
                differences.Add(label + "：实际=" + Number(actual) + "，目标=" + Number(expected) + " mm");
        }
        private bool ValidPoint(double[] point) => point != null && point.Length == 3 && point.All(Finite);
        private bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private string Point(double[] point) => point == null ? "<缺失>" : "(" + string.Join(",", point.Select(Number)) + ") mm";
        private string Number(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
        private string Text(string value) => string.IsNullOrEmpty(value) ? "<缺失>" : value;
    }
}
