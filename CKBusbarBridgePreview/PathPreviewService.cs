using GeometRi;
using SolidWorks.Interop.sldworks;
using System.Collections.Generic;
using System.Drawing;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// 将 Worker 中心线点集显示为 SolidWorks 临时线体。
    /// 坐标输入使用装配体 mm，SolidWorks Modeler 调用使用 m。
    /// </summary>
    public static class PathPreviewService
    {
        public static List<FreeDrawBody> PreviewPath(
            SldWorks swApp,
            IReadOnlyList<Point3d> centerlinePoints,
            Color previewColor,
            Component2 previewComponent)
        {
            var previewBodies = new List<FreeDrawBody>();
            if (swApp == null || previewComponent == null ||
                centerlinePoints == null || centerlinePoints.Count < 2)
            {
                return previewBodies;
            }

            try
            {
                var solidWorksModeler = (IModeler)swApp.IGetModeler();
                var previewPartToRootAssemblyTransform = previewComponent.Transform2;
                var rootAssemblyToPreviewPartTransform =
                    previewPartToRootAssemblyTransform?.IInverse();
                if (rootAssemblyToPreviewPartTransform == null)
                {
                    return previewBodies;
                }

                for (var pointIndex = 0;
                     pointIndex < centerlinePoints.Count - 1;
                     pointIndex++)
                {
                    var startPoint = centerlinePoints[pointIndex];
                    var endPoint = centerlinePoints[pointIndex + 1];
                    if (startPoint == null || endPoint == null ||
                        startPoint.DistanceTo(endPoint) < 0.001)
                    {
                        continue;
                    }

                    var startPointMeters = new[]
                    {
                        startPoint.X / 1000,
                        startPoint.Y / 1000,
                        startPoint.Z / 1000
                    };
                    var endPointMeters = new[]
                    {
                        endPoint.X / 1000,
                        endPoint.Y / 1000,
                        endPoint.Z / 1000
                    };
                    var lineDirectionMeters = new[]
                    {
                        endPointMeters[0] - startPointMeters[0],
                        endPointMeters[1] - startPointMeters[1],
                        endPointMeters[2] - startPointMeters[2]
                    };
                    var sourceLineCurve = (Curve)solidWorksModeler.CreateLine(
                        startPointMeters,
                        lineDirectionMeters);
                    var trimmedLineCurve = sourceLineCurve == null
                        ? null
                        : (Curve)sourceLineCurve.CreateTrimmedCurve2(
                            startPointMeters[0],
                            startPointMeters[1],
                            startPointMeters[2],
                            endPointMeters[0],
                            endPointMeters[1],
                            endPointMeters[2]);
                    var centerlineBody = trimmedLineCurve == null
                        ? null
                        : (Body2)trimmedLineCurve.CreateWireBody();
                    if (centerlineBody == null)
                    {
                        continue;
                    }

                    centerlineBody.ApplyTransform(rootAssemblyToPreviewPartTransform);
                    previewBodies.Add(new FreeDrawBody
                    {
                        Body = centerlineBody,
                        Component2 = previewComponent,
                        Color = previewColor,
                        TempMater = 0.7
                    });
                }
            }
            catch
            {
                // 保留已经创建的中心线，单条线创建失败不影响其余路径。
            }

            return previewBodies;
        }
    }
}
