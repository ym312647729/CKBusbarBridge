using GeometRi;
using System;
using System.Collections.Generic;

namespace RouteLinkBar.Algorithm.Geometry
{
    public enum LinkageRouteSolidRole
    {
        ConductiveBusbar = 0,
        FixedTerminalBusbar = 1,
        GeneratedBusbar = 2,
        InputObstacle = 3,
        ForbiddenTerminalRegion = 4,
        LapClearanceEnvelope = 5
    }

    public sealed class LinkageRouteSolid
    {
        private Point3d center;
        private Vector3d lengthDirection;
        private Vector3d widthDirection;
        private Vector3d thicknessDirection;
        private double halfLength;
        private double halfWidth;
        private double halfThickness;
        private IReadOnlyList<Point3d> cachedVertices;
        private IReadOnlyList<Vector3d> cachedAxes;
        private double[] cachedAxisAlignedExtents;

        // 同一预定柜顶附件的静态避让视图。自身真实实体仍由完整生成实体门禁检查。
        internal bool IsPreparedCabinetAccessory { get; set; }
        public string GroupKey { get; internal set; }
        /// <summary>
        /// 绝缘距离排除键。GroupKey 标识独立逻辑组，本字段标识电气相别；
        /// 同相不同逻辑组只免除绝缘距离，仍保留各自的实体身份。
        /// </summary>
        public string ClearanceGroupKey { get; internal set; }
        /// <summary>
        /// 实体在路径约束和绝缘计算中的业务角色。输入障碍物和终端禁区
        /// 只限制实体进入，不属于带电铜排，不能参加相间绝缘距离统计。
        /// </summary>
        public LinkageRouteSolidRole SolidRole { get; internal set; }
        public bool IsLapClearanceEnvelope =>
            SolidRole == LinkageRouteSolidRole.LapClearanceEnvelope;
        /// <summary>仅用于内部诊断定位，不参与距离、相序或去重计算。</summary>
        public string SolidSourceKey { get; internal set; }
        public int SegmentIndex { get; internal set; }
        public Point3d Center
        {
            get => center;
            internal set
            {
                center = value;
                InvalidateDerivedGeometry();
            }
        }

        public Vector3d LengthDirection
        {
            get => lengthDirection;
            internal set
            {
                lengthDirection = value;
                InvalidateDerivedGeometry();
            }
        }

        public Vector3d WidthDirection
        {
            get => widthDirection;
            internal set
            {
                widthDirection = value;
                InvalidateDerivedGeometry();
            }
        }

        public Vector3d ThicknessDirection
        {
            get => thicknessDirection;
            internal set
            {
                thicknessDirection = value;
                InvalidateDerivedGeometry();
            }
        }

        public double HalfLength
        {
            get => halfLength;
            internal set
            {
                halfLength = value;
                InvalidateDerivedGeometry();
            }
        }

        public double HalfWidth
        {
            get => halfWidth;
            internal set
            {
                halfWidth = value;
                InvalidateDerivedGeometry();
            }
        }

        public double HalfThickness
        {
            get => halfThickness;
            internal set
            {
                halfThickness = value;
                InvalidateDerivedGeometry();
            }
        }
        /// <summary>
        /// 表示所有物理始端或所有柜顶在不可伸出侧形成的整体禁入区域。
        /// 它不参与预览，也不叠加绝缘距离；只禁止生成排正体积进入，
        /// 边界接触以及位于边界外的任意非负距离均合法。
        /// </summary>
        public bool IsForbiddenTerminalRegion { get; internal set; }

        public IReadOnlyList<Point3d> GetVertices()
        {
            if (cachedVertices != null)
            {
                return cachedVertices;
            }

            var solidVertices = new List<Point3d>(8);
            for (var lengthSign = -1; lengthSign <= 1; lengthSign += 2)
            {
                for (var widthSign = -1; widthSign <= 1; widthSign += 2)
                {
                    for (var thicknessSign = -1; thicknessSign <= 1; thicknessSign += 2)
                    {
                        solidVertices.Add(LinkageRouteSolidGeometry.Translate(
                            Center,
                            LengthDirection,
                            lengthSign * HalfLength,
                            WidthDirection,
                            widthSign * HalfWidth,
                            ThicknessDirection,
                            thicknessSign * HalfThickness));
                    }
                }
            }

            cachedVertices = solidVertices.AsReadOnly();
            return cachedVertices;
        }

        internal IReadOnlyList<Vector3d> GetAxes()
        {
            if (cachedAxes == null)
            {
                cachedAxes = new[]
                {
                    LengthDirection,
                    WidthDirection,
                    ThicknessDirection
                };
            }

            return cachedAxes;
        }

