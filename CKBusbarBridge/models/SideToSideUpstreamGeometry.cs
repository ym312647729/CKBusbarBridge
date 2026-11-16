using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.Prototype
{
    public sealed class SideToSideUpstreamGeometry
    {
        public BridgeCoordinateFrame Coordinates { get; }
        public SideToSideCopperPlanningInput Copper { get; }
        public IReadOnlyList<BridgeDesignObject> FixedObjects { get; }
        public double FrameInsulatorBodyHeight { get; }
        public string Position { get; }
        public string PreviewReferenceComponentName { get; }
        public SideToSideUpstreamGeometry(BridgeCoordinateFrame coordinates, SideToSideCopperPlanningInput copper,
            IEnumerable<BridgeDesignObject> objects, double frameInsulatorBodyHeight, string position)
            : this(coordinates, copper, objects, frameInsulatorBodyHeight, position, null) { }
        public SideToSideUpstreamGeometry(BridgeCoordinateFrame coordinates, SideToSideCopperPlanningInput copper,
            IEnumerable<BridgeDesignObject> objects, double frameInsulatorBodyHeight, string position,
            string previewReferenceComponentName)
        {
            Coordinates = coordinates ?? throw new ArgumentNullException(nameof(coordinates));
            Copper = copper ?? throw new ArgumentNullException(nameof(copper));
            FixedObjects = Array.AsReadOnly(objects.ToArray());
            FrameInsulatorBodyHeight = frameInsulatorBodyHeight; Position = position;
            PreviewReferenceComponentName = previewReferenceComponentName;
        }
    }
}
