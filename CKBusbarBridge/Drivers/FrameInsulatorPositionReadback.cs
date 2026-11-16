using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridge.Prototype;
using CKBusbarBridgePreview;
using SolidWorks.Interop.sldworks;

namespace CKBusbarBridge.Drivers
{
    internal sealed class FrameInsulatorInstallationPoint
    {
        internal FrameInsulatorInstallationPoint(string componentName, bool hanging, double[] worldPoint)
            : this(componentName, hanging, worldPoint, null) { }

        internal FrameInsulatorInstallationPoint(string componentName, bool hanging, double[] worldPoint, int? installationIndex)
        { ComponentName = componentName; Hanging = hanging; WorldPoint = worldPoint; InstallationIndex = installationIndex; }

        internal string ComponentName { get; }
        internal bool Hanging { get; }
        internal double[] WorldPoint { get; }
        internal int? InstallationIndex { get; }
    }

    internal interface IFrameInsulatorPositionSource
    {
        string FrameName { get; }
        double Length { get; }
        double[] Transform { get; }
        IReadOnlyList<FrameInsulatorInstallationPoint> Installations { get; }
        double Dimension(string name);
        double[] Coordinate(string name);
    }

    // 普通读取与业务反读共用；只有原驱动尺寸和实际安装几何相符，才能恢复可编辑位置。
    internal sealed class FrameInsulatorPositionReadback
    {
        internal const double InstallationToleranceMm = 1.0;

        internal IReadOnlyList<double> Read(IFrameInsulatorPositionSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var installations = source.Installations;
            if (installations == null || installations.Count > 3 || !Finite(source.Length) || source.Length <= 0)
                throw new InvalidOperationException("基架绝缘子数量或长度无效：" + source.FrameName);
            if (installations.Count == 0) return Array.Empty<double>();
            RequireTransform(source.Transform, source.FrameName);
            var coordinates = new Dictionary<string, double[]>(StringComparer.Ordinal);
            var positions = new double[installations.Count];
            var occupied = new HashSet<int>();
            foreach (var child in installations.OrderBy(c => c.ComponentName, StringComparer.Ordinal))
            {
                RequirePoint(child.WorldPoint, source.FrameName + "/" + child.ComponentName + "/安装坐标系");
                if (child.InstallationIndex.HasValue && (child.InstallationIndex < 1 || child.InstallationIndex > installations.Count))
                    throw new InvalidOperationException("绝缘子安装位标识超出实际安装数量：基架=" + source.FrameName + "；组件=" + child.ComponentName);
                var candidates = child.InstallationIndex.HasValue ? new[] { child.InstallationIndex.Value } : Enumerable.Range(1, installations.Count);
                var matches = candidates.Where(index =>
                {
                    var route = FaceToFaceFrameInsulatorDriver.ResolvePlacementRoute(index, child.Hanging);
                    if (!coordinates.TryGetValue(route.FrameCoordinateName, out var point))
                    {
                        point = source.Coordinate(route.FrameCoordinateName);
                        RequirePoint(point, source.FrameName + "/" + route.FrameCoordinateName);
                        coordinates.Add(route.FrameCoordinateName, point);
                    }
                    return BridgeBusinessGeometry.Distance(child.WorldPoint,
                        BridgeBusinessGeometry.TransformWorld(point, source.Transform)) <= InstallationToleranceMm;
                }).ToArray();
                if (matches.Length != 1 || !occupied.Add(matches[0]))
                    throw new InvalidOperationException("绝缘子实际安装位缺失、重复或无法唯一映射：基架=" + source.FrameName + "；组件=" + child.ComponentName);
                var selected = FaceToFaceFrameInsulatorDriver.ResolvePlacementRoute(matches[0], child.Hanging);
                var position = source.Dimension(selected.DimensionName);
                if (!Finite(position) || position < 0 || position > source.Length)
                    throw new InvalidOperationException("绝缘子原定位尺寸无效：基架=" + source.FrameName + "；尺寸=" + selected.DimensionName);
                var driven = coordinates[selected.FrameCoordinateName].ToArray();
                // 模板X为桥向，原定位尺寸从基架端面起算；其余坐标必须来自真实安装坐标系。
                driven[0] = position - source.Length / 2d;
                var expected = BridgeBusinessGeometry.TransformWorld(driven, source.Transform);
                var difference = BridgeBusinessGeometry.Distance(child.WorldPoint, expected);
                if (difference > InstallationToleranceMm)
                    throw new InvalidOperationException("绝缘子原定位尺寸与实际安装点不一致：基架=" + source.FrameName +
                        "；组件=" + child.ComponentName + "；尺寸=" + selected.DimensionName + "；定位=" + Number(position) +
                        " mm；预期安装点=" + Point(expected) + "；实际安装点=" + Point(child.WorldPoint) +
                        "；安装点距离偏差=" + Number(difference) + " mm（允许" + Number(InstallationToleranceMm) + " mm）。");
                positions[matches[0] - 1] = position;
            }
            return Array.AsReadOnly(positions);
        }

