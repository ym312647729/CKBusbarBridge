using SolidWorks.Interop.sldworks;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridgePreview
{
    /// <summary>从 SolidWorks 实体抽取出的虚拟实体。</summary>
    public sealed class BodyInto
    {
        public ComponentInto ComponentInto { get; set; }
        public Body2 Body { get; set; }
        public List<Face2> Face2s { get; set; } = new List<Face2>();
        public List<FaceInto> FaceIntos { get; set; } = new List<FaceInto>();

        public BodyInto Copy(ComponentInto componentInto = null)
        {
            var copiedComponent = componentInto ?? ComponentInto;
            return new BodyInto
            {
                ComponentInto = copiedComponent,
                Body = Body,
                Face2s = Face2s?.ToList() ?? new List<Face2>(),
                FaceIntos = FaceIntos?
                    .Where(faceInto => faceInto != null)
                    .Select(faceInto => faceInto.Copy(copiedComponent))
                    .ToList() ?? new List<FaceInto>()
            };
        }
    }
}
