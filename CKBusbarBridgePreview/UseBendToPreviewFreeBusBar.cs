using GeometRi;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using AutoOpeningHoleDataModel;

namespace CKBusbarBridgePreview
{
    public static class UseBendToPreviewFreeBusBar
    {
        /// <summary>
        /// 铜排折弯预览体生成核心方法（支持平弯+立弯混合场景）
        /// 【核心功能】根据折弯配置参数（BendPreviewSetting）解析路径点，区分平弯/立弯段，执行点位清理、线段偏移、轮廓拉伸，最终生成合并后的预览体（FreeDrawBody）
        /// 【输入要求】BendPreviewSetting需包含完整的路径点、折弯半径、板厚/排宽、方向向量等参数
        /// 【返回值】生成成功返回FreeDrawBody（含预览体几何/组件/向量信息），失败返回null
        /// </summary>
        /// <param name="iSwApp">SolidWorks应用程序实例（核心API调用入口）</param>
        /// <param name="bend">折弯预览配置对象（包含路径点、折弯半径、板厚/排宽等核心参数）</param>
        /// <param name="color">预览体显示颜色（区分不同相位/类型的铜排）</param>
        /// <param name="swComp">预览体所属组件（可选，为空则自动查找有效组件）</param>
        /// <param name="bVector">是否仅计算向量（调试用，true则返回null，不生成实体）</param>
        /// <returns>FreeDrawBody：封装预览体几何、组件、最后段向量等信息；失败返回null</returns>
        public static FreeDrawBody PreviewBendBody(SldWorks iSwApp, BendPreviewSetting bend, Color color,
            Component2 swComp = null, bool bVector = false)
        {
            // ========== 1. 基础环境初始化：获取当前激活文档 ==========
            ModelDoc2 swDoc = (ModelDoc2)iSwApp.ActiveDoc;

            // ========== 2. 预览组件自动查找（swComp为空时执行） ==========
            if (swComp == null)
            {
                // 2.1 获取装配体中所有模型文档列表（排除隐藏/无效文档）
                List<ModelDoc2> listDoc = GetModelListByTree(iSwApp, swDoc, false, false);
                List<int> listCount = new List<int>();
                GetDocAndCount(iSwApp, ref listDoc, ref listCount);

                // 2.2 优先查找计数=1的组件（单一实例组件，避免重复）
                for (int i = 0; i < listDoc.Count; i++)
                {
                    if (listCount[i] == 1)
                    {
                        try
                        {
                            // 根据文件名获取组件对象
                            swComp = GetComponent(swDoc, listDoc[i].GetFileName());
                            // 跳过隐藏组件，找到第一个非隐藏组件则终止循环
                            if (swComp.IsHidden(false))
                            {
                                continue;
                            }
                            else
                            {
                                break;
                            }
                        }
                        catch (Exception previewComponentException)
                        {
                            FaceToWallPreviewTrace.Error(
                                "铜排实体内核-查找预览组件",
                                previewComponentException);
                            // 组件获取失败直接返回null
                            return null;
                        }
                    }
                }

                // 2.3 若未找到计数=1的组件，遍历所有组件找非隐藏的
                if (swComp == null)
                {
                    for (int i = 0; i < listDoc.Count; i++)
                    {
                        swComp = GetComponent(swDoc, listDoc[i].GetFileName());

                        if (swComp.IsHidden(false))
                        {
                            continue;
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }

            // 2.4 最终组件为空 → 提示无可用预览零部件
            if (swComp == null)
            {
                FaceToWallPreviewTrace.Warning(
                    "铜排预览-组件查找",
                    "无可显示预览零部件。"
                );
                return null;
            }

            // ========== 3. 核心参数提取（从BendPreviewSetting中拆解） ==========
            Vector3d LastVector; // 最后折弯段的方向向量（用于返回给FreeDrawBody）

            // 平弯折弯半径（mm）
            double barBendingRadius = bend.BarBendingRadius;
            // 立弯折弯半径（mm）→ 注释：原逻辑中排宽/2被注释，可根据业务启用
            double barLWRadius = bend.BusBarVerticalRadius /*+ bend.BusBarWidth / 2*/;
            // 铜排厚度（mm）
            double barThiness = bend.BusBarThickness;
            // 铜排宽度（mm）
            double barWidth = bend.BusBarWidth;
            // 宽度方向向量（定义铜排宽度延伸方向）
            Vector3d Wvector = bend.WidthVector;
            // 起始厚度方向向量（定义铜排厚度延伸方向）
            Vector3d startvector3d = bend.StartBusbarVector;
            // 路径点字符串列表（格式：X,Y,Z,平/立）
            List<string> listBox = bend.ListPoint;

            // ========== 4. 路径点分类存储：区分平弯/立弯点 ==========
            List<Point3d> point3Ds = new List<Point3d>(); // 第一段平弯点集合
            List<Point3d> point3Ds2 = new List<Point3d>(); // 第二段平弯点集合（立弯后）
            List<Point3d> Lpoint3Ds = new List<Point3d>(); // 立弯点集合

            // ========== 5. 线段/向量临时存储（偏移计算用） ==========
            List<object> PYsegment3Ds = new List<object>(); // 第一段平弯偏移线段集合
            List<object> PYsegment3Ds2 = new List<object>(); // 第二段平弯偏移线段集合
            Vector3d LVector3d = new Vector3d(); // 立弯厚度方向向量
            Vector3d WVector3d = new Vector3d(); // 立弯宽度方向向量
            Vector3d LWLVector3d = new Vector3d(); // 第二段平弯厚度方向向量
            Vector3d LWWVector3d = new Vector3d(); // 第二段平弯宽度方向向量
            Vector3d LVector3d2 = new Vector3d(); // 备用厚度向量
            Vector3d WVector3d2 = new Vector3d(); // 备用宽度向量

            List<MySegment> segment3Ds = new List<MySegment>(); // 第一段平弯线段集合
            List<MySegment> segment3Ds2 = new List<MySegment>(); // 第二段平弯线段集合
            List<MySegment> Lsegment3Ds = new List<MySegment>(); // 立弯线段集合
            List<object> LPYsegment3Ds = new List<object>(); // 立弯偏移线段集合

            // ========== 6. 路径点解析：拆分平弯/立弯点 ==========
            bool bol = true; // 立弯起始标记（控制首次立弯点处理）
            bool liW = false; // 立弯状态标记（true=正在处理立弯点）
            bool bol2 = true; // 第二段平弯起始标记
            for (int i = 0; i < listBox.Count; i++)
            {
                // 拆分路径点字符串：X,Y,Z,平/立
                string[] str1 = listBox[i].ToString().Split(',');

                // 场景1：平弯点（未进入立弯状态）→ 存入第一段平弯集合
                if (str1[3].ToString().Contains("平") && !liW)
                {
                    Point3d ps = new Point3d(Convert.ToDouble(str1[0]), Convert.ToDouble(str1[1]),
                        Convert.ToDouble(str1[2]));
                    point3Ds.Add(ps);
                }
                // 场景2：立弯点 → 存入立弯集合，同时处理平弯转立弯的过渡点
                else if (str1[3].ToString().Contains("立"))
                {
                    Point3d ps = new Point3d(Convert.ToDouble(str1[0]), Convert.ToDouble(str1[1]),
                        Convert.ToDouble(str1[2]));

                    // 首次处理立弯点：计算平弯段的圆角过渡，生成立弯起始点
                    if (bol)
                    {
                        // 取平弯段最后两段线段
                        MySegment mySegment1 =
                            new MySegment(point3Ds[point3Ds.Count - 3], point3Ds[point3Ds.Count - 2]);
                        MySegment mySegment2 =
                            new MySegment(point3Ds[point3Ds.Count - 2], point3Ds[point3Ds.Count - 1]);

                        // 计算虚拟线段圆角过渡距离（厚度+平弯半径）
                        double PYjl =
                            VirtualLineSegmentFilletCornering2(mySegment1, mySegment2, barThiness + barBendingRadius);

                        // 计算立弯起始中心点（平弯最后点沿向量移动过渡距离+1）
                        Point3d centerP = point3Ds[point3Ds.Count - 2]
                            .Move(new Vector3d(point3Ds[point3Ds.Count - 2], point3Ds[point3Ds.Count - 1]), PYjl + 1);

                        // 更新立弯点集合和平弯点集合（插入过渡中心点）
                        Lpoint3Ds.Add(centerP);
                        Lpoint3Ds.Add(point3Ds[point3Ds.Count - 1]);
                        point3Ds.RemoveAt(point3Ds.Count - 1);
                        point3Ds.Add(centerP);

                        bol = false; // 关闭首次立弯标记
                    }

                    Lpoint3Ds.Add(ps); // 添加当前立弯点
                    liW = true; // 标记进入立弯状态
                }
                // 场景3：平弯点（已进入立弯状态）→ 存入第二段平弯集合，处理立弯转平弯的过渡点
                else if (str1[3].ToString().Contains("平") && liW)
                {
                    Point3d ps = new Point3d(Convert.ToDouble(str1[0]), Convert.ToDouble(str1[1]),
                        Convert.ToDouble(str1[2]));

                    // 首次处理第二段平弯点：计算立弯段的圆角过渡，生成平弯起始点
                    if (bol2)
                    {
                        // 取立弯段最后两段线段
                        MySegment mySegment1 =
                            new MySegment(Lpoint3Ds[Lpoint3Ds.Count - 2], Lpoint3Ds[Lpoint3Ds.Count - 1]);
                        MySegment mySegment2 = new MySegment(Lpoint3Ds[Lpoint3Ds.Count - 1], ps);

                        // 计算虚拟线段圆角过渡距离（厚度+平弯半径）
                        double PYjl =
                            VirtualLineSegmentFilletCornering2(mySegment1, mySegment2, barThiness + barBendingRadius);

                        // 计算第二段平弯起始中心点（立弯最后点沿反向向量移动过渡距离+1）
                        Point3d centerP = Lpoint3Ds[Lpoint3Ds.Count - 1]
                            .Move(new Vector3d(Lpoint3Ds[Lpoint3Ds.Count - 1], Lpoint3Ds[Lpoint3Ds.Count - 2]),
                                PYjl + 1);

                        // 更新第二段平弯点集合和立弯点集合（插入过渡中心点）
                        point3Ds2.Add(centerP);
                        point3Ds2.Add(Lpoint3Ds[Lpoint3Ds.Count - 1]);
                        Lpoint3Ds.RemoveAt(Lpoint3Ds.Count - 1);
                        Lpoint3Ds.Add(centerP);

                        bol2 = false; // 关闭首次第二段平弯标记
                    }

                    point3Ds2.Add(ps); // 添加当前第二段平弯点
                }
            }

            // ========== 7. 第一段平弯点清理与规范化 ==========
            // 7.1 投影到基准平面（确保所有点在同一平面，避免空间偏移）
            if (point3Ds.Count > 1)
            {
                Plane3d plane3D = new Plane3d(point3Ds[0], Wvector);
                for (int i = 1; i < point3Ds.Count; i++)
                {
                    Point3d point3D = point3Ds[i];
                    // 点不在平面上则移动到平面（保证平弯段共面）
                    if (!point3Ds[i].IfPointOntInPlaneMovePointToPlane(plane3D, out point3D, true))
                    {
                        point3Ds[i] = point3D;
                    }
                }
            }

            // 7.2 移除重复点（距离<0.1mm视为同一位置）
            for (int i = 0; i < point3Ds.Count - 1; i++)
            {
                if (point3Ds[i].DistanceTo(point3Ds[i + 1]) < 0.1)
                {
                    point3Ds.RemoveAt(i + 1);
                    i--; // 移除后索引回退，避免漏检
                }
            }

            // 7.3 移除共线点（三点共线则移除中间点）
            for (int i = 0; i < point3Ds.Count - 2; i++)
            {
                if (boolP3InLine3dP1AndP2(point3Ds[i], point3Ds[i + 1], point3Ds[i + 2], 0.01))
                {
                    point3Ds.Remove(point3Ds[i + 1]);
                    i = -1; // 重置索引，重新遍历
                }
            }

            // 暂存清理后的平弯点（用于返回FreeDrawBody的点数量）
            List<Point3d> point3ds = point3Ds;

            // 7.4 平弯点沿宽度方向偏移（偏移距离=排宽/2，生成宽度方向的轮廓）
            for (int i = 0; i < point3Ds.Count; i++)
            {
                point3Ds[i] = point3Ds[i].Move(Wvector, barWidth / 2);
            }

            // 7.5 生成第一段平弯线段集合（清理后的点连成线段）
            for (int i = 0; i < point3Ds.Count - 1; i++)
            {
                segment3Ds.Add(new MySegment(point3Ds[i], point3Ds[i + 1]));
            }

            // 7.6 第一段平弯线段偏移计算（生成折弯后的轮廓线段）
            try
            {
                PYsegment3Ds = OffsetLineSegment(iSwApp, false, segment3Ds, startvector3d, barThiness, barBendingRadius,
                    out LVector3d, out WVector3d);
            }
            catch (Exception firstPlanarOffsetException)
            {
                FaceToWallPreviewTrace.Error(
                    "铜排实体内核-第一段平弯偏移",
                    firstPlanarOffsetException,
                    "点数=" + point3Ds.Count +
                    "，规格=" + barWidth.ToString("0.###") +
                    "x" + barThiness.ToString("0.###"));
            }

            // ========== 8. 立弯点清理与规范化 ==========
            // 8.1 移除共线/重叠点（距离<0.001mm）
            for (int i = 0; i < Lpoint3Ds.Count - 2; i++)
            {
                if (Lpoint3Ds[i].DistanceTo(new Line3d(Lpoint3Ds[i + 1], Lpoint3Ds[i + 2])) < 0.001)
                {
                    // 三点共线则移除中间点
                    if (boolP3InLine3dP1AndP2(Lpoint3Ds[i], Lpoint3Ds[i + 1], Lpoint3Ds[i + 2]))
                    {
                        Lpoint3Ds.Remove(Lpoint3Ds[i + 1]);
                        i--;
                    }
                    // 非共线但重叠 → 提示并返回null
                    else
                    {
                        FaceToWallPreviewTrace.Warning(
                            "铜排实体内核-第一段路径重叠",
                            "数据有重叠，可尝试切换搭接方式。");
                        LastVector = null;
                        return null;
                    }
                }
            }

            // 8.2 投影到基准平面（确保立弯点共面）
            if (Lpoint3Ds.Count > 1)
            {
                Plane3d plane3D = new Plane3d(Lpoint3Ds[0], LVector3d);
                for (int i = 1; i < Lpoint3Ds.Count; i++)
                {
                    Point3d point3D = Lpoint3Ds[i];
                    if (!Lpoint3Ds[i].IfPointOntInPlaneMovePointToPlane(plane3D, out point3D, true))
                    {
                        Lpoint3Ds[i] = point3D;
                    }
                }
            }

            // 8.3 生成立弯线段集合
            for (int i = 0; i < Lpoint3Ds.Count - 1; i++)
            {
                Lsegment3Ds.Add(new MySegment(Lpoint3Ds[i], Lpoint3Ds[i + 1]));
            }

            // 8.4 立弯线段偏移计算（生成立弯后的轮廓线段）
            if (Lsegment3Ds.Count > 0)
            {
                try
                {
                    LPYsegment3Ds = OffsetLineSegment(iSwApp, true, Lsegment3Ds, WVector3d, barWidth, barLWRadius,
                        out LWLVector3d, out LWWVector3d);
                }
                catch (Exception verticalOffsetException)
                {
                    FaceToWallPreviewTrace.Error(
                        "铜排实体内核-立弯偏移",
                        verticalOffsetException,
                        "点数=" + Lpoint3Ds.Count);
                }
            }

            // ========== 9. 第二段平弯点清理与规范化 ==========
            // 9.1 投影到基准平面（立弯后的平弯段共面）
            if (point3Ds2.Count > 1)
            {
                Plane3d plane3D = new Plane3d(point3Ds2[0], LWLVector3d);
                for (int i = 1; i < point3Ds2.Count; i++)
                {
                    Point3d point3D = point3Ds2[i];
                    if (!point3Ds2[i].IfPointOntInPlaneMovePointToPlane(plane3D, out point3D, true))
                    {
                        point3Ds2[i] = point3D;
                    }
                }
            }

            // 9.2 移除共线/重叠点
            for (int i = 0; i < point3Ds2.Count - 2; i++)
            {
                if (point3Ds2[i].DistanceTo(new Line3d(point3Ds2[i + 1], point3Ds2[i + 2])) < 0.001)
                {
                    if (boolP3InLine3dP1AndP2(point3Ds2[i], point3Ds2[i + 1], point3Ds2[i + 2]))
                    {
                        point3Ds2.Remove(point3Ds2[i + 1]);
                        i--;
                    }
                    else
                    {
                        FaceToWallPreviewTrace.Warning(
                            "铜排实体内核-第二段路径重叠",
                            "数据有重叠，可尝试切换搭接方式。");
                        LastVector = null;
                        return null;
                    }
                }
            }

            // ========== 10. 第二段平弯线段偏移计算 ==========
            if (Lsegment3Ds.Count > 0 && point3Ds2.Count > 0)
            {
                // 10.1 沿厚度方向偏移（偏移距离=排宽/2）
                for (int i = 0; i < point3Ds2.Count; i++)
                {
                    point3Ds2[i] = point3Ds2[i].Move(LWLVector3d, barWidth / 2);
                }

                // 10.2 生成第二段平弯线段集合
                for (int i = 0; i < point3Ds2.Count - 1; i++)
                {
                    segment3Ds2.Add(new MySegment(point3Ds2[i], point3Ds2[i + 1]));
                }

                // 10.3 第二段平弯线段偏移计算
                try
                {
                    PYsegment3Ds2 = OffsetLineSegment(iSwApp, false, segment3Ds2, LVector3d, barThiness,
                        barBendingRadius, out LVector3d2, out WVector3d2);
                }
                catch (Exception secondPlanarOffsetException)
                {
                    FaceToWallPreviewTrace.Error(
                        "铜排实体内核-第二段平弯偏移",
                        secondPlanarOffsetException,
                        "点数=" + point3Ds2.Count);
                }
            }

            // ========== 11. 最后段向量赋值（用于FreeDrawBody返回） ==========
            if (PYsegment3Ds.Count > 0)
            {
                if (PYsegment3Ds2.Count > 0)
                {
                    LastVector = LVector3d2; // 有第二段平弯则取第二段向量
                }
                else
                {
                    LastVector = LVector3d; // 无第二段平弯则取立弯向量
                }
            }
            else
            {
                LastVector = null; // 无有效平弯段 → 返回null
                return null;
            }

            // 调试标记：仅计算向量则返回null（不生成实体）
            if (bVector)
            {
                var fd = new FreeDrawBody();
                fd.LastVector3d = LastVector;
                return fd;
            }

            // ========== 12. 生成预览体几何实体（拉伸轮廓） ==========
            List<Body2> tempBody1 = new List<Body2>(); // 临时实体集合（存储各段折弯实体）

            // 12.1 校验平弯偏移线段有效性
            if (PYsegment3Ds == null)
            {
                LastVector = null;
                return null;
            }

            // 12.2 拉伸第一段平弯轮廓 → 生成实体
            if (PYsegment3Ds.Count > 0)
            {
                try
                {
                    tempBody1.Add(ExtrudeContours(iSwApp, PYsegment3Ds, swComp, barWidth,
                        Wvector.Negate()));
                }
                catch (Exception firstPlanarBodyException)
                {
                    FaceToWallPreviewTrace.Error(
                        "铜排实体内核-第一段平弯实体",
                        firstPlanarBodyException,
                        "偏移段数=" + PYsegment3Ds.Count);
                }
            }

            // 12.3 拉伸立弯轮廓 → 生成实体（并记录立弯厚度向量到配置）
            LVector3d.Normalize();
            if (Lsegment3Ds.Count > 0)
            {
                try
                {
                    tempBody1.Add(ExtrudeContours(iSwApp, LPYsegment3Ds, swComp, barThiness, LVector3d));
                    bend.VerticalThicknessVector3d = LVector3d; // 更新配置中的立弯厚度向量
                }
                catch (Exception verticalBodyException)
                {
                    FaceToWallPreviewTrace.Error(
                        "铜排实体内核-立弯实体",
                        verticalBodyException,
                        "偏移段数=" + LPYsegment3Ds.Count);
                }
            }

            // 12.4 拉伸第二段平弯轮廓 → 生成实体
            LWWVector3d.Normalize();
            if (PYsegment3Ds2.Count > 0)
            {
                try
                {
                    tempBody1.Add(ExtrudeContours(iSwApp, PYsegment3Ds2, swComp, barWidth,
                        LWLVector3d.Negate()));
                }
                catch (Exception secondPlanarBodyException)
                {
                    FaceToWallPreviewTrace.Error(
                        "铜排实体内核-第二段平弯实体",
                        secondPlanarBodyException,
                        "偏移段数=" + PYsegment3Ds2.Count);
                }
            }

            // ========== 13. 合并多段实体为单个Body2 ==========
            Body2 body2 = null;
            for (int i = 0; i < tempBody1.Count; i++)
            {
                if (body2 == null)
                {
                    body2 = tempBody1[i]; // 初始化合并实体为第一段
                }
                else
                {
                    try
                    {
                        // 实体合并操作（"A"=合并/相加）
                        body2 = Bady2Operationgs2(body2, tempBody1[i], "A")[0];
                    }
                    catch (Exception bodyMergeException)
                    {
                        FaceToWallPreviewTrace.Error(
                            "铜排实体内核-合并实体",
                            bodyMergeException,
                            "实体序号=" + i + "，实体总数=" + tempBody1.Count);
                    }
                }
            }

            // ========== 14. 返回封装后的FreeDrawBody对象 ==========
            var previewResult = new FreeDrawBody(
                color,
                body2,
                swComp,
                LastVector,
                point3ds.Count)
            {
                IsMilledBar = bend.IsMilledBar,
                Name = bend.IsMilledBar ? "铣排预览段" : string.Empty
            };
            return previewResult;
        }

        public static List<List<Point3d>> HandleListPoint(List<string> listPoint)
        {
            #region 处理bend.listPoint数据

            List<List<Point3d>> list = new List<List<Point3d>>();
            List<Point3d> PWlistPoint3d = new List<Point3d>();
            List<Point3d> LWlistPoint3d = new List<Point3d>();
            List<Point3d> PWlistPoint3d2 = new List<Point3d>();

            bool bol1 = true;
            bool bol2 = true;
            for (int i = 0; i < listPoint.Count; i++)
            {
                Point3d point = new Point3d(Convert.ToDouble(listPoint[i].Split(',')[0]),
                    Convert.ToDouble(listPoint[i].Split(',')[1]),
                    Convert.ToDouble(listPoint[i].Split(',')[2]));

                if (listPoint[i].Split(',')[3].Contains("平") && bol1)
                {
                    PWlistPoint3d.Add(point);
                }
                else if (listPoint[i].Split(',')[3].Contains("立") && bol2)
                {
                    bol1 = false;
                    LWlistPoint3d.Add(point);
                }
                else if (listPoint[i].Split(',')[3].Contains("平"))
                {
                    bol2 = false;
                    PWlistPoint3d2.Add(point);
                }
            }

            for (int i = 0; i < PWlistPoint3d.Count - 1; i++)
            {
                if (PWlistPoint3d[i].DistanceTo(PWlistPoint3d[i + 1]) < 0.001)
                {
                    PWlistPoint3d.RemoveAt(i + 1);
                    i--;
                }
            }

            for (int i = 0; i < LWlistPoint3d.Count - 1; i++)
            {
                if (LWlistPoint3d[i].DistanceTo(LWlistPoint3d[i + 1]) < 0.001)
                {
                    LWlistPoint3d.RemoveAt(i + 1);
                    i--;
                }
            }

            for (int i = 0; i < PWlistPoint3d2.Count - 1; i++)
            {
                if (PWlistPoint3d2[i].DistanceTo(PWlistPoint3d2[i + 1]) < 0.001)
                {
                    PWlistPoint3d2.RemoveAt(i + 1);
                    i--;
                }
            }

            for (int i = 0; i < PWlistPoint3d.Count - 2; i++)
            {
                if (PWlistPoint3d[i].DistanceTo(new Line3d(PWlistPoint3d[i + 1], PWlistPoint3d[i + 2])) < 0.001)
                {
                    PWlistPoint3d.Remove(PWlistPoint3d[i + 1]);
                    i--;
                }
            }

            for (int i = 0; i < LWlistPoint3d.Count - 2; i++)
            {
                if (LWlistPoint3d[i].DistanceTo(new Line3d(LWlistPoint3d[i + 1], LWlistPoint3d[i + 2])) < 0.001)
                {
                    LWlistPoint3d.Remove(LWlistPoint3d[i + 1]);
                    i--;
                }
            }

            for (int i = 0; i < PWlistPoint3d2.Count - 2; i++)
            {
                if (PWlistPoint3d2[i].DistanceTo(new Line3d(PWlistPoint3d2[i + 1], PWlistPoint3d2[i + 2])) < 0.001)
                {
                    PWlistPoint3d2.Remove(PWlistPoint3d2[i + 1]);
                    i--;
                }
            }

            list.Add(PWlistPoint3d);
            list.Add(LWlistPoint3d);
            list.Add(PWlistPoint3d2);

            return list;

            #endregion 处理bend.listPoint数据
        }

        /// <summary>
        /// 两条线段倒圆角
        /// </summary>
        /// <param name="mySegments"></param>
        /// <returns></returns>
        private static bool VirtualLineSegmentFilletCornering(MySegment myS1, MySegment myS2, double R,
            out MySegment mySegments1, out MySegment mySegments2, out Arc3d arc)
        {
            if (myS1.EndPoint != myS2.StartPoint)
            {
                FaceToWallPreviewTrace.Error(
                    "铜排预览-倒圆角",
                    new InvalidOperationException(
                        "相邻线段端点不重合，无法倒圆角。"));
                arc = null;
                mySegments1 = null;
                mySegments2 = null;
                return false;
            }

            Point3d P1 = myS1.StartPoint.UnitX1000Copy();
            Point3d P2 = myS1.EndPoint.UnitX1000Copy();
            Point3d P3 = myS2.EndPoint.UnitX1000Copy();

            var plane3d = new Plane3d(P1, P2, P3);

            R = R * 1000;
            double JiaoDu = Calculatengle(new Vector3d(P2, P1), new Vector3d(P2, P3));

            Vector3d vectorC =
                new Vector3d(P2, P1).Rotate(new Rotation(plane3d.Normal.Negate(), JiaoDu / 2 / 180 * Math.PI));

            double centerPY = R / Math.Sin(JiaoDu / 2 / 180 * Math.PI);
            double PYjl = R / Math.Tan(JiaoDu / 2 / 180 * Math.PI);

            Point3d p1S = P2.Move(new Vector3d(P2, P1), PYjl);

            Point3d p2S = P2.Move(new Vector3d(P2, P3), PYjl);

            Point3d centerP = P2.Move(vectorC, centerPY);

            mySegments1 = new MySegment(P1.UnitX0001Copy(), p1S.UnitX0001Copy());
            mySegments2 = new MySegment(p2S.UnitX0001Copy(), P3.UnitX0001Copy());

            arc = new Arc3d(centerP.UnitX0001Copy(), p1S.UnitX0001Copy(), p2S.UnitX0001Copy(), plane3d.Normal,
                R / 1000.0);

            return true;
        }

        /// <summary>
        /// 两条线段倒圆角(偏移距离)
        /// </summary>
        /// <param name="mySegments"></param>
        /// <returns></returns>
        private static double VirtualLineSegmentFilletCornering2(MySegment myS1, MySegment myS2, double R)
        {
            if (myS1.EndPoint != myS2.StartPoint)
            {
                FaceToWallPreviewTrace.Warning(
                    "铜排实体内核-圆角线段不连续",
                    "两条线段必须首尾相连。");
            }

            Point3d P1 = myS1.StartPoint.UnitX1000Copy();
            Point3d P2 = myS1.EndPoint.UnitX1000Copy();
            Point3d P3 = myS2.EndPoint.UnitX1000Copy();

            var plane3d = new Plane3d(P1, P2, P3);

            R = R * 1000;
            double JiaoDu = Calculatengle(new Vector3d(P2, P1), new Vector3d(P2, P3));

            Vector3d vectorC =
                new Vector3d(P2, P1).Rotate(new Rotation(plane3d.Normal.Negate(), JiaoDu / 2 / 180 * Math.PI));

            double centerPY = R / Math.Sin(JiaoDu / 2 / 180 * Math.PI);
            double PYjl = R / Math.Tan(JiaoDu / 2 / 180 * Math.PI);

            return PYjl / 1000.0;
        }

        /// <summary>
        /// 计算向量夹角
        /// </summary>
        /// <param name="v1"></param>
        /// <param name="v2"></param>
        /// <returns></returns>
        private static double Calculatengle(Vector3d v1, Vector3d v2)
        {
            double angle = -1;
            try
            {
                // 计算两个向量的点积  
                double dotProduct = DotProduct(v1, v2);
                double normA = v1.Norm;
                double normB = v2.Norm;
                // 计算夹角的余弦值  
                double cosTheta = dotProduct / (normA * normB);
                double thetaRadians = Math.Acos(cosTheta); // 将余弦值转换为角度 
                double thetaDegrees = thetaRadians * (180.0 / Math.PI); // 将弧度转换为度数
                angle = thetaDegrees;
            }
            catch (Exception)
            {

                throw;
            }

            return angle;
        }

        /// <summary>
        /// 向量点积
        /// </summary>
        /// <param name="A"></param>
        /// <param name="B"></param>
        /// <returns></returns>
        private static double DotProduct(Vector3d A, Vector3d B)
        {
            return A.X * B.X + A.Y * B.Y + A.Z * B.Z;
        }

        /// <summary>
        /// 偏移首尾相连的虚拟线段并倒圆角返回封闭曲线钣金截面
        /// </summary>
        internal static List<object> OffsetLineSegment(SldWorks swApp, bool LW, List<MySegment> mySegments,
            Vector3d Startvector3D, double OffsetD, double radius, out Vector3d LVector, out Vector3d WVector)
        {
            LVector = new Vector3d();
            List<MySegment> mySs = new List<MySegment>();
            List<MySegment> mySs2 = new List<MySegment>();
            var swModeler = swApp.IGetModeler();
            double radius2 = OffsetD + radius;

            for (int i = 0; i < mySegments.Count; i++)
            {
                mySs.Add(new MySegment(mySegments[i].StartPoint, mySegments[i].EndPoint));
                mySs2.Add(new MySegment(mySegments[i].StartPoint, mySegments[i].EndPoint));
            }

            Vector3d vgd = Startvector3D;

            if (LW)
            {
                mySs[0].StartPoint = mySs[0].StartPoint.Move(Startvector3D, OffsetD / 2);
                mySs[0].EndPoint = mySs[0].EndPoint.Move(Startvector3D, OffsetD / 2);
            }
            else
            {
                mySs[0].StartPoint = mySs[0].StartPoint.Move(Startvector3D, OffsetD);
                mySs[0].EndPoint = mySs[0].EndPoint.Move(Startvector3D, OffsetD);
            }

            Plane3d plane3d = new Plane3d(mySegments[0].StartPoint, mySegments[0].EndPoint, mySs[0].StartPoint);
            WVector = plane3d.Normal;

            List<object> list = new List<object>();
            if (mySs.Count > 1)
            {
                for (int i = 1; i < mySs.Count; i++)
                {
                    Vector3d myv = new Vector3d(mySegments[i].StartPoint, mySegments[i].EndPoint);
                    myv.Normalize();
                    Vector3d gdv = myv.Cross(plane3d.Normal.Negate());
                    gdv.Normalize();
                    if (LW)
                    {
                        mySs[i].StartPoint = mySs[i].StartPoint.Move(gdv, OffsetD / 2);
                        mySs[i].EndPoint = mySs[i].EndPoint.Move(gdv, OffsetD / 2);
                    }
                    else
                    {
                        mySs[i].StartPoint = mySs[i].StartPoint.Move(gdv, OffsetD);
                        mySs[i].EndPoint = mySs[i].EndPoint.Move(gdv, OffsetD);
                    }
                }

                for (int i = 1; i < mySs.Count; i++)
                {
                    var point = GetSegmentIntersection3D(mySs[i - 1], mySs[i]);

                    if (point != null)
                    {
                        Point3d point3Dgd = (Point3d)point.UnitX1000Copy();
                        mySs[i - 1].EndPoint = point3Dgd;
                        mySs[i].StartPoint = point3Dgd;
                    }
                    else
                    {
                        FaceToWallPreviewTrace.Warning(
                            "铜排实体内核-偏移线段不相交",
                            "偏移直线段不相交，已按相邻路径合并处理。");
                        mySs.RemoveAt(i - 1);
                        mySs.RemoveAt(i);

                        MySegment segment = new MySegment(mySs[i - 1].StartPoint, mySs[i].EndPoint);
                        mySs.Insert(i - 1, segment);

                        mySegments.RemoveAt(i - 1);
                        mySegments.RemoveAt(i);

                        segment = new MySegment(mySegments[i - 1].StartPoint, mySegments[i].EndPoint);
                        mySegments.Insert(i - 1, segment);
                    }
                }


                if (LW)
                {
                    mySs2[0].StartPoint = mySs2[0].StartPoint.Move(Startvector3D.Negate(), OffsetD / 2);
                    mySs2[0].EndPoint = mySs2[0].EndPoint.Move(Startvector3D.Negate(), OffsetD / 2);

                    for (int i = 1; i < mySs2.Count; i++)
                    {
                        //Vector3d myv = new Vector3d(mySegments[i].StartPoint, mySegments[i].EndPoint);
                        //Vector3d gdv = myv.Cross(plane3d.Normal);
                        Vector3d myv = new Vector3d(mySegments[i].StartPoint, mySegments[i].EndPoint);
                        myv.Normalize();
                        Vector3d gdv = myv.Cross(plane3d.Normal);
                        gdv.Normalize();

                        mySs2[i].StartPoint = mySs2[i].StartPoint.Move(gdv, OffsetD / 2);
                        mySs2[i].EndPoint = mySs2[i].EndPoint.Move(gdv, OffsetD / 2);
                    }

                    for (int i = 1; i < mySs2.Count; i++)
                    {
                        var point = GetSegmentIntersection3D(mySs2[i - 1], mySs2[i]);

                        if (point != null)
                        {
                            Point3d point3Dgd = (Point3d)point.UnitX1000Copy();
                            mySs2[i - 1].EndPoint = point3Dgd;
                            mySs2[i].StartPoint = point3Dgd;
                        }
                        else
                        {
#if DEBUG
                            FaceToWallPreviewTrace.Warning(
                                "铜排实体内核-第二组偏移线段不相交",
                                "偏移直线段不相交，已按相邻路径合并处理。");
#endif
                            mySs2.RemoveAt(i - 1);
                            mySs2.RemoveAt(i);

                            MySegment segment = new MySegment(mySs2[i - 1].StartPoint, mySs2[i].EndPoint);
                            mySs2.Insert(i - 1, segment);

                            mySegments.RemoveAt(i - 1);
                            mySegments.RemoveAt(i);

                            segment = new MySegment(mySegments[i - 1].StartPoint, mySegments[i].EndPoint);
                            mySegments.Insert(i - 1, segment);
                        }
                    }

                    mySegments = mySs2;
                }

                for (int i = 0; i < mySs.Count - 1; i++)
                {
                    MySegment gdA = mySs[i];
                    MySegment gdB = mySs[i + 1];
                    Arc3d arcgd = new Arc3d();

                    Plane3d plane3Dgd = new Plane3d(gdA.StartPoint, gdA.EndPoint, gdB.EndPoint);
                    bool Vb = VectorsAreEqual(plane3Dgd.Normal, plane3d.Normal);

                    double dR = 0.0;
                    if (Vb)
                    {
                        dR = radius2;
                    }
                    else
                    {
                        dR = radius;
                    }

                    bool bol = VirtualLineSegmentFilletCornering(mySegments[i], mySegments[i + 1], dR, out gdA, out gdB,
                        out arcgd);
                    if (!bol) return null;
                    list.Add(new MySegment(gdA.StartPoint.UnitX0001Copy(), gdA.EndPoint.UnitX0001Copy()));
                    list.Add(new Arc3d(arcgd.CenterPoint.UnitX0001Copy(), arcgd.StartPoint.UnitX0001Copy(),
                        arcgd.EndPoint.UnitX0001Copy(), arcgd.Normal, arcgd.R / 1000.0));

                    mySegments[i] = gdA;
                    mySegments[i + 1] = gdB;

                    if (i == mySs.Count - 2)
                    {
                        list.Add(new MySegment(gdB.StartPoint.UnitX0001Copy(), gdB.EndPoint.UnitX0001Copy()));
                        list.Add(new MySegment(mySegments[mySs.Count - 1].EndPoint.UnitX0001Copy(),
                            mySs[mySs.Count - 1].EndPoint.UnitX0001Copy()));
                        LVector = new Vector3d(mySegments[mySs.Count - 1].EndPoint.UnitX0001Copy(),
                            mySs[mySs.Count - 1].EndPoint.UnitX0001Copy());
                    }
                }

                for (int i = 0; i < mySs.Count; i++)
                {
                    Point3d gdp = mySs[i].EndPoint;
                    mySs[i].EndPoint = mySs[i].StartPoint;
                    mySs[i].StartPoint = gdp;
                }

                for (int i = mySs.Count - 1; i > 0; i--)
                {
                    MySegment gdA = mySs[i];
                    MySegment gdB = mySs[i - 1];
                    Arc3d arcgd = new Arc3d();

                    Plane3d plane3Dgd = new Plane3d(gdA.StartPoint, gdA.EndPoint, gdB.EndPoint);
                    bool Vb = VectorsAreEqual(plane3Dgd.Normal, plane3d.Normal);

                    double dR = 0.0;
                    if (Vb)
                    {
                        dR = radius2;
                    }
                    else
                    {
                        dR = radius;
                    }

                    bool bol = VirtualLineSegmentFilletCornering(mySs[i], mySs[i - 1], dR, out gdA, out gdB, out arcgd);
                    if (!bol) return null;
                    list.Add(new MySegment(gdA.StartPoint.UnitX0001Copy(), gdA.EndPoint.UnitX0001Copy()));
                    list.Add(new Arc3d(arcgd.CenterPoint.UnitX0001Copy(), arcgd.StartPoint.UnitX0001Copy(),
                        arcgd.EndPoint.UnitX0001Copy(), arcgd.Normal, arcgd.R / 1000.0));

                    mySs[i] = gdA;
                    mySs[i - 1] = gdB;

                    if (i == 1)
                    {
                        list.Add(new MySegment(gdB.StartPoint.UnitX0001Copy(), gdB.EndPoint.UnitX0001Copy()));
                        list.Add(new MySegment(mySs[0].EndPoint.UnitX0001Copy(),
                            mySegments[0].StartPoint.UnitX0001Copy()));
                    }
                }
            }
            else
            {
                list.Add(
                    new MySegment(mySegments[0].StartPoint.UnitX0001Copy(), mySegments[0].EndPoint.UnitX0001Copy()));
                list.Add(new MySegment(mySegments[0].EndPoint.UnitX0001Copy(), mySs[0].EndPoint.UnitX0001Copy()));
                list.Add(new MySegment(mySs[0].EndPoint.UnitX0001Copy(), mySs[0].StartPoint.UnitX0001Copy()));
                list.Add(new MySegment(mySs[0].StartPoint.UnitX0001Copy(), mySegments[0].StartPoint.UnitX0001Copy()));

                LVector = new Vector3d(mySegments[0].EndPoint.UnitX0001Copy(), mySs[0].EndPoint.UnitX0001Copy());
            }

            return list;
        }

        private static bool Panduan(List<object> list)
        {
            bool pangd = true;
            for (int i = 0; i < list.Count; i++)
            {
                Point3d startP;
                Point3d endP;
                if (i != list.Count - 1)
                {
                    if (list[i] is MySegment)
                    {
                        MySegment gdseg = (MySegment)list[i];
                        startP = gdseg.EndPoint;
                    }
                    else
                    {
                        Arc3d gdseg = (Arc3d)list[i];
                        startP = gdseg.EndPoint;
                    }

                    if (list[i + 1] is MySegment)
                    {
                        MySegment gdseg = (MySegment)list[i + 1];
                        endP = gdseg.StartPoint;
                    }
                    else
                    {
                        Arc3d gdseg = (Arc3d)list[i + 1];
                        endP = gdseg.StartPoint;
                    }
                }
                else
                {
                    if (list[i] is MySegment)
                    {
                        MySegment gdseg = (MySegment)list[i];
                        startP = gdseg.EndPoint;
                    }
                    else
                    {
                        Arc3d gdseg = (Arc3d)list[i];
                        startP = gdseg.EndPoint;
                    }

                    if (list[0] is MySegment)
                    {
                        MySegment gdseg = (MySegment)list[0];
                        endP = gdseg.StartPoint;
                    }
                    else
                    {
                        Arc3d gdseg = (Arc3d)list[0];
                        endP = gdseg.StartPoint;
                    }
                }

                if (startP != endP)
                {
                    pangd = false;
                }
            }

            return pangd;
        }

        /// <summary>
        /// 求两条MySegment所在直线交点
        /// </summary>
        /// <param name="segmentA"></param>
        /// <param name="segmentB"></param>
        /// <returns></returns>
        private static Point3d GetSegmentIntersection3D(MySegment mySs11, MySegment mySs12)
        {
            MySegment mySs1 = new MySegment(mySs11.StartPoint.UnitX0001Copy(), mySs11.EndPoint.UnitX0001Copy());
            MySegment mySs2 = new MySegment(mySs12.StartPoint.UnitX0001Copy(), mySs12.EndPoint.UnitX0001Copy());

            Line3d line1 = new Line3d(mySs1.StartPoint, mySs1.EndPoint);
            Line3d line2 = new Line3d(mySs2.StartPoint, mySs2.EndPoint);

            return GetCrossPt(line1, line2, true);

        }

        /// <summary>
        /// 判断两个向量是否相等
        /// </summary>
        /// <param name="varVec1"></param>
        /// <param name="varVec2"></param>
        /// <returns></returns>
        private static bool VectorsAreEqual(Vector3d varVec1, Vector3d varVec2)
        {
            varVec1.Normalize();
            varVec2.Normalize();

            bool functionReturnValue = false;
            double dblMag = 0;
            double dblDot = 0;
            double[] dblUnit1 = new double[3];
            double[] dblUnit2 = new double[3];
            dblMag = Math.Pow((varVec1[0] * varVec1[0] + varVec1[1] * varVec1[1] + varVec1[2] * varVec1[2]), 0.5);
            dblUnit1[0] = varVec1[0] / dblMag;
            dblUnit1[1] = varVec1[1] / dblMag;
            dblUnit1[2] = varVec1[2] / dblMag;
            dblMag = Math.Pow((varVec2[0] * varVec2[0] + varVec2[1] * varVec2[1] + varVec2[2] * varVec2[2]), 0.5);
            dblUnit2[0] = varVec2[0] / dblMag;
            dblUnit2[1] = varVec2[1] / dblMag;
            dblUnit2[2] = varVec2[2] / dblMag;
            dblDot = dblUnit1[0] * dblUnit2[0] + dblUnit1[1] * dblUnit2[1] + dblUnit1[2] * dblUnit2[2];
            dblDot = Math.Abs(dblDot - 1.0);
            // Compare within a tolerance
            //1.0e-10
            if (dblDot < 1E-10)
            {
                functionReturnValue = true;
            }
            else
            {
                functionReturnValue = false;
            }

            return functionReturnValue;
        }

        /// <summary>
        /// 两直线交点 可延长求交点
        /// </summary>
        private static Point3d GetCrossPt(Line3d line1, Line3d line2, bool extended)
        {
            Point3d crossPt = null;
            //直线延长
            if (extended)
            {
                var startPt = line1.Point.Move(line1.Direction, 1000);
                var endPt = line1.Point.Move(line1.Direction, -1000);
                var helpLine = new Line3d(startPt, endPt);
                crossPt = (Point3d)helpLine.IntersectionWith(line2);
            }
            else
            {
                crossPt = (Point3d)line1.IntersectionWith(line2);
            }

            return crossPt;
        }

        /// <summary>
        /// 将铣排的三段预览 Body 依次相加为一个 Body；两侧既可以是
        /// 普通直排，也可以是已经分别合并完成的多点桥式排。
        /// </summary>
        internal static Body2 MergePreviewBodySegments(
            IReadOnlyList<Body2> previewBodySegments)
        {
            if (previewBodySegments == null ||
                previewBodySegments.Count != 3 ||
                previewBodySegments.Any(previewBodySegment =>
                    previewBodySegment == null))
            {
                throw new ArgumentException(
                    "铣排预览必须包含三个有效 Body。",
                    nameof(previewBodySegments));
            }

            var mergedPreviewBody = previewBodySegments[0];
            for (var previewSegmentIndex = 1;
                 previewSegmentIndex < previewBodySegments.Count;
                 previewSegmentIndex++)
            {
                var bodyAdditionResults = Bady2Operationgs2(
                    mergedPreviewBody,
                    previewBodySegments[previewSegmentIndex],
                    "A");
                if (bodyAdditionResults == null ||
                    bodyAdditionResults.Count != 1 ||
                    bodyAdditionResults[0] == null)
                {
                    throw new InvalidOperationException(
                        "铣排第" + (previewSegmentIndex + 1) +
                        "段与前段 Body 相加失败；三段必须在端面处连续接触。" );
                }

                mergedPreviewBody = bodyAdditionResults[0];
            }

            return mergedPreviewBody;
        }

        /// <summary>
        /// 求两个实体（相加A，相减C，相交I）
        /// </summary>
        /// <param name="body1"></param>
        /// <param name="body2"></param>
        /// <param name="Type"></param>
        /// <returns></returns>
        internal static List<Body2> Bady2Operationgs2(Body2 body1, Body2 body2, string Type)
        {
            int iType = 0;
            int Error = 0;
            switch (Type)
            {
                case "A":
                    iType = (int)swBodyOperationType_e.SWBODYADD;
                    break;
                case "C":
                    iType = (int)swBodyOperationType_e.SWBODYCUT;
                    break;
                case "I":
                    iType = (int)swBodyOperationType_e.SWBODYINTERSECT;
                    break;
            }

            if (iType != 0)
            {
                if (body1 == null || body2 == null)
                {
                    return new List<Body2>();
                }

                try
                {
                    EnumBodies2 EnumBodies = body1.IOperations2(iType, body2, out Error);

                    // 重置枚举器
                    EnumBodies.Reset();

                    // 枚举所有Body2对象
                    List<Body2> Bodys = new List<Body2>();
                    int fetched = 0;
                    Body2 body;

                    EnumBodies.Next(1, out body, ref fetched);

                    while (fetched > 0)
                    {
                        if (body != null)
                        {
                            Bodys.Add(body);
                            EnumBodies.Next(1, out body, ref fetched);
                        }
                    }

                    return Bodys;
                }
                catch (Exception bodyOperationException)
                {
                    FaceToWallPreviewTrace.Error(
                        "铜排实体内核-布尔运算",
                        bodyOperationException,
                        "操作=" + Type);
                    return new List<Body2>();
                }
            }
            else
            {
                return new List<Body2>();
            }
        }

        /// <summary>
        /// 按特征树获取文档列表
        /// </summary>        
        /// <param name="iSwApp">SW对象</param>
        /// <param name="swDoc">装配体文档</param>
        /// <param name="bAssem">是否含总装/子装配体文档</param>
        /// <param name="bCount">是否去重</param>
        /// <returns>文档列表</returns>
        private static List<ModelDoc2> GetModelListByTree(ISldWorks iSwApp, ModelDoc2 swDoc, bool bAssem, bool bCount)
        {
            List<ModelDoc2> listDoc = GetModelListByTree(swDoc);

            //去除装配体
            if (!bAssem)
            {
                for (int i = 0; i < listDoc.Count; i++)
                {
                    if (listDoc[i].GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                    {
                        listDoc.RemoveAt(i);
                        i--;
                    }
                }
            }

            //去除重复项
            if (bCount)
            {
                for (int i = 0; i < listDoc.Count - 1; i++)
                {
                    for (int j = i + 1; j < listDoc.Count; j++)
                    {
                        if (iSwApp.IsSame(listDoc[i], listDoc[j]) == 1)
                        {
                            listDoc.RemoveAt(j);
                            j--;
                        }
                    }

                }
            }

            return listDoc;
        }

        /// <summary>
        /// 获取模型树结构，含顶装配，结构树完全展开，数量全部为1 
        /// </summary>
        /// <param name="swDoc">装配体文档</param>
        /// <returns></returns>
        private static List<ModelDoc2> GetModelListByTree(ModelDoc2 swDoc)
        {
            List<ModelDoc2> listDoc = new List<ModelDoc2>();

            listDoc.Add(swDoc);
            if (swDoc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY) return listDoc;
            AssemblyDoc swAssembly = (AssemblyDoc)swDoc;
            Feature swFeature = (Feature)swDoc.FirstFeature();
            while (swFeature != null)
            {
                string sFeatName = swFeature.Name;
                Component2 swCompnt = (Component2)swAssembly.GetComponentByName(sFeatName);
                if (swCompnt != null)
                {
                    ModelDoc2 swDoc1 = (ModelDoc2)swCompnt.GetModelDoc2();
                    if (swDoc1 != null)
                    {
                        List<ModelDoc2> listDoc1 = GetModelListByTree(swDoc1);
                        listDoc.AddRange(listDoc1.ToArray());
                    }
                }

                swFeature = (Feature)swFeature.GetNextFeature();
            }

            return listDoc;
        }

        /// <summary>
        /// 汇总模型及数量
        /// </summary>       
        /// <param name="iSwApp">SW对象</param>
        /// <param name="listDoc">文档列表</param>
        /// <param name="listCount">数量列表</param>
        private static void GetDocAndCount(ISldWorks iSwApp, ref List<ModelDoc2> listDoc, ref List<int> listCount)
        {
            listCount.Clear();
            for (int i = 0; i < listDoc.Count; i++)
            {
                listCount.Add(1);
            }

            for (int i = 0; i < listDoc.Count - 1; i++)
            {
                for (int j = i + 1; j < listDoc.Count; j++)
                {
                    if (iSwApp.IsSame(listDoc[i], listDoc[j]) == 1)
                    {
                        listCount[i] = listCount[i] + 1;
                        listDoc.RemoveAt(j);
                        listCount.RemoveAt(j);
                        j--;
                    }
                }
            }

        }

        /// <summary>
        /// 获取指定名称的组件对象(仅首层)
        /// </summary>        
        /// <param name="swDoc"></param>
        /// <param name="sDocName"></param>
        /// <returns></returns>
        private static Component2 GetComponent(ModelDoc2 swDoc, string sDocName)
        {
            AssemblyDoc swAssembly = (AssemblyDoc)swDoc;
            for (int i = 1; i < 1000; i++)
            {
                Component2 swCompnt = swAssembly.GetComponentByName(sDocName + "-" + i.ToString());
                if (swCompnt != null) return swCompnt;
            }

            return null;
        }

        /// <summary>
        /// 返回文件名
        /// </summary>
        /// <param name="swModel"></param>
        /// <param name="includeExt"></param>
        /// <returns></returns>
        private static string GetFileName(this ModelDoc2 swModel, bool includeExt = false)
        {
            var tempPath = swModel.GetPathName();

            if (includeExt)
            {

                return System.IO.Path.GetFileName(tempPath);

            }
            else
            {
                return System.IO.Path.GetFileNameWithoutExtension(tempPath);

            }
        }

        /// <summary>
        /// 判断P3是否在线段P1P2外且正向量方向路径上
        /// </summary>
        /// <returns></returns>
        private static bool boolP3InLine3dP1AndP2(Point3d P1, Point3d P2, Point3d P3, double dtp = 1e-10)
        {
            Line3d line = new Line3d(P1, P2);
            if (P3.DistanceTo(line) < dtp)
            {
                Vector3d P1P2 = new Vector3d(P1, P2);
                P1P2.Normalize();
                Vector3d P1P3 = new Vector3d(P1, P3);
                P1P3.Normalize();
                Vector3d P2P3 = new Vector3d(P2, P3);
                P2P3.Normalize();

                if (P1P3 == new Vector3d() || P2P3 == new Vector3d())
                {
                    return false;
                }

                if (P1P2.Normalized.Equals(P1P3.Normalized) && P1P2.Normalized.Equals(P2P3.Normalized))
                {
                    return true;
                }

                return false;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// 拉伸薄面预览(装配体)
        /// </summary>
        /// <param name="thickness"></param>
        /// <param name="bReverse"></param>
        /// <param name="swApp"></param>
        /// <param name="PYsegment3Ds"></param>
        /// <returns></returns>
        public static Body2 ExtrudeContours(SldWorks swApp, List<object> PYsegment3Ds, Component2 component,
            double thickness, Vector3d bReverse)
        {
            //轮廓创建体（薄薄一个平面）
            var swBody = CreateBodiesFromDummySlot(swApp, PYsegment3Ds);

            if (swBody == null) return null;
            //装配体转到零部件
            swBody.ApplyTransform(component.Transform.IInverse());

            var swModeler = (Modeler)swApp.GetModeler();

            var facesObj = (object[])swBody.GetFaces();
            //（薄薄一个平面）
            var swFace = (Face2)facesObj[0];

            var swModel = (ModelDoc2)swApp.ActiveDoc;


            var faceInAsm = (Face2)component.GetCorrespondingEntity(swFace);

            var swMathUtility = (MathUtility)swApp.GetMathUtility();

            var swDir = (MathVector)swMathUtility.CreateVector(faceInAsm.Normal); //(swFace.Normal);

            //Vector3d negDir = new Vector3d((double[])swDir.ArrayData).Negate();
            //negDir.Normalize();

            bReverse = bReverse.ConvertVectorAssemblyToPart(swApp, component);
            bReverse.Normalize();

            Vector3d negDir = bReverse;

            swDir = (MathVector)swMathUtility.CreateVector(negDir.ToArrayData());

            var body = swModeler.CreateExtrudedBody(swBody, swDir, thickness / 1000);
            if (body == null)
            {
                FaceToWallPreviewTrace.Warning(
                    "铜排实体内核-拉伸预览实体失败",
                    "CreateExtrudedBody 返回空；轮廓可能存在重叠。");
            }

            return body;

        }

        /// <summary>
        /// 创建轮廓（装配体）
        /// </summary>
        public static Body2 CreateBodiesFromDummySlot(SldWorks swApp, List<object> list)
        {
            var swModeler = swApp.IGetModeler();

            List<Curve> listCurve = new List<Curve>();
            List<MySegment> segment3ds = new List<MySegment>();

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is MySegment)
                {
                    segment3ds.Add((MySegment)list[i]);
                    listCurve.Add(CreateCurveLine(swModeler, (MySegment)list[i]));
                }
                else
                {
                    listCurve.Add((Curve)CreateArc(swModeler, (Arc3d)list[i]));
                }
            }

            var plane3d = new Plane3d(segment3ds[0].StartPoint, segment3ds[0].EndPoint, segment3ds[1].EndPoint);

            var startArr = new double[3];
            var endArr = new double[3];
            var ptArr = new double[3];
            var dirArr = new double[3];

            ptArr[0] = segment3ds[0].StartPoint.X;
            ptArr[1] = segment3ds[0].StartPoint.Y;
            ptArr[2] = segment3ds[0].StartPoint.Z;
            dirArr[0] = plane3d.Normal.Normalized.X;
            dirArr[1] = plane3d.Normal.Normalized.Y;
            dirArr[2] = plane3d.Normal.Normalized.Z;
            startArr[0] = plane3d.Normal.Normalized.OrthogonalVector.X;
            startArr[1] = plane3d.Normal.Normalized.OrthogonalVector.Y;
            startArr[2] = plane3d.Normal.Normalized.OrthogonalVector.Z;

            var swSurface = (Surface)swModeler.CreatePlanarSurface2((ptArr), (dirArr), (startArr));

            if (swSurface == null) return null;

            var swBody2 = (Body2)swSurface.CreateTrimmedSheet4(listCurve.ToArray(), true);

            //var body3 = swModeler.CreateWireBody(listCurve.ToArray(), (int)swCreateWireBodyOptions_e.swCreateWireBodyByDefault);

            //return swBody2;
            return swBody2;
        }

        public static ICurve CreateArc(Modeler swModeler, Arc3d arc3d)
        {
            if (arc3d == null || double.IsNaN(arc3d.R) || double.IsInfinity(arc3d.R) || arc3d.R <= 0d)
                throw new InvalidOperationException("铜排预览圆弧半径无效，请核对原始折弯参数。");
            object center = arc3d.CenterPoint.ToArrayData();
            object axis = new[] { arc3d.Normal.X, arc3d.Normal.Y, arc3d.Normal.Z };
            object startPoint = arc3d.StartPoint.ToArrayData();
            object endPoint = arc3d.EndPoint.ToArrayData();
            var tempLine = (Curve)swModeler.CreateArc(center, axis, arc3d.R, startPoint, endPoint);
            if (tempLine == null)
                throw new InvalidOperationException("SolidWorks创建铜排圆弧失败，半径(mm)=" +
                    (arc3d.R * 1000d).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            var temp2 = tempLine.CreateTrimmedCurve(arc3d.StartPoint.ToArrayData(), arc3d.EndPoint.ToArrayData());
            return temp2;
        }

        public static Curve CreateCurveLine(Modeler swModeler, MySegment segment3D)
        {
            var curve1 =
                (Curve)swModeler.CreateLine(segment3D.StartPoint.ToArrayData(), segment3D.ToVector().ToArrayData());

            curve1 = (Curve)curve1.CreateTrimmedCurve(segment3D.StartPoint.ToArrayData(),
                segment3D.EndPoint.ToArrayData());

            return curve1;
        }


        public static ICurve CreateTrimmedCurve(this ICurve curve, double[] start, double[] end)
        {
            return curve.CreateTrimmedCurve2(start[0], start[1], start[2], end[0], end[1], end[2]);
        }
       

        /// <summary>
        /// 返回运行时Dll的路径
        /// </summary>
        /// <param name="s"></param>
        /// <returns></returns>
        public static string RegDllPath()
        {
            try
            {
                var codeBase = Assembly.GetExecutingAssembly().CodeBase;
                var uri = new UriBuilder(codeBase);
                var path = Uri.UnescapeDataString(uri.Path);
                return Path.GetDirectoryName(path);
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>
        /// 获取铜排相序颜色
        /// </summary>
        public static Color GetBusbarPhaseColor(BusbarPhase_e? busbarPhaseE)
        {
            if (busbarPhaseE == null)
            {
                busbarPhaseE = BusbarPhase_e.Unknow;
            }

            //var resColor = CurrentSettings.PhaseColorList[(BusbarPhase_e)busbarPhaseE];

            Color resColor;
            if (busbarPhaseE == BusbarPhase_e.A)
            {
                resColor = Color.Yellow;
            }
            else if(busbarPhaseE == BusbarPhase_e.B)
            {
                resColor = Color.Green;
            }
            else if(busbarPhaseE == BusbarPhase_e.C)
            {
                resColor = Color.Red;
            }
            else
            {
                resColor = Color.Orange;
            }


            return resColor;

        }
    }


    public static class Body2AndFile
    {
        public static IBody2 InBody2(this ISldWorks swApp, string sPath, double[] dPoint, List<Vector3d> Lvector)
        {
            IBody2 body = BodyHelper.LoadBodyFromFile(swApp, sPath);
            Vector3d vectorX = Lvector[0];
            Vector3d vectorY = Lvector[1];

            if (body != null)
            {
                Color color = Color.Yellow;

                int colorInt = color.ToArgb();
                int red2 = colorInt >> 16 & 255;
                int green2 = colorInt >> 8 & 255;
                int blue2 = colorInt & 255;
                int refcolor2 = (int)blue2 << 16 | (int)green2 << 8 | (int)red2;

                // 规范化输入向量
                vectorX.Normalize();

                // 定义目标X轴方向（即输入向量方向）
                double[] xAxis = new double[] { vectorX.X, vectorX.Y, vectorX.Z };

                vectorY.Normalize();
                double[] yAxis = new double[] { vectorY.X, vectorY.Y, vectorY.Z };

                Vector3d vectorZ = vectorX.Cross(vectorY);
                vectorZ.Normalize();

                double[] zAxis = new double[] { vectorZ.X, vectorZ.Y, vectorZ.Z };
                // 构建变换矩阵
                double[] Xform = new double[16];

                // 设置旋转部分 - 矩阵的前9个元素
                Xform[0] = xAxis[0];  // X轴方向
                Xform[1] = xAxis[1];
                Xform[2] = xAxis[2];

                Xform[3] = yAxis[0];  // Y轴方向
                Xform[4] = yAxis[1];
                Xform[5] = yAxis[2];

                Xform[6] = zAxis[0];  // Z轴方向
                Xform[7] = zAxis[1];
                Xform[8] = zAxis[2];

                // 设置平移部分
                Xform[9] = dPoint[0];  // 平移 x
                Xform[10] = dPoint[1]; // 平移 y
                Xform[11] = dPoint[2]; // 平移 z

                // 设置缩放和其他参数
                Xform[12] = 1.0;       // 比例因子
                Xform[13] = 0.0;       // 未使用
                Xform[14] = 0.0;       // 未使用
                Xform[15] = 0.0;       // 未使用

                object vXform = Xform;

                var MathUtility = (MathUtility)swApp.GetMathUtility();
                var MathXform = (MathTransform)MathUtility.CreateTransform(vXform);

                body.ApplyTransform(MathXform);

                return body.ICopy();
            }
            else
                throw new Exception("加载实体失败！");
        }


        public class BodyHelper
        {
            [System.Runtime.InteropServices.DllImport("ole32")]
            public static extern long CreateStreamOnHGlobal(IntPtr hGlobal, bool fDeleteOnRelease, ref IStream ppstm);

            //private const string FILE_PATH = @"D:\body.dat";

            public static void ExportBodyToFile(string FILE_PATH, ISldWorks app)
            {
                //ISldWorks app = Connect.GetRunningSolidWorks();
                app.Visible = true;

                IModelDoc2 swModel;

                swModel = app.IActiveDoc2;

                if (swModel != null)
                {
                    IBody2 body = (Body2)swModel.ISelectionManager.GetSelectedObject6(1, -1);

                    if (body != null)
                        SaveBodyToFile(body, FILE_PATH);
                    else
                        throw new Exception("Please select body to export");
                }
                else
                    throw new Exception("Please open the model");
            }

            /// <summary>
            /// 把body导出为文件流
            /// </summary>
            /// <param name="body"></param>
            /// <param name="filePath"></param>
            public static void SaveBodyToFile(IBody2 body, string filePath)
            {
                IStream stream = null;

                CreateStreamOnHGlobal(IntPtr.Zero, true, ref stream);
                body.Save(stream);

                var comStream = new ComStreamBody(ref stream, false, false);

                using (var fileStream = File.Create(filePath))
                {
                    comStream.Seek(0, SeekOrigin.Begin);
                    comStream.CopyTo(fileStream);
                }
            }

            /// <summary>
            /// 从文件加载实体
            /// </summary>
            /// <param name="app"></param>
            /// <param name="filePath"></param>
            /// <returns></returns>
            public static IBody2 LoadBodyFromFile(ISldWorks app, string filePath)
            {
                IStream stream = null;

                CreateStreamOnHGlobal(IntPtr.Zero, true, ref stream);

                var comStream = new ComStreamBody(ref stream, true, true);

                using (var fileStream = File.OpenRead(filePath))
                {
                    fileStream.CopyTo(comStream);
                    comStream.Seek(0, SeekOrigin.Begin);
                }

                IModeler modeler = app.IGetModeler();

                return (IBody2)modeler.Restore(stream);
            }
        }

        public class ComStreamBody : Stream
        {
            private readonly IStream m_ComStream;
            private readonly bool m_Commit;
            private bool m_IsWritable;

            public ComStreamBody(ref IStream comStream, bool writable, bool commit = true)
            {
                if (comStream == null)
                    throw new ArgumentNullException(nameof(comStream));

                m_ComStream = comStream;
                m_IsWritable = writable;
                m_Commit = commit;
            }

            public override bool CanRead
            {
                get
                {
                    return true;
                }
            }

            public override bool CanSeek
            {
                get
                {
                    return true;
                }
            }

            public override bool CanWrite
            {
                get
                {
                    return m_IsWritable;
                }
            }

            public override long Length
            {
                get
                {
                    const int STATSFLAG_NONAME = 1;

                    System.Runtime.InteropServices.ComTypes.STATSTG stats = default(System.Runtime.InteropServices.ComTypes.STATSTG);
                    m_ComStream.Stat(out stats, STATSFLAG_NONAME);

                    return stats.cbSize;
                }
            }

            public override long Position
            {
                get
                {
                    return Seek(0, SeekOrigin.Current);
                }
                set
                {
                    Seek(value, SeekOrigin.Begin);
                }
            }

            public override void Flush()
            {
                if (m_Commit)
                {
                    const int STGC_DEFAULT = 0;
                    m_ComStream.Commit(STGC_DEFAULT);
                }
            }

            public override void SetLength(long Value)
            {
                m_ComStream.SetSize(Value);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (offset != 0)
                {
                    int bufferSize;
                    bufferSize = buffer.Length - offset;
                    byte[] tmpBuffer = new byte[bufferSize + 1];
                    System.Array.Copy(buffer, offset, tmpBuffer, 0, bufferSize);
                    m_ComStream.Write(tmpBuffer, bufferSize, default(IntPtr));
                }
                else
                    m_ComStream.Write(buffer, count, default(IntPtr));
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int bytesRead = 0;
                object boxBytesRead = bytesRead;
                GCHandle hObject = GCHandle.Alloc(boxBytesRead, GCHandleType.Pinned);

                try
                {
                    IntPtr pBytesRead = hObject.AddrOfPinnedObject();

                    if (offset != 0)
                    {
                        byte[] tmpBuffer = new byte[count - 1 + 1];
                        m_ComStream.Read(tmpBuffer, count, pBytesRead);
                        bytesRead = System.Convert.ToInt32(boxBytesRead);
                        System.Array.Copy(tmpBuffer, 0, buffer, offset, bytesRead);
                    }
                    else
                    {
                        m_ComStream.Read(buffer, count, pBytesRead);
                        bytesRead = System.Convert.ToInt32(boxBytesRead);
                    }
                }
                finally
                {
                    if (hObject.IsAllocated)
                        hObject.Free();
                }

                return bytesRead;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                long curPosition = 0;
                object boxCurPosition = curPosition;
                GCHandle hObject = GCHandle.Alloc(boxCurPosition, GCHandleType.Pinned);

                try
                {
                    IntPtr pCurPosition = hObject.AddrOfPinnedObject();

                    m_ComStream.Seek(offset, (int)origin, pCurPosition);
                    curPosition = System.Convert.ToInt64(boxCurPosition);
                }
                finally
                {
                    if (hObject.IsAllocated)
                        hObject.Free();
                }

                return curPosition;
            }

            protected override void Dispose(bool disposing)
            {
                try
                {
                    if (disposing)
                        m_IsWritable = false;
                }
                finally
                {
                    base.Dispose(disposing);
                }
            }

            ~ComStreamBody()
            {
                Dispose(false);
            }
        }
    }
}
