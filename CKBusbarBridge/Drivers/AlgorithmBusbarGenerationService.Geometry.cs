using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridgePreview;
using GeometRi;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CKBusbarBridge.Drivers
{
    internal sealed class BusbarGenerationIntegrity
    {
        internal VerticalBendSetting ResolveVerticalBend(PlanarBendBusbarSetting source)
        {
            if (source.IsVerticalBend) return VerticalBendPreviewAdapter.Create(source);
            if (source.IsMilledBar || source.PointList.Count < 3) return null;
            var origin = source.PointList[0];
            var width = source.WidthVector.Normalized;
            var thickness = source.StartBusbarVector.Normalized;
            var offsets = source.PointList.Select(p => new Vector3d(origin, p)).ToArray();
            if (!offsets.Any(v => Math.Abs(v.Dot(width)) > .001d)) return null;
            if (offsets.Any(v => Math.Abs(v.Dot(thickness)) > .001d))
                throw new InvalidOperationException("铜排包含混合平弯和立弯，不能投影到平弯模板：" + source.GroupKey);
            // 旧柜端计划未标记立弯；只在实际路径完整位于排宽平面时使用已有立弯链。
            var copy = source.Copy();
            copy.IsVerticalBend = true;
            return VerticalBendPreviewAdapter.Create(copy);
        }

        internal void RequireSave(bool saved, int errors, int warnings, string context)
        {
            var invalid = (int)(swFileSaveWarning_e.swFileSaveWarning_RebuildError |
                swFileSaveWarning_e.swFileSaveWarning_NeedsRebuild |
                swFileSaveWarning_e.swFileSaveWarning_OpenedViewOnly);
            if (!saved || errors != 0 || (warnings & invalid) != 0)
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "{0}保存未通过校验，错误码={1}，警告码={2}。", context, errors, warnings));
        }

        internal void RequireRebuild(bool rebuilt, string context)
        {
            if (!rebuilt) throw new InvalidOperationException(context + "重建失败，禁止接纳生成结果。");
        }

        internal void RequireSolid(int count, int faces, double volume, string context)
        {
            if (count <= 0 || faces <= 0 || double.IsNaN(volume) || double.IsInfinity(volume) || volume <= 0d)
                throw new InvalidOperationException(context + "缺少有效铜排实体，实际实体数=" + count + "。");
        }

        internal void RequireSketchPlane(IReadOnlyList<Point3d> points)
        {
            if (points.Count < 2 || points.Any(p => p == null ||
                double.IsNaN(p.X) || double.IsInfinity(p.X) || double.IsNaN(p.Y) || double.IsInfinity(p.Y) ||
                double.IsNaN(p.Z) || double.IsInfinity(p.Z) || Math.Abs(p.Z) > 1e-7d) ||
                Math.Abs(points[0].X) + Math.Abs(points[0].Y) + Math.Abs(points[0].Z) > 1e-7d ||
                points[1].X <= 1e-7d || Math.Abs(points[1].Y) > 1e-7d)
                throw new InvalidOperationException("铜排目标路径与模板形状草图平面或首段定位基准不一致，禁止投影或丢弃偏移。");
        }
    }

    internal sealed partial class AlgorithmBusbarGenerationService
    {
        private static void RebuildGeneratedModel(ModelDoc2 model, string stage)
        {
            var rebuilt = model.EditRebuild3();
            RequireFeatureHealth(model, stage);
            new BusbarGenerationIntegrity().RequireRebuild(rebuilt, stage + "：" + model.GetPathName());
        }

        private static void RequireFeatureHealth(ModelDoc2 model, string stage)
        {
            for (var feature = model.FirstFeature() as Feature; feature != null; feature = feature.GetNextFeature() as Feature)
            {
                if (feature.IsSuppressed()) continue;
                bool warning;
                var error = feature.GetErrorCode2(out warning);
                if (error != 0 && !warning)
                    throw new InvalidOperationException(stage + "：" + model.GetPathName() + "/" + feature.Name +
                        "，特征错误=" + error + "（" + (swFeatureError_e)error + "）。");
            }
        }

        private static void RequireCopperSolid(ModelDoc2 model, string stage)
        {
            var part = model as PartDoc;
            if (part == null) throw new InvalidOperationException(stage + "未取得铜排零件文档。");
            RequireFeatureHealth(model, stage);
            var bodies = (part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[] ?? Array.Empty<object>())
                .OfType<Body2>().ToArray();
            RequireBodies(bodies, stage + "：" + model.GetPathName());
        }

        private static void RequireCopperComponent(Component2 component, string stage)
        {
            var model = component.GetModelDoc2() as ModelDoc2;
            if (model == null || model.ConfigurationManager.ActiveConfiguration.Name != component.ReferencedConfiguration)
                throw new InvalidOperationException(stage + "铜排文档未解析或引用配置未激活：" + component.Name2);
            RequireCopperSolid(model, stage);
            object info;
            var bodies = (component.GetBodies3((int)swBodyType_e.swSolidBody, out info) as object[] ?? Array.Empty<object>())
                .OfType<Body2>().ToArray();
            RequireBodies(bodies, stage + "：" + component.Name2);
        }

        private static void RequireBodies(Body2[] bodies, string context)
        {
            var policy = new BusbarGenerationIntegrity();
            // 立弯的钣金展开按分段保留多实体；逐个核验，不改变已有实体组织或执行合并。
            if (bodies.Length == 0) policy.RequireSolid(0, 0, 0d, context);
            foreach (var body in bodies)
            {
                var mass = body.GetMassProperties(1d) as double[];
                policy.RequireSolid(1, body.GetFaceCount(), mass != null && mass.Length > 3 ? mass[3] : double.NaN, context);
            }
        }

        private static void CheckGenerationSave(bool saved, int errors, int warnings, string context)
        {
            new BusbarGenerationIntegrity().RequireSave(saved, errors, warnings, context);
            if (warnings != 0) GenerationLogger.Warn("铜排保存附带非重建警告：{0}，警告码={1}", context, warnings);
        }

        private static void RewriteSketchKeepingTemplateReference(ModelDoc2 assembly, ModelDoc2 part,
            Sketch sketch, IReadOnlyList<Point3d> points)
        {
            new BusbarGenerationIntegrity().RequireSketchPlane(points);
            var segments = (sketch.GetSketchSegments() as object[] ?? Array.Empty<object>()).OfType<SketchSegment>().ToArray();
            var anchors = segments.Where(segment =>
            {
                var line = segment as SketchLine;
                if (line == null || segment.ConstructionGeometry) return false;
                var start = line.GetStartPoint2() as SketchPoint;
                var end = line.GetEndPoint2() as SketchPoint;
                return start != null && end != null && Math.Abs(start.X) + Math.Abs(start.Y) + Math.Abs(start.Z) < 1e-7d &&
                    end.X > 1e-7d && Math.Abs(end.Y) + Math.Abs(end.Z) < 1e-7d;
            }).ToArray();
            if (anchors.Length != 1) throw new InvalidOperationException("铜排模板必须具有唯一从原点沿正X的首条定位线。");
            var anchor = anchors[0];
            // 保留基体引用的首线、原点和SL尺寸；草图始终非空，不让基体引用经历删除重建。
            assembly.ClearSelection2(true);
            foreach (var segment in segments.Where(s => !ReferenceEquals(s, anchor)))
                if (!segment.Select(true)) throw new InvalidOperationException("无法选中铜排模板待替换线段。");
            if (segments.Length > 1 && !assembly.Extension.DeleteSelection2(0))
                throw new InvalidOperationException("无法删除铜排模板的其余路径线段。");
            assembly.ClearSelection2(true);
            var length = part.Parameter("SL@形状草图") as Dimension;
            if (length == null) throw new InvalidOperationException("铜排模板首线缺少SL驱动尺寸。");
            length.SystemValue = points[1].X;
            if (Math.Abs(length.SystemValue - points[1].X) > 1e-7d)
                throw new InvalidOperationException("铜排模板首线SL驱动反读不符。");
            for (var i = 1; i < points.Count - 1; i++)
                if (assembly.SketchManager.CreateLine(points[i].X, points[i].Y, 0d,
                    points[i + 1].X, points[i + 1].Y, 0d) == null)
                    throw new InvalidOperationException("铜排模板路径草图线段创建失败。");
            RequireSketchPath(sketch, points);
        }

        private static void RequireSketchPath(Sketch sketch, IReadOnlyList<Point3d> points)
        {
            var lines = (sketch.GetSketchSegments() as object[] ?? Array.Empty<object>()).OfType<SketchLine>().ToArray();
            if (lines.Length != points.Count - 1) throw new InvalidOperationException("铜排形状草图实际线段数量不符。");
            Func<SketchPoint, Point3d, bool> same = (actual, target) => actual != null &&
                Math.Abs(actual.X - target.X) + Math.Abs(actual.Y - target.Y) + Math.Abs(actual.Z - target.Z) < 1e-7d;
            for (var i = 0; i < points.Count - 1; i++)
                if (lines.Count(line => same(line.GetStartPoint2() as SketchPoint, points[i]) &&
                    same(line.GetEndPoint2() as SketchPoint, points[i + 1]) ||
                    same(line.GetEndPoint2() as SketchPoint, points[i]) && same(line.GetStartPoint2() as SketchPoint, points[i + 1])) != 1)
                    throw new InvalidOperationException("铜排形状草图实际端点与目标路径不符，段号=" + (i + 1) + "。");
        }
    }
}
