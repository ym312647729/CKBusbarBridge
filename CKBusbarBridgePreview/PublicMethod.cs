//using AutoOpeningHoleBase;
using AutoOpeningHoleBase;
//using AutoOpeningHoleDataModel;
using GeometRi;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using Sunny.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AutoOpeningHoleDataModel;

namespace CKBusbarBridgePreview
{
    public static class PublicMethod
    {
        private const string SystemPartTypeConfigurationPropertyName = "RD类型";
        private const string SystemPartDescriptionPropertyName = "Description";
        private const string BusbarShapePropertyName = "铜排形状";
        private const string VerticalBendShapePropertyValue = "自由立弯";

        /// <summary>
        /// 基于两个组件上的坐标系创建配合（兼容旧调用）。
        /// </summary>
        /// <param name="swApp">SolidWorks 应用对象。</param>
        /// <param name="targetAssemblyModel">配合创建所在的装配体文档；为空则使用当前活动文档。</param>
        /// <param name="compA">前端组件（坐标系默认在该组件本体上）。</param>
        /// <param name="compB">后端组件（坐标系默认在该组件本体上）。</param>
        /// <param name="nameA">组件 A 上的坐标系名称。</param>
        /// <param name="nameB">组件 B 上的坐标系名称。</param>
        /// <returns>配合创建成功返回 true，否则返回 false。</returns>
        public static bool AddMateUseCoord(ISldWorks swApp, IModelDoc2 targetAssemblyModel, Component2 compA, Component2 compB,
            string nameA = "出", string nameB = "进")
        {
            var assemblyDoc = targetAssemblyModel as AssemblyDoc;
            if (assemblyDoc == null && swApp?.IActiveDoc2 is AssemblyDoc activeAssemblyDoc)
            {
                assemblyDoc = activeAssemblyDoc;
            }

            // 兼容旧调用：坐标系在 compA / compB 本体（装配体/零件）上。
            return AddMateUseCoord(swApp, assemblyDoc, compA, compB, nameA, nameB);
        }

