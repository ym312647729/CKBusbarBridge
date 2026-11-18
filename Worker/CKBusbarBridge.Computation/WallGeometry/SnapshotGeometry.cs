using System.Collections.Generic;
using GeometRi;
namespace CKBusbarBridge.Computation.WallGeometry
{
    internal static class SnapshotVectorExtensions
    {
        internal static Vector3d Negate(this Vector3d vector) => vector == null ? null : new Vector3d(-vector.X, -vector.Y, -vector.Z);
    }
    internal sealed class ComponentInto { internal string Name; internal List<BodyInto> BodyIntos; }
    internal sealed class BodyInto { internal List<FaceInto> FaceIntos; }
    internal sealed class FaceInto { internal bool IsPlane; internal Plane3d FacePlane; internal List<EdgeInto> EdgeIntos; }
    internal enum SwCurveType { Line, Arc, Other }
    internal sealed class EdgeInto { internal SwCurveType CurveType; internal Segment3d Segment; }
    internal sealed class ModeledBusbar
    {
        internal PlanarBendBusbarSetting CenterlineSetting;
        internal Segment3d TerminalSegment;
        internal Point3d CenterPoint;
    }
}
