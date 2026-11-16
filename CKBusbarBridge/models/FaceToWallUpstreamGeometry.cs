using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridgePreview;
using GeometRi;
using CKBusbarBridge.Models;

namespace CKBusbarBridge.Prototype
{
    // 仅固定上游的值快照；中间基架及其绝缘子不进入铜排输入。
    public sealed class FaceToWallUpstreamGeometry
    {
        public BridgeCoordinateFrame Coordinates { get; }
        public SideToSideCabinetGeometry Cabinet { get; }
        // 孔口来自已核验的“基座安装坐标系”；名义显示中心经舍入后不能用于反算安装接缝。
        public double BaseInstallationSeamX { get; }
        public IReadOnlyList<SideToSideExtensionGeometry> WallConductors { get; }
        public IReadOnlyList<BridgeDesignObject> FixedObjects { get; }
        public IReadOnlyList<Point3d> WallOpeningCorners { get; }
        public string Position { get; }
        public string PreviewReferenceComponentName { get; }
        public int RootCount { get; }
        public double Clearance { get; }
        public double ModelClearance { get; }
        public double WallOpeningHeight { get; }
        public double OverlapLength { get; }
        public double WallCabinetEccentricity { get; }
        public double AppliedWallVerticalClearance { get; }
        public FaceToWallInstallationValues WallInstallation { get; }
        public bool IsPhaseSwitched { get; }
        public double TargetWallHeight => WallInstallation == null ? WallOpeningHeight - 2 * AppliedWallVerticalClearance : WallInstallation.FrameHeight(WallOpeningHeight);
        public double TargetWallWidth => WallOpeningCorners.Max(p => p.Z) - WallOpeningCorners.Min(p => p.Z) - (WallInstallation == null ? 0 : WallInstallation.Left + WallInstallation.Right);
        public double TargetWallCenterHeight => WallOpeningCorners.Average(p => p.Y) + (WallInstallation?.VerticalOffset ?? 0);
        public double TargetWallCenterAcross => WallOpeningCorners.Average(p => p.Z) + (WallInstallation?.AcrossOffset ?? 0);
        public double TargetWallEnd => WallOpeningCorners.Average(p => p.X) + (WallInstallation?.EntryDistance ?? 0);
        public double EffectiveWallEccentricity => WallInstallation == null ? WallCabinetEccentricity : Cabinet.Opening.Z - TargetWallCenterAcross;
        // 保留既有纯几何调用入口；生产反读走下一重载，显式传入总装原驱动变量。
        public FaceToWallUpstreamGeometry(BridgeCoordinateFrame coordinates,
            SideToSideCabinetGeometry cabinet, IEnumerable<SideToSideExtensionGeometry> wallConductors,
            IEnumerable<BridgeDesignObject> fixedObjects, IEnumerable<Point3d> wallOpeningCorners,
            string position, string previewReferenceComponentName, int rootCount, double clearance, double overlapLength)
            : this(coordinates, cabinet, wallConductors, fixedObjects, wallOpeningCorners, position,
                previewReferenceComponentName, rootCount, clearance, overlapLength,
                cabinet.Opening.Z - wallOpeningCorners.Average(p => p.Z))
        {
        }

        public FaceToWallUpstreamGeometry(BridgeCoordinateFrame coordinates,
            SideToSideCabinetGeometry cabinet, IEnumerable<SideToSideExtensionGeometry> wallConductors,
            IEnumerable<BridgeDesignObject> fixedObjects, IEnumerable<Point3d> wallOpeningCorners,
            string position, string previewReferenceComponentName, int rootCount, double clearance, double overlapLength,
            double wallCabinetEccentricity, double? modelClearance = null, double appliedWallVerticalClearance = 0)
            : this(coordinates, cabinet, wallConductors, fixedObjects, wallOpeningCorners, position,
                previewReferenceComponentName, rootCount, clearance, overlapLength, wallCabinetEccentricity,
                modelClearance, appliedWallVerticalClearance, null)
        {
        }

