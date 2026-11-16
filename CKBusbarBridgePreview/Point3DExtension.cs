using GeometRi;
using SolidWorks.Interop.sldworks;
using Sunny.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using AutoOpeningHoleDataModel;

namespace CKBusbarBridgePreview
{
    public static class Point3DExtension
    {
        public static Point3d Move(this Point3d point3D, Vector3d unitVector3D, double distance)
        {
            return new Point3d(point3D.X + unitVector3D.Normalized.X * distance, point3D.Y + unitVector3D.Normalized.Y * distance, point3D.Z + unitVector3D.Normalized.Z * distance);
        }

        public static Point3d ToPoint3d(this double[] ds)
        {
            return new Point3d(ds);
        }

        /// <summary>
        /// 判断线段是否与平面相交
        /// </summary>
        /// <param name="segment3d"></param>
        /// <param name="plane3d"></param>
        /// <returns></returns>
        public static bool IntersectionWithPlane3d(this Segment3d segment3d, Plane3d plane3d)
        {
            var intersectionWith = plane3d.IntersectionWith(segment3d.ToLine);

            if (intersectionWith is Point3d)
            {
                var intPoint = intersectionWith as Point3d;
                var d1 = segment3d.P1.DistanceTo(intPoint.ConvertToGlobal());
                var d2 = segment3d.P2.DistanceTo(intPoint.ConvertToGlobal());
                var lineLength = segment3d.P2.DistanceTo(segment3d.P1);

                if (Math.Abs(d1 + d2 - lineLength) < 0.01)
                {
                    return true;
                }

                return false;




            }

            return false;


        }

        public static bool CheckPointInArea(Point3d p, List<Point3d> point3Ds, double tolDis = 2)
        {
            if (point3Ds.Count < 3)
            {
                new Exception("点数太少！");
                return false;
            }

            var tempPlane = new Plane3d(point3Ds[0], point3Ds[1], point3Ds[2]);

            if (p.DistanceTo(tempPlane) < tolDis)
            {
                Coord3d coord3D = new Coord3d(point3Ds[0], point3Ds[1], point3Ds[2]);

                List<Point3d> point2Ds = new List<Point3d>();

                foreach (var point in point3Ds)
                {
                    var tempP = point.ConvertTo(coord3D);
                    point2Ds.Add(new Point3d(Math.Round(tempP.X, 3), Math.Round(tempP.Y, 3), Math.Round(tempP.Z, 3)));

                }
                var p2 = p.ConvertTo(coord3D);
                p = new Point3d(p2.X, p2.Y, p2.Z);


                Point3d[] point3dref = new Point3d[point2Ds.Count];
                int pointNumber = 0;
                Point3DExtension.Graham_scan(point2Ds.ToArray(), point3dref, point2Ds.Count, ref pointNumber);

                var centerP = Point3DExtension.GravityCenter(pointNumber, point3dref);

                return Point3DExtension.PointInFences(p, point3dref);


            }
            else
            {
                return false;

            }



        }

        public static double isLeft(Point3d P0, Point3d P1, Point3d P2)
        {
            if (P0 == null || P1 == null || P2 == null)
            {
                return 0.0;
            }

            double abc = ((P1.X - P0.X) * (P2.Y - P0.Y) - (P2.X - P0.X) * (P1.Y - P0.Y));

            return abc;
        }

        public static bool PointInFences(Point3d pnt1, Point3d[] fencePnts)
        {
            if (pnt1 == null)
            {
                return false;
            }

            int wn = 0, j = 0; //wn 计数器 j第二个点指针
            for (int i = 0; i < fencePnts.Length; i++)
            {//开始循环
                if (i == fencePnts.Length - 1)
                    j = 0;//若是 循环到最后一点 第二个指针指向第一点
                else
                    j = j + 1; //若是不是 ，则找下一点

                if (fencePnts[i]?.Y <= pnt1.Y) // 若是多边形的点 小于等于 选定点的 Y 坐标
                {
                    if (fencePnts[j]?.Y > pnt1.Y) // 若是多边形的下一点 大于于 选定点的 Y 坐标
                    {
                        if (isLeft(fencePnts[i], fencePnts[j], pnt1) > 0)
                        {
                            wn++;
                        }
                    }
                }
                else
                {
                    if (fencePnts[j]?.Y <= pnt1.Y)
                    {
                        if (isLeft(fencePnts[i], fencePnts[j], pnt1) < 0)
                        {
                            wn--;
                        }
                    }
                }
            }
            if (wn == 0)
                return false;
            else
                return true;
        }