        internal double[] GetAxisAlignedExtents()
        {
            if (cachedAxisAlignedExtents == null)
            {
                cachedAxisAlignedExtents = new[]
                {
                    Math.Abs(LengthDirection.X) * HalfLength +
                    Math.Abs(WidthDirection.X) * HalfWidth +
                    Math.Abs(ThicknessDirection.X) * HalfThickness,
                    Math.Abs(LengthDirection.Y) * HalfLength +
                    Math.Abs(WidthDirection.Y) * HalfWidth +
                    Math.Abs(ThicknessDirection.Y) * HalfThickness,
                    Math.Abs(LengthDirection.Z) * HalfLength +
                    Math.Abs(WidthDirection.Z) * HalfWidth +
                    Math.Abs(ThicknessDirection.Z) * HalfThickness
                };
            }

            return cachedAxisAlignedExtents;
        }

        private void InvalidateDerivedGeometry()
        {
            cachedVertices = null;
            cachedAxes = null;
            cachedAxisAlignedExtents = null;
        }
    }

    public static class LinkageRouteSolidGeometry
    {
        private const double DirectionTolerance =
            LinkageRouteGeometryTolerances.ExactDirection;

        public static LinkageRouteSolid CreateSegmentSolid(
            string groupKey,
            Point3d segmentStart,
            Point3d segmentEnd,
            Vector3d widthDirection,
            Vector3d thicknessDirection,
            double busbarWidth,
            double stackEnvelopeThickness,
            int segmentIndex = -1,
            string clearanceGroupKey = null,
            LinkageRouteSolidRole solidRole =
                LinkageRouteSolidRole.ConductiveBusbar,
            string solidSourceKey = null)
        {
            if (segmentStart == null || segmentEnd == null)
            {
                throw new ArgumentNullException(nameof(segmentStart));
            }

            if (busbarWidth <= 0d || stackEnvelopeThickness <= 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(busbarWidth));
            }

            var lengthVector = new Vector3d(segmentStart, segmentEnd);
            if (lengthVector.Norm <= DirectionTolerance)
            {
                throw new ArgumentException("扫描实体的路径段长度必须大于零。", nameof(segmentEnd));
            }

            var normalizedLengthDirection = lengthVector.Normalized;
            var normalizedWidthDirection = Orthogonalize(widthDirection, normalizedLengthDirection);
            if (normalizedWidthDirection == null)
            {
                throw new ArgumentException("排宽方向必须与路径段正交。", nameof(widthDirection));
            }

            var resolvedThicknessDirection = normalizedLengthDirection.Cross(normalizedWidthDirection);
            if (resolvedThicknessDirection == null || resolvedThicknessDirection.Norm <= DirectionTolerance)
            {
                throw new ArgumentException("无法建立扫描实体的排厚方向。", nameof(thicknessDirection));
            }

            resolvedThicknessDirection = resolvedThicknessDirection.Normalized;
            if (thicknessDirection != null && thicknessDirection.Norm > DirectionTolerance &&
                resolvedThicknessDirection.Dot(thicknessDirection.Normalized) < 0d)
            {
                resolvedThicknessDirection = Negate(resolvedThicknessDirection);
            }

            return new LinkageRouteSolid
            {
                GroupKey = groupKey,
                ClearanceGroupKey = string.IsNullOrWhiteSpace(clearanceGroupKey)
                    ? groupKey
                    : clearanceGroupKey,
                SolidRole = solidRole,
                SolidSourceKey = solidSourceKey,
                SegmentIndex = segmentIndex,
                Center = new Point3d(
                    (segmentStart.X + segmentEnd.X) / 2d,
                    (segmentStart.Y + segmentEnd.Y) / 2d,
                    (segmentStart.Z + segmentEnd.Z) / 2d),
                LengthDirection = normalizedLengthDirection,
                WidthDirection = normalizedWidthDirection,
                ThicknessDirection = resolvedThicknessDirection,
                HalfLength = lengthVector.Norm / 2d,
                HalfWidth = busbarWidth / 2d,
                HalfThickness = stackEnvelopeThickness / 2d
            };
        }

        public static LinkageRouteSolid CreateAxisAlignedBoxSolid(
            string groupKey,
            Point3d minimumPoint,
            Point3d maximumPoint,
            int segmentIndex = -1,
            string clearanceGroupKey = null,
            LinkageRouteSolidRole solidRole =
                LinkageRouteSolidRole.ConductiveBusbar,
            string solidSourceKey = null)
        {
            if (minimumPoint == null || maximumPoint == null ||
                maximumPoint.X <= minimumPoint.X || maximumPoint.Y <= minimumPoint.Y ||
                maximumPoint.Z <= minimumPoint.Z)
            {
                throw new ArgumentException("障碍物包围盒必须具有有效的三维尺寸。" );
            }

            return new LinkageRouteSolid
            {
                GroupKey = groupKey,
                ClearanceGroupKey = string.IsNullOrWhiteSpace(clearanceGroupKey)
                    ? groupKey
                    : clearanceGroupKey,
                SolidRole = solidRole,
                SolidSourceKey = solidSourceKey,
                SegmentIndex = segmentIndex,
                Center = new Point3d(
                    (minimumPoint.X + maximumPoint.X) / 2d,
                    (minimumPoint.Y + maximumPoint.Y) / 2d,
                    (minimumPoint.Z + maximumPoint.Z) / 2d),
                LengthDirection = new Vector3d(1d, 0d, 0d),
                WidthDirection = new Vector3d(0d, 1d, 0d),
                ThicknessDirection = new Vector3d(0d, 0d, 1d),
                HalfLength = (maximumPoint.X - minimumPoint.X) / 2d,
                HalfWidth = (maximumPoint.Y - minimumPoint.Y) / 2d,
                HalfThickness = (maximumPoint.Z - minimumPoint.Z) / 2d
            };
        }