        public FaceToWallUpstreamGeometry(BridgeCoordinateFrame coordinates,
            SideToSideCabinetGeometry cabinet, IEnumerable<SideToSideExtensionGeometry> wallConductors,
            IEnumerable<BridgeDesignObject> fixedObjects, IEnumerable<Point3d> wallOpeningCorners,
            string position, string previewReferenceComponentName, int rootCount, double clearance, double overlapLength,
            double wallCabinetEccentricity, double? modelClearance, double appliedWallVerticalClearance,
            FaceToWallInstallationValues wallInstallation)
            : this(coordinates, cabinet, wallConductors, fixedObjects, wallOpeningCorners, position,
                previewReferenceComponentName, rootCount, clearance, overlapLength, wallCabinetEccentricity,
                modelClearance, appliedWallVerticalClearance, wallInstallation, false)
        {
        }

        public FaceToWallUpstreamGeometry(BridgeCoordinateFrame coordinates,
            SideToSideCabinetGeometry cabinet, IEnumerable<SideToSideExtensionGeometry> wallConductors,
            IEnumerable<BridgeDesignObject> fixedObjects, IEnumerable<Point3d> wallOpeningCorners,
            string position, string previewReferenceComponentName, int rootCount, double clearance, double overlapLength,
            double wallCabinetEccentricity, double? modelClearance, double appliedWallVerticalClearance,
            FaceToWallInstallationValues wallInstallation, bool isPhaseSwitched)
        {
            Coordinates = coordinates ?? throw new ArgumentNullException(nameof(coordinates));
            Cabinet = cabinet ?? throw new ArgumentNullException(nameof(cabinet));
            if (cabinet.Opening == null || double.IsNaN(cabinet.Opening.X) || double.IsInfinity(cabinet.Opening.X))
                throw new ArgumentException("面对墙缺少有效的基座实际安装接缝坐标。", nameof(cabinet));
            BaseInstallationSeamX = cabinet.Opening.X;
            WallConductors = Array.AsReadOnly(wallConductors.ToArray());
            FixedObjects = Array.AsReadOnly(fixedObjects.ToArray());
            var corners = wallOpeningCorners.Select(p => p.Copy()).ToArray();
            if (corners.Length != 4 || corners.Any(p => new[] { p.X, p.Y, p.Z }.Any(v => double.IsNaN(v) || double.IsInfinity(v))))
                throw new ArgumentException("墙洞必须提供四个已核验的实际草图角点。");
            WallOpeningCorners = Array.AsReadOnly(corners);
            WallOpeningHeight = corners.Max(p => p.Y) - corners.Min(p => p.Y);
            if (rootCount < 2 || rootCount > 4 || double.IsNaN(clearance) || double.IsInfinity(clearance) || clearance < 0)
                throw new ArgumentException("面对墙生成根数或绝缘距离无效。");
            RootCount = rootCount; Clearance = clearance;
            ModelClearance = modelClearance ?? clearance;
            if (double.IsNaN(ModelClearance) || double.IsInfinity(ModelClearance) || ModelClearance < 0)
                throw new ArgumentException("总装反读绝缘距离无效。");
            if (double.IsNaN(overlapLength) || double.IsInfinity(overlapLength) || overlapLength <= 0)
                throw new ArgumentException("总装搭接长度无效。");
            OverlapLength = overlapLength;
            if (double.IsNaN(wallCabinetEccentricity) || double.IsInfinity(wallCabinetEccentricity))
                throw new ArgumentException("柜对墙偏心驱动值无效。");
            WallCabinetEccentricity = wallCabinetEccentricity;
            if (double.IsNaN(appliedWallVerticalClearance) || double.IsInfinity(appliedWallVerticalClearance))
                throw new ArgumentException("面对墙已应用上下墙距无效。");
            AppliedWallVerticalClearance = appliedWallVerticalClearance;
            WallInstallation = wallInstallation;
            IsPhaseSwitched = isPhaseSwitched;
            wallInstallation?.Validate(corners.Max(p => p.Z) - corners.Min(p => p.Z), WallOpeningHeight);
            Position = position; PreviewReferenceComponentName = previewReferenceComponentName;
        }
    }
}
