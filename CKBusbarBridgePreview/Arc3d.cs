using GeometRi;
using SolidWorks.Interop.sldworks;

namespace CKBusbarBridgePreview
{
    public class Arc3d
    {
        public Point3d CenterPoint { get; set; }
        public Point3d StartPoint { get; set; }
        public Point3d EndPoint { get; set; }
        public Vector3d Normal { get; set; }

        public double R { get; set; }
        public double Angle { get; set; }

        public Arc3d()
        {

        }

        public Arc3d(Point3d centerPoint, Point3d startPoint, Point3d endPoint, Vector3d normal, double r)
        {
            CenterPoint = centerPoint;
            StartPoint = startPoint;
            EndPoint = endPoint;
            Normal = normal;
            R = r;
         
        }
    }

    public class MySegment3d
    {
 
        public Point3d StartPoint { get; set; }
        public Point3d EndPoint { get; set; }
        public Edge swEdge { get; set; }



 

        public MySegment3d()
        {

        }

        public MySegment3d( Point3d startPoint, Point3d endPoint,Edge edge)
        {
        
            StartPoint = startPoint;
            EndPoint = endPoint;
            swEdge = edge;
         
        }
    }


}
