using System;
using System.Collections.Generic;

namespace CKBusbarBridge.Models
{
    /// <summary>生成尺寸与物理反读共用的柜宽约束，禁止用派生值覆盖不一致的模型读数。</summary>
    internal static class CabinetBaseDimensionRules
    {
        internal static string GetMismatch(string position, string dimension, double? cabinetWidth, double? actual)
            => GetMismatch(position, dimension, cabinetWidth, actual, "所属柜体宽度", "柜宽");

        internal static string GetMismatch(string position, string dimension, double? cabinetWidth, double? actual,
            string expectedDescription, string expectedLabel)
        {
            if (cabinetWidth.HasValue && actual.HasValue
                && cabinetWidth.Value > 0d && actual.Value > 0d
                && !double.IsInfinity(cabinetWidth.Value) && !double.IsInfinity(actual.Value)
                && Math.Abs(cabinetWidth.Value - actual.Value) <= 0.01d + 1e-9d)
                return null;

            return position + "/" + dimension + "必须等于" + expectedDescription + "；" + expectedLabel + "="
                + Format(cabinetWidth) + " mm，实际=" + Format(actual) + " mm。";
        }

        internal static void RequireMatch(string position, string dimension, double? cabinetWidth, double? actual)
        {
            var mismatch = GetMismatch(position, dimension, cabinetWidth, actual);
            if (mismatch != null) throw new InvalidOperationException(mismatch);
        }

        private static string Format(double? value)
        {
            return value.HasValue
                ? value.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                : "缺失";
        }
    }

    /// <summary>
    /// 面对面独立基座模板类型。类型参与缓存签名，防止普通基座与增高基座交叉复用。
    /// </summary>
    internal enum FaceToFaceBaseModelKind
    {
        Base,
        ElevatedBase
    }

    /// <summary>
    /// 单个基座位置的绝缘子驱动及定位计划。
    /// 定位尺寸使用 SolidWorks 的“尺寸名@草图名”格式，数值单位为毫米。
    /// </summary>
    internal sealed class FaceToFaceBaseInsulatorPlan
    {
        internal FaceToFaceBaseInsulatorPlan(
            FaceToFaceBaseModelKind targetBaseKind,
            string placementDimensionName,
            double placementHeight,
            IReadOnlyDictionary<string, object> globalVariables,
            int singlePhaseRootCount,
            string busbarSpec)
        {
            TargetBaseKind = targetBaseKind;
            PlacementDimensionName = string.IsNullOrWhiteSpace(placementDimensionName)
                ? throw new ArgumentException("绝缘子定位尺寸名称不能为空。", nameof(placementDimensionName))
                : placementDimensionName;
            PlacementHeight = placementHeight;
            GlobalVariables = globalVariables
                ?? throw new ArgumentNullException(nameof(globalVariables));
            SinglePhaseRootCount = singlePhaseRootCount;
            BusbarSpec = string.IsNullOrWhiteSpace(busbarSpec)
                ? throw new ArgumentException("铜排规格不能为空。", nameof(busbarSpec))
                : busbarSpec;
        }

        internal FaceToFaceBaseModelKind TargetBaseKind { get; }

        internal string PlacementDimensionName { get; }

        internal double PlacementHeight { get; }

        internal IReadOnlyDictionary<string, object> GlobalVariables { get; }

        internal int SinglePhaseRootCount { get; }

        internal string BusbarSpec { get; }
    }

