using GeometRi;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridgePreview
{
    /// <summary>母线桥业务轴所采用的装配体坐标轴方向。</summary>
    public enum BusbarBridgeAxisDirection_e
    {
        PositiveX = 0,
        NegativeX = 1,
        PositiveY = 2,
        NegativeY = 3,
        PositiveZ = 4,
        NegativeZ = 5,
        Custom = 6
    }

    /// <summary>模型识别和预览使用的正交业务坐标。</summary>
    public sealed class BusbarBridgeAxisSetting
    {
        public BusbarBridgeAxisSetting(
            BusbarBridgeAxisDirection_e lengthDirection,
            BusbarBridgeAxisDirection_e heightDirection,
            BusbarBridgeAxisDirection_e widthDirection)
            : this(
                lengthDirection,
                heightDirection,
                widthDirection,
                ToVector(lengthDirection),
                ToVector(heightDirection),
                ToVector(widthDirection))
        {
        }

        private BusbarBridgeAxisSetting(
            BusbarBridgeAxisDirection_e lengthDirection,
            BusbarBridgeAxisDirection_e heightDirection,
            BusbarBridgeAxisDirection_e widthDirection,
            Vector3d lengthVector,
            Vector3d heightVector,
            Vector3d widthVector)
        {
            LengthDirection = lengthDirection;
            HeightDirection = heightDirection;
            WidthDirection = widthDirection;

            var absoluteAxes = new HashSet<int>();
            if (lengthDirection != BusbarBridgeAxisDirection_e.Custom &&
                heightDirection != BusbarBridgeAxisDirection_e.Custom &&
                widthDirection != BusbarBridgeAxisDirection_e.Custom)
            {
                absoluteAxes.Add(GetAbsoluteAxis(lengthDirection));
                absoluteAxes.Add(GetAbsoluteAxis(heightDirection));
                absoluteAxes.Add(GetAbsoluteAxis(widthDirection));
            }
            if (absoluteAxes.Count > 0 && absoluteAxes.Count != 3)
            {
                throw new ArgumentException("长度、高度、宽度必须分别使用三个不同的 XYZ 坐标轴。");
            }

            LengthVector = lengthVector;
            HeightVector = heightVector;
            WidthVector = widthVector;
        }

        /// <summary>
        /// Creates a business axis from already resolved model vectors.  The
        /// The vectors are resolved from the actual cabinet-top bars instead
        /// of being guessed from a fixed default.  They may be arbitrary
        /// orthonormal model directions; the enum properties are labels only
        /// when a vector is not aligned with global XYZ.
        /// </summary>
        internal static BusbarBridgeAxisSetting CreateFromVectors(
            Vector3d lengthVector,
            Vector3d heightVector,
            Vector3d widthVector)
        {
            var normalizedLengthVector = NormalizeResolvedVector(
                lengthVector,
                nameof(lengthVector));
            var normalizedHeightVector = NormalizeResolvedVector(
                heightVector,
                nameof(heightVector));
            var normalizedWidthVector = NormalizeResolvedVector(
                widthVector,
                nameof(widthVector));
            if (Math.Abs(normalizedLengthVector.Dot(normalizedHeightVector)) >
                    1e-4d ||
                Math.Abs(normalizedLengthVector.Dot(normalizedWidthVector)) >
                    1e-4d ||
                Math.Abs(normalizedHeightVector.Dot(normalizedWidthVector)) >
                    1e-4d)
            {
                throw new ArgumentException(
                    "解析出的长度、高度、宽度向量必须两两正交。");
            }

            var expectedLengthVector = normalizedHeightVector
                .Cross(normalizedWidthVector)
                .Normalized;
            if (Math.Abs(expectedLengthVector.Dot(normalizedLengthVector)) <
                    1d - 1e-4d)
            {
                throw new ArgumentException(
                    "解析出的长度向量必须与高度向量 Cross 宽度向量平行。");
            }

            return new BusbarBridgeAxisSetting(
                ResolveDirectionLabel(normalizedLengthVector),
                ResolveDirectionLabel(normalizedHeightVector),
                ResolveDirectionLabel(normalizedWidthVector),
                normalizedLengthVector,
                normalizedHeightVector,
                normalizedWidthVector);
        }

        private static Vector3d NormalizeResolvedVector(
            Vector3d sourceVector,
            string argumentName)
        {
            if (sourceVector == null || sourceVector.Norm < 1e-9d)
            {
                throw new ArgumentException(
                    "解析出的业务轴向量无效。",
                    argumentName);
            }

            return sourceVector.Normalized;
        }

        private static BusbarBridgeAxisDirection_e ResolveDirectionLabel(
            Vector3d sourceVector)
        {
            var axisCandidates = new[]
            {
                new { Positive = BusbarBridgeAxisDirection_e.PositiveX, Negative = BusbarBridgeAxisDirection_e.NegativeX, Value = Math.Abs(sourceVector.X), Component = sourceVector.X },
                new { Positive = BusbarBridgeAxisDirection_e.PositiveY, Negative = BusbarBridgeAxisDirection_e.NegativeY, Value = Math.Abs(sourceVector.Y), Component = sourceVector.Y },
                new { Positive = BusbarBridgeAxisDirection_e.PositiveZ, Negative = BusbarBridgeAxisDirection_e.NegativeZ, Value = Math.Abs(sourceVector.Z), Component = sourceVector.Z }
            };
            var bestAxis = axisCandidates.OrderByDescending(candidate =>
                candidate.Value).First();
            return bestAxis.Value >= 1d - 1e-4d
                ? bestAxis.Component >= 0d
                ? bestAxis.Positive
                : bestAxis.Negative
                : BusbarBridgeAxisDirection_e.Custom;
        }

        public BusbarBridgeAxisDirection_e LengthDirection { get; private set; }
        public BusbarBridgeAxisDirection_e HeightDirection { get; private set; }
        public BusbarBridgeAxisDirection_e WidthDirection { get; private set; }
        public Vector3d LengthVector { get; private set; }
        public Vector3d HeightVector { get; private set; }
        public Vector3d WidthVector { get; private set; }

        public static BusbarBridgeAxisSetting CreateDefault()
        {
            return new BusbarBridgeAxisSetting(
                BusbarBridgeAxisDirection_e.PositiveX,
                BusbarBridgeAxisDirection_e.PositiveY,
                BusbarBridgeAxisDirection_e.PositiveZ);
        }

        private static int GetAbsoluteAxis(BusbarBridgeAxisDirection_e direction)
        {
            switch (direction)
            {
                case BusbarBridgeAxisDirection_e.PositiveX:
                case BusbarBridgeAxisDirection_e.NegativeX:
                    return 0;
                case BusbarBridgeAxisDirection_e.PositiveY:
                case BusbarBridgeAxisDirection_e.NegativeY:
                    return 1;
                case BusbarBridgeAxisDirection_e.PositiveZ:
                case BusbarBridgeAxisDirection_e.NegativeZ:
                    return 2;
                default:
                    throw new ArgumentOutOfRangeException(nameof(direction));
            }
        }

        private static Vector3d ToVector(BusbarBridgeAxisDirection_e direction)
        {
            switch (direction)
            {
                case BusbarBridgeAxisDirection_e.PositiveX:
                    return new Vector3d(1d, 0d, 0d);
                case BusbarBridgeAxisDirection_e.NegativeX:
                    return new Vector3d(-1d, 0d, 0d);
                case BusbarBridgeAxisDirection_e.PositiveY:
                    return new Vector3d(0d, 1d, 0d);
                case BusbarBridgeAxisDirection_e.NegativeY:
                    return new Vector3d(0d, -1d, 0d);
                case BusbarBridgeAxisDirection_e.PositiveZ:
                    return new Vector3d(0d, 0d, 1d);
                case BusbarBridgeAxisDirection_e.NegativeZ:
                    return new Vector3d(0d, 0d, -1d);
                default:
                    throw new ArgumentOutOfRangeException(nameof(direction));
            }
        }
    }
}
