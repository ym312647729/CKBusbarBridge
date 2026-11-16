using System.Collections.Generic;
using System.Linq;
using GeometRi;
using CKBusbarBridgePreview;
namespace CKBusbarBridge.Prototype
{
    // 界面持有原生参数的独立快照，避免高频绘制调用受保护的预览 Copy 方法。
    internal static class BridgeNativePreviewCopy
    {
        internal static PlanarBendBusbarSetting Copy(PlanarBendBusbarSetting source)
        {
            if (source == null) return null;
            return new PlanarBendBusbarSetting
            {
                IsCenter = source.IsCenter,
                StartBusbarVector = CopyVector(source.StartBusbarVector),
                WidthVector = CopyVector(source.WidthVector),
                PointList = CopyPoints(source.PointList),
                BarBendingRadius = source.BarBendingRadius,
                VerticalBendingRadius = source.VerticalBendingRadius,
                BendAllowance = source.BendAllowance,
                BusBarThickness = source.BusBarThickness,
                BusBarWidth = source.BusBarWidth,
                StartLapWidth = source.StartLapWidth,
                EndLapWidth = source.EndLapWidth,
                GroupKey = source.GroupKey,
                PlanningSegmentIndex = source.PlanningSegmentIndex,
                IsMilledBar = source.IsMilledBar,
                MilledBarOriginalWidth = source.MilledBarOriginalWidth,
                MilledBarAdditionalWidth = source.MilledBarAdditionalWidth,
                MilledBarStartLength = source.MilledBarStartLength,
                MilledBarEndLength = source.MilledBarEndLength,
                MilledBarFeatureSide = source.MilledBarFeatureSide,
                IsVerticalBend = source.IsVerticalBend,
                PreviewBendPointKinds = source.PreviewBendPointKinds == null
                    ? new List<BusbarPreviewBendPointKind_e>()
                    : new List<BusbarPreviewBendPointKind_e>(
                        source.PreviewBendPointKinds)
            };
        }
        private static Vector3d CopyVector(Vector3d value) => value == null ? null : new Vector3d(value.X, value.Y, value.Z);
        private static List<Point3d> CopyPoints(IEnumerable<Point3d> values) => values == null ? new List<Point3d>() :
            values.Select(value => value == null ? null : new Point3d(value.X, value.Y, value.Z)).ToList();
    }
}