    /// <summary>
    /// 一棵基座模型树的驱动计划。
    /// 普通基座写入基座高/宽/深、L1～L3 和基座开孔高度；增高基座写入高度/宽度/深度和 L1～L3。
    /// </summary>
    internal sealed class FaceToFaceBaseModelPlan
    {
        internal FaceToFaceBaseModelPlan(
            FaceToFaceBaseModelKind kind,
            double height,
            double width,
            double depth,
            double l1,
            double l2,
            double l3,
            bool isTemplateDefault,
            string cacheKey,
            IReadOnlyDictionary<string, object> globalVariables,
            bool? hasElevatedBase)
        {
            Kind = kind;
            Height = height;
            Width = width;
            Depth = depth;
            L1 = l1;
            L2 = l2;
            L3 = l3;
            IsTemplateDefault = isTemplateDefault;
            CacheKey = cacheKey ?? throw new ArgumentNullException(nameof(cacheKey));
            GlobalVariables = globalVariables
                ?? throw new ArgumentNullException(nameof(globalVariables));
            HasElevatedBase = hasElevatedBase;
        }

        internal FaceToFaceBaseModelKind Kind { get; }

        internal double Height { get; }

        internal double Width { get; }

        internal double Depth { get; }

        internal double L1 { get; }

        internal double L2 { get; }

        internal double L3 { get; }

        internal bool IsTemplateDefault { get; }

        internal string CacheKey { get; }

        internal IReadOnlyDictionary<string, object> GlobalVariables { get; }

        /// <summary>
        /// 普通基座是否与增高基座成套使用；增高基座自身不需要该状态。
        /// </summary>
        internal bool? HasElevatedBase { get; }
    }

    /// <summary>
    /// 独立基座规划使用的柜孔尺寸快照。
    /// 面对面和面对墙孔参数对象字段同构，规划前先投影到该类型，避免复制两套公式。
    /// </summary>
    internal sealed class StandaloneBaseHoleInput
    {
        internal bool IsEnabled { get; set; }

        internal double CabinetWidth { get; set; }

        internal double BaseHeight { get; set; }

        internal double ExtensionBaseHeight { get; set; }

        internal double FixedHoleSpacing { get; set; }

        internal double L1 { get; set; }

        internal double L2 { get; set; }

        internal double L3 { get; set; }

        internal double BaseOpeningHeight { get; set; }

        internal double InsulatorPosition { get; set; }

        internal static StandaloneBaseHoleInput From(CabinetHoleParameters hole, double cabinetWidth)
        {
            if (hole == null)
            {
                return null;
            }

            return new StandaloneBaseHoleInput
            {
                CabinetWidth = cabinetWidth,
                IsEnabled = hole.IsEnabled,
                BaseHeight = hole.BaseHeight,
                ExtensionBaseHeight = hole.ExtensionBaseHeight,
                FixedHoleSpacing = hole.FixedHoleSpacing,
                L1 = hole.L1,
                L2 = hole.L2,
                L3 = hole.L3,
                BaseOpeningHeight = hole.BaseOpeningHeight,
                InsulatorPosition = hole.InsulatorPosition
            };
        }

        internal static StandaloneBaseHoleInput From(FaceToWallCabinetHoleParameters hole, double cabinetWidth)
        {
            if (hole == null)
            {
                return null;
            }

            return new StandaloneBaseHoleInput
            {
                CabinetWidth = cabinetWidth,
                IsEnabled = hole.IsEnabled,
                BaseHeight = hole.BaseHeight,
                ExtensionBaseHeight = hole.ExtensionBaseHeight,
                FixedHoleSpacing = hole.FixedHoleSpacing,
                L1 = hole.L1,
                L2 = hole.L2,
                L3 = hole.L3,
                BaseOpeningHeight = hole.BaseOpeningHeight,
                InsulatorPosition = hole.InsulatorPosition
            };
        }
    }

    /// <summary>
    /// 独立基座内绝缘子规格输入。相间距字段名因场景而异，规划前统一到这里。
    /// </summary>
    internal sealed class StandaloneBaseInsulatorInput
    {
        internal double PhaseSpacing { get; set; }

        internal double InsulatorHeight { get; set; }

        internal int SinglePhaseRootCount { get; set; }

        internal string BusbarSpec { get; set; }