        internal static void RequireTransform(double[] transform, string context)
        {
            if (transform == null || transform.Length != 16 || transform.Any(v => !Finite(v)) || Math.Abs(transform[12] - 1d) > .000001)
                throw new InvalidOperationException("绝缘子安装组件变换缺失或不支持：" + context);
            for (var row = 0; row < 3; row++)
                for (var other = row; other < 3; other++)
                {
                    var dot = Enumerable.Range(0, 3).Sum(axis => transform[row * 3 + axis] * transform[other * 3 + axis]);
                    if (Math.Abs(dot - (row == other ? 1d : 0d)) > .000001)
                        throw new InvalidOperationException("绝缘子安装组件坐标轴退化或不正交：" + context);
                }
        }

        private void RequirePoint(double[] point, string context)
        {
            if (point == null || point.Length != 3 || point.Any(v => !Finite(v)))
                throw new InvalidOperationException("绝缘子实际安装坐标缺失或无效：" + context);
        }

        private string Point(double[] point) => "(" + string.Join(",", point.Select(Number)) + ") mm";
        private string Number(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    internal sealed class SolidWorksFrameInsulatorPositionSource : IFrameInsulatorPositionSource
    {
        private readonly Component2 _frame;
        private readonly Func<string, double> _dimension;

        internal SolidWorksFrameInsulatorPositionSource(Component2 frame, double length, Func<string, double> dimension)
        {
            _frame = frame ?? throw new ArgumentNullException(nameof(frame));
            _dimension = dimension ?? throw new ArgumentNullException(nameof(dimension));
            Length = length;
            Transform = frame.GetTotalTransform(false)?.ArrayData as double[];
            Installations = (frame.GetChildren() as object[] ?? Array.Empty<object>()).OfType<Component2>()
                .Where(c => !c.IsSuppressed() && c.GetConfiguredRdSystemPartType() == SystemPartType_e.Insulator)
                .Select(c => new FrameInsulatorInstallationPoint(c.Name2, FaceToFaceFrameInsulatorDriver.IsHangingInsulator(c),
                    WorldCoordinate(c, "安装坐标系"), FaceToFaceFrameInsulatorDriver.ReadInstallationIndex(
                        c.ComponentReference, 3, c.Name2))).ToArray();
        }

        public string FrameName => _frame.Name2;
        public double Length { get; }
        public double[] Transform { get; }
        public IReadOnlyList<FrameInsulatorInstallationPoint> Installations { get; }
        public double Dimension(string name) => _dimension(name);
        public double[] Coordinate(string name) => LocalCoordinate(_frame, name);

        private double[] WorldCoordinate(Component2 component, string name)
        {
            var transform = component.GetTotalTransform(false)?.ArrayData as double[];
            FrameInsulatorPositionReadback.RequireTransform(transform, component.Name2);
            return BridgeBusinessGeometry.TransformWorld(LocalCoordinate(component, name), transform);
        }

        private double[] LocalCoordinate(Component2 component, string name)
        {
            var definition = component.FeatureByName(name)?.GetDefinition() as CoordinateSystemFeatureData;
            var transform = definition?.Transform?.ArrayData as double[];
            FrameInsulatorPositionReadback.RequireTransform(transform, component.Name2 + "/" + name);
            return new[] { transform[9] * 1000d, transform[10] * 1000d, transform[11] * 1000d };
        }
    }
}