        /// <summary>
        /// 基于“前/后装配体组件 + 其内部需要配合的子件”定位坐标系并添加配合。
        /// 说明：
        /// - frontAssembly / rearAssembly：当前装配体中已插入的前/后装配体组件（Component2）。
        /// - frontChild / rearChild：位于对应装配体内部、真正承载坐标系的子件（Component2）。若坐标系就在装配体组件本体上，可传 null。
        /// - targetAssemblyModel：配合创建所在的装配体文档（通常就是当前驱动的装配体，如 L 排装配体）。
        /// </summary>
        /// <param name="swApp">SolidWorks 应用对象。</param>
        /// <param name="targetAssemblyModel">配合创建所在的装配体文档；为空则使用当前活动文档。</param>
        /// <param name="frontAssembly">前端装配体组件（顶层组件）。</param>
        /// <param name="rearAssembly">后端装配体组件（顶层组件）。</param>
        /// <param name="frontChild">前端装配体内真正承载坐标系的子件；坐标系在装配体组件本体上时传 null。</param>
        /// <param name="rearChild">后端装配体内真正承载坐标系的子件；坐标系在装配体组件本体上时传 null。</param>
        /// <param name="frontCoordName">前端坐标系名称。</param>
        /// <param name="rearCoordName">后端坐标系名称。</param>
        /// <returns>配合创建成功返回 true，否则返回 false。</returns>
        public static bool AddMateUseCoord(
            ISldWorks swApp,
            AssemblyDoc swAssDoc,
            Component2 frontChild,
            Component2 rearChild,
            string frontCoordName = "出",
            string rearCoordName = "进")
        {
            if (swApp == null || swAssDoc == null) return false;

            var swAssModel = swAssDoc as IModelDoc2 ?? swApp.IActiveDoc2 as IModelDoc2;
            if (swAssModel == null) return false;

            try
            {
                int errors = 0;
                swApp.ActivateDoc3(swAssModel.GetTitle(), true, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
            }
            catch
            {
                // ignore
            }

            swAssModel.ClearSelection2(true);

            try
            {
                var ok1 = TrySelectCoordinateSystem(swAssModel, frontCoordName, frontChild, append: false);
                if (!ok1) return false;

                var ok2 = TrySelectCoordinateSystem(swAssModel, rearCoordName, rearChild, append: true);
                if (!ok2) return false;

                // 通过已选中的两个坐标系创建配合。
                var swMate = swAssDoc.AddMate5(
                    (int)swMateType_e.swMateCOORDINATE,
                    (int)swMateAlign_e.swAlignNONE,
                    false,
                    0,
                    0.001,
                    0.001,
                    0.001,
                    0.001,
                    0,
                    0,
                    0,
                    false,
                    false,
                    0,
                    out var longstatus);
                return swMate != null;
            }
            catch (Exception e)
            {
                Debug.Print(e.StackTrace);
                return false;
            }
            finally
            {
                swAssModel.ClearSelection2(true);
            }
        }

        /// <summary>
        /// 在顶层装配体文档中选择指定组件（或子件）上的坐标系。
        /// </summary>
        /// <param name="assemblyModel">顶层装配体 ModelDoc。</param>
        /// <param name="coordName">坐标系特征名称。</param>
        /// <param name="childComponent">装配体组件内部的子件实例；若坐标系在装配体组件本体上则传 null。</param>
        /// <param name="append">是否以追加方式选择（true：保留已有选择）。</param>
        /// <returns>选择成功返回 true，否则返回 false。</returns>
        private static bool TrySelectCoordinateSystem(IModelDoc2 assemblyModel, string coordName, Component2 childComponent, bool append)
        {
            if (assemblyModel == null) return false;
            if (string.IsNullOrWhiteSpace(coordName)) return false;

            // 1) 坐标系在装配体根级（不属于任何组件）
            if (childComponent == null)
            {
                return assemblyModel.Extension.SelectByID2($@"{coordName}", "COORDSYS", 0, 0, 0, append, 0, null, 0);
            }

            // 3) 坐标系在装配体内部子件上，需要拼出选择路径。
            var childPath = childComponent != null ? childComponent.GetSelectByIDString() : null;
            return assemblyModel.Extension.SelectByID2($@"{coordName}@{childPath}", "COORDSYS", 0, 0, 0, append, 0, null, 0);
        }

        /// <summary>
        /// 隐藏列表中的所有 <see cref="FreeDrawBody"/>。
        /// </summary>
        /// <param name="fdBodys">FreeDrawBody 列表。</param>
        public static void HideAllFreeBodys(this List<FreeDrawBody> fdBodys)
        {
            if (fdBodys?.Count > 0)
            {
                fdBodys.ForEach(x => x.Hide());
            }
        }

        /// <summary>
        /// 显示列表中的所有 <see cref="FreeDrawBody"/>。
        /// </summary>
        /// <param name="fdBodys">FreeDrawBody 列表。</param>
        public static void ShowAllFreeBodys(this List<FreeDrawBody> fdBodys)
        {
            if (fdBodys?.Count > 0)
            {
                fdBodys.ForEach(x => x.Show());
            }
        }

        /// <summary>
        /// 判断点是否在面内
        /// </summary>
        /// <param name="p"></param>
        /// <param name="face"></param>
        /// <returns></returns>
        public static bool JudgePointInFace(this Point3d p, Face2 face)
        {
            var listSegs = GetOuterFourSegments(face);

            return listSegs.All(x => p.ProjectionTo(x.ToLine).JudgePointInSegment(x));
        }

        /// <summary>
        /// 判断点是否在面内
        /// </summary>
        /// <param name="p"></param>
        /// <param name="face"></param>
        /// <returns></returns>
        public static bool JudgePointInFace(this Point3d p, List<Segment3d> listSegs)
        {
            return listSegs.All(x => p.ProjectionTo(x.ToLine).JudgePointInSegment(x));
        }

        /// <summary>
        /// 获取面最小矩形
        /// </summary>
        /// <param name="face"></param>
        /// <returns></returns>
        public static List<Segment3d> GetOuterFourSegments(Face2 face)
        {
            var edges = FaceUtility.GetFaceOuter(face);

            var segments = new List<object>();
            foreach (var edge in edges)
            {
                var cur = edge.IGetCurve();
                if (cur.IsLine())
                {
                    segments.Add(edge.EdgeToSegment1000());
                }
                else if (cur.IsCircle())
                {
                    var cir = edge.ToCircle3d(face.ToPlane3d().Normal);

                    cir = new Circle3d(cir.Center.UnitX1000Copy(), cir.R * 1000.0, cir.Normal.Normalized);
                    segments.Add(cir);
                }
            }

            if (ProcessGeometries2(segments,
                    out Segment3d[] segment3ds, out Point3d center))
            {
                return segment3ds.ToList();
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// 将边转换为线段（单位：mm*1000）
        /// </summary>
        /// <param name="edge">SolidWorks 边对象</param>
        /// <param name="swApp">SolidWorks 应用程序对象</param>
        /// <returns>转换后的线段对象</returns>
        public static Segment3d EdgeToSegment1000(this Edge edge, ISldWorks swApp = null)
        {
            return new Segment3d(EdgeUtility.GetStart(edge).UnitX1000Copy(), EdgeUtility.GetEnd(edge).UnitX1000Copy());
        }

        /// <summary>
        /// 判断线段和圆的集合是否共面，并计算平面上的最小包围矩形（支持倾斜平面）
        /// </summary>
        /// <param name="geometries">输入的线段或圆的集合</param>
        /// <param name="rectangleEdges">输出：构成最小矩形的四条线段</param>
        /// <param name="rectangleMidpoint">输出：矩形的中心点</param>
        /// <returns>若成功计算则返回true，否则返回false</returns>
        public static bool ProcessGeometries2(IEnumerable<object> geometries, out Segment3d[] rectangleEdges,
            out Point3d rectangleMidpoint)
        {
            GeometRi3D.Tolerance = 0.01;

            // 初始化输出参数
            rectangleEdges = null;
            rectangleMidpoint = null;
            var geometryList = geometries?.ToList() ?? new List<object>();

            // 处理空输入
            if (!geometryList.Any())
                return false;

            // 提取所有几何元素的关键点（线段端点+圆的采样点）并去重
            var allPoints = new List<Point3d>();
            foreach (var geom in geometryList)
            {
                if (geom is Segment3d segment)
                {
                    // 添加线段端点
                    allPoints.Add(segment.P1);
                    allPoints.Add(segment.P2);
                }
                else if (geom is Circle3d circle)
                {
                    // 对圆进行均匀采样（16个点足够覆盖轮廓）
                    Vector3d normal1 = circle.Normal;
                    Vector3d uAxis = normal1.OrthogonalVector.Normalized; // 圆平面内的U轴
                    Vector3d vAxis = normal1.Cross(uAxis).Normalized; // 圆平面内的V轴（与U轴正交）
                    double radius = circle.R;
                    Point3d center = circle.Center;

                    for (int i = 0; i < 16; i++)
                    {
                        double angle = 2 * Math.PI * i / 16;
                        Point3d samplePoint = center.Move(uAxis, radius * Math.Cos(angle))
                            .Move(vAxis, radius * Math.Sin(angle));

                        allPoints.Add(samplePoint);
                    }
                }
                else
                {
                    // 不支持的几何类型
                    return false;
                }
            }

            // 去重处理（减少计算量）
            allPoints = allPoints.Distinct().ToList();
            if (allPoints.Count < 3)
                return false; // 点数量不足，无法确定平面

            // 寻找不共线的三个点计算平面
            Point3d p0 = allPoints[0];
            Point3d p1 = allPoints[1];
            Point3d p2 = allPoints[2];
            Vector3d v1 = new Vector3d(p0, p1);
            Vector3d v2 = new Vector3d(p0, p2);
            Vector3d normal = v1.Cross(v2);

            // 处理共线情况（所有点共线则无法构成矩形）
            if (normal.Norm < GeometRi3D.Tolerance)
            {
                for (int i = 3; i < allPoints.Count; i++)
                {
                    v2 = new Vector3d(p0, allPoints[i]);
                    normal = v1.Cross(v2);
                    if (normal.Norm > GeometRi3D.Tolerance)
                    {
                        p2 = allPoints[i];
                        break;
                    }
                }

                if (normal.Norm < GeometRi3D.Tolerance)
                    return false; // 所有点共线，无包围矩形
            }

            // 构建平面并验证所有点共面
            Plane3d plane = new Plane3d(p0, normal.Normalized);
            if (!allPoints.All(p => p.BelongsTo(plane)))
                return false;

            try
            {
                // 构建平面的局部UV坐标系
                Vector3d uAxis = v1.Normalized; // U轴：取第一个向量的单位向量
                Vector3d vAxis = normal.Cross(uAxis).Normalized; // V轴：法向量叉乘U轴，保证正交

                // 将所有3D点投影到局部UV坐标系（转换为2D坐标）
                var uvPoints = new List<Tuple<double, double>>();
                foreach (var p in allPoints)
                {
                    Vector3d op = new Vector3d(plane.Point, p); // 从平面原点到点P的向量
                    double u = op.Dot(uAxis); // 投影到U轴的长度
                    double v = op.Dot(vAxis); // 投影到V轴的长度
                    uvPoints.Add(new Tuple<double, double>(u, v));
                }

                // 计算2D UV空间的最小包围盒边界
                double minU = uvPoints.Min(p => p.Item1);
                double maxU = uvPoints.Max(p => p.Item1);
                double minV = uvPoints.Min(p => p.Item2);
                double maxV = uvPoints.Max(p => p.Item2);

                // 将2D包围盒顶点映射回3D平面
                Point3d[] vertices3D = new[]
                {
                    // 顶点1: minU, minV
                    new Point3d(
                        plane.Point.X + (minU * uAxis.X) + (minV * vAxis.X),
                        plane.Point.Y + (minU * uAxis.Y) + (minV * vAxis.Y),
                        plane.Point.Z + (minU * uAxis.Z) + (minV * vAxis.Z)
                    ),
                    // 顶点2: maxU, minV
                    new Point3d(
                        plane.Point.X + (maxU * uAxis.X) + (minV * vAxis.X),
                        plane.Point.Y + (maxU * uAxis.Y) + (minV * vAxis.Y),
                        plane.Point.Z + (maxU * uAxis.Z) + (minV * vAxis.Z)
                    ),
                    // 顶点3: maxU, maxV
                    new Point3d(
                        plane.Point.X + (maxU * uAxis.X) + (maxV * vAxis.X),
                        plane.Point.Y + (maxU * uAxis.Y) + (maxV * vAxis.Y),
                        plane.Point.Z + (maxU * uAxis.Z) + (maxV * vAxis.Z)
                    ),
                    // 顶点4: minU, maxV
                    new Point3d(
                        plane.Point.X + (minU * uAxis.X) + (maxV * vAxis.X),
                        plane.Point.Y + (minU * uAxis.Y) + (maxV * vAxis.Y),
                        plane.Point.Z + (minU * uAxis.Z) + (maxV * vAxis.Z)
                    )
                };

                // 构建3D矩形的四条边
                rectangleEdges = new[]
                {
                    new Segment3d(vertices3D[0], vertices3D[1]), new Segment3d(vertices3D[1], vertices3D[2]),
                    new Segment3d(vertices3D[2], vertices3D[3]), new Segment3d(vertices3D[3], vertices3D[0])
                };

                // 计算矩形中心点（3D空间）
                rectangleMidpoint = new Point3d(
                    vertices3D.Average(p => p.X),
                    vertices3D.Average(p => p.Y),
                    vertices3D.Average(p => p.Z)
                );

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"计算最小包围矩形失败：{ex.Message}");
                return false;
            }
            finally
            {
                GeometRi3D.Tolerance = 1e-6;
            }
        }

        /// <summary>
        /// 判断点是否在线段范围内
        /// </summary>
        /// <param name="point"></param>
        /// <param name="segment"></param>
        /// <returns></returns>
        public static bool JudgePointInSegment(this Point3d point, Segment3d segment, double tolerance = 1e-4)
        {
            if (point.DistanceTo(segment) > tolerance)
            {
                return false;
            }
            else
            {
                double dP1P2 = segment.P1.DistanceTo(segment.P2);
                double dP1 = segment.P1.DistanceTo(point);
                double dP2 = segment.P2.DistanceTo(point);

                if (dP1 - dP1P2 > tolerance || dP2 - dP1P2 > tolerance)
                {
                    return false;
                }
                else
                {
                    return true;
                }
            }
        }

        /// <summary>
        /// 转为Circle3d
        /// </summary>
        /// <param name="edge1">边1 </param>
        /// <returns>圆或弧线</returns>
        public static Circle3d ToCircle3d(this Edge edge, Vector3d Normal)
        {
            var curve1 = (Curve)edge.GetCurve();

            if (!curve1.IsCircle())
            {
                return null;
            }

            Point3d centerPoint = CurveUtility.GetCircleCenter(curve1);
            double R = CurveUtility.GetCircleRadius(curve1);

            var circle3d = new Circle3d(centerPoint, R, Normal);

            return circle3d;
        }

        /// <summary>
        /// 获取组件在母线桥系统中的零件类型。
        /// 按“引用配置的 RD类型、组件 Name2、文档自定义属性 Description”的顺序识别。
        /// </summary>
        /// <param name="component">需要识别的 SolidWorks 组件。</param>
        /// <returns>识别到的系统零件类型；无法识别时返回 <see cref="SystemPartType_e.Unknow"/>。</returns>
        public static SystemPartType_e GetSystemPartType(this Component2 component)
        {
            if (component == null)
            {
                return SystemPartType_e.Unknow;
            }

            ModelDoc2 componentModel = null;
            try
            {
                componentModel = component.IGetModelDoc();
            }
            catch (Exception componentModelException)
            {
                FaceToWallPreviewTrace.Error(
                    "零件类型识别-获取模型",
                    componentModelException,
                    "组件=" + GetComponentInstanceName(component));
                // 轻量化或未解析组件可能暂时无法取得模型，继续用 Name2 识别。
            }

            if (componentModel != null)
            {
                var referencedConfigurationName = GetReferencedConfigurationName(component, componentModel);
                var configuredPartTypeValue = ReadModelCustomProperty(
                    componentModel,
                    referencedConfigurationName,
                    SystemPartTypeConfigurationPropertyName);
                var configuredSystemPartType = ParseSystemPartType(configuredPartTypeValue);
                if (configuredSystemPartType != SystemPartType_e.Unknow)
                {
                    return configuredSystemPartType;
                }
            }

            var componentInstanceName = GetComponentInstanceName(component);
            var namedSystemPartType = ParseSystemPartType(componentInstanceName);
            if (namedSystemPartType != SystemPartType_e.Unknow)
            {
                return namedSystemPartType;
            }

            if (componentModel != null)
            {
                var componentDescriptionValue = ReadModelCustomProperty(
                    componentModel,
                    string.Empty,
                    SystemPartDescriptionPropertyName);
                var describedSystemPartType = ParseSystemPartType(componentDescriptionValue);
                if (describedSystemPartType != SystemPartType_e.Unknow)
                {
                    return describedSystemPartType;
                }
            }

            return SystemPartType_e.Unknow;
        }

        /// <summary>
        /// 只读取组件引用配置或文档级的“RD类型”，不使用组件名称或 Description 回退。
        /// 用于必须由模型业务属性确定身份的结构识别。
        /// </summary>
        public static SystemPartType_e GetConfiguredRdSystemPartType(
            this Component2 component)
        {
            if (component == null)
            {
                return SystemPartType_e.Unknow;
            }

            try
            {
                var componentModel = component.IGetModelDoc();
                if (componentModel == null)
                {
                    return SystemPartType_e.Unknow;
                }

                var referencedConfigurationName =
                    GetReferencedConfigurationName(component, componentModel);
                var configuredPartTypeValue = ReadModelCustomProperty(
                    componentModel,
                    referencedConfigurationName,
                    SystemPartTypeConfigurationPropertyName);
                var configuredSystemPartType =
                    ParseSystemPartType(configuredPartTypeValue);
                if (configuredSystemPartType != SystemPartType_e.Unknow)
                {
                    return configuredSystemPartType;
                }

                var documentPartTypeValue = ReadModelCustomProperty(
                    componentModel,
                    string.Empty,
                    SystemPartTypeConfigurationPropertyName);
                return ParseSystemPartType(documentPartTypeValue);
            }
            catch (Exception configuredRdTypeException)
            {
                FaceToWallPreviewTrace.Error(
                    "零件类型识别-RD类型严格读取",
                    configuredRdTypeException,
                    "组件=" + GetComponentInstanceName(component));
                return SystemPartType_e.Unknow;
            }
        }

        private static string GetReferencedConfigurationName(Component2 component, ModelDoc2 componentModel)
        {
            try
            {
                var referencedConfigurationName = component.ReferencedConfiguration;
                if (!string.IsNullOrWhiteSpace(referencedConfigurationName))
                {
                    return referencedConfigurationName;
                }
            }
            catch (Exception referencedConfigurationException)
            {
                FaceToWallPreviewTrace.Error(
                    "零件类型识别-引用配置",
                    referencedConfigurationException,
                    "组件=" + GetComponentInstanceName(component));
                // 组件引用配置不可用时，回退到当前活动配置。
            }

            try
            {
                return componentModel.ConfigurationManager.ActiveConfiguration.Name;
            }
            catch (Exception activeConfigurationException)
            {
                FaceToWallPreviewTrace.Error(
                    "零件类型识别-活动配置",
                    activeConfigurationException,
                    "组件=" + GetComponentInstanceName(component));
                return string.Empty;
            }
        }

        private static string ReadModelCustomProperty(
            ModelDoc2 componentModel,
            string configurationName,
            string propertyName)
        {
            if (componentModel == null || string.IsNullOrWhiteSpace(propertyName))
            {
                return string.Empty;
            }

            try
            {
                var customPropertyManager = componentModel.Extension.CustomPropertyManager[
                    configurationName ?? string.Empty];
                if (customPropertyManager == null)
                {
                    return string.Empty;
                }

                string rawPropertyValue;
                string resolvedPropertyValue;
                customPropertyManager.Get2(propertyName, out rawPropertyValue, out resolvedPropertyValue);
                return !string.IsNullOrWhiteSpace(resolvedPropertyValue)
                    ? resolvedPropertyValue.Trim()
                    : (rawPropertyValue ?? string.Empty).Trim();
            }
            catch (Exception customPropertyException)
            {
                FaceToWallPreviewTrace.Error(
                    "零件类型识别-自定义属性",
                    customPropertyException,
                    "配置=" + (configurationName ?? string.Empty) +
                    "，属性=" + propertyName +
                    "，模型运行时类型=" + ((object)componentModel).GetType().FullName);
                return string.Empty;
            }
        }

        private static string GetComponentInstanceName(Component2 component)
        {
            string componentFullInstanceName;
            try
            {
                componentFullInstanceName = component.Name2;
            }
            catch (Exception componentNameException)
            {
                FaceToWallPreviewTrace.Error(
                    "零件类型识别-组件名称",
                    componentNameException);
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(componentFullInstanceName))
            {
                return string.Empty;
            }

            var componentLeafInstanceName = componentFullInstanceName.Trim();
            var hierarchySeparatorIndex = componentLeafInstanceName.LastIndexOf('/');
            if (hierarchySeparatorIndex >= 0)
            {
                componentLeafInstanceName = componentLeafInstanceName.Substring(hierarchySeparatorIndex + 1);
            }

            var selectionPathSeparatorIndex = componentLeafInstanceName.IndexOf('@');
            if (selectionPathSeparatorIndex >= 0)
            {
                componentLeafInstanceName = componentLeafInstanceName.Substring(0, selectionPathSeparatorIndex);
            }

            return componentLeafInstanceName.Trim();
        }

        private static SystemPartType_e ParseSystemPartType(string partTypeSourceText)
        {
            if (string.IsNullOrWhiteSpace(partTypeSourceText))
            {
                return SystemPartType_e.Unknow;
            }

            var normalizedPartTypeText = partTypeSourceText.Trim();
            foreach (SystemPartType_e candidateSystemPartType in Enum.GetValues(typeof(SystemPartType_e)))
            {
                if (candidateSystemPartType == SystemPartType_e.Unknow)
                {
                    continue;
                }

                if (string.Equals(
                        normalizedPartTypeText,
                        candidateSystemPartType.ToString(),
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        normalizedPartTypeText,
                        EnumHelper.GetDescription(candidateSystemPartType),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return candidateSystemPartType;
                }
            }

            if (normalizedPartTypeText.IndexOf("基座", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return SystemPartType_e.Base;
            }

            if (normalizedPartTypeText.IndexOf("基架", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return SystemPartType_e.Frame;
            }

            if (normalizedPartTypeText.IndexOf("母线框", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return SystemPartType_e.Frame;
            }

            if (normalizedPartTypeText.IndexOf("柜顶伸出排", StringComparison.OrdinalIgnoreCase) >= 0
                || normalizedPartTypeText.IndexOf("柜内伸出排", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return SystemPartType_e.CabinetTopExtensionBusbar;
            }

            if (normalizedPartTypeText.IndexOf("绝缘子", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return SystemPartType_e.Insulator;
            }

            if (normalizedPartTypeText.IndexOf(
                    "穿墙套管安装板",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return SystemPartType_e.Unknow;
            }

            if (normalizedPartTypeText.IndexOf("穿墙套管", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(normalizedPartTypeText, "套管", StringComparison.OrdinalIgnoreCase))
            {
                return SystemPartType_e.WallBushing;
            }

            if (normalizedPartTypeText.IndexOf("垫块", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return SystemPartType_e.SupportBlock;
            }

            return SystemPartType_e.Unknow;
        }

        /// <summary>
        /// 获取零件的类型
        /// </summary>
        /// <param name="modelDoc2"></param>
        /// <returns></returns>
        public static PartType_e GetModelType(this ModelDoc2 modelDoc2, string refconfigName = null)
        {
            try
            {
                if (modelDoc2 == null)
                {
                    return PartType_e.Unknow;
                }
                var swCusMgr = modelDoc2.Extension.CustomPropertyManager[modelDoc2.ConfigurationManager.ActiveConfiguration.Name];

                if (string.IsNullOrWhiteSpace(swCusMgr.Get(CommonString.PropertyPartType)))
                {
                    swCusMgr = modelDoc2.Extension.CustomPropertyManager[""];
                }
                if (string.IsNullOrWhiteSpace(swCusMgr.Get(CommonString.PropertyPartType)))
                {
                    swCusMgr = modelDoc2.Extension.CustomPropertyManager[refconfigName];
                }

                if (swCusMgr == null)
                {
                    return PartType_e.Unknow;
                }
                var partType = swCusMgr.Get(CommonString.PropertyPartType);

                if (string.IsNullOrWhiteSpace(partType))
                {
                    return PartType_e.Unknow;
                }

                return GetPartTypeByName(partType);
            }
            catch (Exception ex)
            {
                return PartType_e.Unknow;
            }

        }

        /// <summary>
        /// 通过名字获取零件类型
        /// </summary>
        private static PartType_e GetPartTypeByName(string ptName)
        {


            if (string.IsNullOrWhiteSpace(ptName))
            {
                return PartType_e.Unknow;
            }
            return EnumHelper.GetEnum<PartType_e>(ptName);

        }

        /// <summary>
        /// 判断组件是否为自由立弯铜排。优先使用生成时写入的“铜排形状”
        /// 属性，并兼容旧模型及当前生成器的钣金基体特征名称。
        /// </summary>
        public static bool IsVerticalBendBusbar(this Component2 component)
        {
            if (component == null)
            {
                return false;
            }

            try
            {
                var componentModel = component.IGetModelDoc() as ModelDoc2;
                var componentPart = componentModel as PartDoc;
                if (componentModel == null || componentPart == null)
                {
                    return false;
                }

                var referencedConfigurationName =
                    GetReferencedConfigurationName(component, componentModel);
                var configuredShapeValue = ReadModelCustomProperty(
                    componentModel,
                    referencedConfigurationName,
                    BusbarShapePropertyName);
                if (string.IsNullOrWhiteSpace(configuredShapeValue))
                {
                    configuredShapeValue = ReadModelCustomProperty(
                        componentModel,
                        string.Empty,
                        BusbarShapePropertyName);
                }

                if (string.Equals(
                        configuredShapeValue,
                        VerticalBendShapePropertyValue,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (componentPart.IFeatureByName("立弯铜排基体") != null)
                {
                    return true;
                }

                for (var verticalBaseFeatureIndex = 1;
                    verticalBaseFeatureIndex < 20;
                    verticalBaseFeatureIndex++)
                {
                    if (componentPart.IFeatureByName(
                            "立弯铜排基体" +
                            verticalBaseFeatureIndex) != null)
                    {
                        return true;
                    }
                }
            }
            catch (Exception verticalBendDetectionException)
            {
                FaceToWallPreviewTrace.Error(
                    "自由立弯识别失败",
                    verticalBendDetectionException,
                    "组件=" + GetComponentInstanceName(component));
            }

            return false;
        }

        /// <summary>
        /// 获取零部件形状草图中的有序线段列表（已转换到装配体坐标系）
        /// </summary>
        /// <param name="swApp">SolidWorks 应用程序对象</param>
        /// <param name="comp2">零部件对象</param>
        /// <returns>按顺序排列的线段列表，保证从起点到终点的连续性</returns>
        public static List<Segment3d> GetSegmentsInAss(this Component2 comp2, ISldWorks swApp)
        {
            if (comp2 == null) throw new ArgumentNullException(nameof(comp2));
            if (swApp == null) throw new ArgumentNullException(nameof(swApp));

            var centerlineComponentName = BusbarGeometryUtility.SafeName(comp2);
            object centerlineModelDocument;
            try
            {
                centerlineModelDocument = comp2.IGetModelDoc();
            }
            catch (Exception modelDocumentException)
            {
                FaceToWallPreviewTrace.Error(
                    "读取柜顶中心线-获取零件文档",
                    modelDocumentException,
                    "组件=" + centerlineComponentName);
                throw;
            }

            var centerlinePartDocument = centerlineModelDocument as PartDoc;
            if (centerlinePartDocument == null)
            {
                var runtimeDocumentType = centerlineModelDocument?.GetType().FullName ?? "null";
                var invalidDocumentMessage =
                    "组件不是可读取形状草图的 PartDoc。组件=" + centerlineComponentName +
                    "，运行时类型=" + runtimeDocumentType;
                FaceToWallPreviewTrace.Warning(
                    "读取柜顶中心线-校验零件文档",
                    invalidDocumentMessage);
                throw new InvalidOperationException(invalidDocumentMessage);
            }

            FaceToWallPreviewTrace.Info(
                "读取柜顶中心线-零件文档",
                "组件=" + centerlineComponentName +
                "，运行时类型=" + centerlineModelDocument.GetType().FullName);

            var isVerticalBendBusbar = comp2.IsVerticalBendBusbar();
            var skPartSegments = GetBusbarCenterlineSketchSegments(
                centerlinePartDocument,
                isVerticalBendBusbar,
                out var centerlineSketchName);
            ////铜排草图已排序
            var mySegments = Segment3dUtility.OrderSegment3ds(skPartSegments);
            if (mySegments == null || mySegments.Count == 0)
            {
                var missingCenterlineMessage =
                    "组件的中心线草图没有可用直线。组件=" + centerlineComponentName +
                    "，草图=" + centerlineSketchName;
                FaceToWallPreviewTrace.Warning(
                    "读取柜顶中心线-草图为空",
                    missingCenterlineMessage);
                throw new InvalidOperationException(missingCenterlineMessage);
            }

            var p1 = mySegments[0].StartPoint;
            var p2 = mySegments[mySegments.Count - 1].EndPoint;
            if (p1.DistanceTo(new Point3d()) - p2.DistanceTo(new Point3d()) > 0.1)
            {
                for (int i = 0; i < mySegments.Count; i++)
                {
                    mySegments[i] = new AutoOpeningHoleDataModel.MySegment(mySegments[i].EndPoint, mySegments[i].StartPoint);
                }

                mySegments.Reverse();
            }

            var segments = mySegments.Select(x =>
                new Segment3d(x.StartPoint.ConvertPointInPartToAssembly1000(swApp, comp2)
                    , x.EndPoint.ConvertPointInPartToAssembly1000(swApp, comp2))).ToList();

            FaceToWallPreviewTrace.Info(
                "读取柜顶中心线-完成",
                "组件=" + centerlineComponentName +
                "，草图=" + centerlineSketchName +
                "，线段数=" + segments.Count);

            return segments;
        }

        /// <summary>
        /// 获取钣金零件的所有线段
        /// </summary>
        /// <param name="partDoc"></param>
        /// <param name="sketchName">形状草图</param>
        /// <returns></returns>
        public static List<MySegment> GetMySegment(this PartDoc partDoc, string sketchName = "形状草图")
        {
            if (partDoc == null) throw new ArgumentNullException(nameof(partDoc));

            var mySegments = new List<MySegment>();
            try
            {
                var sketchFea = partDoc.FeatureByName(sketchName) as Feature;
                if (sketchFea == null)
                {
                    FaceToWallPreviewTrace.Warning(
                        "读取柜顶中心线-查找草图",
                        "未找到草图=" + sketchName);
                    return mySegments;
                }

                var rawSketchObject = sketchFea.GetSpecificFeature2();
                var sketch = rawSketchObject as Sketch;
                if (sketch == null)
                {
                    FaceToWallPreviewTrace.Warning(
                        "读取柜顶中心线-转换草图",
                        "特征无法转换为 Sketch。草图=" + sketchName +
                        "，运行时类型=" +
                        (rawSketchObject?.GetType().FullName ?? "null"));
                    return mySegments;
                }

                var skLines = sketch.GetSketchSegments() as object[];
                if (skLines != null)
                {
                    foreach (var rawSketchSegment in skLines)
                    {
                        var swSketchSeg = rawSketchSegment as SketchSegment;
                        if (swSketchSeg == null)
                        {
                            FaceToWallPreviewTrace.Warning(
                                "读取柜顶中心线-转换草图段",
                                "草图段无法转换为 SketchSegment。草图=" + sketchName +
                                "，运行时类型=" +
                                (rawSketchSegment?.GetType().FullName ?? "null"));
                            continue;
                        }

                        if (!swSketchSeg.ConstructionGeometry)
                        {
                            if ((int)swSketchSegments_e.swSketchLINE == swSketchSeg.GetType())
                            {
                                var sketchSeg = swSketchSeg as SketchLine;
                                if (sketchSeg == null)
                                {
                                    FaceToWallPreviewTrace.Warning(
                                        "读取柜顶中心线-转换直线",
                                        "线型草图段无法转换为 SketchLine。草图=" + sketchName +
                                        "，运行时类型=" +
                                        ((object)swSketchSeg).GetType().FullName);
                                    continue;
                                }

                                var tempId = swSketchSeg.GetSelectId();

                                //var tempString = swSketchSeg.GetName();
                                var tempSegment = new MySegment(sketchSeg.IGetStartPoint2().ConvertToPoint3d().ConvertSkPointToModelPoint3d(sketch), sketchSeg.IGetEndPoint2().ConvertToPoint3d().ConvertSkPointToModelPoint3d(sketch), swSketchSeg.GetName());

                                tempSegment.Id = tempId;
                                //tempSegment.SketchSegment = swSketchSeg;

                                mySegments.Add(tempSegment);
                            }
                        }
                    }
                }
            }
            catch (Exception sketchReadException)
            {
                FaceToWallPreviewTrace.Error(
                    "读取柜顶中心线-解析草图",
                    sketchReadException,
                    "草图=" + sketchName);
                throw;
            }

            return mySegments;
        }

        /// <summary>
        /// 读取铜排完整中心路径。旧立弯模型使用“基础草图”，当前立弯
        /// 生成器创建的完整三维路径使用“原始草图”，两种模型均需兼容。
        /// </summary>
        private static List<MySegment> GetBusbarCenterlineSketchSegments(
            PartDoc busbarPartDocument,
            bool isVerticalBendBusbar,
            out string selectedSketchName)
        {
            var centerlineSketchCandidates = isVerticalBendBusbar
                ? new[] { "基础草图", "原始草图", "形状草图" }
                : new[] { "形状草图" };

            foreach (var centerlineSketchCandidate in centerlineSketchCandidates)
            {
                var candidateSegments = busbarPartDocument.GetMySegment(
                    centerlineSketchCandidate);
                if (candidateSegments.Count > 0)
                {
                    selectedSketchName = centerlineSketchCandidate;
                    return candidateSegments;
                }
            }

            selectedSketchName = string.Join("/", centerlineSketchCandidates);
            return new List<MySegment>();
        }

        /// <summary>
        /// 获取零部件形状草图中的有序线段列表
        /// <param name="swApp">SolidWorks 应用程序对象</param>
        /// <param name="comp2">零部件对象</param>
        /// <returns>按顺序排列的线段列表，保证从起点到终点的连续性</returns>
        public static List<Segment3d> GetSegmentsInPart(this Component2 comp2,ISldWorks swApp)
        {
            var busbarPartDocument = (PartDoc)comp2.IGetModelDoc();
            var skPartSegments = GetBusbarCenterlineSketchSegments(
                busbarPartDocument,
                comp2.IsVerticalBendBusbar(),
                out var centerlineSketchName);
            ////铜排草图已排序
            var mySegments = Segment3dUtility.OrderSegment3ds(skPartSegments);

            if (mySegments == null || mySegments.Count == 0)
            {
                throw new InvalidOperationException(
                    "组件的中心线草图没有可用直线。组件=" +
                    BusbarGeometryUtility.SafeName(comp2) +
                    "，草图=" + centerlineSketchName);
            }

            var p1 = mySegments[0].StartPoint;
            var p2 = mySegments[mySegments.Count - 1].EndPoint;
            if (p1.DistanceTo(new Point3d()) - p2.DistanceTo(new Point3d()) > 0.1)
            {
                for (int i = 0; i < mySegments.Count; i++)
                {
                    mySegments[i] = new AutoOpeningHoleDataModel.MySegment(mySegments[i].EndPoint, mySegments[i].StartPoint);
                }

                mySegments.Reverse();
            }

            var segments = mySegments.Select(x =>
                new Segment3d(x.StartPoint, x.EndPoint)).ToList();

            return segments;
        }

        /// <summary>
        /// 获取线段的中点坐标
        /// </summary>
        /// <param name="segment3D">输入线段</param>
        /// <returns>线段中点坐标</returns>
        public static Point3d GetMiddlePoint2(this Segment3d segment3D)
        {
            return segment3D.P1.Move(segment3D.ToVector, segment3D.Length / 2);
        }

        /// <summary>
        /// 将边转换为装配体坐标系下的线段（单位：mm*1000）
        /// </summary>
        /// <param name="edge">SolidWorks 边对象</param>
        /// <param name="swApp">SolidWorks 应用程序对象</param>
        /// <param name="comp2">零部件对象</param>
        /// <returns>装配体坐标系下的线段</returns>
        public static Segment3d EdgeToSegmentInAss1000(this Edge edge, ISldWorks swApp, Component2 comp2)
        {
            return edge.EdgeToSegment1000(swApp).ConvertSegment3dInPartToAssembly1000(swApp, comp2);
        }

        /// <summary>
        /// 获取obj上的面
        /// </summary>
        /// <param name="swApp"></param>
        /// <param name="tempCompA"></param>
        /// <param name="obj"></param>
        /// <returns></returns>
        public static Face2 GetFaceByObjInAss(this ISldWorks swApp, Component2 tempComp, object obj, double d = 0.01)
        {
            try
            {
                if (tempComp == null) return null;

                ModelDoc2 swModel;

                swModel = (tempComp as Component2).IGetModelDoc();

                Segment3d segment = null;
                MySegment myseg = null;
                Point3d point = null;
                if (obj is Segment3d)
                {
                    segment = obj as Segment3d;
                }
                else if (obj is MySegment)
                {
                    myseg = obj as MySegment;
                }
                else if (obj is Point3d)
                {
                    point = obj as Point3d;
                }
                else
                {
                    return null;
                }

                List<Face2> allFaces = new List<Face2>();


                var allBodys = ((swModel as PartDoc)?.GetBodies(0) as object[])?.Cast<Body2>();

                allFaces = allBodys.SelectMany(x =>
                    (x?.GetFaces() as object[])?.Select(tempComp.GetCorrespondingEntity).Cast<Face2>()).ToList();
                allFaces = allFaces.Where(x => x.ToPlane3d() != null)
                    .OrderByDescending(x => Math.Round(x.GetArea(), 10)).ToList();

                List<Face2> face2s = new List<Face2>();
                foreach (var face in allFaces)
                {
                    var facePlane = face.ToPlane3d();

                    if (facePlane == null)
                    {
                        continue;
                    }

                    var dist1 = 1.0;
                    var dist2 = 1.0;

                    if (obj is Segment3d)
                    {
                        dist1 = segment.P1.DistanceTo(facePlane);
                        dist2 = segment.P2.DistanceTo(facePlane);
                    }
                    else if (obj is MySegment)
                    {
                        dist1 = myseg.StartPoint.DistanceTo(facePlane);
                        dist2 = myseg.EndPoint.DistanceTo(facePlane);
                    }
                    else
                    {
                        dist1 = dist2 = point.DistanceTo(facePlane);
                    }

                    if (dist1 < d && dist2 < d)
                    {
                        Point3d thisP = null;

                        if (obj is Segment3d)
                        {
                            thisP = segment.GetMiddlePoint2();
                        }
                        else if (obj is MySegment)
                        {
                            thisP = myseg.CenterPoint;
                        }
                        else
                        {
                            thisP = point;
                        }

                        if (thisP.JudgePointInFace(face))
                        {
                            return face;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                throw;
            }

            return null;
        }


        /// <summary>
        /// 给一系列相连的线段排序，返回排列好的List
        /// </summary>
        /// <param name="mySegments"></param>
        /// <returns></returns>
        public static List<MySegment> OrderSegment3ds(List<MySegment> mySegments)
        {
            if (mySegments.Count == 0)
            {
                return null;
            }


            if (!mySegments.Any(x => x.IsUsed == false))
            {
                return mySegments;
            }


            //List<MySegment> mySegments = new List<MySegment>();
            //mySegments.Move(new MySegment(new Point3d(-58, -19, 0), new Point3d(-15, 23, 0), 1));
            //mySegments.Move(new MySegment(new Point3d(-15, 23, 0), new Point3d(36, 23, 0), 2));
            //mySegments.Move(new MySegment(new Point3d(80.32, 56.24, 0), new Point3d(36, 23, 0), 3));
            //mySegments.Move(new MySegment(new Point3d(80.32, 56.24, 0), new Point3d(55, 90, 0), 4));

            var keyValuePairs = new Dictionary<Point3d, int>();

            foreach (var seg in mySegments)
            {
                if (keyValuePairs.Keys.Contains(seg.StartPoint))
                {
                    keyValuePairs[seg.StartPoint] = keyValuePairs[seg.StartPoint] + 1;
                }
                else
                {
                    keyValuePairs[seg.StartPoint] = 1;
                }
                if (keyValuePairs.Keys.Contains(seg.EndPoint))
                {
                    keyValuePairs[seg.EndPoint] = keyValuePairs[seg.EndPoint] + 1;
                }
                else
                {
                    keyValuePairs[seg.EndPoint] = 1;
                }
            }

            var ployStartAndEndPoint = keyValuePairs.Where(x => x.Value == 1).Select(y => y.Key).ToList();

            Point3d startPoint = null;

            Point3d endPoint = null;

            var mySegmentsOrdered = new List<MySegment>();

            if (ployStartAndEndPoint.Count == 2)
            {
                if (Math.Abs(ployStartAndEndPoint[0].X) < 0.01 && Math.Abs(ployStartAndEndPoint[0].Y) < 0.01 && Math.Abs(ployStartAndEndPoint[0].Z) < 0.01)
                {
                    startPoint = ployStartAndEndPoint[0];
                    endPoint = ployStartAndEndPoint[1];
                }
                else if (Math.Abs(ployStartAndEndPoint[1].X) < 0.01 && Math.Abs(ployStartAndEndPoint[1].Y) < 0.01 && Math.Abs(ployStartAndEndPoint[1].Z) < 0.01)
                {
                    endPoint = ployStartAndEndPoint[1];
                    startPoint = ployStartAndEndPoint[0];
                }
                else
                {
                    var tempSeg1 = mySegments.FirstOrDefault(x => x.IsUsed == false && (x.StartPoint == ployStartAndEndPoint[0] || x.EndPoint == ployStartAndEndPoint[0]));
                    var tempSeg2 = mySegments.FirstOrDefault(x => x.IsUsed == false && (x.StartPoint == ployStartAndEndPoint[1] || x.EndPoint == ployStartAndEndPoint[1]));

                    if (new Segment3d(tempSeg1.StartPoint, tempSeg1.EndPoint).DistanceTo(new Point3d()) < new Segment3d(tempSeg2.StartPoint, tempSeg2.EndPoint).DistanceTo(new Point3d()))
                    {
                        startPoint = ployStartAndEndPoint[0];
                        endPoint = ployStartAndEndPoint[1];
                    }
                    else
                    {
                        startPoint = ployStartAndEndPoint[0];
                        endPoint = ployStartAndEndPoint[1];
                    }

                    //endPoint = ployStartAndEndPoint[1];
                    //startPoint = ployStartAndEndPoint[0];
                }
            }
            else
            {
                startPoint = mySegments[0].StartPoint;
                endPoint = mySegments[0].EndPoint;
            }

            //起点直线
            var firstLine = mySegments.FirstOrDefault(x => x.IsUsed == false && (x.StartPoint == startPoint || x.EndPoint == startPoint));

            if (firstLine != null)
            {
                var tempEndPoint = firstLine != null && firstLine.StartPoint == startPoint ? firstLine.EndPoint : firstLine.StartPoint; //起点直线的终点

                var tempSeg1 = new MySegment(startPoint, tempEndPoint, firstLine.Id, firstLine.SelectIdInSketch);
                tempSeg1.IsCircle = firstLine.IsCircle;
                tempSeg1.R = firstLine.R;
                tempSeg1.CenterPoint = firstLine.CenterPoint;

                var boolStartIsEnd1 = firstLine.StartPoint == tempEndPoint ? true : false; //起点直线的终点
                tempSeg1.StartIsEnd = boolStartIsEnd1;

                mySegmentsOrdered.Add(tempSeg1);



                firstLine.IsUsed = true;

                do
                {
                    var nextLine = mySegments.FirstOrDefault(x => x.IsUsed == false && (x.StartPoint == tempEndPoint || x.EndPoint == tempEndPoint));

                    if (nextLine == null)
                    {
                        break;
                    }

                    var tempNextEndPoint = nextLine.StartPoint == tempEndPoint ? nextLine.EndPoint : nextLine.StartPoint; //起点直线的终点
                    var boolStartIsEnd = nextLine.StartPoint == tempEndPoint ? true : false; //起点直线的终点

                    var tempSeg = new MySegment(tempEndPoint, tempNextEndPoint, nextLine.Id, nextLine.SelectIdInSketch);
                    tempSeg.IsCircle = nextLine.IsCircle;
                    tempSeg.R = nextLine.R;
                    tempSeg.CenterPoint = nextLine.CenterPoint;
                    tempSeg.StartIsEnd = boolStartIsEnd;

                    mySegmentsOrdered.Add(tempSeg);

                    nextLine.IsUsed = true;

                    tempEndPoint = tempNextEndPoint;
                } while (tempEndPoint != endPoint);
            }

            return mySegmentsOrdered;
        }
    }
}
