using CKBusbarBridge.Helpers;
using CKBusbarBridge.models;
using CKBusbarBridge.Models;
using CKBusbarBridgePreview;
using GeometRi;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 单个物理基架在三维预览中的中心、局部轴及绝缘子中心。
    /// </summary>
    internal sealed class FaceToFaceFramePreviewPose
    {
        internal FaceToFaceFramePreviewPose(
            FrameItem frame,
            Point3d centerPoint,
            Vector3d lengthVector,
            Vector3d widthVector,
            Vector3d insulatorPositionVector,
            Vector3d insulatorPhaseVector,
            double insulatorReferenceLength,
            double insulatorPhaseSpan,
            IReadOnlyList<Point3d> insulatorCenters)
        {
            Frame = frame ?? throw new ArgumentNullException(nameof(frame));
            CenterPoint = centerPoint ?? throw new ArgumentNullException(nameof(centerPoint));
            LengthVector = lengthVector ?? throw new ArgumentNullException(nameof(lengthVector));
            WidthVector = widthVector ?? throw new ArgumentNullException(nameof(widthVector));
            InsulatorPositionVector = insulatorPositionVector
                ?? throw new ArgumentNullException(nameof(insulatorPositionVector));
            InsulatorPhaseVector = insulatorPhaseVector
                ?? throw new ArgumentNullException(nameof(insulatorPhaseVector));
            InsulatorReferenceLength = insulatorReferenceLength;
            InsulatorPhaseSpan = insulatorPhaseSpan;
            InsulatorCenters = insulatorCenters ?? throw new ArgumentNullException(nameof(insulatorCenters));
        }

        internal FrameItem Frame { get; }

        internal Point3d CenterPoint { get; }

        internal Vector3d LengthVector { get; }

        internal Vector3d WidthVector { get; }

        /// <summary>
        /// 绝缘子位置值递增的物理方向，起点固定对应大偏移模板的“进1/出2”参考边。
        /// </summary>
        internal Vector3d InsulatorPositionVector { get; }

        internal Vector3d InsulatorPhaseVector { get; }

        internal double InsulatorReferenceLength { get; }

        internal double InsulatorPhaseSpan { get; }

        internal IReadOnlyList<Point3d> InsulatorCenters { get; }
    }

    /// <summary>
    /// 面对面大偏移全部物理基架的三维位姿结果。
    /// </summary>
    internal sealed class FaceToFaceFramePreviewPoseLayout
    {
        internal FaceToFaceFramePreviewPoseLayout(
            IReadOnlyList<FaceToFaceFramePreviewPose> items)
        {
            Items = items ?? throw new ArgumentNullException(nameof(items));
        }

        internal IReadOnlyList<FaceToFaceFramePreviewPose> Items { get; }
    }

    /// <summary>
    /// 计算面对面大偏移基架的真实转弯路径；母排仍使用原逻辑段直线算法。
    /// </summary>
    internal static class FaceToFaceFramePreviewPosePlanner
    {
        private const double Tolerance = 0.001d;

        internal static FaceToFaceFramePreviewPoseLayout Create(
            IReadOnlyList<FrameItem> frames,
            double signedEccentricity,
            Point3d startPoint,
            Vector3d originalLengthVector,
            Vector3d originalWidthVector)
        {
            if (frames == null)
            {
                throw new ArgumentNullException(nameof(frames));
            }

            if (startPoint == null)
            {
                throw new ArgumentNullException(nameof(startPoint));
            }

            ValidateEccentricity(signedEccentricity);
            ValidateVector(originalLengthVector, nameof(originalLengthVector));
            ValidateVector(originalWidthVector, nameof(originalWidthVector));

            // 逻辑投影会同时验证大偏移组连续、角色顺序和共享长度约束。
            FaceToFaceFrameLogicalProjection.Project(frames);
            var bCornerIndex = FindSingleRole(frames, FaceToFaceLargeOffsetFrameRole.BSideCorner);
            var aCornerIndex = FindSingleRole(frames, FaceToFaceLargeOffsetFrameRole.ASideCorner);
            if (aCornerIndex <= bCornerIndex + 1)
            {
                throw new InvalidOperationException("大偏移组必须包含至少一个连接基架。");
            }

            var lengthVector = originalLengthVector.Normalized;
            var widthVector = originalWidthVector.Normalized;
            ValidatePerpendicular(lengthVector, widthVector);
            var offsetSign = signedEccentricity > 0d ? 1d : -1d;
            var offsetVector = offsetSign > 0d ? widthVector.Copy() : widthVector.Negate().Normalized;
            var connectorWidthVector = offsetSign > 0d
                ? lengthVector.Negate().Normalized
                : lengthVector.Copy();

            var bCorner = frames[bCornerIndex];
            var aCorner = frames[aCornerIndex];
            var connectors = frames.Skip(bCornerIndex + 1)
                .Take(aCornerIndex - bCornerIndex - 1)
                .ToList();
            if (!FaceToFaceFrameLogicalProjection.TryValidateOffsetLength(
                frames, signedEccentricity, out var validationMessage))
            {
                throw new InvalidOperationException(validationMessage);
            }

            var poses = new List<FaceToFaceFramePreviewPose>(frames.Count);
            var chainCursor = 0d;
            for (var index = 0; index < bCornerIndex; index++)
            {
                var frame = frames[index];
                var center = startPoint.Move(lengthVector, chainCursor + frame.Length / 2d);
                poses.Add(CreatePose(frame, center, lengthVector, widthVector));
                chainCursor += frame.Length;
            }

            // 两个拐角基架占用同一个逻辑段长度，不能把三个物理件重复计入母排总长。
            var groupCenter = startPoint.Move(lengthVector, chainCursor + bCorner.Length / 2d);

            // 028 拐角模板的绝缘子位置始终以“进1/出2”所在物理边为零点。
            // 右偏时沿原长度轴计量；左偏时沿原宽度轴计量，并同步旋转相位排列轴。
            var bCornerPositionVector = offsetSign > 0d
                ? lengthVector.Copy()
                : widthVector.Copy();
            var bCornerPhaseVector = offsetSign > 0d
                ? widthVector.Copy()
                : lengthVector.Negate().Normalized;
            var cornerReferenceLength = offsetSign > 0d
                ? bCorner.Length
                : bCorner.Width;
            var cornerPhaseSpan = offsetSign > 0d
                ? bCorner.Width
                : bCorner.Length;
            poses.Add(CreatePose(
                bCorner,
                groupCenter,
                lengthVector,
                widthVector,
                bCornerPositionVector,
                bCornerPhaseVector,
                cornerReferenceLength,
                cornerPhaseSpan));

            var offsetCursor = bCorner.Width / 2d;
            foreach (var connector in connectors)
            {
                var connectorCenter = groupCenter.Move(
                    offsetVector,
                    offsetCursor + connector.Length / 2d);
                poses.Add(CreatePose(
                    connector,
                    connectorCenter,
                    offsetVector,
                    connectorWidthVector));
                offsetCursor += connector.Length;
            }

            var aCornerCenter = groupCenter.Move(
                offsetVector,
                offsetCursor + aCorner.Width / 2d);
            var aCornerPositionVector = offsetSign > 0d
                ? lengthVector.Negate().Normalized
                : widthVector.Negate().Normalized;
            var aCornerPhaseVector = offsetSign > 0d
                ? widthVector.Negate().Normalized
                : lengthVector.Copy();
            poses.Add(CreatePose(
                aCorner,
                aCornerCenter,
                lengthVector,
                widthVector,
                aCornerPositionVector,
                aCornerPhaseVector,
                offsetSign > 0d ? aCorner.Length : aCorner.Width,
                offsetSign > 0d ? aCorner.Width : aCorner.Length));
            chainCursor += bCorner.Length;

            for (var index = aCornerIndex + 1; index < frames.Count; index++)
            {
                var frame = frames[index];
                var center = startPoint
                    .Move(lengthVector, chainCursor + frame.Length / 2d)
                    .Move(offsetVector, Math.Abs(signedEccentricity));
                poses.Add(CreatePose(frame, center, lengthVector, widthVector));
                chainCursor += frame.Length;
            }

            return new FaceToFaceFramePreviewPoseLayout(poses.AsReadOnly());
        }

        private static FaceToFaceFramePreviewPose CreatePose(
            FrameItem frame,
            Point3d centerPoint,
            Vector3d lengthVector,
            Vector3d widthVector)
        {
            // 常规基架仍保留原反装语义；大偏移组三件只依靠局部轴表达真实转弯。
            var isReversed = !frame.IsLargeOffsetMember && frame.IsReversed;
            var insulatorPositionVector = isReversed
                ? lengthVector.Negate().Normalized
                : lengthVector.Copy();
            var insulatorPhaseVector = isReversed
                ? widthVector.Negate().Normalized
                : widthVector.Copy();
            return CreatePose(
                frame,
                centerPoint,
                lengthVector,
                widthVector,
                insulatorPositionVector,
                insulatorPhaseVector,
                frame.Length,
                frame.Width);
        }

        private static FaceToFaceFramePreviewPose CreatePose(
            FrameItem frame,
            Point3d centerPoint,
            Vector3d lengthVector,
            Vector3d widthVector,
            Vector3d insulatorPositionVector,
            Vector3d insulatorPhaseVector,
            double insulatorReferenceLength,
            double insulatorPhaseSpan)
        {
            var centers = ParseInsulatorPositions(frame, insulatorReferenceLength)
                .Select(position => centerPoint.Move(
                    insulatorPositionVector,
                    position - insulatorReferenceLength / 2d))
                .ToList()
                .AsReadOnly();
            return new FaceToFaceFramePreviewPose(
                frame,
                centerPoint,
                lengthVector.Copy(),
                widthVector.Copy(),
                insulatorPositionVector.Copy(),
                insulatorPhaseVector.Copy(),
                insulatorReferenceLength,
                insulatorPhaseSpan,
                centers);
        }

        private static IReadOnlyList<double> ParseInsulatorPositions(
            FrameItem frame,
            double referenceLength)
        {
            if (!int.TryParse(
                    frame.InsulatorCountText,
                    NumberStyles.Integer,
                    CultureInfo.CurrentCulture,
                    out var count)
                || count <= 0)
            {
                return Array.Empty<double>();
            }

            return FrameItem.ParseInsulatorPositions(frame.InsulatorPosition)
                .Take(count)
                .Where(position => position >= 0d && position <= referenceLength)
                .ToList()
                .AsReadOnly();
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
                throw new InvalidOperationException($"大偏移预览必须且只能包含一个 {role}。");
            }

            return indexes[0];
        }

        private static void ValidateEccentricity(double signedEccentricity)
        {
            if (signedEccentricity == 0d
                || double.IsNaN(signedEccentricity)
                || double.IsInfinity(signedEccentricity))
            {
                throw new ArgumentOutOfRangeException(nameof(signedEccentricity));
            }
        }

        private static void ValidateVector(Vector3d vector, string parameterName)
        {
            if (vector == null
                || double.IsNaN(vector.X)
                || double.IsNaN(vector.Y)
                || double.IsNaN(vector.Z)
                || double.IsInfinity(vector.X)
                || double.IsInfinity(vector.Y)
                || double.IsInfinity(vector.Z)
                || vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z <= Tolerance)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }

        private static void ValidatePerpendicular(Vector3d lengthVector, Vector3d widthVector)
        {
            var dot = lengthVector.X * widthVector.X
                + lengthVector.Y * widthVector.Y
                + lengthVector.Z * widthVector.Z;
            if (Math.Abs(dot) > Tolerance)
            {
                throw new ArgumentException("基架原长度轴和宽度轴必须互相垂直。");
            }
        }
    }
}
