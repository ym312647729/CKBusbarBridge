using CKBusbarBridge.models;
using CKBusbarBridge.Models;
using GeometRi;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 俯视图中单个绝缘子的圆形符号，坐标相对所属绝缘子支架左上角。
    /// </summary>
    internal sealed class FaceToFaceFrameTopViewCirclePlan
    {
        internal FaceToFaceFrameTopViewCirclePlan(double left, double top, double diameter)
        {
            Left = left;
            Top = top;
            Diameter = diameter;
        }

        internal double Left { get; }

        internal double Top { get; }

        internal double Diameter { get; }
    }

    /// <summary>
    /// 俯视图中的三相绝缘子支架，坐标相对所属基架左上角。
    /// </summary>
    internal sealed class FaceToFaceFrameTopViewMarkerPlan
    {
        internal FaceToFaceFrameTopViewMarkerPlan(
            double left,
            double top,
            double width,
            double height,
            IReadOnlyList<FaceToFaceFrameTopViewCirclePlan> circles)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            Circles = circles;
        }

        internal double Left { get; }

        internal double Top { get; }

        internal double Width { get; }

        internal double Height { get; }

        internal IReadOnlyList<FaceToFaceFrameTopViewCirclePlan> Circles { get; }
    }

    /// <summary>
    /// 单个物理基架的俯视图矩形和内部绝缘子位置。
    /// </summary>
    internal sealed class FaceToFaceFrameTopViewItemPlan
    {
        internal FaceToFaceFrameTopViewItemPlan(
            FrameItem frame,
            double left,
            double top,
            double width,
            double height,
            IReadOnlyList<FaceToFaceFrameTopViewMarkerPlan> markers)
        {
            Frame = frame;
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            Markers = markers;
        }

        internal FrameItem Frame { get; }

        internal double Left { get; }

        internal double Top { get; }

        internal double Width { get; }

        internal double Height { get; }

        internal IReadOnlyList<FaceToFaceFrameTopViewMarkerPlan> Markers { get; }
    }

    /// <summary>
    /// 全部物理基架共享同一缩放比例的俯视布局结果。
    /// </summary>
    internal sealed class FaceToFaceFrameTopViewLayout
    {
        internal FaceToFaceFrameTopViewLayout(
            double width,
            double height,
            double scale,
            double busbarLeft,
            double busbarWidth,
            IReadOnlyList<FaceToFaceFrameTopViewItemPlan> items)
        {
            Width = width;
            Height = height;
            Scale = scale;
            BusbarLeft = busbarLeft;
            BusbarWidth = busbarWidth;
            Items = items;
        }

        internal double Width { get; }

        internal double Height { get; }

        internal double Scale { get; }

        /// <summary>
        /// 铜排预览在俯视视口中的水平起点；仅共享整体投影范围，不约束内部断点。
        /// </summary>
        internal double BusbarLeft { get; }

        internal double BusbarWidth { get; }

        internal IReadOnlyList<FaceToFaceFrameTopViewItemPlan> Items { get; }
    }

    /// <summary>
    /// 计算面对面大偏移基架的二维俯视路径；B 侧固定在左，A 侧按偏心符号上下偏移。
    /// </summary>
    internal static class FaceToFaceFrameTopViewPlanner
    {
        private const double Margin = 20d;
        // 1000 mm 基架至少显示为 150 DIP；长链通过滚动查看，不牺牲轮廓和标记可读性。
        private const double MinimumPreviewScale = 0.15d;
        // 收窄俯视绝缘子支架，减少对基架轮廓和相邻标记的遮挡。
        private const double SupportThicknessRatio = 1d / 8d;
        private const double InsulatorDiameterRatio = 0.7d;
        private const int PhaseCount = 3;

        internal static FaceToFaceFrameTopViewLayout Create(
            IReadOnlyList<FrameItem> frames,
            double signedEccentricity,
            double viewportWidth,
            double viewportHeight)
        {
            if (frames == null)
            {
                throw new ArgumentNullException(nameof(frames));
            }

            if (signedEccentricity == 0
                || double.IsNaN(signedEccentricity)
                || double.IsInfinity(signedEccentricity))
            {
                throw new ArgumentOutOfRangeException(nameof(signedEccentricity));
            }

            if (viewportWidth <= Margin * 2 || viewportHeight <= Margin * 2)
            {
                throw new ArgumentOutOfRangeException(nameof(viewportWidth), "俯视预览视口尺寸过小。");
            }

            // 投影器同时负责校验大偏移组连续性和共享尺寸约束。
            FaceToFaceFrameLogicalProjection.Project(frames);
            var bCornerIndex = FindSingleRole(frames, FaceToFaceLargeOffsetFrameRole.BSideCorner);
            var aCornerIndex = FindSingleRole(frames, FaceToFaceLargeOffsetFrameRole.ASideCorner);
            if (aCornerIndex <= bCornerIndex + 1)
            {
                throw new InvalidOperationException("大偏移组必须包含至少一个连接基架。");
            }

            // 编辑过程中允许暂未闭合，按当前连接段实长展示；生成入口仍校验目标柜间偏心值。
            var previewEccentricity = frames[bCornerIndex].Width / 2d
                + frames.Skip(bCornerIndex + 1).Take(aCornerIndex - bCornerIndex - 1).Sum(frame => frame.Length)
                + frames[aCornerIndex].Width / 2d;
            var poses = FaceToFaceFramePreviewPosePlanner.Create(
                frames,
                Math.Sign(signedEccentricity) * previewEccentricity,
                new Point3d(0, 0, 0),
                new Vector3d(1, 0, 0),
                new Vector3d(0, 1, 0));
            var poseByFrame = poses.Items.ToDictionary(pose => pose.Frame);
            var raw = poses.Items.ToDictionary(
                pose => pose.Frame,
                pose => pose.Frame.LargeOffsetRole == FaceToFaceLargeOffsetFrameRole.Connector
                    ? new RawRectangle(
                        pose.CenterPoint.X - pose.Frame.Width / 2d,
                        pose.CenterPoint.Y - pose.Frame.Length / 2d,
                        pose.Frame.Width,
                        pose.Frame.Length)
                    : new RawRectangle(
                        pose.CenterPoint.X - pose.Frame.Length / 2d,
                        pose.CenterPoint.Y - pose.Frame.Width / 2d,
                        pose.Frame.Length,
                        pose.Frame.Width));

            var minLeft = raw.Values.Min(rectangle => rectangle.Left);
            var minTop = raw.Values.Min(rectangle => rectangle.Top);
            var maxRight = raw.Values.Max(rectangle => rectangle.Left + rectangle.Width);
            var maxBottom = raw.Values.Max(rectangle => rectangle.Top + rectangle.Height);
            var rawWidth = maxRight - minLeft;
            var rawHeight = maxBottom - minTop;
            var fitScale = Math.Min(
                (viewportWidth - Margin * 2d) / rawWidth,
                (viewportHeight - Margin * 2d) / rawHeight);
            var scale = Math.Max(MinimumPreviewScale, fitScale);
            var contentWidth = Math.Max(viewportWidth, rawWidth * scale + Margin * 2d);
            var contentHeight = Math.Max(viewportHeight, rawHeight * scale + Margin * 2d);
            var offsetX = (contentWidth - rawWidth * scale) / 2d - minLeft * scale;
            var offsetY = (contentHeight - rawHeight * scale) / 2d - minTop * scale;

            var items = frames.Select(frame =>
            {
                var pose = poseByFrame[frame];
                var rectangle = raw[frame];
                return new FaceToFaceFrameTopViewItemPlan(
                    frame,
                    rectangle.Left * scale + offsetX,
                    rectangle.Top * scale + offsetY,
                    rectangle.Width * scale,
                    rectangle.Height * scale,
                    BuildMarkers(pose, rectangle, scale));
            }).ToList().AsReadOnly();

            return new FaceToFaceFrameTopViewLayout(
                contentWidth,
                contentHeight,
                scale,
                minLeft * scale + offsetX,
                rawWidth * scale,
                items);
        }

        private static int FindSingleRole(
            IReadOnlyList<FrameItem> frames,
            FaceToFaceLargeOffsetFrameRole role)
        {
            var indexes = frames
                .Select((frame, index) => new { frame, index })
                .Where(item => item.frame?.LargeOffsetRole == role)
                .Select(item => item.index)
                .ToList();
            if (indexes.Count != 1)
            {
                throw new InvalidOperationException($"大偏移俯视图必须且只能包含一个{role}。");
            }

            return indexes[0];
        }

        private static IReadOnlyList<FaceToFaceFrameTopViewMarkerPlan> BuildMarkers(
            FaceToFaceFramePreviewPose pose,
            RawRectangle rectangle,
            double scale)
        {
            if (pose.InsulatorCenters.Count == 0)
            {
                return Array.Empty<FaceToFaceFrameTopViewMarkerPlan>();
            }

            // 直接使用三维姿态规划器给出的“进1/出2”参考轴，保证俯视图与 3D 不会发生镜像分歧。
            var positionAlongHorizontal = Math.Abs(pose.InsulatorPositionVector.X)
                >= Math.Abs(pose.InsulatorPositionVector.Y);
            var markers = new List<FaceToFaceFrameTopViewMarkerPlan>();
            foreach (var center in pose.InsulatorCenters)
            {
                var centerLeft = (center.X - rectangle.Left) * scale;
                var centerTop = (center.Y - rectangle.Top) * scale;
                if (positionAlongHorizontal)
                {
                    markers.Add(BuildVerticalSupport(
                        centerLeft,
                        rectangle.Height * scale));
                }
                else
                {
                    markers.Add(BuildHorizontalSupport(
                        rectangle.Width * scale,
                        centerTop));
                }
            }

            return markers.AsReadOnly();
        }

        private static FaceToFaceFrameTopViewMarkerPlan BuildVerticalSupport(
            double centerLeft,
            double supportSpan)
        {
            var supportThickness = supportSpan * SupportThicknessRatio;
            var circleDiameter = supportThickness * InsulatorDiameterRatio;
            var circles = Enumerable.Range(1, PhaseCount)
                .Select(index => new FaceToFaceFrameTopViewCirclePlan(
                    (supportThickness - circleDiameter) / 2d,
                    supportSpan * index / (PhaseCount + 1d) - circleDiameter / 2d,
                    circleDiameter))
                .ToList()
                .AsReadOnly();

            // 水平基架的绝缘子支架横跨基架宽度，因此俯视图中呈竖向三圆排列。
            return new FaceToFaceFrameTopViewMarkerPlan(
                centerLeft - supportThickness / 2d,
                0d,
                supportThickness,
                supportSpan,
                circles);
        }

        private static FaceToFaceFrameTopViewMarkerPlan BuildHorizontalSupport(
            double supportSpan,
            double centerTop)
        {
            var supportThickness = supportSpan * SupportThicknessRatio;
            var circleDiameter = supportThickness * InsulatorDiameterRatio;
            var circles = Enumerable.Range(1, PhaseCount)
                .Select(index => new FaceToFaceFrameTopViewCirclePlan(
                    supportSpan * index / (PhaseCount + 1d) - circleDiameter / 2d,
                    (supportThickness - circleDiameter) / 2d,
                    circleDiameter))
                .ToList()
                .AsReadOnly();

            // 连接基架旋转九十度后，支架和三相绝缘子同步改为横向排列。
            return new FaceToFaceFrameTopViewMarkerPlan(
                0d,
                centerTop - supportThickness / 2d,
                supportSpan,
                supportThickness,
                circles);
        }

        private sealed class RawRectangle
        {
            internal RawRectangle(double left, double top, double width, double height)
            {
                Left = left;
                Top = top;
                Width = width;
                Height = height;
            }

            internal double Left { get; }

            internal double Top { get; }

            internal double Width { get; }

            internal double Height { get; }
        }
    }
}
