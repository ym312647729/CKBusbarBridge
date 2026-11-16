using CKBusbarBridge.Models;
using CKBusbarBridgePreview;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    internal interface IFaceToFaceFrameModelReadSource
    {
        FaceToFaceFrameTopologySnapshot CaptureTopology(
            string projectPath,
            string bCabinetBasePositionName);

        FaceToFaceFramePhysicalSnapshot ReadPhysicalModel(
            FaceToFaceFrameComponentSnapshot component);
    }

    /// <summary>
    /// 将总装配合拓扑与基架物理参数合并为界面可消费的完整读取结果。
    /// </summary>
    internal sealed class FaceToFaceFrameModelReadService
    {
        private const double WidthTolerance = 0.001d;
        private readonly IFaceToFaceFrameModelReadSource _source;
        private readonly bool _allowDivergentLargeFrameWidth;
        private readonly bool _useSpatialFirstFrameSelection;
        private readonly double? _spatialExpectedSignedEccentricity;
        private readonly bool _businessReadback;

        internal FaceToFaceFrameModelReadService(IFaceToFaceFrameModelReadSource source)
            : this(source, false)
        {
        }

        internal FaceToFaceFrameModelReadService(
            IFaceToFaceFrameModelReadSource source,
            bool allowDivergentLargeFrameWidth)
            : this(source, allowDivergentLargeFrameWidth, false)
        {
        }

        internal FaceToFaceFrameModelReadService(
            IFaceToFaceFrameModelReadSource source,
            bool allowDivergentLargeFrameWidth,
            bool useSpatialFirstFrameSelection,
            double? spatialExpectedSignedEccentricity = null, bool businessReadback = false)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _allowDivergentLargeFrameWidth = allowDivergentLargeFrameWidth;
            _useSpatialFirstFrameSelection = useSpatialFirstFrameSelection;
            _spatialExpectedSignedEccentricity = spatialExpectedSignedEccentricity;
            _businessReadback = businessReadback;
        }

        internal FaceToFaceFrameReadResult Read(
            string projectPath,
            string bCabinetBasePositionName)
        {
            try
            {
                return ReadCore(projectPath, bCabinetBasePositionName);
            }
            catch (Exception recognitionException)
            {
                FaceToWallPreviewTrace.RecognitionError(
                    "基架模型场景识别",
                    recognitionException,
                    "项目目录=" + projectPath +
                    "；B柜基座位置坐标系=" + bCabinetBasePositionName);
                throw;
            }
        }

        private FaceToFaceFrameReadResult ReadCore(
            string projectPath,
            string bCabinetBasePositionName)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                throw new ArgumentException("项目目录不能为空。", nameof(projectPath));
            }

            if (string.IsNullOrWhiteSpace(bCabinetBasePositionName))
            {
                throw new ArgumentException("普通基座定位坐标系不能为空。", nameof(bCabinetBasePositionName));
            }

            var topology = _source.CaptureTopology(projectPath, bCabinetBasePositionName)
                ?? throw new InvalidOperationException("基架模型读取失败：未取得总装配合拓扑。" );
            if (!topology.Components.Any(component => component.IsFrame))
            {
                return FaceToFaceFrameReadResult.Empty;
            }

            // 大偏移折转后，距 B 基座的直线距离不代表 B→A 链顺序。
            // 028 组必须沿实际配合链读取位置和进出坐标系，避免二次驱动校验拿错组件。
            var useSpatialOrder = _useSpatialFirstFrameSelection
                && !topology.Components.Any(component => component.IsFrame
                    && component.FrameTemplateKind == FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner);
            var orderedInstances = FaceToFaceFrameChainAnalyzer.Analyze(
                topology,
                useSpatialOrder);
            var results = new List<FaceToFaceFrameReadItem>();

            foreach (var orderedInstance in orderedInstances)
            {
                var component = orderedInstance.Component;
                // 每个安装实例分别读取姿态和参数；读取完后统一校验同名模型身份一致。
                FaceToWallPreviewTrace.Info(
                    "基架模型Comp读取开始",
                    "组件=" + component.InstanceId
                    + "；BOX中心=" + FormatBoxCenter(component.BoxCenter));
                var physical = _source.ReadPhysicalModel(component);
                ValidatePhysicalSnapshot(component, physical);
                FaceToWallPreviewTrace.Info(
                    "基架模型Comp读取完成",
                    "组件=" + component.InstanceId
                    + "；宽=" + physical.Width.ToString("0.###")
                    + "；长=" + physical.Length.ToString("0.###")
                    + "；高=" + physical.Height.ToString("0.###")
                    + "；绝缘子数=" + physical.InsulatorPositions.Count);

                results.Add(new FaceToFaceFrameReadItem(
                    component.InstanceId,
                    component.SourceName,
                    component.ModelPath,
                    physical.Width,
                    physical.Length,
                    physical.Height,
                    orderedInstance.IsReversed,
                    physical.InsulatorPositions,
                    component.FrameTemplateKind,
                    physical.SmallOffsetFrame,
                    orderedInstance.InletCoordinateName,
                    orderedInstance.OutletCoordinateName,
                    null,
                    FaceToFaceLargeOffsetFrameRole.None,
                    physical.HasExplicitTemplateIdentity
                        ? physical.TemplateIdentity
                        : (FrameTemplateIdentity?)null,
                    physical.HasExplicitTemplateIdentity,
                    physical.SideCoverPlateWidth,
                    physical.WallEntryDistance,
                    physical.HasWallEntryDistanceProperty));
            }

            if (_businessReadback) return new FaceToFaceFrameReadResult(results, 0, results.Sum(item => item.Length));
            var largeOffsetResult = FaceToFaceLargeOffsetFrameReadbackAnalyzer.Analyze(
                results,
                _spatialExpectedSignedEccentricity,
                !useSpatialOrder);
            if (largeOffsetResult.HasLargeOffsetGroup)
            {
                FaceToFaceFrameParameterSnapshot.FromReadResult(largeOffsetResult);
                // 028 组允许 A/B 两侧宽度不同，统一宽度固定为 0；总长只计一次拐角共享长度。
                return largeOffsetResult;
            }

            var unifiedWidth = ResolveUnifiedWidth(results);

            var result = new FaceToFaceFrameReadResult(
                results,
                unifiedWidth,
                results.Sum(item => item.Length));
            FaceToFaceFrameParameterSnapshot.FromReadResult(result);
            return result;
        }

        private double ResolveUnifiedWidth(IReadOnlyList<FaceToFaceFrameReadItem> results)
        {
            var smallOffsetFrames = results
                .Where(item => item.IsSmallOffsetFrame)
                .ToArray();
            if (FaceToFaceSmallOffsetFramePlanner.TryReadWidened(results, out _, out _, out _))
                return results[0].Width;
            if (smallOffsetFrames.Length > 1)
            {
                throw new InvalidOperationException("面对面小偏移场景只能有一段偏移基架。");
            }

            if (smallOffsetFrames.Length == 1)
            {
                // 小偏移场景允许 B 柜侧、偏移段和 A 柜侧分别采用不同宽度。
                return 0;
            }

            FaceToFaceFrameReadItem firstRegular = null;
            foreach (var item in results)
            {
                if (_allowDivergentLargeFrameWidth && item.UseLargeFrameTemplate)
                {
                    continue;
                }

                if (firstRegular == null)
                {
                    firstRegular = item;
                    continue;
                }

                if (Math.Abs(firstRegular.Width - item.Width) > WidthTolerance)
                {
                    throw new InvalidOperationException(
                        $"基架模型宽度不一致，无法回填统一宽度："
                        + $"{firstRegular.ModelPath} = {firstRegular.Width:0.###} mm；"
                        + $"{item.ModelPath} = {item.Width:0.###} mm。" );
                }
            }

            return firstRegular?.Width ?? 0;
        }

        private static string FormatBoxCenter(IReadOnlyList<double> boxCenter)
        {
            if (boxCenter == null || boxCenter.Count != 3)
            {
                return "<无效>";
            }

            return string.Join(",", boxCenter.Select(value => value.ToString("0.######")));
        }

        private static void ValidatePhysicalSnapshot(
            FaceToFaceFrameComponentSnapshot component,
            FaceToFaceFramePhysicalSnapshot physical)
        {
            if (physical == null)
            {
                throw new InvalidOperationException(
                    $"基架物理参数读取为空：组件={component.InstanceId}；路径={component.ModelPath}。" );
            }

            ValidatePositiveFinite(component, "基架宽度", physical.Width);
            ValidatePositiveFinite(component, "基架长度", physical.Length);
            ValidatePositiveFinite(component, "基架高度", physical.Height);
            if (physical.SideCoverPlateWidth.HasValue
                && (double.IsNaN(physical.SideCoverPlateWidth.Value)
                    || double.IsInfinity(physical.SideCoverPlateWidth.Value)
                    || physical.SideCoverPlateWidth.Value < 0d))
            {
                throw new InvalidOperationException(
                    $"大基架2侧盖板宽度必须为非负有限值：组件={component.InstanceId}；"
                    + $"路径={component.ModelPath}；当前值={physical.SideCoverPlateWidth.Value}。" );
            }

            if (!FaceToFaceFrameTemplateRules.SupportsInternalInsulators(component.FrameTemplateKind)
                && physical.InsulatorPositions.Count > 0)
            {
                throw new InvalidOperationException(
                    $"大基架1不应包含内部绝缘子：组件={component.InstanceId}；"
                    + $"路径={component.ModelPath}；当前数量={physical.InsulatorPositions.Count}。" );
            }

            if (physical.InsulatorPositions.Count > 3)
            {
                throw new InvalidOperationException(
                    $"基架内绝缘子数量必须为 0～3：组件={component.InstanceId}；"
                    + $"路径={component.ModelPath}；当前数量={physical.InsulatorPositions.Count}。" );
            }

            for (var index = 0; index < physical.InsulatorPositions.Count; index++)
            {
                var position = physical.InsulatorPositions[index];
                if (double.IsNaN(position)
                    || double.IsInfinity(position)
                    || position < 0
                    || position > physical.Length)
                {
                    throw new InvalidOperationException(
                        $"基架绝缘子定位 D{index + 1}@草图3 无效：组件={component.InstanceId}；"
                        + $"路径={component.ModelPath}；定位={position:0.###} mm；"
                        + $"允许范围=0～{physical.Length:0.###} mm。" );
                }
            }
        }

        private static void ValidatePositiveFinite(
            FaceToFaceFrameComponentSnapshot component,
            string parameterName,
            double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            {
                throw new InvalidOperationException(
                    $"基架模型参数“{parameterName}”无效：组件={component.InstanceId}；"
                    + $"路径={component.ModelPath}；数值={value}。" );
            }
        }
    }
}
