using GeometRi;
using SolidWorks.Interop.sldworks;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridgePreview
{
    /// <summary>从 SolidWorks 面抽取出的虚拟面；平面和边均使用装配体坐标（mm）。</summary>
    public sealed class FaceInto
    {
        public ComponentInto ComponentInto { get; set; }
        public Face2 Face { get; set; }
        public string FaceName { get; set; }
        public bool IsPlane { get; set; }
        public Plane3d FacePlane { get; set; }
        public List<Edge> Edges { get; set; } = new List<Edge>();
        public List<EdgeInto> EdgeIntos { get; set; } = new List<EdgeInto>();

        public FaceInto Copy(ComponentInto componentInto = null)
        {
            return new FaceInto
            {
                ComponentInto = componentInto ?? ComponentInto,
                Face = Face,
                FaceName = FaceName,
                IsPlane = IsPlane,
                FacePlane = FacePlane == null
                    ? null
                    : new Plane3d(FacePlane.Point.Copy(), FacePlane.Normal.Copy()),
                Edges = Edges?.ToList() ?? new List<Edge>(),
                EdgeIntos = EdgeIntos?
                    .Where(edgeInto => edgeInto != null)
                    .Select(edgeInto => edgeInto.Copy())
                    .ToList() ?? new List<EdgeInto>()
            };
        }
    }
}
