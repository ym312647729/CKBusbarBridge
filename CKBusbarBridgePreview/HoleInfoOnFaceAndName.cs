using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AutoOpeningHoleDataModel;

namespace CKBusbarBridgePreview
{
    public class HoleInfoOnFaceAndName
    {
        public List<HoleInfoOnFace> thisHole { get; set; }

        public string featureName { get; set; }

        public string sketchName { get; set; }
    }
}
