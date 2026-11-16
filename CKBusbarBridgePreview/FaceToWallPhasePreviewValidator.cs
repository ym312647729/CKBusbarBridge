using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridgePreview
{
    /// <summary>面对墙 A/B/C 逐相预览数据校验。</summary>
    public static class FaceToWallPhasePreviewValidator
    {
        private static readonly IReadOnlyDictionary<FaceToWallPreviewTemplateKind, string[]> RequiredDimensions
            = new Dictionary<FaceToWallPreviewTemplateKind, string[]>
            {
                {
                    FaceToWallPreviewTemplateKind.VerticalBend,
                    new[]
                    {
                        "偏移排总长度@控制骨架草图1",
                        "折弯高度@控制骨架草图1",
                        "折弯排抬高长度@控制骨架草图1",
                        "偏移第一段长度尺寸@控制骨架草图2",
                        "偏移第二段长度尺寸@控制骨架草图2"
                    }
                },
                {
                    FaceToWallPreviewTemplateKind.OffsetConnection,
                    new[]
                    {
                        "第一节直排长度@控制骨架草图1",
                        "折弯排第一段长度@控制骨架草图1",
                        "抬高高度@控制骨架草图1",
                        "折弯排第二段长度@控制骨架草图1"
                    }
                },
                {
                    FaceToWallPreviewTemplateKind.MilledBar,
                    new[]
                    {
                        "第一段排长度尺寸@控制骨架草图1",
                        "第一段铣排尺寸@控制骨架草图1",
                        "折弯高度@控制骨架草图1",
                        "折弯排第二段长度@控制骨架草图1"
                    }
                },
                {
                    FaceToWallPreviewTemplateKind.StraightRaiseOnly,
                    new[]
                    {
                        "折弯排第一段长度@控制骨架草图1",
                        "折弯排第二段长度@控制骨架草图1",
                        "抬高尺寸@控制骨架草图1"
                    }
                }
            };

        public static IReadOnlyList<FaceToWallPhasePreviewData> ValidateAndOrder(
            IEnumerable<FaceToWallPhasePreviewData> source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var ordered = source.Where(item => item != null).OrderBy(item => item.PhaseIndex).ToList();
            if (ordered.Count != 3
                || ordered[0].PhaseIndex != 0
                || ordered[1].PhaseIndex != 1
                || ordered[2].PhaseIndex != 2)
            {
                throw new InvalidOperationException("面对墙铜排预览数据必须恰好包含 A、B、C 三相且不得重复。");
            }

            foreach (var phase in ordered)
            {
                ValidatePhase(phase);
            }

            return ordered;
        }

        private static void ValidatePhase(FaceToWallPhasePreviewData phase)
        {
            var phaseName = GetPhaseName(phase.PhaseIndex);
            if (!IsFinite(phase.DeltaX) || phase.DeltaX <= 0)
            {
                throw new InvalidOperationException(phaseName + "相预览排长 DeltaX 必须大于 0。");
            }

            if (!IsFinite(phase.DeltaY) || phase.DeltaY < 0)
            {
                throw new InvalidOperationException(phaseName + "相预览抬高值 DeltaY 不能小于 0。");
            }

            if (!IsFinite(phase.DeltaZ)
                || !IsFinite(phase.OffsetSize)
                || Math.Abs(phase.OffsetSize - Math.Abs(phase.DeltaZ)) > 0.01)
            {
                throw new InvalidOperationException(phaseName + "相预览偏移尺寸必须等于 |DeltaZ|。");
            }

            var isZeroOffset = Math.Abs(phase.DeltaZ) < 0.001;
            if (isZeroOffset != (phase.TemplateKind == FaceToWallPreviewTemplateKind.StraightRaiseOnly))
            {
                throw new InvalidOperationException(
                    string.Format("{0}相预览模板 {1} 与偏移值 {2:0.0} 不匹配。", phaseName, phase.TemplateKind, phase.DeltaZ));
            }

            var expectedDirection = phase.DeltaZ < 0 ? 1 : 0;
            if (phase.OffsetDirection != expectedDirection)
            {
                throw new InvalidOperationException(phaseName + "相预览偏移方向与 DeltaZ 符号不一致。");
            }

            if (phase.DrivenDimensions == null)
            {
                throw new InvalidOperationException(phaseName + "相预览缺少模板驱动尺寸。");
            }

            if (!RequiredDimensions.TryGetValue(phase.TemplateKind, out var dimensionNames))
            {
                throw new InvalidOperationException(
                    string.Format("{0}相预览模板类型不受支持：{1}。", phaseName, phase.TemplateKind));
            }

            foreach (var dimensionName in dimensionNames)
            {
                if (!phase.DrivenDimensions.TryGetValue(dimensionName, out var value))
                {
                    throw new InvalidOperationException(
                        string.Format("{0}相{1}预览缺少尺寸“{2}”。", phaseName, phase.TemplateKind, dimensionName));
                }

                if (!IsFinite(value) || value < 0)
                {
                    throw new InvalidOperationException(
                        string.Format("{0}相{1}预览尺寸“{2}”无效：{3}。", phaseName, phase.TemplateKind, dimensionName, value));
                }
            }
        }

        private static string GetPhaseName(int phaseIndex)
        {
            switch (phaseIndex)
            {
                case 0:
                    return "A";
                case 1:
                    return "B";
                case 2:
                    return "C";
                default:
                    return phaseIndex.ToString();
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
