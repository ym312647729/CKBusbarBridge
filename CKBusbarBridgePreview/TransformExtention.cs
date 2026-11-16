using GeometRi;
using SolidWorks.Interop.sldworks;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridgePreview
{
    public static class TransformExtention
    {
        /// <summary>
        /// 零件转到装配体(mm)
        /// </summary>
        /// <param name="point3d"></param>
        /// <param name="swApp"></param>
        /// <param name="baseComponent2"></param>
        /// <returns></returns>
        public static Point3d ConvertPointInPartToAssembly1000(this Point3d point3d, ISldWorks swApp,
            Component2 baseComponent2)
        {
            if (baseComponent2 is null)
                return null;

            return point3d.ConvertPointInPartToAssembly1000(swApp.IGetMathUtility(), baseComponent2);
        }
        
        /// <summary>
        /// 装配体转到零件(mm)
        /// </summary>
        /// <param name="point3d"></param>
        /// <param name="swApp"></param>
        /// <param name="baseComponent2"></param>
        /// <returns></returns>
        public static Point3d ConvertPointInAssemblyToPart1000(this Point3d point3d, ISldWorks swApp,
            Component2 baseComponent2)
        {
            if (baseComponent2 is null)
                return null;

            return point3d.ConvertPointInAssemblyToPart1000(swApp.IGetMathUtility(), baseComponent2);
        }

        /// <summary>
        /// 零件转到装配体(mm)
        /// </summary>
        /// <param name="point3d"></param>
        /// <param name="swApp"></param>
        /// <param name="baseComponent2"></param>
        /// <returns></returns>
        public static Point3d ConvertPointInPartToAssembly1000(this Point3d point3d, IMathUtility swMathUtility,
            Component2 baseComponent2)
        {
            if (baseComponent2 is null)
                return null;
            var mathPoint3d = (MathPoint)swMathUtility.CreatePoint(point3d.UnitX0001Copy().ToArrayData());

            var resPoint = (MathPoint)mathPoint3d.MultiplyTransform(baseComponent2.Transform);

            return new Point3d((double[])resPoint.ArrayData).UnitX1000Copy();
        }

        /// <summary>
        /// 装配体转到零件(mm)
        /// </summary>
        /// <param name="point3d"></param>
        /// <param name="swApp"></param>
        /// <param name="baseComponent2"></param>
        /// <returns></returns>
        public static Point3d ConvertPointInAssemblyToPart1000(this Point3d point3d, IMathUtility swMathUtility,
            Component2 baseComponent2)
        {
            if (baseComponent2 is null)
                return null;
            var mathPoint3d = (MathPoint)swMathUtility.CreatePoint(point3d.UnitX0001Copy().ToArrayData());
            var resPoint = (MathPoint)mathPoint3d.MultiplyTransform(baseComponent2.Transform.Inverse());
            return new Point3d((double[])resPoint.ArrayData).UnitX1000Copy();
        }

        /// <summary>
        /// 零件转到零件(mm)
        /// </summary>
        /// <param name="point3d"></param>
        /// <param name="swApp"></param>
        /// <param name="baseComponent2"></param>
        /// <returns></returns>
        public static Point3d ConvertPointInPartToPart1000(this Point3d point3d, ISldWorks swApp,
            Component2 fromComp2, Component2 toComp2)
        {
            if (fromComp2 is null || toComp2 is null)
            {
                return null;
            }

            var mathPoint3d = point3d.ConvertPointInPartToAssembly1000(swApp, fromComp2);
            return mathPoint3d.ConvertPointInAssemblyToPart1000(swApp, toComp2);
        }

        /// <summary>
        /// 转换零件中的向量到装配体
        /// </summary>
        public static Vector3d ConvertVectorPartToAssembly(this Vector3d v, ISldWorks swApp, Component2 baseComponent2)
        {
            if (v == null)
            {
                return null;
            }

            var p1 = v.ToPoint;
            var p2 = v.ToPoint.Move(v, 100);

            var vectorInAsm1Start = p1.ConvertPointInPartToAssembly1000(swApp, baseComponent2);
            var vectorInAsm1End = p2.ConvertPointInPartToAssembly1000(swApp, baseComponent2);

            return new Vector3d(vectorInAsm1Start, vectorInAsm1End);

        }

        /// <summary>
        /// 转换装配体中的向量到零件
        /// </summary>
        public static Vector3d ConvertVectorAssemblyToPart(this Vector3d v, ISldWorks swApp, Component2 baseComponent2)
        {
            var p1 = v.ToPoint;
            var p2 = v.ToPoint.Move(v, 100);

            var vectorInAsm1Start = p1.ConvertPointInAssemblyToPart1000(swApp, baseComponent2);
            var vectorInAsm1End = p2.ConvertPointInAssemblyToPart1000(swApp, baseComponent2);

            return new Vector3d(vectorInAsm1Start, vectorInAsm1End);
        }
        
        /// <summary>
        /// 零件转到装配体(mm)
        /// </summary>
        /// <param name="point3d"></param>
        /// <param name="swApp"></param>
        /// <param name="baseComponent2"></param>
        /// <returns></returns>
        public static Segment3d ConvertSegment3dInPartToAssembly1000(this Segment3d segment, ISldWorks swApp,
            Component2 baseComponent2)
        {
            if (baseComponent2 is null)
                return null;
            var swMathUtility = swApp.IGetMathUtility();

            var mathPoint1 = (MathPoint)swMathUtility.CreatePoint(segment.P1.UnitX0001Copy().ToArrayData());
            var mathPoint2 = (MathPoint)swMathUtility.CreatePoint(segment.P2.UnitX0001Copy().ToArrayData());

            var resPoint1 = (MathPoint)mathPoint1.MultiplyTransform(baseComponent2.Transform);
            var resPoint2 = (MathPoint)mathPoint2.MultiplyTransform(baseComponent2.Transform);

            return new Segment3d(new Point3d((double[])resPoint1.ArrayData).UnitX1000Copy()
                , new Point3d((double[])resPoint2.ArrayData).UnitX1000Copy());
        }

        /// <summary>
        /// 装配体转到零件(mm)
        /// </summary>
        /// <param name="point3d"></param>
        /// <param name="swApp"></param>
        /// <param name="baseComponent2"></param>
        /// <returns></returns>
        public static Segment3d ConvertSegment3dInAssemblyToPart1000(this Segment3d segment, ISldWorks swApp,
            Component2 baseComponent2)
        {
            if (baseComponent2 is null)
                return null;
            var swMathUtility = swApp.IGetMathUtility();

            var P1 = segment.P1.ConvertPointInAssemblyToPart1000(swApp, baseComponent2);
            var P2 = segment.P2.ConvertPointInAssemblyToPart1000(swApp, baseComponent2);

            return new Segment3d(P1, P2);
        }

        /// <summary>
        /// 装配体转到零件(mm)
        /// </summary>
        /// <param name="point3d"></param>
        /// <param name="swApp"></param>
        /// <param name="baseComponent2"></param>
        /// <returns></returns>
        public static List<Segment3d> ConvertSegment3dsInPartToAssembly1000(this List<Segment3d> segments, ISldWorks swApp,
            Component2 baseComponent2)
        {
            return segments.Select(x => x.ConvertSegment3dInPartToAssembly1000(swApp, baseComponent2)).ToList();
        }

        /// <summary>
        /// 零件转到装配体(mm)
        /// </summary>
        /// <param name="point3d"></param>
        /// <param name="swApp"></param>
        /// <param name="baseComponent2"></param>
        /// <returns></returns>
        public static List<Segment3d> ConvertSegment3dsInAssemblyToPart1000(this List<Segment3d> segments, ISldWorks swApp,
            Component2 baseComponent2)
        {
            return segments.Select(x => x.ConvertSegment3dInAssemblyToPart1000(swApp, baseComponent2)).ToList();
        }

        /// <summary>
        /// 零件转到装配体(mm)
        /// </summary>
        /// <param name="point3d"></param>
        /// <param name="swApp"></param>
        /// <param name="baseComponent2"></param>
        /// <returns></returns>
        public static Plane3d ConvertPlaneInPartToAssembly1000(this Plane3d plane, ISldWorks swApp, Component2 baseComponent2)
        {
            if (plane == null) return null;

            Point3d p1 = plane.Point;
            Point3d p2 = plane.Point.Move(plane.Normal, 100);

            Point3d vectorInAsm1Start = p1.ConvertPointInPartToAssembly1000(swApp, baseComponent2);
            Point3d vectorInAsm1End = p2.ConvertPointInPartToAssembly1000(swApp, baseComponent2);

            return new Plane3d(vectorInAsm1Start, new Vector3d(vectorInAsm1Start, vectorInAsm1End));
        }

        /// <summary>
        /// 装配体转到零件(mm)
        /// </summary>
        /// <param name="point3d"></param>
        /// <param name="swApp"></param>
        /// <param name="baseComponent2"></param>
        /// <returns></returns>
        public static Plane3d ConvertPlaneInAssemblyToPart1000(this Plane3d plane, ISldWorks swApp, Component2 baseComponent2)
        {
            if (plane == null) return null;

            Point3d p1 = plane.Point;
            Point3d p2 = plane.Point.Move(plane.Normal, 100);

            Point3d vectorInAsm1Start = p1.ConvertPointInAssemblyToPart1000(swApp, baseComponent2);
            Point3d vectorInAsm1End = p2.ConvertPointInAssemblyToPart1000(swApp, baseComponent2);

            return new Plane3d(vectorInAsm1Start, new Vector3d(vectorInAsm1Start, vectorInAsm1End));
        }

        /// <summary>
        /// 转换零件的点坐标到草图中
        /// </summary>
        /// <param name="point3D"></param>
        /// <param name="swApp"></param>
        /// <param name="baseSketch"></param>
        /// <returns></returns>
        public static Point3d ConvertModelPointToSketch(this Point3d point3D, ISldWorks swApp, Sketch baseSketch)
        {
            var pointToModelTransForm = baseSketch.ModelToSketchTransform;

            var swMathUtility = swApp.IGetMathUtility();
            var tempMathStartPoint = (MathPoint)swMathUtility.CreatePoint(point3D.ToArrayData());

            var tempPointInSkecth = (MathPoint)tempMathStartPoint.MultiplyTransform(pointToModelTransForm);
            var tempStartPointInModel = (double[])(tempPointInSkecth.ArrayData);

            return new Point3d(tempStartPointInModel[0], tempStartPointInModel[1], tempStartPointInModel[2]);
        }
        
        /// <summary>
        /// 把草图中的坐标点转换到模型中(单位变换为mm)
        /// </summary>
        /// <param name="sketchPoint"></param>
        /// <param name="baseSketch"></param>
        /// <returns></returns>
        public static Point3d ConvertSketchPointToModel1000(this SketchPoint sketchPoint, Sketch baseSketch)
        {
            var pointToModelTransForm = baseSketch.ModelToSketchTransform;
            var arrayData = (double[])pointToModelTransForm.ArrayData;

            var resPoint3d = sketchPoint.ConvertToBasePoint3d();

            var matrix3d = new Matrix3d(new[] { arrayData[0], arrayData[1], arrayData[2] },
                new[] { arrayData[3], arrayData[4], arrayData[5] },
                new[] { arrayData[6], arrayData[7], arrayData[8] });

            var tempRPoint = matrix3d.Mult(resPoint3d);
            var movePoint = tempRPoint.Move(new Vector3d(arrayData[0], arrayData[1], arrayData[2]), arrayData[9])
                .Move(new Vector3d(arrayData[3], arrayData[4], arrayData[5]), arrayData[10])
                .Move(new Vector3d(arrayData[6], arrayData[7], arrayData[8]), arrayData[11]);

            return movePoint.UnitX1000();

            //var swMathUtility = swApp.IGetMathUtility();
            //var tempMathStartPoint = (MathPoint)swMathUtility.CreatePoint(tempStartPoint.ToArrayData());
            //var tempMathEndPoint = (MathPoint)swMathUtility.CreatePoint(tempEndPoint.ToArrayData());

            // var tempStartPointInModel = (double[])tempMathStartPoint.IMultiplyTransform(pointToModelTransForm).ArrayData;

            //return new Point3d(sketchPoint.X, sketchPoint.Y, sketchPoint.N);
        }

        /// <summary>
        /// 转换一个点坐标到模型中
        /// </summary>
        /// <param name="resPoint3d"></param>
        /// <param name="baseSketch"></param>
        /// <returns></returns>
        public static Point3d ConvertSketchPointToModel1000(this Point3d resPoint3d, Sketch baseSketch)
        {
            var pointToModelTransForm = baseSketch.ModelToSketchTransform;
            var arrayData = (double[])pointToModelTransForm.ArrayData;

            var matrix3d = new Matrix3d(new[] { arrayData[0], arrayData[1], arrayData[2] },
                new[] { arrayData[3], arrayData[4], arrayData[5] },
                new[] { arrayData[6], arrayData[7], arrayData[8] });

            var tempCoor = new Coord3d(new Point3d(arrayData[9] * 1000, arrayData[10] * 1000, arrayData[11] * 1000), matrix3d);
            var movePoint = resPoint3d.ConvertTo(tempCoor);

            return new Point3d(movePoint.X, movePoint.Y, movePoint.Z);
        }

        /// <summary>
        /// 草图转模型空间点
        /// </summary>
        public static Point3d ConvertSketchPointToModel(this Point3d point3D, ISldWorks swApp, Sketch baseSketch)
        {
            var pointToModelTransForm = baseSketch.ModelToSketchTransform;
            var swMathUtility = swApp.IGetMathUtility();
            var tempMathStartPoint = (MathPoint)swMathUtility.CreatePoint(point3D.ToArrayData());

            var tempPointInSkecth = (MathPoint)tempMathStartPoint.MultiplyTransform(pointToModelTransForm.Inverse());
            var tempStartPointInModel = (double[])(tempPointInSkecth.ArrayData);

            return new Point3d(tempStartPointInModel[0], tempStartPointInModel[1], tempStartPointInModel[2]);
        }
        
    }
}