        /// <summary>
        /// 求凸多边形的重心,要求输入多边形按逆时针排序
        /// </summary>
        /// <param name="vcount"></param>
        /// <param name="polygon"></param>
        /// <returns></returns>
        public static Point3d GravityCenter(int vcount, Point3d[] polygon)
        {
            Point3d tp = new Point3d();
            double x, y, s, x0, y0, cs, k;
            x = 0; y = 0; s = 0;
            for (int i = 1; i < vcount - 1; i++)
            {
                x0 = (polygon[0].X + polygon[i].X + polygon[i + 1].X) / 3;
                y0 = (polygon[0].Y + polygon[i].Y + polygon[i + 1].Y) / 3; //求当前三角形的重心
                cs = Multiply(polygon[i], polygon[i + 1], polygon[0]) / 2;
                //三角形面积可以直接利用该公式求解
                if (Math.Abs(s) < 1e-20)
                {
                    x = x0; y = y0; s += cs; continue;
                }
                k = cs / s; //求面积比例
                x = (x + k * x0) / (1 + k);
                y = (y + k * y0) / (1 + k);
                s += cs;
            }
            tp.X = x;
            tp.Y = y;
            return tp;
        }

        /// <summary>
        /// 寻找凸包的graham 扫描法
        /// </summary>
        /// <param name="PointSet">输入的点集</param>
        /// <param name="ch">输出的凸包上的点集，按照逆时针方向排列</param>
        /// <param name="n">为PointSet中的点的数目</param>
        /// <param name="len">为输出的凸包上的点的个数</param>
        public static void Graham_scan(Point3d[] PointSet, Point3d[] ch, int n, ref int len)
        {
            int i, j, k = 0, top = 2;
            Point3d tmp = new Point3d();
            // 选取PointSet中y坐标最小的点PointSet[k]，如果这样的点有多个，则取最左边的一个
            for (i = 1; i < n; i++)
                if (PointSet[i].Y < PointSet[k].Y || (PointSet[i].Y == PointSet[k].Y) && (PointSet[i].X < PointSet[k].X))
                    k = i;
            tmp = PointSet[0];
            PointSet[0] = PointSet[k];
            PointSet[k] = tmp; // 现在PointSet中y坐标最小的点在PointSet[0]
            for (i = 1; i < n - 1; i++) /* 对顶点按照相对PointSet[0]的极角从小到大进行排序，极角相同的按照距离PointSet[0]从近到远进行排序 */
            {
                k = i;
                for (j = i + 1; j < n; j++)
                    if (Multiply(PointSet[j], PointSet[k], PointSet[0]) > 0 ||  // 极角更小
                     (Multiply(PointSet[j], PointSet[k], PointSet[0]) == 0) && /* 极角相等，距离更短 */
                     Dist(PointSet[0], PointSet[j]) < Dist(PointSet[0], PointSet[k])
                       )
                        k = j;
                tmp = PointSet[i];
                PointSet[i] = PointSet[k];
                PointSet[k] = tmp;
            }
            ch[0] = PointSet[0];
            ch[1] = PointSet[1];
            ch[2] = PointSet[2];
            for (i = 3; i < n; i++)
            {
                while (Multiply(PointSet[i], ch[top], ch[top - 1]) >= 0)
                    top--;
                ch[++top] = PointSet[i];
            }
            len = top + 1;
        }

        /// <summary>
        /// r=Multiply(sp,ep,op),得到(sp-op)和(ep-op)的叉积
        /// r大于0：ep在矢量opsp的逆时针方向；
        /// r=0：opspep三点共线；
        /// r小于0：ep在矢量opsp的顺时针方向
        /// </summary>
        /// <param name="sp"></param>
        /// <param name="ep"></param>
        /// <param name="op"></param>
        /// <returns></returns>
        public static double Multiply(Point3d sp, Point3d ep, Point3d op)
        {
            return ((sp.X - op.X) * (ep.Y - op.Y) - (ep.X - op.X) * (sp.Y - op.Y));
        }
        /// <summary>
        /// 返回两点之间欧氏距离
        /// </summary>
        /// <param name="p1"></param>
        /// <param name="p2"></param>
        /// <returns></returns>
        public static double Dist(Point3d p1, Point3d p2)
        {
            return (Math.Sqrt((p1.X - p2.X) * (p1.X - p2.X) + (p1.Y - p2.Y) * (p1.Y - p2.Y)));
        }

        /// <summary>
        /// 检查点在面的哪一侧？ 
        /// </summary>
        /// <param name="point3D"></param>
        /// <param name="plane3D"></param>
        /// <returns></returns>
        public static bool CheckPointSideOnPlane(this Point3d point3D, Plane3d plane3D)
        {

            var pOnPlane = point3D.ProjectionTo(plane3D);

            if (pOnPlane == point3D)
            {
                return true;
            }

            if (new Vector3d(pOnPlane, point3D).Normalized == plane3D.Normal.Normalized)
            {
                return false;

            }
            else
            {
                return true;

            }

        }

