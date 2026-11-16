using AutoOpeningHoleBase;
using AutoOpeningHoleDataModel;
using GeometRi;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// SolidWorks 组件、实体、面、边到虚拟几何的转换层。
    /// 结构沿用 BusbarToolsInWeb，本项目只抽取本阶段需要的平面和直线。
    /// </summary>
    public static class BodyHelper
    {
        public static ComponentInto GetComponentInto(this Component2 component, ISldWorks swApp)
        {
            if (component == null || swApp == null)
            {
                return null;
            }

            try
            {
                var rawBodies = component.GetBodies((int)swBodyType_e.swSolidBody) as object[];
                var solidBodies = rawBodies?
                    .OfType<Body2>()
                    .Where(solidBody => solidBody != null)
                    .ToList() ?? new List<Body2>();
                FaceToWallPreviewTrace.Info(
                    "虚拟组件-读取实体",
                    "组件=" + SafeGetComponentName(component) +
                    "，原始实体数=" + (rawBodies?.Length ?? 0) +
                    "，可转换实体数=" + solidBodies.Count);
                if (solidBodies.Count == 0)
                {
                    FaceToWallPreviewTrace.Warning(
                        "虚拟组件-读取实体",
                        "组件没有可转换的实体。组件=" + SafeGetComponentName(component));
                    return null;
                }

                var componentInto = new ComponentInto
                {
                    swApp = swApp,
                    Component = component,
                    Name = SafeGetComponentName(component),
                    PartTypeE = component.GetSystemPartType()
                };
                foreach (var solidBody in solidBodies)
                {
                    var bodyInto = solidBody.GetBodyIntos(componentInto);
                    if (bodyInto != null)
                    {
                        componentInto.BodyIntos.Add(bodyInto);
                    }
                }

                return componentInto.BodyIntos.Count == 0 ? null : componentInto;
            }
            catch (Exception componentConvertException)
            {
                FaceToWallPreviewTrace.Error(
                    "虚拟组件-转换组件",
                    componentConvertException,
                    "组件=" + SafeGetComponentName(component));
                return null;
            }
        }

        public static BodyInto GetBodyIntos(this Body2 body, ComponentInto componentInto)
        {
            if (body == null || componentInto == null)
            {
                return null;
            }

            try
            {
                var bodyInto = new BodyInto
                {
                    Body = body,
                    ComponentInto = componentInto,
                    Face2s = (body.GetFaces() as object[])?
                        .OfType<Face2>()
                        .Where(face => face != null)
                        .ToList() ?? new List<Face2>()
                };
                FaceToWallPreviewTrace.Info(
                    "虚拟组件-读取实体面",
                    "组件=" + SafeGetComponentName(componentInto.Component) +
                    "，可转换面数=" + bodyInto.Face2s.Count);
                foreach (var face in bodyInto.Face2s)
                {
                    var faceInto = face.GetFaceIntos(componentInto);
                    if (faceInto != null)
                    {
                        bodyInto.FaceIntos.Add(faceInto);
                    }
                }

                return bodyInto;
            }
            catch (Exception bodyConvertException)
            {
                FaceToWallPreviewTrace.Error(
                    "虚拟组件-转换实体",
                    bodyConvertException,
                    "组件=" + SafeGetComponentName(componentInto.Component));
                return null;
            }
        }

        public static FaceInto GetFaceIntos(this Face2 face, ComponentInto componentInto)
        {
            if (face == null || componentInto == null)
            {
                return null;
            }

            try
            {
                var faceInto = new FaceInto
                {
                    Face = face,
                    ComponentInto = componentInto,
                    Edges = (face.GetEdges() as object[])?
                        .OfType<Edge>()
                        .Where(edge => edge != null)
                        .ToList() ?? new List<Edge>()
                };

                try
                {
                    var surface = face.GetSurface() as Surface;
                    if (surface != null && surface.IsPlane())
                    {
                        var partPlane = face.ToPlane3d();
                        faceInto.IsPlane = partPlane != null;
                        faceInto.FacePlane = partPlane?
                            .ConvertPlaneInPartToAssembly1000(
                                componentInto.swApp,
                                componentInto.Component);
                    }
                }
                catch (Exception planeConvertException)
                {
                    FaceToWallPreviewTrace.Error(
                        "虚拟组件-转换平面",
                        planeConvertException,
                        "组件=" + SafeGetComponentName(componentInto.Component));
                    faceInto.IsPlane = false;
                    faceInto.FacePlane = null;
                }

                foreach (var edge in faceInto.Edges)
                {
                    var edgeInto = edge.GetEdgeInto(componentInto);
                    if (edgeInto != null)
                    {
                        faceInto.EdgeIntos.Add(edgeInto);
                    }
                }

                return faceInto;
            }
            catch (Exception faceConvertException)
            {
                FaceToWallPreviewTrace.Error(
                    "虚拟组件-转换面",
                    faceConvertException,
                    "组件=" + SafeGetComponentName(componentInto.Component));
                return null;
            }
        }

        public static EdgeInto GetEdgeInto(this Edge edge, ComponentInto componentInto)
        {
            if (edge == null || componentInto == null)
            {
                return null;
            }

            try
            {
                var curve = edge.IGetCurve();
                if (curve == null)
                {
                    return null;
                }

                var edgeInto = new EdgeInto
                {
                    swApp = componentInto.swApp,
                    Comp = componentInto.Component,
                    Edge = edge,
                    Curve = curve,
                    CurveType = curve.IsLine() ? SwCurveType.Line :
                        curve.IsCircle() ? SwCurveType.Arc : SwCurveType.Other
                };
                if (edgeInto.CurveType == SwCurveType.Line)
                {
                    edgeInto.SegmentInPart = edge.EdgeToSegment1000(componentInto.swApp);
                    edgeInto.Segment = edgeInto.SegmentInPart?
                        .ConvertSegment3dInPartToAssembly1000(
                            componentInto.swApp,
                            componentInto.Component);
                }

                return edgeInto;
            }
            catch (Exception edgeConvertException)
            {
                FaceToWallPreviewTrace.Error(
                    "虚拟组件-转换边",
                    edgeConvertException,
                    "组件=" + SafeGetComponentName(componentInto.Component));
                return null;
            }
        }

        /// <summary>
        /// 兼容原测试入口，将实体保存转发给现有预览内核中的 COM 流实现。
        /// </summary>
        public static void SaveBodyToFile(IBody2 body, string filePath)
        {
            Body2AndFile.BodyHelper.SaveBodyToFile(body, filePath);
        }

        private static string SafeGetComponentName(Component2 component)
        {
            try
            {
                return component.Name2 ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