        public static LinkageRouteSolid CreateBendSweepSolid(
            string groupKey,
            Point3d bendCenter,
            Vector3d incomingDirection,
            Vector3d outgoingDirection,
            Vector3d widthDirection,
            double stackBendAllowance,
            double busbarWidth,
            int segmentIndex = -1,
            string clearanceGroupKey = null,
            LinkageRouteSolidRole solidRole =
                LinkageRouteSolidRole.ConductiveBusbar,
            string solidSourceKey = null)
        {
            if (bendCenter == null || incomingDirection == null || outgoingDirection == null ||
                widthDirection == null || stackBendAllowance <= 0d || busbarWidth <= 0d)
            {
                throw new ArgumentException("折弯扫描实体参数无效。");
            }

            var normalizedIncomingDirection = incomingDirection.Normalized;
            var normalizedOutgoingDirection = outgoingDirection.Normalized;
            var normalizedWidthDirection = widthDirection.Normalized;
            if (Math.Abs(normalizedIncomingDirection.Dot(normalizedWidthDirection)) > 1e-6d ||
                Math.Abs(normalizedOutgoingDirection.Dot(normalizedWidthDirection)) > 1e-6d ||
                normalizedIncomingDirection.Cross(normalizedOutgoingDirection).Norm <= 1e-6d)
            {
                throw new ArgumentException("折弯扫描实体的相邻段必须位于同一有效平面且不能共线。");
            }

            var inPlaneNormalDirection = normalizedWidthDirection
                .Cross(normalizedIncomingDirection).Normalized;

            return new LinkageRouteSolid
            {
                GroupKey = groupKey,
                ClearanceGroupKey = string.IsNullOrWhiteSpace(clearanceGroupKey)
                    ? groupKey
                    : clearanceGroupKey,
                SolidRole = solidRole,
                SolidSourceKey = solidSourceKey,
                SegmentIndex = segmentIndex,
                Center = new Point3d(bendCenter.X, bendCenter.Y, bendCenter.Z),
                LengthDirection = normalizedIncomingDirection,
                WidthDirection = inPlaneNormalDirection,
                ThicknessDirection = normalizedWidthDirection,
                HalfLength = stackBendAllowance,
                HalfWidth = stackBendAllowance,
                HalfThickness = busbarWidth / 2d
            };
        }

        internal static Point3d Translate(
            Point3d sourcePoint,
            Vector3d firstDirection,
            double firstDistance,
            Vector3d secondDirection,
            double secondDistance,
            Vector3d thirdDirection,
            double thirdDistance)
        {
            return new Point3d(
                sourcePoint.X + firstDirection.X * firstDistance +
                secondDirection.X * secondDistance + thirdDirection.X * thirdDistance,
                sourcePoint.Y + firstDirection.Y * firstDistance +
                secondDirection.Y * secondDistance + thirdDirection.Y * thirdDistance,
                sourcePoint.Z + firstDirection.Z * firstDistance +
                secondDirection.Z * secondDistance + thirdDirection.Z * thirdDistance);
        }

        internal static Point3d Translate(Point3d sourcePoint, Vector3d direction, double distance)
        {
            return new Point3d(
                sourcePoint.X + direction.X * distance,
                sourcePoint.Y + direction.Y * distance,
                sourcePoint.Z + direction.Z * distance);
        }

        internal static Vector3d Negate(Vector3d sourceDirection)
        {
            return new Vector3d(-sourceDirection.X, -sourceDirection.Y, -sourceDirection.Z);
        }

        private static Vector3d Orthogonalize(Vector3d sourceDirection, Vector3d normalDirection)
        {
            if (sourceDirection == null || sourceDirection.Norm <= DirectionTolerance)
            {
                return null;
            }

            var normalizedSourceDirection = sourceDirection.Normalized;
            var normalProjection = normalizedSourceDirection.Dot(normalDirection);
            var orthogonalDirection = new Vector3d(
                normalizedSourceDirection.X - normalDirection.X * normalProjection,
                normalizedSourceDirection.Y - normalDirection.Y * normalProjection,
                normalizedSourceDirection.Z - normalDirection.Z * normalProjection);
            return orthogonalDirection.Norm <= DirectionTolerance ? null : orthogonalDirection.Normalized;
        }
    }
}
