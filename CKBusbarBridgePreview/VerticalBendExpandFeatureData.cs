using SolidWorks.Interop.sldworks;
using System.Collections.Generic;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// 铜排大师立弯展开阶段使用的实体、配合面和持久化引用数据。
    /// </summary>
    public sealed class VerticalBendExpandFeatureData
    {
        public ModelDoc2 Model { get; set; }
        public bool IsPlanarBendSegment { get; set; }
        public Vertex StartVertex { get; set; }
        public Vertex EndVertex { get; set; }
        public Face2 ExpandFace { get; set; }
        public Face2 StartCoincidenceExpandFace { get; set; }
        public Face2 StartCoincidenceFace { get; set; }
        public Face2 StartOtherFace { get; set; }
        public Face2 EndCoincidenceExpandFace { get; set; }
        public Face2 EndCoincidenceFace { get; set; }
        public Face2 EndOtherFace { get; set; }
        public List<Face2> StartFaces { get; set; } = new List<Face2>();
        public List<Face2> EndFaces { get; set; } = new List<Face2>();
        public List<string> BendFeatureNames { get; set; } =
            new List<string>();
        public object ExpandFaceId { get; private set; }
        public object StartCoincidenceExpandFaceId { get; private set; }
        public object StartCoincidenceFaceId { get; private set; }
        public object StartOtherFaceId { get; private set; }
        public object EndCoincidenceExpandFaceId { get; private set; }
        public object EndCoincidenceFaceId { get; private set; }
        public object EndOtherFaceId { get; private set; }

        public void CapturePersistentReferences()
        {
            ExpandFaceId = GetPersistentReference(ExpandFace);
            StartCoincidenceExpandFaceId = GetPersistentReference(
                StartCoincidenceExpandFace);
            StartCoincidenceFaceId = GetPersistentReference(
                StartCoincidenceFace);
            StartOtherFaceId = GetPersistentReference(StartOtherFace);
            EndCoincidenceExpandFaceId = GetPersistentReference(
                EndCoincidenceExpandFace);
            EndCoincidenceFaceId = GetPersistentReference(
                EndCoincidenceFace);
            EndOtherFaceId = GetPersistentReference(EndOtherFace);
        }

        public Face2 ResolveFace(object persistentReference)
        {
            if (persistentReference == null || Model == null)
            {
                return null;
            }
            return Model.Extension.GetObjectByPersistReference3(
                persistentReference,
                out _) as Face2;
        }

        private object GetPersistentReference(object entity)
        {
            return entity == null || Model == null
                ? null
                : Model.Extension.GetPersistReference3(entity);
        }
    }
}