        /// <summary>
        /// 两点中点
        /// </summary>
        /// <param name="pt1">点1 </param>
        /// <param name="pt2">点2 </param>
        /// <returns>point</returns>
        public static Point3d GetMiddlePoint(this Point3d pt1, Point3d pt2)
        {
            return new Point3d((pt1.X + pt2.X) / 2, (pt1.Y + pt2.Y) / 2, (pt1.Z + pt2.Z) / 2);
        }

        /// <summary>
        /// 零件 点坐标 转向装配体
        /// </summary>
        /// <param name="point3D">点 </param>
        /// <param name="swApp"> </param>
        ///  <param name="component2">零部件 </param>
        /// <returns>point</returns>
        public static Point3d PointInPartToAsm(this Point3d point3D, SldWorks swApp, Component2 component2)
        {
            var swMathUtility = swApp.IGetMathUtility();
            var planePoint = point3D.UnitX0001().ToArrayData();
            var tempPoint = (MathPoint)swMathUtility.CreatePoint(planePoint);
            var tempPointInAsm = tempPoint.IMultiplyTransform(component2.Transform2);
            var tempPoint3dInAsm = new Point3d((double[])tempPointInAsm.ArrayData);
            return tempPoint3dInAsm;
        }
        public static Point3d PointInPartToAsm2(this Point3d point3D, SldWorks swApp, Component2 component2)
        {
            if (point3D is null || swApp is null || component2 is null)
                return new Point3d();
            var swMathUtility = swApp.IGetMathUtility();
            var planePoint = point3D.UnitX0001().ToArrayData();
            var tempPoint = (MathPoint)swMathUtility.CreatePoint(planePoint);
            var tempPointInAsm = tempPoint.IMultiplyTransform(component2.Transform2);
            var tempPoint3dInAsm = new Point3d((double[])tempPointInAsm.ArrayData);
            return tempPoint3dInAsm;
        }

        /// <summary>
        /// Point转double[]
        /// </summary>
        /// <param name="point3D"></param>
        /// <returns>double[]</returns>
        public static double[] ToArrayData(this Point3d point3D)
        {
            return new[]
            {
                point3D.X, point3D.Y, point3D.Z
            };
        }

        public static double[] ToArrayDataRound3(this Point3d point3D)
        {
            return new[]
            {
                Math.Round(point3D.X,3), Math.Round(point3D.Y,3), Math.Round(point3D.Z,3)
            };
        }

        public static Point3d UnitX0001(this Point3d point3D)
        {
            point3D.X /= 1000;
            point3D.Y /= 1000;
            point3D.Z /= 1000;

            return point3D;
        }

        public static Point3d UnitX1000(this Point3d point3D)
        {
            point3D.X *= 1000;
            point3D.Y *= 1000;
            point3D.Z *= 1000;

            return point3D;
        }

        /// <summary>
        /// 单位从m改到mm
        /// </summary>
        /// <param name="point3D"></param>
        /// <returns></returns>
        public static Point3d UnitX1000Copy(this Point3d point3D)
        {
            var x = point3D.X * 1000;
            var y = point3D.Y * 1000;
            var z = point3D.Z * 1000;

            return new Point3d(x, y, z);
        }

        public static Point3d UnitX0001Copy(this Point3d point3D)
        {
            var x = point3D.X / 1000;
            var y = point3D.Y / 1000;
            var z = point3D.Z / 1000;

            return new Point3d(x, y, z);
        }

        /// <summary>
        /// 把草图点换成Point3d(单位不变)
        /// </summary>
        /// <param name="sketchPoint"></param>
        /// <returns></returns>
        public static Point3d ConvertToBasePoint3d(this SketchPoint sketchPoint)
        {
            return new Point3d(sketchPoint.X, sketchPoint.Y, sketchPoint.Z);
        }

        /// <summary>
        /// 判断P3是否在线段P1P2外且正向量方向路径上
        /// </summary>
        /// <returns></returns>
        public static bool boolP3InLine3dP1AndP2(Point3d P1, Point3d P2, Point3d P3, double dtp = 1e-10)
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

