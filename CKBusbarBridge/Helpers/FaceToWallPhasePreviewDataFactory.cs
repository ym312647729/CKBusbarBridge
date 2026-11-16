using CKBusbarBridge.Drivers;
using CKBusbarBridge.models;
using CKBusbarBridgePreview;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// 将面对墙逐相几何计划及实际模板驱动尺寸映射为预览模块数据。
    /// </summary>
    public sealed class FaceToWallPhasePreviewDataFactory
    {
        private readonly FaceToWallSpecialBusbarDimensionCalculatorRegistry dimensionCalculators;

        private FaceToWallPhasePreviewDataFactory(
            FaceToWallSpecialBusbarDimensionCalculatorRegistry dimensionCalculators)
        {
            this.dimensionCalculators = dimensionCalculators
                ?? throw new ArgumentNullException(nameof(dimensionCalculators));
        }

        public static FaceToWallPhasePreviewDataFactory CreateConfigured()
        {
            return new FaceToWallPhasePreviewDataFactory(
                FaceToWallSpecialBusbarDimensionCalculatorRegistry.CreateConfigured());
        }

        public IReadOnlyList<FaceToWallPhasePreviewData> Create(
            IReadOnlyList<FaceToWallSpecialBusbarGeometryPlan> geometries,
            double busbarWidth,
            double overlapLength,
            FaceToWallSpecialBusbarLongitudinalLayout longitudinalLayout)
        {
            if (geometries == null)
            {
                throw new ArgumentNullException(nameof(geometries));
            }

            if (longitudinalLayout == null)
            {
                throw new ArgumentNullException(nameof(longitudinalLayout));
            }

            var ordered = geometries
                .Where(item => item != null)
                .OrderBy(item => item.Phase)
                .ToList();
            var expectedPhases = new[]
            {
                BusbarPhaseSequence.A,
                BusbarPhaseSequence.B,
                BusbarPhaseSequence.C
            };
            if (ordered.Count != expectedPhases.Length
                || !ordered.Select(item => item.Phase).SequenceEqual(expectedPhases))
            {
                throw new InvalidOperationException("面对墙铜排预览计划必须恰好包含 A、B、C 三相且不得重复。");
            }

            var preparation = new BusbarGenerationPreparationResult
            {
                BusbarWidth = busbarWidth,
                OverlapLength = overlapLength,
                FaceToWallSpecialBusbarLongitudinalLayout = longitudinalLayout
            };
            var result = new List<FaceToWallPhasePreviewData>(ordered.Count);
            foreach (var geometry in ordered)
            {
                var calculationPlan = new FaceToWallSpecialBusbarPhasePlan(
                    geometry.SegmentIndex,
                    geometry.Phase,
                    string.Empty,
                    geometry.DeltaX,
                    geometry.DeltaY,
                    geometry.DeltaZ,
                    geometry.OffsetDirection,
                    geometry.OffsetSize,
                    geometry.TemplateKind,
                    geometry.HeadKind,
                    string.Empty);
                var dimensions = dimensionCalculators.CalculateRequired(
                    calculationPlan,
                    preparation);

                result.Add(new FaceToWallPhasePreviewData(
                    (int)geometry.Phase,
                    MapTemplateKind(geometry.TemplateKind),
                    MapHeadKind(geometry.HeadKind),
                    geometry.DeltaX,
                    geometry.DeltaY,
                    geometry.DeltaZ,
                    geometry.OffsetDirection,
                    geometry.OffsetSize,
                    dimensions.ToDictionary(pair => pair.Key, pair => pair.Value)));
            }

            return result;
        }

        private static FaceToWallPreviewTemplateKind MapTemplateKind(
            FaceToWallPhaseSpecialTemplateKind templateKind)
        {
            switch (templateKind)
            {
                case FaceToWallPhaseSpecialTemplateKind.VerticalBend:
                    return FaceToWallPreviewTemplateKind.VerticalBend;
                case FaceToWallPhaseSpecialTemplateKind.MilledBar:
                    return FaceToWallPreviewTemplateKind.MilledBar;
                case FaceToWallPhaseSpecialTemplateKind.OffsetConnection:
                    return FaceToWallPreviewTemplateKind.OffsetConnection;
                case FaceToWallPhaseSpecialTemplateKind.StraightRaiseOnly:
                    return FaceToWallPreviewTemplateKind.StraightRaiseOnly;
                default:
                    throw new InvalidOperationException(
                        string.Format("不支持的面对墙铜排预览模板类型：{0}。", templateKind));
            }
        }

        private static FaceToWallPreviewHeadKind MapHeadKind(
            FaceToWallSpecialBusbarHeadKind headKind)
        {
            switch (headKind)
            {
                case FaceToWallSpecialBusbarHeadKind.StraightHead:
                    return FaceToWallPreviewHeadKind.StraightHead;
                case FaceToWallSpecialBusbarHeadKind.BridgeHead:
                    return FaceToWallPreviewHeadKind.BridgeHead;
                default:
                    throw new InvalidOperationException(
                        string.Format("不支持的面对墙铜排预览接头类型：{0}。", headKind));
            }
        }
    }
}
