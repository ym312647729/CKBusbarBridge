using AutoOpeningHoleDataModel;
using GeometRi;
using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// 铜排大师立弯预览算法的项目内版本。每个平弯/立弯分段独立生成
    /// Body，随后按接触关系执行实体相加；不再把立弯路径当成一张斜平面。
    /// </summary>
    public static class VerticalBendPreviewBody
    {
        public static List<FreeDrawBody> GetVerticalBendPreviewBodies(
            this VerticalBendSetting verticalBendSetting,
            ISldWorks solidWorksApplication,
            Color previewColor,
            Component2 previewComponent)
        {
            if (verticalBendSetting == null)
            {
                throw new ArgumentNullException(nameof(verticalBendSetting));
            }
            if (solidWorksApplication == null)
            {
                throw new ArgumentNullException(
                    nameof(solidWorksApplication));
            }
            if (previewComponent == null || previewComponent.IsHidden(true))
            {
                throw new InvalidOperationException(
                    "没有可用于立弯预览坐标转换的可见零部件。");
            }

            var originalGeometryTolerance = GeometRi3D.Tolerance;
            try
            {
                GeometRi3D.Tolerance = 1e-2d;
                verticalBendSetting.GetWidthVector();
                verticalBendSetting.SetSegmentPoints(false);
                var segmentBodies = CreateSegmentBodies(
                    verticalBendSetting,
                    (SldWorks)solidWorksApplication,
                    previewComponent);
                var mergedBodies = MergeSegmentBodies(segmentBodies);
                return mergedBodies.Select(mergedBody =>
                    new FreeDrawBody(
                        previewColor,
                        mergedBody,
                        previewComponent,
                        verticalBendSetting.LastBarVector,
                        verticalBendSetting.PointList.Count,
                        (verticalBendSetting.GroupKey ?? "未命名") +
                        "-立弯",
                        1)
                    {
                        IsVerticalBend = true
                    }).ToList();
            }
            finally
            {
                GeometRi3D.Tolerance = originalGeometryTolerance;
            }
        }

        private static List<Body2> CreateSegmentBodies(
            VerticalBendSetting verticalBendSetting,
            SldWorks solidWorksApplication,
            Component2 previewComponent)
        {
            var segmentBodies = new List<Body2>();
            var lastBarVector = verticalBendSetting.WidthVector;
            var segmentedPaths = verticalBendSetting.SegmentationPoints
                .Select(segment => Tuple.Create(
                    segment.Item1,
                    segment.Item2.Select(point => point.Copy()).ToList(),
                    segment.Item3.Copy()))
                .ToList();
            for (var segmentIndex = 0;
                 segmentIndex < segmentedPaths.Count;
                 segmentIndex++)
            {
                var segmentPoints = segmentedPaths[segmentIndex].Item2;
                RemoveRedundantSegmentPoints(segmentPoints);
                if (segmentPoints.Count < 2 ||
                    !IsUsableVector(lastBarVector))
                {
                    throw new InvalidOperationException(
                        "立弯第" + (segmentIndex + 1) +
                        "段没有形成有效路径。");
                }

                var isPlanarBendSegment =
                    segmentedPaths[segmentIndex].Item1;
                ResolveSegmentDirections(
                    segmentPoints,
                    lastBarVector,
                    isPlanarBendSegment,
                    segmentIndex,
                    out var segmentWidthDirection,
                    out var segmentBarDirection);
                var segmentWidth = isPlanarBendSegment
                    ? verticalBendSetting.BusBarWidth
                    : verticalBendSetting.BusBarThickness;
                var segmentExtrusionThickness = isPlanarBendSegment
                    ? verticalBendSetting.BusBarThickness
                    : verticalBendSetting.BusBarWidth;
                var segmentBendingRadius = isPlanarBendSegment
                    ? verticalBendSetting.BarBendingRadius
                    : verticalBendSetting.BusBarVerticalRadius;
                ValidateSegmentDimensions(
                    segmentWidth,
                    segmentExtrusionThickness,
                    segmentBendingRadius,
                    segmentIndex);

                // 铜排大师原方案：平弯按半排宽定位，立弯按一个排厚定位。
                var widthOffset = isPlanarBendSegment
                    ? segmentWidth / 2d
                    : segmentWidth;
                for (var pointIndex = 0;
                     pointIndex < segmentPoints.Count;
                     pointIndex++)
                {
                    segmentPoints[pointIndex] = segmentPoints[pointIndex]
                        .Move(segmentWidthDirection, widthOffset);
                }
                if (segmentIndex != 0)
                {
                    segmentPoints[0] = segmentPoints[0].Move(
                        new Vector3d(segmentPoints[1], segmentPoints[0]),
                        0.001d);
                }
                if (segmentIndex != segmentedPaths.Count - 1)
                {
                    var lastPointIndex = segmentPoints.Count - 1;
                    segmentPoints[lastPointIndex] =
                        segmentPoints[lastPointIndex].Move(
                            new Vector3d(
                                segmentPoints[lastPointIndex - 1],
                                segmentPoints[lastPointIndex]),
                            0.001d);
                }

                var centerlineSegments = new List<MySegment>();
                for (var pointIndex = 0;
                     pointIndex < segmentPoints.Count - 1;
                     pointIndex++)
                {
                    centerlineSegments.Add(new MySegment(
                        segmentPoints[pointIndex],
                        segmentPoints[pointIndex + 1]));
                }
                var offsetContour = UseBendToPreviewFreeBusBar
                    .OffsetLineSegment(
                        solidWorksApplication,
                        !isPlanarBendSegment,
                        centerlineSegments,
                        segmentBarDirection,
                        segmentExtrusionThickness,
                        segmentBendingRadius,
                        out _,
                        out _);
                if (offsetContour == null || offsetContour.Count == 0)
                {
                    throw new InvalidOperationException(
                        "立弯第" + (segmentIndex + 1) +
                        "段轮廓偏移失败。");
                }

                var segmentBody = ExtrudeSegmentWithSingleRetry(
                    solidWorksApplication,
                    offsetContour,
                    previewComponent,
                    segmentWidth,
                    segmentWidthDirection.Negate(),
                    verticalBendSetting.GroupKey,
                    segmentIndex);
                if (segmentBody == null)
                {
                    throw new InvalidOperationException(
                        "立弯第" + (segmentIndex + 1) +
                        "段没有生成有效 Body。");
                }

                segmentBodies.Add(segmentBody);
                lastBarVector = segmentedPaths[segmentIndex].Item3;
            }

            return segmentBodies;
        }

        /// <summary>
        /// SolidWorks Modeler 连续创建大量临时 Body 时，偶尔会在有效轮廓上
        /// 返回空。重新从同一轮廓创建薄面并拉伸一次，不改变路径、方向或尺寸。
        /// </summary>
        private static Body2 ExtrudeSegmentWithSingleRetry(
            SldWorks solidWorksApplication,
            List<object> offsetContour,
            Component2 previewComponent,
            double segmentWidth,
            Vector3d extrusionDirection,
            string groupKey,
            int segmentIndex)
        {
            var segmentBody = UseBendToPreviewFreeBusBar.ExtrudeContours(
                solidWorksApplication,
                offsetContour,
                previewComponent,
                segmentWidth,
                extrusionDirection);
            if (segmentBody != null)
            {
                return segmentBody;
            }

            FaceToWallPreviewTrace.Warning(
                "立弯预览实体-拉伸重试",
                "组=" + (groupKey ?? "未命名") +
                "，分段=" + (segmentIndex + 1) +
                "，首次 CreateExtrudedBody 返回空，使用同一轮廓重试一次。");
            return UseBendToPreviewFreeBusBar.ExtrudeContours(
                solidWorksApplication,
                offsetContour,
                previewComponent,
                segmentWidth,
                extrusionDirection);
        }

        private static void ResolveSegmentDirections(
            IReadOnlyList<Point3d> segmentPoints,
            Vector3d lastBarVector,
            bool isPlanarBendSegment,
            int segmentIndex,
            out Vector3d segmentWidthDirection,
            out Vector3d segmentBarDirection)
        {
            segmentWidthDirection = segmentPoints.Count > 2
                ? new Plane3d(
                    segmentPoints[0],
                    segmentPoints[1],
                    segmentPoints[2]).Normal
                : null;
            if (segmentIndex == 0 && !isPlanarBendSegment)
            {
                segmentBarDirection = lastBarVector.Normalized;
                var calculatedWidthDirection = segmentBarDirection.Cross(
                    new Vector3d(segmentPoints[0], segmentPoints[1]));
                if (segmentPoints.Count == 2)
                {
                    segmentWidthDirection = calculatedWidthDirection;
                }
                else if (!lastBarVector.Normalized.EqualsToTol(
                             calculatedWidthDirection.Normalized,
                             0.1d))
                {
                    segmentWidthDirection = calculatedWidthDirection.Negate();
                }
            }
            else
            {
                if (segmentPoints.Count == 2)
                {
                    segmentWidthDirection = lastBarVector.Normalized;
                }
                else if (!lastBarVector.Normalized.EqualsToTol(
                             segmentWidthDirection.Normalized,
                             0.1d))
                {
                    segmentWidthDirection = segmentWidthDirection.Negate();
                }
                segmentBarDirection = segmentWidthDirection.Cross(
                    new Vector3d(segmentPoints[0], segmentPoints[1]));
            }

            if (!IsUsableVector(segmentWidthDirection) ||
                !IsUsableVector(segmentBarDirection))
            {
                throw new InvalidOperationException(
                    "立弯分段的排宽或排厚方向无效。");
            }
        }

        private static List<Body2> MergeSegmentBodies(
            IReadOnlyList<Body2> segmentBodies)
        {
            var disconnectedBodies = new List<Body2>();
            Body2 mergedBody = null;
            foreach (var segmentBody in segmentBodies)
            {
                if (mergedBody == null)
                {
                    mergedBody = segmentBody;
                    continue;
                }

                var additionResults = UseBendToPreviewFreeBusBar
                    .Bady2Operationgs2(mergedBody, segmentBody, "A");
                if (additionResults.Count == 1)
                {
                    mergedBody = additionResults[0];
                }
                else if (additionResults.Count > 1)
                {
                    disconnectedBodies.AddRange(additionResults);
                    mergedBody = additionResults[additionResults.Count - 1];
                    disconnectedBodies.Remove(mergedBody);
                }
                else
                {
                    disconnectedBodies.Add(segmentBody);
                }
            }
            if (mergedBody != null)
            {
                disconnectedBodies.Add(mergedBody);
            }
            if (disconnectedBodies.Count == 0)
            {
                throw new InvalidOperationException(
                    "立弯分段没有形成可显示的预览 Body。");
            }

            return disconnectedBodies;
        }

        private static void RemoveRedundantSegmentPoints(
            IList<Point3d> segmentPoints)
        {
            for (var pointIndex = 0;
                 pointIndex < segmentPoints.Count - 1;
                 pointIndex++)
            {
                if (segmentPoints[pointIndex].DistanceTo(
                        segmentPoints[pointIndex + 1]) < 0.1d)
                {
                    segmentPoints.RemoveAt(pointIndex + 1);
                    pointIndex--;
                }
            }
            for (var pointIndex = 0;
                 pointIndex < segmentPoints.Count - 2;
                 pointIndex++)
            {
                if (segmentPoints[pointIndex].DistanceTo(new Line3d(
                        segmentPoints[pointIndex + 1],
                        segmentPoints[pointIndex + 2])) < 0.1d)
                {
                    segmentPoints.RemoveAt(pointIndex + 1);
                    pointIndex--;
                }
            }
        }

        private static void ValidateSegmentDimensions(
            double segmentWidth,
            double segmentThickness,
            double bendingRadius,
            int segmentIndex)
        {
            if (!IsFinite(segmentWidth) || !IsFinite(segmentThickness) ||
                !IsFinite(bendingRadius) || segmentWidth <= 0d ||
                segmentThickness <= 0d || bendingRadius < 0d)
            {
                throw new InvalidOperationException(
                    "立弯第" + (segmentIndex + 1) +
                    "段规格或折弯半径无效。");
            }
        }

        private static bool IsUsableVector(Vector3d vector)
        {
            return vector != null && IsFinite(vector.X) &&
                   IsFinite(vector.Y) && IsFinite(vector.Z) &&
                   vector.Norm > 0.000001d;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