                if (P1P2.VectorNormalizedEqual(P1P3) && P1P2.VectorNormalizedEqual(P2P3))
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
        /// 判断点是否在平面上，如果不在bChange=true时移动点到平面上
        /// </summary>
        /// <returns></returns>
        public static bool IfPointOntInPlaneMovePointToPlane(this Point3d point, Plane3d plane, out Point3d ChangeP,
            bool bChange = false)
        {
            if (point.DistanceTo(plane) < 1e-10)
            {
                ChangeP = point;
                return true;
            }
            else
            {
                if (bChange)
                {
                    double PY = point.DistanceTo(plane);
                    Point3d point1 = point.Move(plane.Normal, PY);
                    if (point1.DistanceTo(plane) > 1e-10)
                    {
                        point1 = point.Move(plane.Normal.Negate(), PY);
                    }

                    ChangeP = point1;
                }
                else
                {
                    ChangeP = point;
                }

                return false;
            }
        }

        /// <summary>
        /// 获取偏移线段
        /// </summary>
        public static List<Point3d> OffsetListPoint(this List<Point3d> points, Vector3d startV,
            double offsetD, out Vector3d lastV)
        {
            List<Segment3d> mySs = new List<Segment3d>();
            List<Segment3d> mySs2 = new List<Segment3d>();
            lastV = null;

            List<Segment3d> segments = new List<Segment3d>();
            for (int i = 0; i < points.Count - 1; i++)
            {
                segments.Add(new Segment3d(points[i], points[i + 1]));
            }

            for (int i = 0; i < segments.Count - 1; i++)
            {
                if (segments[i].P1.DistanceTo(segments[i + 1].ToLine) < 0.1)
                {
                    // 三点共线则移除中间点
                    if (boolP3InLine3dP1AndP2(segments[i].P1, segments[i].P2, segments[i + 1].P2, 0.1))
                    {
                        segments[i] = new Segment3d(segments[i].P1, segments[i + 1].P2);
                        segments.Remove(segments[i + 1]);
                        i--;
                    }
                }
            }

            for (int i = 0; i < segments.Count; i++)
            {
                mySs.Add(new Segment3d(segments[i].P1, segments[i].P2));
                mySs2.Add(new Segment3d(segments[i].P1, segments[i].P2));
            }

            mySs[0].P1 = mySs[0].P1.Move(startV, offsetD);
            mySs[0].P2 = mySs[0].P2.Move(startV, offsetD);

            Plane3d plane3d = new Plane3d(segments[0].P1, segments[0].P2, mySs[0].P1);
            //WVector = plane3d.Normal;

            List<Segment3d> list = new List<Segment3d>();
            if (mySs.Count > 1)
            {
                for (int i = 1; i < mySs.Count; i++)
                {
                    Vector3d myv = new Vector3d(segments[i].P1, segments[i].P2);
                    myv.Normalize();
                    Vector3d gdv = myv.Cross(plane3d.Normal.Negate());
                    gdv.Normalize();

                    mySs[i].P1 = mySs[i].P1.Move(gdv, offsetD);
                    mySs[i].P2 = mySs[i].P2.Move(gdv, offsetD);
                }

                for (int i = 1; i < mySs.Count; i++)
                {
                    var point = GetSegmentIntersection3D(mySs[i - 1], mySs[i]);

                    if (point != null)
                    {
                        mySs[i - 1].P2 = point;
                        mySs[i].P1 = point;
                    }
                    else
                    {
                        //UIMessageTip.ShowWarning("偏移直线段不相交", 5000);
                        mySs.RemoveAt(i - 1);
                        mySs.RemoveAt(i);

                        Segment3d segment = new Segment3d(mySs[i - 1].P1, mySs[i].P2);
                        mySs.Insert(i - 1, segment);

                        segments.RemoveAt(i - 1);
                        segments.RemoveAt(i);

                        segment = new Segment3d(segments[i - 1].P1, segments[i].P2);
                        segments.Insert(i - 1, segment);
                    }
                }

                list = mySs.ToList();
            }
            else
            {
                list.Add(new Segment3d(mySs[0].P1, mySs[0].P2));
            }

            lastV = new Vector3d(segments[segments.Count - 1].P2, mySs[mySs.Count - 1].P2);

            var listP = new List<Point3d>(){ list[0].P1 };
            for (int i = 0; i < list.Count; i++)
            {
                listP.Add(list[i].P2);
            }

            return listP;
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
        /// 求两条MySegment所在直线交点
        /// </summary>
        /// <param name="segmentA"></param>
        /// <param name="segmentB"></param>
        /// <returns></returns>
        private static Point3d GetSegmentIntersection3D(Segment3d mySs11, Segment3d mySs12, double dD = 1e-4)
        {
            var d = GeometRi3D.Tolerance;
            GeometRi3D.Tolerance = dD;

            var p = new Line3d(mySs11.P1.UnitX0001Copy(), mySs11.Direction).IntersectionWith(
                new Line3d(mySs12.P1.UnitX0001Copy(), mySs12.Direction)) as Point3d;

            GeometRi3D.Tolerance = d;

            return p.UnitX1000Copy();
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
    }
}
