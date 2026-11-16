using System;
using System.IO;
using CKBusbarBridge.Helpers;
using SolidWorks.Interop.sldworks;

namespace CKBusbarBridge.Drivers
{
    internal sealed class BridgeCopperGenerationContext
    {
        internal string AssemblyPath { get; }
        internal string Configuration { get; }
        internal IDrawingNumberGenerator DrawingNumbers { get; }
        internal BridgeCopperGenerationContext(string assemblyPath, string configuration, IDrawingNumberGenerator drawingNumbers)
        { AssemblyPath = assemblyPath; Configuration = configuration; DrawingNumbers = drawingNumbers; }
        internal void Validate(IModelDoc2 model)
        {
            if (!(model is AssemblyDoc) || string.IsNullOrWhiteSpace(model.GetPathName()) ||
                !string.Equals(Path.GetFullPath(model.GetPathName()), Path.GetFullPath(AssemblyPath), StringComparison.OrdinalIgnoreCase) ||
                model.ConfigurationManager.ActiveConfiguration.Name != Configuration)
                throw new InvalidOperationException("铜排生成目标总装或配置已经改变。");
        }
    }
}
