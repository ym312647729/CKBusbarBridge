using SolidWorks.Interop.sldworks;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridgePreview
{
    /// <summary>从 SolidWorks 组件抽取出的虚拟组件。</summary>
    public sealed class ComponentInto
    {
        public ISldWorks swApp { get; set; }
        public Component2 Component { get; set; }
        public string Name { get; set; }
        public List<BodyInto> BodyIntos { get; set; } = new List<BodyInto>();
        public SystemPartType_e PartTypeE { get; set; } = SystemPartType_e.Unknow;

        public ComponentInto Copy()
        {
            var copiedComponent = new ComponentInto
            {
                swApp = swApp,
                Component = Component,
                Name = Name,
                PartTypeE = PartTypeE
            };
            copiedComponent.BodyIntos = BodyIntos?
                .Where(bodyInto => bodyInto != null)
                .Select(bodyInto => bodyInto.Copy(copiedComponent))
                .ToList() ?? new List<BodyInto>();
            return copiedComponent;
        }
    }
}
