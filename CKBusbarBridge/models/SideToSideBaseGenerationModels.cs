using System;
using System.Collections.Generic;
using CKBusbarBridge.Drivers;

namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 侧对侧基座模型类型。三个类型分别对应三套固定模板，禁止跨类型复用模型树。
    /// </summary>
    internal enum SideToSideBaseModelKind
    {
        Base1,
        Base2,
        ElevatedBase
    }

    /// <summary>
    /// 单棵基座模型树的全局变量驱动计划。
    /// </summary>
    internal sealed class SideToSideBaseModelPlan
    {
        internal SideToSideBaseModelPlan(
            SideToSideBaseModelKind kind,
            IReadOnlyDictionary<string, double> globalVariables,
            bool isTemplateDefault = false)
        {
            Kind = kind;
            GlobalVariables = globalVariables
                ?? throw new ArgumentNullException(nameof(globalVariables));
            IsTemplateDefault = isTemplateDefault;
        }

        internal SideToSideBaseModelKind Kind { get; }

        internal IReadOnlyDictionary<string, double> GlobalVariables { get; }

        internal bool IsTemplateDefault { get; }
    }

    /// <summary>
    /// 一个绝缘子实例的驱动、定位尺寸和坐标系配合计划。
    /// </summary>
    internal sealed class SideToSideInsulatorPlacementPlan
    {
        internal SideToSideInsulatorPlacementPlan(
            SideToSideBaseModelKind hostModelKind,
            string hostCoordinateName,
            string childCoordinateName,
            string placementDimensionName,
            double placementHeight,
            string busbarSpec,
            double phaseSpacing,
            double insulatorHeight,
            double bracketHostSpan,
            int singlePhaseRootCount,
            UniversalInsulatorLayout layout = null)
        {
            HostModelKind = hostModelKind;
            HostCoordinateName = RequireName(hostCoordinateName, nameof(hostCoordinateName));
            ChildCoordinateName = RequireName(childCoordinateName, nameof(childCoordinateName));
            PlacementDimensionName = RequireName(
                placementDimensionName,
                nameof(placementDimensionName));
            PlacementHeight = placementHeight;
            BusbarSpec = RequireName(busbarSpec, nameof(busbarSpec));
            PhaseSpacing = phaseSpacing;
            InsulatorHeight = insulatorHeight;
            BracketHostSpan = bracketHostSpan;
            SinglePhaseRootCount = singlePhaseRootCount;
            Layout = layout;
        }

        internal SideToSideBaseModelKind HostModelKind { get; }

        internal string HostCoordinateName { get; }

        internal string ChildCoordinateName { get; }

        internal string PlacementDimensionName { get; }

        internal double PlacementHeight { get; }

        internal string BusbarSpec { get; }

        internal double PhaseSpacing { get; }

        internal double InsulatorHeight { get; }

        /// <summary>
        /// 绝缘子支架所在方向的宿主尺寸；公共绝缘子驱动器据此计算“支架长度 = 宿主尺寸 - 20”。
        /// </summary>
        internal double BracketHostSpan { get; }

        internal int SinglePhaseRootCount { get; }

        internal UniversalInsulatorLayout Layout { get; }

        private static string RequireName(string value, string parameterName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("名称不能为空。", parameterName)
                : value;
        }
    }

    /// <summary>
    /// 总装配体中一个柜孔位置的完整基座计划。
    /// </summary>
    internal sealed class SideToSideBasePositionPlan
    {
        internal SideToSideBasePositionPlan(
            string positionName,
            bool isACabinet,
            SideToSideBaseModelPlan baseModel,
            SideToSideBaseModelPlan elevatedBaseModel,
            string baseInstallationCoordinateName,
            IReadOnlyList<SideToSideInsulatorPlacementPlan> insulators)
        {
            PositionName = string.IsNullOrWhiteSpace(positionName)
                ? throw new ArgumentException("基座位置名称不能为空。", nameof(positionName))
                : positionName;
            IsACabinet = isACabinet;
            BaseModel = baseModel ?? throw new ArgumentNullException(nameof(baseModel));
            ElevatedBaseModel = elevatedBaseModel;
            BaseInstallationCoordinateName = string.IsNullOrWhiteSpace(
                baseInstallationCoordinateName)
                ? throw new ArgumentException(
                    "基座安装坐标系名称不能为空。",
                    nameof(baseInstallationCoordinateName))
                : baseInstallationCoordinateName;
            Insulators = insulators ?? throw new ArgumentNullException(nameof(insulators));
        }

        internal string PositionName { get; }

        internal bool IsACabinet { get; }

        internal SideToSideBaseModelPlan BaseModel { get; }

        internal SideToSideBaseModelPlan ElevatedBaseModel { get; }

        internal string BaseInstallationCoordinateName { get; }

        internal IReadOnlyList<SideToSideInsulatorPlacementPlan> Insulators { get; }

        internal bool HasElevatedBase => ElevatedBaseModel != null;
    }

    /// <summary>
    /// 已插入总装配体的侧对侧基座组件记录，用于映射写入和失败回滚。
    /// </summary>
    internal sealed class SideToSideBaseAssemblyToken
    {
        internal SideToSideBaseAssemblyToken(
            string positionName,
            SideToSideBaseModelKind kind,
            string modelPath,
            object nativeComponent,
            string componentFullName)
        {
            PositionName = positionName
                ?? throw new ArgumentNullException(nameof(positionName));
            Kind = kind;
            ModelPath = modelPath ?? throw new ArgumentNullException(nameof(modelPath));
            NativeComponent = nativeComponent;
            ComponentFullName = componentFullName;
        }

        internal string PositionName { get; }

        internal SideToSideBaseModelKind Kind { get; }

        internal string ModelPath { get; }

        internal object NativeComponent { get; }

        internal string ComponentFullName { get; }
    }
}