        internal static StandaloneBaseInsulatorInput From(CabinetGeneralParameters general)
        {
            if (general == null)
            {
                return null;
            }

            return new StandaloneBaseInsulatorInput
            {
                PhaseSpacing = general.PhaseSpacing,
                InsulatorHeight = general.InsulatorHeight,
                SinglePhaseRootCount = general.SinglePhaseRootCount,
                BusbarSpec = general.BusbarSpec
            };
        }

        internal static StandaloneBaseInsulatorInput From(FaceToWallGeneralParameters general)
        {
            if (general == null)
            {
                return null;
            }

            return new StandaloneBaseInsulatorInput
            {
                // 面对墙柜体驱动用伸出排相间距，对应面对面的相间距。
                PhaseSpacing = general.ExposedBusbarPhaseSpacing,
                InsulatorHeight = general.InsulatorHeight,
                SinglePhaseRootCount = general.SinglePhaseRootCount,
                BusbarSpec = general.BusbarSpec
            };
        }
    }

    /// <summary>
    /// 单个总装配体坐标位置所需的普通基座和可选增高基座计划。
    /// </summary>
    internal sealed class FaceToFaceBasePositionPlan
    {
        internal FaceToFaceBasePositionPlan(
            string positionName,
            string rootCoordinateName,
            FaceToFaceBaseModelPlan baseModel,
            FaceToFaceBaseModelPlan elevatedBaseModel,
            FaceToFaceBaseInsulatorPlan insulator)
        {
            PositionName = string.IsNullOrWhiteSpace(positionName)
                ? throw new ArgumentException("基座位置名称不能为空。", nameof(positionName))
                : positionName;
            RootCoordinateName = string.IsNullOrWhiteSpace(rootCoordinateName)
                ? throw new ArgumentException("总装根坐标系名称不能为空。", nameof(rootCoordinateName))
                : rootCoordinateName;
            BaseModel = baseModel ?? throw new ArgumentNullException(nameof(baseModel));
            ElevatedBaseModel = elevatedBaseModel;
            Insulator = insulator ?? throw new ArgumentNullException(nameof(insulator));
        }

        /// <summary>
        /// 位置身份，用于项目目录和模型对照映射键，例如「柜前」「A柜前」。
        /// </summary>
        internal string PositionName { get; }

        /// <summary>
        /// 总装配体根级配合坐标系。面对面与位置名相同；面对墙为「柜前/柜后安装坐标系」。
        /// </summary>
        internal string RootCoordinateName { get; }

        internal FaceToFaceBaseModelPlan BaseModel { get; }

        internal FaceToFaceBaseModelPlan ElevatedBaseModel { get; }

        internal FaceToFaceBaseInsulatorPlan Insulator { get; }

        internal bool HasElevatedBase => ElevatedBaseModel != null;
    }

    /// <summary>
    /// 编排服务记录的一次装配实例。测试替身可保存普通对象，
    /// SolidWorks 实现则在 NativeComponent 中保存 Component2。
    /// ComponentFullName 为零部件属性全名（含实例号），用于写入「模型对照映射」。
    /// </summary>
    internal sealed class FaceToFaceBaseAssemblyToken
    {
        internal FaceToFaceBaseAssemblyToken(
            string positionName,
            FaceToFaceBaseModelKind kind,
            string modelPath,
            object nativeComponent,
            string componentFullName = null,
            bool isInsulator = false)
        {
            PositionName = positionName ?? throw new ArgumentNullException(nameof(positionName));
            Kind = kind;
            ModelPath = modelPath ?? throw new ArgumentNullException(nameof(modelPath));
            NativeComponent = nativeComponent;
            ComponentFullName = componentFullName;
            IsInsulator = isInsulator;
        }

        internal string PositionName { get; }

        internal FaceToFaceBaseModelKind Kind { get; }

        internal string ModelPath { get; }

        internal object NativeComponent { get; }

        /// <summary>
        /// 总装中该零部件的全名（含实例号），对应 SolidWorks Component2.Name2。
        /// </summary>
        internal string ComponentFullName { get; }

        internal bool IsInsulator { get; }
    }
}
