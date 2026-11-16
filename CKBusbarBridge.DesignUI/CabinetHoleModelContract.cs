using System;
using System.Collections.Generic;

namespace CKBusbarBridge.Drivers
{
    /// <summary>柜体开孔写入与反读共用的原模型尺寸路径。</summary>
    internal sealed class CabinetHoleModelContract
    {
        internal static CabinetHoleModelContract ForSideToSide(bool isFront)
        {
            // 柜后模板原尺寸名为 D3，写入和反读必须绑定同一实际对象。
            return new CabinetHoleModelContract(isFront,
                fixedHoleSpacingDimensionName: isFront ? null : "D3@草图8");
        }

        internal CabinetHoleModelContract(bool isFront, bool leftRight = false, string fixedHoleSpacingDimensionName = null)
        {
            FeaturePositionName = "柜" + (isFront ? "前" : "后") + (leftRight ? "左右开孔" : string.Empty);
            SketchName = leftRight ? (isFront ? "草图9" : "草图12") : (isFront ? "草图6" : "草图8");
            EdgeDimensionName = (isFront || leftRight ? "距柜前距离" : "距柜后距离") + "@" + SketchName;
            FixedHoleSpacingDimensionName = fixedHoleSpacingDimensionName ?? "接口固定孔间距@" + SketchName;
            L1DimensionName = "L1@" + SketchName;
            L2DimensionName = "L2@" + SketchName;
            L3DimensionName = "L3@" + SketchName;
            AssemblyDimensionName = isFront ? "铜排中心至柜前距离@距离3" : "铜排中心距柜后距离@距离4";
        }

        internal string SketchName { get; }
        internal string EdgeDimensionName { get; }
        internal string FixedHoleSpacingDimensionName { get; }
        internal string L1DimensionName { get; }
        internal string L2DimensionName { get; }
        internal string L3DimensionName { get; }
        internal string AssemblyDimensionName { get; }
        private string FeaturePositionName { get; }

        internal string FeatureName(string cabinetName)
        {
            if (string.IsNullOrWhiteSpace(cabinetName)) throw new ArgumentException("柜体名称不能为空。", nameof(cabinetName));
            return cabinetName + "顶开孔（" + FeaturePositionName + "）";
        }

        internal IDictionary<string, double> CreatePartDimensionMap(double edgeDistance, double fixedSpacing,
            double l1, double l2, double l3) => new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            [EdgeDimensionName] = edgeDistance,
            [FixedHoleSpacingDimensionName] = fixedSpacing,
            [L1DimensionName] = l1,
            [L2DimensionName] = l2,
            [L3DimensionName] = l3
        };

        internal CabinetHoleModelValues Read(bool enabled, Func<string, double> readPart, Func<string, double> readAssembly)
        {
            // 写入端仅驱动已启用孔；未启用孔没有本次驱动尺寸，不读取其模板残留或构造默认读数。
            if (!enabled) return new CabinetHoleModelValues { IsEnabled = false };
            if (readPart == null) throw new ArgumentNullException(nameof(readPart));
            if (readAssembly == null) throw new ArgumentNullException(nameof(readAssembly));
            return new CabinetHoleModelValues
            {
                IsEnabled = true,
                EdgeDistance = readPart(EdgeDimensionName),
                FixedSpacing = readPart(FixedHoleSpacingDimensionName),
                L1 = readPart(L1DimensionName),
                L2 = readPart(L2DimensionName),
                L3 = readPart(L3DimensionName),
                CenterDistance = readAssembly(AssemblyDimensionName)
            };
        }
    }

    internal sealed class CabinetHoleModelValues
    {
        internal bool IsEnabled { get; set; }
        internal double? EdgeDistance { get; set; }
        internal double? FixedSpacing { get; set; }
        internal double? L1 { get; set; }
        internal double? L2 { get; set; }
        internal double? L3 { get; set; }
        internal double? CenterDistance { get; set; }
    }
}
