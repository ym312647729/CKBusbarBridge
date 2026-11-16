using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CKBusbarBridge.models
{
    [Serializable]
    public class BusBridgeDimSizeDefine
    {
        public string Id { get; set; }
        public string SizeName { get; set; }

        public List<SizeDefine> SizeDefine { get; set; } = new List<SizeDefine>();
    }

    [Serializable]
    public class SizeDefine
    {

        public string Name { get; set; }
        public string DisplayName { get; set; }
        public double BaseValue { get; set; }
        public double Scale { get; set; }
        public string Func { get; set; }
        public bool IsScaleReplace { get; set; }
    }
}
