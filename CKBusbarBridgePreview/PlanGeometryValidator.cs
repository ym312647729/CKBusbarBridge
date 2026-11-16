using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CKBusbarBridgePreview
{
    public enum PlanGeometryDiagnosticSeverity
    {
        Warning,
        Error
    }

    public sealed class PlanGeometryDiagnostic
    {
        internal PlanGeometryDiagnostic(
            PlanGeometryDiagnosticSeverity severity,
            string code,
            string message)
        {
            Severity = severity;
            Code = code;
            Message = message;
        }

        public PlanGeometryDiagnosticSeverity Severity { get; }
        public string Code { get; }
        public string Message { get; }
    }

    /// <summary>
    /// 对统一规划结果执行与实体引擎无关的路径级检查。
    /// SolidWorks 实体净距和干涉仍需在实体生成后由专用几何服务验证。
    /// </summary>
    public static class PlanGeometryValidator
    {
        private const double Tolerance = .001d;

        public static IReadOnlyList<PlanGeometryDiagnostic> ValidatePath(
            MiddleBusbarPathPlan plan,
            double maximumDevelopedLength,
            double phaseChangeInsulationClearance,
            bool requiresPhaseChange)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var diagnostics = new List<PlanGeometryDiagnostic>();
            if (double.IsNaN(maximumDevelopedLength)
                || double.IsInfinity(maximumDevelopedLength)
                || maximumDevelopedLength <= 0d)
            {
                diagnostics.Add(Error("PLAN_INPUT_MAX_LENGTH", "规划最大展开长度无效。"));
                return diagnostics.AsReadOnly();
            }

            if (plan.Points.Count < 2 || plan.Segments.Count != plan.Points.Count - 1)
            {
                diagnostics.Add(Error("PLAN_TOPOLOGY", "路径点和路径段数量不一致。"));
                return diagnostics.AsReadOnly();
            }

            for (var index = 0; index < plan.Points.Count - 1; index++)
            {
                var current = plan.Points[index];
                var next = plan.Points[index + 1];
                if (next.PathDistance <= current.PathDistance + Tolerance)
                {
                    diagnostics.Add(Error(
                        "PLAN_DISTANCE_ORDER",
                        "路径里程必须严格递增，冲突点为 " + index.ToString(CultureInfo.InvariantCulture) + "。"));
                }

                var segment = plan.Segments[index];
                if (segment.DevelopedLength > maximumDevelopedLength + Tolerance &&
                    plan.DeferOffsetLengthLimitToPhysicalGeometry && (segment.Role & MiddleBusbarSegmentRole.Offset) != 0)
                    diagnostics.Add(new PlanGeometryDiagnostic(PlanGeometryDiagnosticSeverity.Warning,
                        "PLAN_PHYSICAL_LENGTH_PENDING", "偏移工艺区域包含多个排件，必须完成物理排件自动分段与长度终检；路径检查不能代替物理长度合格结论。"));
                if (segment.DevelopedLength > maximumDevelopedLength + Tolerance &&
                    !(plan.DeferOffsetLengthLimitToPhysicalGeometry && (segment.Role & MiddleBusbarSegmentRole.Offset) != 0))
                {
                    diagnostics.Add(Error(
                        "PLAN_SEGMENT_MAX_LENGTH",
                        "第 " + index.ToString(CultureInfo.InvariantCulture)
                        + " 段展开长度超过 "
                        + maximumDevelopedLength.ToString("0.###", CultureInfo.InvariantCulture)
                        + " mm。"));
                }
            }

            if (requiresPhaseChange)
            {
                if (double.IsNaN(phaseChangeInsulationClearance)
                    || double.IsInfinity(phaseChangeInsulationClearance)
                    || phaseChangeInsulationClearance < 0d)
                {
                    diagnostics.Add(Error("PLAN_PHASE_CLEARANCE", "换相绝缘净距参数无效。"));
                }
                else if (plan.Segments[0].CenterlineLength + Tolerance
                    < 2d * phaseChangeInsulationClearance)
                {
                    diagnostics.Add(Error(
                        "PLAN_PHASE_CLEARANCE",
                        "换相第一段不足以容纳两侧绝缘净距。"));
                }
            }

            return diagnostics.AsReadOnly();
        }

        public static void ThrowIfErrors(
            IReadOnlyList<PlanGeometryDiagnostic> diagnostics)
        {
            if (diagnostics == null) throw new ArgumentNullException(nameof(diagnostics));
            var error = diagnostics.FirstOrDefault(item =>
                item.Severity == PlanGeometryDiagnosticSeverity.Error);
            if (error != null)
            {
                throw new InvalidOperationException(error.Message);
            }
        }

        private static PlanGeometryDiagnostic Error(string code, string message)
        {
            return new PlanGeometryDiagnostic(
                PlanGeometryDiagnosticSeverity.Error, code, message);
        }
    }
}
