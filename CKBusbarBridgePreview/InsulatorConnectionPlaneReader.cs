using GeometRi;
using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridgePreview
{
    /// <summary>绝缘子位置及“配合面外形1”草图所在平面。</summary>
    public sealed class InsulatorConnectionInfo
    {
        public Component2 Component { get; set; }
        public Point3d ComponentCenter { get; set; }
        public Plane3d ConnectionPlane { get; set; }
        public Point3d ConnectionCircleCenter { get; set; }
        public double ConnectionCircleRadius { get; set; }
    }

    public static class InsulatorConnectionPlaneReader
    {
        public const string ConnectionSketchName = "配合面外形1";

        public static InsulatorConnectionInfo Read(Component2 insulatorComponent, ISldWorks swApp)
        {
            var result = ReadGeometry(insulatorComponent, swApp, false);
            result.ComponentCenter = BusbarGeometryUtility.GetComponentCenter(insulatorComponent);
            return result;
        }

        /// <summary>新预览读取实际配合几何及实体质心（仅用于判断配合面朝外方向），不读取包围盒。</summary>
        public static InsulatorConnectionInfo ReadConnectionOnly(Component2 insulatorComponent, ISldWorks swApp)
        {
            return ReadGeometry(insulatorComponent, swApp, true);
        }

        private static InsulatorConnectionInfo ReadGeometry(Component2 insulatorComponent, ISldWorks swApp, bool readSolidCentroid)
        {
            if (insulatorComponent == null) throw new ArgumentNullException(nameof(insulatorComponent));
            if (swApp == null) throw new ArgumentNullException(nameof(swApp));

            var connectionGeometry = TryReadConnectionGeometry(
                insulatorComponent,
                swApp);
            return new InsulatorConnectionInfo
            {
                Component = insulatorComponent,
                ComponentCenter = readSolidCentroid ? ReadSolidCentroid(insulatorComponent, swApp) : null,
                ConnectionPlane = connectionGeometry?.Plane,
                ConnectionCircleCenter = connectionGeometry?.CircleCenter,
                ConnectionCircleRadius = connectionGeometry?.CircleRadius ?? 0d
            };
        }

        private static Point3d ReadSolidCentroid(Component2 component, ISldWorks swApp)
        {
            var part = component.GetModelDoc2() as PartDoc;
            var bodies = (part?.GetBodies2(0, false) as object[] ?? Array.Empty<object>()).OfType<Body2>().ToArray();
            var properties = bodies.Select(b => b.GetMassProperties(1d) as double[]).ToArray();
            if (properties.Length == 0 || properties.Any(p => p == null || p.Length < 6 ||
                p.Take(6).Any(v => double.IsNaN(v) || double.IsInfinity(v)) || p[3] <= 0d) || component.Transform2 == null)
                throw new InvalidOperationException("绝缘子实体质心或总装变换缺失，无法核验配合面朝外方向：" + component.Name2);
            var volume = properties.Sum(p => p[3]);
            var center = Enumerable.Range(0, 3).Select(i => properties.Sum(p => p[i] * p[3]) / volume).ToArray();
            var point = swApp.IGetMathUtility().CreatePoint(center) as MathPoint;
            var placed = point?.MultiplyTransform(component.Transform2) as MathPoint;
            var values = placed?.ArrayData as double[];
            if (values == null || values.Length < 3 || values.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                throw new InvalidOperationException("绝缘子实体质心变换无效：" + component.Name2);
            return new Point3d(values[0] * 1000d, values[1] * 1000d, values[2] * 1000d);
        }

        private static InsulatorConnectionGeometry TryReadConnectionGeometry(
            Component2 insulatorComponent,
            ISldWorks swApp)
        {
            PartDoc insulatorPart;
            try
            {
                insulatorPart = insulatorComponent.IGetModelDoc() as PartDoc;
            }
            catch (Exception insulatorModelReadException)
            {
                FaceToWallPreviewTrace.Error(
                    "绝缘子配合圆-获取零件模型失败",
                    insulatorModelReadException,
                    "组件=" + BusbarGeometryUtility.SafeName(insulatorComponent));
                throw;
            }

            if (insulatorPart == null)
            {
                FaceToWallPreviewTrace.Warning(
                    "绝缘子配合圆-缺少零件模型",
                    "组件=" + BusbarGeometryUtility.SafeName(insulatorComponent));
                return null;
            }

            var sketchFeature = insulatorPart?.IFeatureByName(ConnectionSketchName);
            var connectionSketch = sketchFeature?.GetSpecificFeature2() as Sketch;
            var modelToSketchTransform = connectionSketch?.ModelToSketchTransform;
            var sketchToModelTransform = modelToSketchTransform?.IInverse();
            var partToAssemblyTransform = insulatorComponent.Transform2;
            if (sketchToModelTransform == null || partToAssemblyTransform == null)
            {
                FaceToWallPreviewTrace.Warning(
                    "绝缘子配合圆-缺少坐标变换",
                    "组件=" + BusbarGeometryUtility.SafeName(insulatorComponent) +
                    "，草图=" + ConnectionSketchName +
                    "，草图存在=" + (connectionSketch != null) +
                    "，草图到零件变换存在=" + (sketchToModelTransform != null) +
                    "，零件到装配体变换存在=" + (partToAssemblyTransform != null));
                return null;
            }

            try
            {
                var mathUtility = swApp.IGetMathUtility();
                var sketchOrigin = mathUtility.CreatePoint(
                    new[] { 0d, 0d, 0d }) as MathPoint;
                var sketchNormalPoint = mathUtility.CreatePoint(
                    new[] { 0d, 0d, 0.1d }) as MathPoint;
                var modelOrigin = sketchOrigin?.MultiplyTransform(sketchToModelTransform) as MathPoint;
                var modelNormalPoint = sketchNormalPoint?.MultiplyTransform(sketchToModelTransform) as MathPoint;
                var assemblyOrigin = modelOrigin?.MultiplyTransform(partToAssemblyTransform) as MathPoint;
                var assemblyNormalPoint = modelNormalPoint?.MultiplyTransform(partToAssemblyTransform) as MathPoint;
                var originData = assemblyOrigin?.ArrayData as double[];
                var normalPointData = assemblyNormalPoint?.ArrayData as double[];
                if (originData == null || originData.Length < 3 ||
                    normalPointData == null || normalPointData.Length < 3)
                {
                    return null;
                }

                var planePoint = new Point3d(
                    originData[0] * 1000d,
                    originData[1] * 1000d,
                    originData[2] * 1000d);
                var planeNormalPoint = new Point3d(
                    normalPointData[0] * 1000d,
                    normalPointData[1] * 1000d,
                    normalPointData[2] * 1000d);
                var planeNormal = new Vector3d(planePoint, planeNormalPoint);
                if (planeNormal.Norm < 0.001d)
                {
                    return null;
                }

                var connectionCircle = ReadLargestConnectionCircle(
                    connectionSketch,
                    mathUtility,
                    sketchToModelTransform,
                    partToAssemblyTransform);
                var connectionGeometry = new InsulatorConnectionGeometry
                {
                    Plane = new Plane3d(planePoint, planeNormal.Normalized),
                    CircleCenter = connectionCircle?.Center,
                    CircleRadius = connectionCircle?.Radius ?? 0d
                };
                FaceToWallPreviewTrace.Info(
                    "绝缘子配合圆-解析完成",
                    "组件=" + BusbarGeometryUtility.SafeName(insulatorComponent) +
                    "，配合面法向=" + connectionGeometry.Plane.Normal +
                    "，圆心=" + FormatPoint(connectionGeometry.CircleCenter) +
                    "，半径=" + connectionGeometry.CircleRadius.ToString("0.###"));
                return connectionGeometry;
            }
            catch (Exception connectionGeometryException)
            {
                FaceToWallPreviewTrace.Error(
                    "绝缘子配合圆-解析失败",
                    connectionGeometryException,
                    "组件=" + BusbarGeometryUtility.SafeName(insulatorComponent));
                throw;
            }
        }

        private static InsulatorConnectionCircle ReadLargestConnectionCircle(
            Sketch connectionSketch,
            MathUtility mathUtility,
            MathTransform sketchToModelTransform,
            MathTransform partToAssemblyTransform)
        {
            var rawSketchSegments = connectionSketch?.GetSketchSegments() as object[];
            var connectionCircles = new List<InsulatorConnectionCircle>();
            foreach (var rawSketchSegment in rawSketchSegments ?? new object[0])
            {
                var sketchSegment = rawSketchSegment as SketchSegment;
                var completeSketchCircle = rawSketchSegment as SketchArc;
                if (sketchSegment == null || completeSketchCircle == null ||
                    completeSketchCircle.IsCircle() != 1)
                {
                    continue;
                }

                var sketchCurve = sketchSegment?.GetCurve() as Curve;
                if (sketchCurve == null || !sketchCurve.IsCircle())
                {
                    continue;
                }

                var circleParameters = sketchCurve.CircleParams as double[];
                if (circleParameters == null || circleParameters.Length < 7 ||
                    circleParameters[6] <= 0d)
                {
                    continue;
                }

                var sketchCircleCenter = mathUtility.CreatePoint(new[]
                {
                    circleParameters[0],
                    circleParameters[1],
                    circleParameters[2]
                }) as MathPoint;
                var modelCircleCenter = sketchCircleCenter?
                    .MultiplyTransform(sketchToModelTransform) as MathPoint;
                var assemblyCircleCenter = modelCircleCenter?
                    .MultiplyTransform(partToAssemblyTransform) as MathPoint;
                var assemblyCircleCenterData = assemblyCircleCenter?.ArrayData as double[];
                if (assemblyCircleCenterData == null || assemblyCircleCenterData.Length < 3)
                {
                    continue;
                }

                connectionCircles.Add(new InsulatorConnectionCircle
                {
                    Center = new Point3d(
                        assemblyCircleCenterData[0] * 1000d,
                        assemblyCircleCenterData[1] * 1000d,
                        assemblyCircleCenterData[2] * 1000d),
                    Radius = circleParameters[6] * 1000d
                });
            }

            return connectionCircles
                .OrderByDescending(connectionCircle => connectionCircle.Radius)
                .FirstOrDefault();
        }

        private static string FormatPoint(Point3d point)
        {
            return point == null
                ? "null"
                : string.Format("({0:0.###},{1:0.###},{2:0.###})", point.X, point.Y, point.Z);
        }

        private sealed class InsulatorConnectionGeometry
        {
            public Plane3d Plane { get; set; }
            public Point3d CircleCenter { get; set; }
            public double CircleRadius { get; set; }
        }

        private sealed class InsulatorConnectionCircle
        {
            public Point3d Center { get; set; }
            public double Radius { get; set; }
        }
    }
}
