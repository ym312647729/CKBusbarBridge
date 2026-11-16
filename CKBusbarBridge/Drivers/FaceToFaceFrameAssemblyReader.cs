using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using CKBusbarBridgePreview;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    internal sealed class FaceToFaceFrameNativeComponent
    {
        internal FaceToFaceFrameNativeComponent(
            string instanceId,
            string componentName,
            string modelPath,
            bool isSuppressed,
            bool isLightweight = false,
            double? heightAxisCoordinate = null,
            SystemPartType_e configuredRdSystemPartType = SystemPartType_e.Unknow,
            IEnumerable<double> boxCenter = null)
        {
            InstanceId = string.IsNullOrWhiteSpace(instanceId)
                ? throw new ArgumentException("组件实例标识不能为空。", nameof(instanceId))
                : instanceId.Trim();
            ComponentName = string.IsNullOrWhiteSpace(componentName)
                ? throw new ArgumentException("组件名称不能为空。", nameof(componentName))
                : componentName.Trim();
            ModelPath = modelPath?.Trim() ?? string.Empty;
            IsSuppressed = isSuppressed;
            IsLightweight = isLightweight;
            HeightAxisCoordinate = heightAxisCoordinate;
            ConfiguredRdSystemPartType = configuredRdSystemPartType;
            BoxCenter = (boxCenter ?? Array.Empty<double>()).ToArray();
        }

        internal string InstanceId { get; }

        internal string ComponentName { get; }

        internal string ModelPath { get; }

        internal bool IsSuppressed { get; }

        internal bool IsLightweight { get; }

        internal double? HeightAxisCoordinate { get; }

        internal SystemPartType_e ConfiguredRdSystemPartType { get; }

        internal IReadOnlyList<double> BoxCenter { get; }
    }

    internal static class FaceToFaceTopLevelBaseOwnershipResolver
    {
        private const string ACabinetComponentName = "A柜-子装配体";
        private const string BCabinetComponentName = "B柜-子装配体";

        internal static FaceToFaceFrameNativeComponent ResolveBCabinetBase(
            IReadOnlyList<FaceToFaceFrameNativeComponent> topLevelComponents,
            IReadOnlyList<FaceToFaceFrameNativeComponent> ordinaryBases)
        {
            if (topLevelComponents == null)
            {
                throw new ArgumentNullException(nameof(topLevelComponents));
            }

            if (ordinaryBases == null)
            {
                throw new ArgumentNullException(nameof(ordinaryBases));
            }

            var aCabinet = ResolveUniqueCabinet(topLevelComponents, ACabinetComponentName);
            var bCabinet = ResolveUniqueCabinet(topLevelComponents, BCabinetComponentName);
            if (ordinaryBases.Count < 2 || ordinaryBases.Count > 4)
            {
                throw new InvalidOperationException(
                    "面对面顶层结构无效：总装配体顶层必须存在 2 至 4 个基座，"
                    + $"实际={ordinaryBases.Count}；候选="
                    + DescribeComponents(ordinaryBases) + "。");
            }

            EnsureValidBoxCenter(aCabinet, "A柜");
            EnsureValidBoxCenter(bCabinet, "B柜");
            foreach (var ordinaryBase in ordinaryBases)
            {
                EnsureValidBoxCenter(ordinaryBase, "普通基座");
            }

            var basesOwnedByA = new List<Tuple<FaceToFaceFrameNativeComponent, double>>();
            var basesOwnedByB = new List<Tuple<FaceToFaceFrameNativeComponent, double>>();
            foreach (var ordinaryBase in ordinaryBases)
            {
                var distanceToA = SquaredDistance(ordinaryBase.BoxCenter, aCabinet.BoxCenter);
                var distanceToB = SquaredDistance(ordinaryBase.BoxCenter, bCabinet.BoxCenter);
                var tieTolerance = Math.Max(1d, Math.Max(distanceToA, distanceToB)) * 1e-12;
                if (Math.Abs(distanceToA - distanceToB) <= tieTolerance)
                {
                    throw new InvalidOperationException(
                        "面对面基座无法按 Box 中心唯一归属："
                        + $"基座={ordinaryBase.InstanceId}；"
                        + $"距A柜平方距离={distanceToA:R}；"
                        + $"距B柜平方距离={distanceToB:R}。");
                }

                (distanceToA < distanceToB ? basesOwnedByA : basesOwnedByB)
                    .Add(distanceToA < distanceToB
                        ? Tuple.Create(ordinaryBase, distanceToA)
                        : Tuple.Create(ordinaryBase, distanceToB));
            }

            if (basesOwnedByA.Count < 1 || basesOwnedByA.Count > 2 ||
                basesOwnedByB.Count < 1 || basesOwnedByB.Count > 2)
            {
                throw new InvalidOperationException(
                    "面对面基座按 Box 中心归属后，A柜/B柜必须各有 1 至 2 个："
                    + $"A柜归属={DescribeComponents(basesOwnedByA.Select(item => item.Item1))}；"
                    + $"B柜归属={DescribeComponents(basesOwnedByB.Select(item => item.Item1))}。");
            }

            // 同一柜体下离柜体更远的是主体基座，离柜体更近的是增高基座。
            return basesOwnedByB
                .OrderByDescending(item => item.Item2)
                .First()
                .Item1;
        }

        private static FaceToFaceFrameNativeComponent ResolveUniqueCabinet(
            IEnumerable<FaceToFaceFrameNativeComponent> components,
            string cabinetComponentName)
        {
            var matches = components
                .Where(component => IsNamedComponent(component, cabinetComponentName))
                .ToList();
            if (matches.Count != 1)
            {
                throw new InvalidOperationException(
                    "面对面顶层结构无效：总装配体顶层必须唯一存在"
                    + cabinetComponentName
                    + $"，实际={matches.Count}；候选={DescribeComponents(matches)}。");
            }

            return matches[0];
        }

        private static bool IsNamedComponent(
            FaceToFaceFrameNativeComponent component,
            string expectedName)
        {
            return HasComponentInstanceName(component.InstanceId, expectedName)
                || HasComponentInstanceName(component.ComponentName, expectedName);
        }

        private static bool HasComponentInstanceName(string actualName, string expectedName)
        {
            return string.Equals(actualName, expectedName, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(actualName)
                    && actualName.StartsWith(
                        expectedName + "-",
                        StringComparison.OrdinalIgnoreCase));
        }

        private static void EnsureValidBoxCenter(
            FaceToFaceFrameNativeComponent component,
            string displayName)
        {
            if (component.BoxCenter == null
                || component.BoxCenter.Count != 3
                || component.BoxCenter.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
            {
                throw new InvalidOperationException(
                    $"面对面{displayName}缺少有效的顶层 Box 中心："
                    + $"组件={component.InstanceId}；路径={component.ModelPath}。");
            }
        }

        private static double SquaredDistance(
            IReadOnlyList<double> left,
            IReadOnlyList<double> right)
        {
            var x = left[0] - right[0];
            var y = left[1] - right[1];
            var z = left[2] - right[2];
            return (x * x) + (y * y) + (z * z);
        }

        private static string DescribeComponents(
            IEnumerable<FaceToFaceFrameNativeComponent> components)
        {
            var names = (components ?? Array.Empty<FaceToFaceFrameNativeComponent>())
                .Select(component => component?.InstanceId)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
            return names.Count == 0 ? "无" : string.Join("、", names);
        }
    }

    internal sealed class FaceToFaceFrameNativeMateEndpoint
    {
        internal FaceToFaceFrameNativeMateEndpoint(
            string componentInstanceId,
            string entityName)
        {
            ComponentInstanceId = componentInstanceId?.Trim();
            EntityName = entityName?.Trim();
        }

        internal string ComponentInstanceId { get; }

        internal string EntityName { get; }
    }

    internal sealed class FaceToFaceFrameNativeMate
    {
        internal FaceToFaceFrameNativeMate(
            FaceToFaceFrameNativeMateEndpoint first,
            FaceToFaceFrameNativeMateEndpoint second)
        {
            First = first ?? throw new ArgumentNullException(nameof(first));
            Second = second ?? throw new ArgumentNullException(nameof(second));
        }

        internal FaceToFaceFrameNativeMateEndpoint First { get; }

        internal FaceToFaceFrameNativeMateEndpoint Second { get; }
    }

    /// <summary>
    /// SolidWorks 外部依赖的最小只读边界。接口故意不暴露保存、重建、打开或关闭文档操作。
    /// </summary>
    internal interface IFaceToFaceFrameSolidWorksReadOperations
    {
        int ResolveLightweightComponents();

        IReadOnlyList<FaceToFaceFrameNativeComponent> GetTopLevelComponents();

        IReadOnlyList<FaceToFaceFrameNativeComponent> GetAllComponents();

        IReadOnlyList<FaceToFaceFrameNativeMate> GetMates(string componentInstanceId);

        FaceToFaceFramePhysicalSnapshot ReadPhysicalModel(
            string componentInstanceId,
            FaceToWallFrameTemplateKind frameTemplateKind);
    }

    internal enum FaceToFaceFrameBaseAnchorRouteMode
    {
        Either = 0,
        DirectOnly = 1,
        StackedOnly = 2
    }

    /// <summary>
    /// 将 SolidWorks 只读操作转换成不含 COM 对象的基架拓扑与物理快照。
    /// </summary>
    internal sealed class SolidWorksFaceToFaceFrameModelReadSource
        : IFaceToFaceFrameModelReadSource
    {
        private const string BaseRootInstallationCoordinateName = "安装坐标系";
        private const string DefaultOrdinaryBaseDisplayName = "B柜普通基座";
        private const double BaseHeightAxisSeparationTolerance = 1e-9;
        private readonly IFaceToFaceFrameSolidWorksReadOperations _operations;
        private readonly DesignScenarioId _scenarioId;
        private readonly SideToSideFrameBaseKind? _sideToSideBaseKind;
        private readonly string _ordinaryBaseDisplayName;
        private readonly string _expectedBaseComponentFullName;
        private readonly FaceToFaceFrameBaseAnchorRouteMode _baseAnchorRouteMode;
        private readonly bool _requireTopLevelSpatialBaseOwnership;
        // 新设计入口已用模型映射和安装配合校验归属，不再进入旧空间分类。
        internal IReadOnlyCollection<string> MappedOrdinaryBaseNames { get; set; }
        internal IReadOnlyCollection<string> MappedElevatedBaseNames { get; set; }
        internal IReadOnlyDictionary<string, FrameTemplateIdentity> ExistingFrameTemplateIdentities { get; set; }
        private readonly Dictionary<string, FaceToFaceFrameNativeComponent> _capturedComponents
            = new Dictionary<string, FaceToFaceFrameNativeComponent>(StringComparer.OrdinalIgnoreCase);

        internal SolidWorksFaceToFaceFrameModelReadSource(IModelDoc2 totalAssemblyModel)
            : this(totalAssemblyModel, false, DefaultOrdinaryBaseDisplayName)
        {
        }

        internal SolidWorksFaceToFaceFrameModelReadSource(
            IModelDoc2 totalAssemblyModel,
            bool requireTopLevelSpatialBaseOwnership)
            : this(
                totalAssemblyModel,
                false,
                DefaultOrdinaryBaseDisplayName,
                null,
                BaseRootInstallationCoordinateName,
                FaceToFaceFrameBaseAnchorRouteMode.Either,
                requireTopLevelSpatialBaseOwnership)
        {
        }

        internal SolidWorksFaceToFaceFrameModelReadSource(
            IModelDoc2 totalAssemblyModel,
            bool allowLargeFrame,
            string ordinaryBaseDisplayName)
            : this(
                totalAssemblyModel,
                allowLargeFrame,
                ordinaryBaseDisplayName,
                null)
        {
        }

        internal SolidWorksFaceToFaceFrameModelReadSource(
            IModelDoc2 totalAssemblyModel,
            bool allowLargeFrame,
            string ordinaryBaseDisplayName,
            string expectedBaseComponentFullName)
            : this(
                totalAssemblyModel,
                allowLargeFrame,
                ordinaryBaseDisplayName,
                expectedBaseComponentFullName,
                BaseRootInstallationCoordinateName)
        {
        }

        internal SolidWorksFaceToFaceFrameModelReadSource(
            IModelDoc2 totalAssemblyModel,
            bool allowLargeFrame,
            string ordinaryBaseDisplayName,
            string expectedBaseComponentFullName,
            string ordinaryBaseAnchorCoordinateName)
            : this(
                totalAssemblyModel,
                allowLargeFrame,
                ordinaryBaseDisplayName,
                expectedBaseComponentFullName,
                ordinaryBaseAnchorCoordinateName,
                FaceToFaceFrameBaseAnchorRouteMode.Either)
        {
        }

        internal SolidWorksFaceToFaceFrameModelReadSource(
            IModelDoc2 totalAssemblyModel,
            bool allowLargeFrame,
            string ordinaryBaseDisplayName,
            string expectedBaseComponentFullName,
            string ordinaryBaseAnchorCoordinateName,
            FaceToFaceFrameBaseAnchorRouteMode baseAnchorRouteMode,
            bool requireTopLevelSpatialBaseOwnership = false)
            : this(
                new SolidWorksFaceToFaceFrameReadOperations(totalAssemblyModel),
                allowLargeFrame,
                ordinaryBaseDisplayName,
                expectedBaseComponentFullName,
                ordinaryBaseAnchorCoordinateName,
                baseAnchorRouteMode,
                requireTopLevelSpatialBaseOwnership)
        {
        }

        internal SolidWorksFaceToFaceFrameModelReadSource(
            IFaceToFaceFrameSolidWorksReadOperations operations)
            : this(operations, false, DefaultOrdinaryBaseDisplayName)
        {
        }

        internal SolidWorksFaceToFaceFrameModelReadSource(
            IFaceToFaceFrameSolidWorksReadOperations operations,
            bool allowLargeFrame,
            string ordinaryBaseDisplayName)
            : this(operations, allowLargeFrame, ordinaryBaseDisplayName, null)
        {
        }

        internal SolidWorksFaceToFaceFrameModelReadSource(
            IFaceToFaceFrameSolidWorksReadOperations operations,
            bool allowLargeFrame,
            string ordinaryBaseDisplayName,
            string expectedBaseComponentFullName)
            : this(
                operations,
                allowLargeFrame,
                ordinaryBaseDisplayName,
                expectedBaseComponentFullName,
                BaseRootInstallationCoordinateName)
        {
        }

        internal SolidWorksFaceToFaceFrameModelReadSource(
            IFaceToFaceFrameSolidWorksReadOperations operations,
            bool allowLargeFrame,
            string ordinaryBaseDisplayName,
            string expectedBaseComponentFullName,
            string ordinaryBaseAnchorCoordinateName)
            : this(
                operations,
                allowLargeFrame,
                ordinaryBaseDisplayName,
                expectedBaseComponentFullName,
                ordinaryBaseAnchorCoordinateName,
                FaceToFaceFrameBaseAnchorRouteMode.Either)
        {
        }

        internal SolidWorksFaceToFaceFrameModelReadSource(
            IFaceToFaceFrameSolidWorksReadOperations operations,
            bool allowLargeFrame,
            string ordinaryBaseDisplayName,
            string expectedBaseComponentFullName,
            string ordinaryBaseAnchorCoordinateName,
            FaceToFaceFrameBaseAnchorRouteMode baseAnchorRouteMode,
            bool requireTopLevelSpatialBaseOwnership = false)
            : this(
                operations,
                allowLargeFrame
                    ? DesignScenarioId.FaceToWall
                    : DesignScenarioId.FaceToFace,
                null,
                ordinaryBaseDisplayName,
                expectedBaseComponentFullName,
                ordinaryBaseAnchorCoordinateName,
                baseAnchorRouteMode,
                requireTopLevelSpatialBaseOwnership)
        {
        }

        internal SolidWorksFaceToFaceFrameModelReadSource(
            IModelDoc2 totalAssemblyModel,
            SideToSideFrameBaseKind sideToSideBaseKind,
            string ordinaryBaseDisplayName,
            string expectedBaseComponentFullName,
            string ordinaryBaseAnchorCoordinateName,
            FaceToFaceFrameBaseAnchorRouteMode baseAnchorRouteMode)
            : this(
                new SolidWorksFaceToFaceFrameReadOperations(totalAssemblyModel),
                DesignScenarioId.SideToSide,
                sideToSideBaseKind,
                ordinaryBaseDisplayName,
                expectedBaseComponentFullName,
                ordinaryBaseAnchorCoordinateName,
                baseAnchorRouteMode,
                false)
        {
        }

        internal SolidWorksFaceToFaceFrameModelReadSource(
            IModelDoc2 totalAssemblyModel,
            SideToSideFrameBaseKind sideToSideBaseKind,
            string ordinaryBaseDisplayName,
            string expectedBaseComponentFullName,
            string ordinaryBaseAnchorCoordinateName,
            FaceToFaceFrameBaseAnchorRouteMode baseAnchorRouteMode,
            bool requireTopLevelSpatialBaseOwnership)
            : this(
                new SolidWorksFaceToFaceFrameReadOperations(totalAssemblyModel),
                DesignScenarioId.SideToSide,
                sideToSideBaseKind,
                ordinaryBaseDisplayName,
                expectedBaseComponentFullName,
                ordinaryBaseAnchorCoordinateName,
                baseAnchorRouteMode,
                requireTopLevelSpatialBaseOwnership)
        {
        }

        internal SolidWorksFaceToFaceFrameModelReadSource(
            IFaceToFaceFrameSolidWorksReadOperations operations,
            SideToSideFrameBaseKind sideToSideBaseKind,
            string ordinaryBaseDisplayName,
            string expectedBaseComponentFullName,
            string ordinaryBaseAnchorCoordinateName,
            FaceToFaceFrameBaseAnchorRouteMode baseAnchorRouteMode)
            : this(
                operations,
                DesignScenarioId.SideToSide,
                sideToSideBaseKind,
                ordinaryBaseDisplayName,
                expectedBaseComponentFullName,
                ordinaryBaseAnchorCoordinateName,
                baseAnchorRouteMode,
                false)
        {
        }

        internal SolidWorksFaceToFaceFrameModelReadSource(
            IFaceToFaceFrameSolidWorksReadOperations operations,
            SideToSideFrameBaseKind sideToSideBaseKind,
            string ordinaryBaseDisplayName,
            string expectedBaseComponentFullName,
            string ordinaryBaseAnchorCoordinateName,
            FaceToFaceFrameBaseAnchorRouteMode baseAnchorRouteMode,
            bool requireTopLevelSpatialBaseOwnership)
            : this(
                operations,
                DesignScenarioId.SideToSide,
                sideToSideBaseKind,
                ordinaryBaseDisplayName,
                expectedBaseComponentFullName,
                ordinaryBaseAnchorCoordinateName,
                baseAnchorRouteMode,
                requireTopLevelSpatialBaseOwnership)
        {
        }

        private SolidWorksFaceToFaceFrameModelReadSource(
            IFaceToFaceFrameSolidWorksReadOperations operations,
            DesignScenarioId scenarioId,
            SideToSideFrameBaseKind? sideToSideBaseKind,
            string ordinaryBaseDisplayName,
            string expectedBaseComponentFullName,
            string ordinaryBaseAnchorCoordinateName,
            FaceToFaceFrameBaseAnchorRouteMode baseAnchorRouteMode,
            bool requireTopLevelSpatialBaseOwnership)
        {
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
            if (!Enum.IsDefined(typeof(FaceToFaceFrameBaseAnchorRouteMode), baseAnchorRouteMode))
            {
                throw new ArgumentOutOfRangeException(nameof(baseAnchorRouteMode));
            }
            _scenarioId = scenarioId;
            _sideToSideBaseKind = sideToSideBaseKind;
            _ordinaryBaseDisplayName = string.IsNullOrWhiteSpace(ordinaryBaseDisplayName)
                ? "普通基座"
                : ordinaryBaseDisplayName.Trim();
            _expectedBaseComponentFullName = string.IsNullOrWhiteSpace(
                expectedBaseComponentFullName)
                    ? null
                    : expectedBaseComponentFullName.Trim();
            // ordinaryBaseAnchorCoordinateName 保留在构造函数签名中以兼容旧调用，
            // 但基座候选与定位不再读取基座内部坐标系名称。
            _baseAnchorRouteMode = baseAnchorRouteMode;
            _requireTopLevelSpatialBaseOwnership = requireTopLevelSpatialBaseOwnership;
        }

        public FaceToFaceFrameTopologySnapshot CaptureTopology(
            string projectPath,
            string bCabinetBasePositionName)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                throw new ArgumentException("项目目录不能为空。", nameof(projectPath));
            }

            if (string.IsNullOrWhiteSpace(bCabinetBasePositionName))
            {
                throw new ArgumentException("普通基座定位坐标系不能为空。", nameof(bCabinetBasePositionName));
            }

            var resolveStatus = _operations.ResolveLightweightComponents();
            if (resolveStatus != (int)swComponentResolveStatus_e.swResolveOk)
            {
                throw new InvalidOperationException(
                    $"面对面基架读取前解析轻量化组件失败：状态码={resolveStatus}。" );
            }

            var allNativeComponents = ((_operations.GetTopLevelComponents())
                ?? Array.Empty<FaceToFaceFrameNativeComponent>())
                .Where(component => component != null)
                .ToList();
            var unresolvedComponent = allNativeComponents.FirstOrDefault(component => component.IsLightweight);
            if (unresolvedComponent != null)
            {
                throw new InvalidOperationException(
                    $"面对面基架读取发现仍处于轻量化状态的顶层组件："
                    + $"组件={unresolvedComponent.InstanceId}；路径={unresolvedComponent.ModelPath}。" );
            }

            var nativeComponents = allNativeComponents
                .Where(component => !component.IsSuppressed)
                .ToList();
            EnsureUniqueNativeComponentIds(nativeComponents);

            _capturedComponents.Clear();
            foreach (var component in nativeComponents)
            {
                _capturedComponents.Add(component.InstanceId, component);
            }

            var frameSnapshots = new List<FaceToFaceFrameComponentSnapshot>();
            var baseCandidates = new List<FaceToFaceFrameNativeComponent>();
            var elevatedBaseCandidates = new List<FaceToFaceFrameNativeComponent>();
            var unclassifiedBaseCandidates = new List<FaceToFaceFrameNativeComponent>();
            foreach (var component in nativeComponents)
            {
                if (component.ConfiguredRdSystemPartType == SystemPartType_e.Frame)
                {
                    frameSnapshots.Add(CreateFrameSnapshot(projectPath, component));
                    continue;
                }

                if (component.ConfiguredRdSystemPartType == SystemPartType_e.Base)
                {
                    unclassifiedBaseCandidates.Add(component);
                    continue;
                }
            }

            if (ExistingFrameTemplateIdentities != null && ExistingFrameTemplateIdentities.Count != frameSnapshots.Count)
                throw new InvalidOperationException("侧对侧转换旧基架清单与实际顶层组件不一致。");
            var spatiallyOwnedBBase = _requireTopLevelSpatialBaseOwnership
                ? FaceToFaceTopLevelBaseOwnershipResolver.ResolveBCabinetBase(
                    nativeComponents,
                    unclassifiedBaseCandidates)
                : null;
            if (MappedOrdinaryBaseNames != null)
            {
                if (_expectedBaseComponentFullName == null || !MappedOrdinaryBaseNames.Any(n =>
                    FaceToFaceBaseModelMappingHelper.ComponentFullNameEquals(n, _expectedBaseComponentFullName)))
                    throw new InvalidOperationException("新设计基架反读缺少已核验的普通基座映射。");
                baseCandidates.AddRange(unclassifiedBaseCandidates.Where(c => MappedOrdinaryBaseNames.Any(n =>
                    FaceToFaceBaseModelMappingHelper.ComponentFullNameEquals(n, c.InstanceId))));
                elevatedBaseCandidates.AddRange(unclassifiedBaseCandidates.Where(c =>
                    MappedElevatedBaseNames != null && MappedElevatedBaseNames.Any(n =>
                        FaceToFaceBaseModelMappingHelper.ComponentFullNameEquals(n, c.InstanceId))));
                if (baseCandidates.Count != MappedOrdinaryBaseNames.Count ||
                    elevatedBaseCandidates.Count != (MappedElevatedBaseNames?.Count ?? 0) ||
                    baseCandidates.Count + elevatedBaseCandidates.Count != unclassifiedBaseCandidates.Count)
                    throw new InvalidOperationException("普通/增高基座组件与已核验映射不一致。");
            }
            else if (_requireTopLevelSpatialBaseOwnership)
            {
                baseCandidates.Add(spatiallyOwnedBBase);
            }
            else
            {
                ClassifyUnclassifiedBaseCandidates(
                    _scenarioId,
                    _sideToSideBaseKind,
                    baseCandidates,
                    elevatedBaseCandidates,
                    unclassifiedBaseCandidates);
            }

            if (frameSnapshots.Count == 0)
            {
                return new FaceToFaceFrameTopologySnapshot(
                    Array.Empty<FaceToFaceFrameComponentSnapshot>(),
                    Array.Empty<FaceToFaceFrameMateSnapshot>());
            }

            var targetBase = FindTargetOrdinaryBase(
                baseCandidates,
                elevatedBaseCandidates,
                bCabinetBasePositionName,
                spatiallyOwnedBBase?.InstanceId);
            var baseSnapshot = new FaceToFaceFrameComponentSnapshot(
                targetBase.InstanceId,
                targetBase.ComponentName,
                targetBase.ModelPath,
                null,
                false,
                true,
                FaceToWallFrameTemplateKind.Regular,
                targetBase.BoxCenter);

            var snapshots = new List<FaceToFaceFrameComponentSnapshot> { baseSnapshot };
            snapshots.AddRange(frameSnapshots);
            var chainMates = CaptureChainMates(snapshots);
            return new FaceToFaceFrameTopologySnapshot(snapshots, chainMates);
        }

        public FaceToFaceFramePhysicalSnapshot ReadPhysicalModel(
            FaceToFaceFrameComponentSnapshot component)
        {
            if (component == null)
            {
                throw new ArgumentNullException(nameof(component));
            }

            if (!_capturedComponents.ContainsKey(component.InstanceId))
            {
                throw new InvalidOperationException(
                    $"基架物理参数读取失败，组件不在当前总装快照中：{component.InstanceId}。" );
            }

            var physical = _operations.ReadPhysicalModel(
                component.InstanceId,
                component.FrameTemplateKind);
            if (ExistingFrameTemplateIdentities != null &&
                (physical == null || !ExistingFrameTemplateIdentities.TryGetValue(component.InstanceId, out var expected) || physical.TemplateIdentity != expected))
                throw new InvalidOperationException("侧对侧转换旧基架的原驱动模板身份不符：" + component.InstanceId);
            return physical;
        }

        private FaceToFaceFrameNativeComponent FindTargetOrdinaryBase(
            IReadOnlyList<FaceToFaceFrameNativeComponent> ordinaryBaseCandidates,
            IReadOnlyList<FaceToFaceFrameNativeComponent> elevatedBaseCandidates,
            string bCabinetBasePositionName,
            string spatialExpectedBaseFullName)
        {
            if (!string.IsNullOrWhiteSpace(spatialExpectedBaseFullName))
            {
                var spatialBase = ordinaryBaseCandidates
                    .SingleOrDefault(candidate =>
                        FaceToFaceBaseModelMappingHelper.ComponentFullNameEquals(
                            candidate.InstanceId,
                            spatialExpectedBaseFullName) ||
                        FaceToFaceBaseModelMappingHelper.ComponentFullNameEquals(
                            candidate.ComponentName,
                            spatialExpectedBaseFullName));
                if (spatialBase == null)
                {
                    throw new InvalidOperationException(
                        "按 A/B 柜 Box 中心归属得到的 B柜普通基座不在普通基座候选中：" +
                        spatialExpectedBaseFullName + "。 ");
                }

                // 侧对侧和面对墙的首基架定位以总装空间位置为准，
                // 不再因为基座与首基架没有配合边而阻断界面初始化。
                return spatialBase;
            }

            var allBaseCandidates = ordinaryBaseCandidates
                .Concat(elevatedBaseCandidates)
                .ToList();
            var matesByComponent = allBaseCandidates.ToDictionary(
                candidate => candidate.InstanceId,
                candidate => _operations.GetMates(candidate.InstanceId)
                    ?? Array.Empty<FaceToFaceFrameNativeMate>(),
                StringComparer.OrdinalIgnoreCase);
            var directRoutes = new List<OrdinaryBaseAnchorRoute>();
            var stackedRoutes = new List<OrdinaryBaseAnchorRoute>();

            foreach (var ordinaryBase in ordinaryBaseCandidates)
            {
                if (matesByComponent[ordinaryBase.InstanceId].Any(mate => IsRootPositionMate(
                        mate,
                        ordinaryBase.InstanceId,
                        bCabinetBasePositionName)))
                {
                    directRoutes.Add(OrdinaryBaseAnchorRoute.Direct(
                        ordinaryBase,
                        bCabinetBasePositionName));
                }
            }

            foreach (var elevatedBase in elevatedBaseCandidates)
            {
                if (!matesByComponent[elevatedBase.InstanceId].Any(mate => IsRootPositionMate(
                        mate,
                        elevatedBase.InstanceId,
                        bCabinetBasePositionName)))
                {
                    continue;
                }

                foreach (var ordinaryBase in ordinaryBaseCandidates)
                {
                    // 叠装时普通基座不直接连接总装根，而是由同位置增高基座的组件关系承接。
                    var hasStackedMate = matesByComponent[elevatedBase.InstanceId]
                        .Concat(matesByComponent[ordinaryBase.InstanceId])
                        .Any(mate => IsComponentInstanceMate(
                            mate,
                            elevatedBase.InstanceId,
                            ordinaryBase.InstanceId));
                    if (hasStackedMate)
                    {
                        stackedRoutes.Add(OrdinaryBaseAnchorRoute.Stacked(
                            ordinaryBase,
                            elevatedBase,
                            bCabinetBasePositionName));
                    }
                }
            }

            var effectiveExpectedBaseFullName = ResolveExpectedBaseFullName(
                spatialExpectedBaseFullName);
            var matchingDirectRoutes = FilterExpectedBaseRoutes(
                directRoutes,
                effectiveExpectedBaseFullName);
            var matchingStackedRoutes = FilterExpectedBaseRoutes(
                stackedRoutes,
                effectiveExpectedBaseFullName);
            List<OrdinaryBaseAnchorRoute> matchingRoutes;
            List<OrdinaryBaseAnchorRoute> forbiddenRoutes;
            switch (_baseAnchorRouteMode)
            {
                case FaceToFaceFrameBaseAnchorRouteMode.Either:
                    matchingRoutes = matchingDirectRoutes
                        .Concat(matchingStackedRoutes)
                        .ToList();
                    forbiddenRoutes = new List<OrdinaryBaseAnchorRoute>();
                    break;
                case FaceToFaceFrameBaseAnchorRouteMode.DirectOnly:
                    matchingRoutes = matchingDirectRoutes;
                    forbiddenRoutes = matchingStackedRoutes;
                    break;
                case FaceToFaceFrameBaseAnchorRouteMode.StackedOnly:
                    matchingRoutes = matchingStackedRoutes;
                    forbiddenRoutes = matchingDirectRoutes;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"不支持的基座定位路径模式：{_baseAnchorRouteMode}。");
            }

            var routes = directRoutes
                .Concat(stackedRoutes)
                .ToList();
            if (matchingRoutes.Count != 1 || forbiddenRoutes.Count != 0)
            {
                throw new InvalidOperationException(
                    $"无法通过总装根坐标系“{bCabinetBasePositionName}”唯一确认 {_ordinaryBaseDisplayName}；"
                    + $"有效定位路径={matchingRoutes.Count}；"
                    + $"禁止定位路径={forbiddenRoutes.Count}；"
                    + (effectiveExpectedBaseFullName == null
                        ? string.Empty
                        : "指定组件=" + effectiveExpectedBaseFullName + "；")
                    + "普通基座候选="
                    + (ordinaryBaseCandidates.Count == 0
                        ? "无"
                        : string.Join("、", ordinaryBaseCandidates.Select(item => item.InstanceId)))
                    + "；增高基座候选="
                    + (elevatedBaseCandidates.Count == 0
                        ? "无"
                        : string.Join("、", elevatedBaseCandidates.Select(item => item.InstanceId)))
                    + "；已识别路径="
                    + (routes.Count == 0
                        ? "无"
                        : string.Join("、", routes.Select(route => route.Description)))
                    + "。" );
            }

            return matchingRoutes[0].OrdinaryBase;
        }

        private FaceToFaceFrameComponentSnapshot CreateFrameSnapshot(
            string projectPath,
            FaceToFaceFrameNativeComponent component)
        {
            // 从模型文件路径中提取 SourceName（直属目录名），使同源基架反读后名称一致。
            // 不再使用 InstanceId（每个实例唯一），避免同一物理模型多次装配时名称不一致。
            // 不允许在路径解析失败时静默退回到组件名称：那样会再次让同源位置得到不同 SourceName。
            var physicalBaseKind = _sideToSideBaseKind;
            if (ExistingFrameTemplateIdentities != null)
            {
                if (_scenarioId != DesignScenarioId.SideToSide ||
                    !ExistingFrameTemplateIdentities.TryGetValue(component.InstanceId, out var expected) ||
                    !FaceToFaceFramePathClassifier.TryGetStoredTemplateIdentity(component.ModelPath, out var identity) || identity != expected)
                    throw new InvalidOperationException("侧对侧转换旧基架不在已核验读取契约中：" + component.InstanceId);
                physicalBaseKind = expected == FrameTemplateIdentity.SideToSidePhaseChange029
                    ? SideToSideFrameBaseKind.Base1 : SideToSideFrameBaseKind.Base2;
            }
            if (!FaceToFaceFramePathClassifier.TryGetFrameSourceName(
                    projectPath,
                    component.ModelPath,
                    _scenarioId,
                    physicalBaseKind,
                    out var sourceName,
                    out var frameTemplateKind,
                    out var extractionError))
            {
                // 路径解析失败说明基架装配体未按 SourceName 目录规范存放，必须立即以
                // 疑似基架不合规错误抛出，暴露路径/模板身份问题。
                throw new InvalidOperationException(
                    "总装配体顶层发现疑似基架不合规组件：组件="
                    + component.InstanceId
                    + "；路径=" + component.ModelPath
                    + "；" + extractionError);
            }

            return new FaceToFaceFrameComponentSnapshot(
                component.InstanceId,
                component.ComponentName,
                component.ModelPath,
                sourceName,
                true,
                false,
                frameTemplateKind,
                component.BoxCenter);
        }

        private static void ClassifyUnclassifiedBaseCandidates(
            DesignScenarioId scenarioId,
            SideToSideFrameBaseKind? sideToSideBaseKind,
            List<FaceToFaceFrameNativeComponent> ordinaryBaseCandidates,
            List<FaceToFaceFrameNativeComponent> elevatedBaseCandidates,
            List<FaceToFaceFrameNativeComponent> unclassifiedBaseCandidates)
        {
            if (unclassifiedBaseCandidates == null || unclassifiedBaseCandidates.Count == 0)
            {
                return;
            }

            var allBaseCandidates = ordinaryBaseCandidates
                .Concat(elevatedBaseCandidates)
                .Concat(unclassifiedBaseCandidates)
                .ToList();

            if (scenarioId == DesignScenarioId.FaceToFace)
            {
                ordinaryBaseCandidates.Clear();
                ordinaryBaseCandidates.AddRange(allBaseCandidates);
                elevatedBaseCandidates.Clear();
                return;
            }

            if (scenarioId == DesignScenarioId.SideToSide &&
                sideToSideBaseKind == SideToSideFrameBaseKind.Base1)
            {
                if (allBaseCandidates.Count != 2)
                {
                    throw new InvalidOperationException(
                        "侧对侧基座1必须通过总装顶层 RD类型=基座 唯一识别两个普通基座；" +
                        "当前数量=" + allBaseCandidates.Count +
                        "；候选=" +
                        FormatBaseCandidateDiagnostics(allBaseCandidates));
                }

                ordinaryBaseCandidates.Clear();
                ordinaryBaseCandidates.AddRange(allBaseCandidates);
                elevatedBaseCandidates.Clear();
                return;
            }

            // 面对墙总装可能只包含一个启用位置的基座，也可能同时包含柜前/柜后两个基座。
            // 两种情况下都只把 RD类型=基座的顶层组件作为候选；普通/增高身份不再由
            // 文件名、实例名或目录名推断，具体位置由总装配体根配合的组件关系确定。
            if (scenarioId == DesignScenarioId.FaceToWall &&
                (allBaseCandidates.Count == 1 || allBaseCandidates.Count == 2))
            {
                ordinaryBaseCandidates.Clear();
                ordinaryBaseCandidates.AddRange(allBaseCandidates);
                elevatedBaseCandidates.Clear();
                return;
            }

            if (allBaseCandidates.Count >= 2 && allBaseCandidates.Count <= 4)
            {
                // 普通/增高身份不能再按高度轴猜测；生产入口由总装配体
                // A/B 柜 Box 中心距离统一归属，兼容路径才保留全部基座候选。
                ordinaryBaseCandidates.Clear();
                ordinaryBaseCandidates.AddRange(allBaseCandidates);
                elevatedBaseCandidates.Clear();
                return;
            }

            if (allBaseCandidates.Count != 4 ||
                allBaseCandidates.Any(component =>
                    !component.HeightAxisCoordinate.HasValue))
            {
                throw new InvalidOperationException(
                    "无法仅通过总装顶层 RD类型=基座 和高度轴位置确定普通/增高基座：" +
                    "必须识别四个基座且每个基座都有有效高度轴位置；" +
                    "当前数量=" + allBaseCandidates.Count +
                    "；候选=" +
                    FormatBaseCandidateDiagnostics(allBaseCandidates));
            }

            var orderedByHeight = allBaseCandidates
                .OrderBy(component => component.HeightAxisCoordinate.Value)
                .ToList();
            if (orderedByHeight[2].HeightAxisCoordinate.Value -
                orderedByHeight[1].HeightAxisCoordinate.Value <=
                BaseHeightAxisSeparationTolerance)
            {
                throw new InvalidOperationException(
                    "四个 RD类型=基座 的高度轴位置未形成普通/增高分界，" +
                    "无法确定两类基座。");
            }

            ordinaryBaseCandidates.Clear();
            elevatedBaseCandidates.Clear();
            ordinaryBaseCandidates.AddRange(orderedByHeight.Take(2));
            elevatedBaseCandidates.AddRange(orderedByHeight.Skip(2));
        }

        private static string FormatBaseCandidateDiagnostics(
            IEnumerable<FaceToFaceFrameNativeComponent> baseCandidates)
        {
            if (baseCandidates == null)
            {
                return "<空>";
            }

            var diagnostics = baseCandidates
                .Select(component =>
                    component.InstanceId + "@高度=" +
                    (component.HeightAxisCoordinate.HasValue
                        ? component.HeightAxisCoordinate.Value.ToString("0.###", CultureInfo.InvariantCulture)
                        : "<缺失>") + "@路径=" + component.ModelPath)
                .ToList();
            return diagnostics.Count == 0
                ? "<空>"
                : string.Join("；", diagnostics);
        }

        private string ResolveExpectedBaseFullName(
            string spatialExpectedBaseFullName)
        {
            if (_expectedBaseComponentFullName != null &&
                spatialExpectedBaseFullName != null &&
                !FaceToFaceBaseModelMappingHelper.ComponentFullNameEquals(
                    _expectedBaseComponentFullName,
                    spatialExpectedBaseFullName))
            {
                throw new InvalidOperationException(
                    "面对面 B柜基座映射与顶层 Box 中心归属冲突：" +
                    "映射=" + _expectedBaseComponentFullName + "；" +
                    "空间归属=" + spatialExpectedBaseFullName + "。");
            }

            return spatialExpectedBaseFullName ??
                _expectedBaseComponentFullName;
        }

        private static List<OrdinaryBaseAnchorRoute> FilterExpectedBaseRoutes(
            IEnumerable<OrdinaryBaseAnchorRoute> routes,
            string expectedBaseComponentFullName)
        {
            if (expectedBaseComponentFullName == null)
            {
                return routes.ToList();
            }

            return routes.Where(route =>
                    FaceToFaceBaseModelMappingHelper.ComponentFullNameEquals(
                        route.OrdinaryBase.InstanceId,
                        expectedBaseComponentFullName)
                    || FaceToFaceBaseModelMappingHelper.ComponentFullNameEquals(
                        route.OrdinaryBase.ComponentName,
                        expectedBaseComponentFullName))
                .ToList();
        }

        private IReadOnlyList<FaceToFaceFrameMateSnapshot> CaptureChainMates(
            IReadOnlyList<FaceToFaceFrameComponentSnapshot> components)
        {
            var componentIds = new HashSet<string>(
                components.Select(component => component.InstanceId),
                StringComparer.OrdinalIgnoreCase);
            var mates = new List<FaceToFaceFrameMateSnapshot>();
            var mateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var component in components)
            {
                var nativeMates = _operations.GetMates(component.InstanceId)
                    ?? Array.Empty<FaceToFaceFrameNativeMate>();
                for (var mateIndex = 0; mateIndex < nativeMates.Count; mateIndex++)
                {
                    var mate = nativeMates[mateIndex];
                    if (mate == null)
                    {
                        continue;
                    }

                    var firstId = mate.First.ComponentInstanceId;
                    var secondId = mate.Second.ComponentInstanceId;
                    var firstRecognized = componentIds.Contains(firstId);
                    var secondRecognized = componentIds.Contains(secondId);
                    if (!firstRecognized || !secondRecognized)
                    {
                        // 总装根配合或可完整识别的外部辅助配合不属于基架链；
                        // 但只要端点身份/名称丢失，就必须失败，不能把损坏配合静默跳过。
                        EnsureNonChainMateIsReadable(
                            component,
                            mate,
                            mateIndex,
                            firstRecognized,
                            secondRecognized);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(mate.First.EntityName)
                        || string.IsNullOrWhiteSpace(mate.Second.EntityName))
                    {
                        throw new InvalidOperationException(
                            $"基架配合实体名称读取失败：组件={firstId}/{secondId}；"
                            + $"配合序号={mateIndex + 1}。" );
                    }

                    var key = BuildMateKey(mate);
                    if (!mateKeys.Add(key))
                    {
                        continue;
                    }

                    mates.Add(new FaceToFaceFrameMateSnapshot(
                        firstId,
                        mate.First.EntityName,
                        secondId,
                        mate.Second.EntityName));
                }
            }

            return mates.AsReadOnly();
        }

        private static void EnsureNonChainMateIsReadable(
            FaceToFaceFrameComponentSnapshot owner,
            FaceToFaceFrameNativeMate mate,
            int mateIndex,
            bool firstRecognized,
            bool secondRecognized)
        {
            var exactlyOneRecognized = firstRecognized ^ secondRecognized;
            var externalEndpoint = firstRecognized ? mate.Second : mate.First;
            var namesReadable = !string.IsNullOrWhiteSpace(mate.First.EntityName)
                && !string.IsNullOrWhiteSpace(mate.Second.EntityName);

            // ReferenceComponent 为空且实体名称可读表示总装根实体，是合法的外部端点。
            var externalIdentityReadable = !string.IsNullOrWhiteSpace(externalEndpoint.ComponentInstanceId)
                || !string.IsNullOrWhiteSpace(externalEndpoint.EntityName);
            if (exactlyOneRecognized && namesReadable && externalIdentityReadable)
            {
                return;
            }

            throw new InvalidOperationException(
                $"基架配合端点读取失败：所属组件={owner.InstanceId}；路径={owner.ModelPath}；"
                + $"配合序号={mateIndex + 1}；端点="
                + $"{FormatNativeMateEndpoint(mate.First)} ↔ {FormatNativeMateEndpoint(mate.Second)}。" );
        }

        private static string FormatNativeMateEndpoint(FaceToFaceFrameNativeMateEndpoint endpoint)
        {
            var componentId = string.IsNullOrWhiteSpace(endpoint.ComponentInstanceId)
                ? "<总装根或未知组件>"
                : endpoint.ComponentInstanceId;
            var entityName = string.IsNullOrWhiteSpace(endpoint.EntityName)
                ? "<未知实体>"
                : endpoint.EntityName;
            return componentId + ":" + entityName;
        }

        private static bool IsRootPositionMate(
            FaceToFaceFrameNativeMate mate,
            string candidateInstanceId,
            string bCabinetBasePositionName)
        {
            return mate != null
                && (IsRootAndBaseEndpoint(
                        mate.First,
                        mate.Second,
                        candidateInstanceId,
                        bCabinetBasePositionName)
                    || IsRootAndBaseEndpoint(
                        mate.Second,
                        mate.First,
                        candidateInstanceId,
                        bCabinetBasePositionName));
        }

        private static bool IsRootAndBaseEndpoint(
            FaceToFaceFrameNativeMateEndpoint root,
            FaceToFaceFrameNativeMateEndpoint baseEndpoint,
            string candidateInstanceId,
            string bCabinetBasePositionName)
        {
            // 基座内部坐标系名称在不同模板/历史模型中可能不同；候选身份已经由
            // 顶层 RD类型=基座确认，这里只按根坐标系与组件实例关系定位。
            return root != null
                && baseEndpoint != null
                && string.IsNullOrWhiteSpace(root.ComponentInstanceId)
                && string.Equals(root.EntityName, bCabinetBasePositionName, StringComparison.Ordinal)
                && string.Equals(
                    baseEndpoint.ComponentInstanceId,
                    candidateInstanceId,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsComponentInstanceMate(
            FaceToFaceFrameNativeMate mate,
            string firstComponentId,
            string secondComponentId)
        {
            return mate != null
                && (IsComponentInstanceEndpointPair(
                        mate.First,
                        mate.Second,
                        firstComponentId,
                        secondComponentId)
                    || IsComponentInstanceEndpointPair(
                        mate.Second,
                        mate.First,
                        firstComponentId,
                        secondComponentId));
        }

        private static bool IsComponentInstanceEndpointPair(
            FaceToFaceFrameNativeMateEndpoint first,
            FaceToFaceFrameNativeMateEndpoint second,
            string firstComponentId,
            string secondComponentId)
        {
            return first != null
                && second != null
                && string.Equals(
                    first.ComponentInstanceId,
                    firstComponentId,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    second.ComponentInstanceId,
                    secondComponentId,
                    StringComparison.OrdinalIgnoreCase);
        }

        private sealed class OrdinaryBaseAnchorRoute
        {
            private OrdinaryBaseAnchorRoute(
                FaceToFaceFrameNativeComponent ordinaryBase,
                string description)
            {
                OrdinaryBase = ordinaryBase;
                Description = description;
            }

            internal FaceToFaceFrameNativeComponent OrdinaryBase { get; }

            internal string Description { get; }

            internal static OrdinaryBaseAnchorRoute Direct(
                FaceToFaceFrameNativeComponent ordinaryBase,
                string rootCoordinateName)
            {
                return new OrdinaryBaseAnchorRoute(
                    ordinaryBase,
                    $"总装根:{rootCoordinateName} → 基座组件:{ordinaryBase.InstanceId}");
            }

            internal static OrdinaryBaseAnchorRoute Stacked(
                FaceToFaceFrameNativeComponent ordinaryBase,
                FaceToFaceFrameNativeComponent elevatedBase,
                string rootCoordinateName)
            {
                return new OrdinaryBaseAnchorRoute(
                    ordinaryBase,
                    $"总装根:{rootCoordinateName} → 增高基座组件:{elevatedBase.InstanceId}"
                    + $" → 普通基座组件:{ordinaryBase.InstanceId}");
            }
        }

        private static string BuildMateKey(FaceToFaceFrameNativeMate mate)
        {
            var first = mate.First.ComponentInstanceId + "\u001f" + mate.First.EntityName;
            var second = mate.Second.ComponentInstanceId + "\u001f" + mate.Second.EntityName;
            return string.Compare(first, second, StringComparison.OrdinalIgnoreCase) <= 0
                ? first + "\u001e" + second
                : second + "\u001e" + first;
        }

        private static void EnsureUniqueNativeComponentIds(
            IEnumerable<FaceToFaceFrameNativeComponent> components)
        {
            var duplicate = components
                .GroupBy(component => component.InstanceId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
            {
                throw new InvalidOperationException(
                    "总装顶层组件实例名重复，无法读取基架模型：" + duplicate.Key + "。" );
            }
        }
    }

    /// <summary>
    /// SolidWorks COM 的只读实现。所有模型均来自已解析的总装组件，
    /// 本类不会打开、保存、重建或关闭任何文档。
    /// </summary>
    internal sealed class SolidWorksFaceToFaceFrameReadOperations
        : IFaceToFaceFrameSolidWorksReadOperations
    {
        private readonly bool _readLegacyBoxCenters;
        private readonly bool _businessReadback;
        private readonly Func<string, bool> _allowUnsavedBusinessDocument;
        private readonly Component2[] _businessComponents, _businessTop;
        private readonly Dictionary<string,IReadOnlyDictionary<IMate2,bool>> _mateSuppression
            = new Dictionary<string,IReadOnlyDictionary<IMate2,bool>>(StringComparer.Ordinal);
        private readonly Dictionary<string, FaceToFaceFramePhysicalSnapshot> _businessPhysical
            = new Dictionary<string, FaceToFaceFramePhysicalSnapshot>(StringComparer.OrdinalIgnoreCase);
        private readonly IModelDoc2 _totalAssemblyModel;
        private readonly IAssemblyDoc _totalAssembly;
        private readonly Dictionary<string, Component2> _componentById
            = new Dictionary<string, Component2>(StringComparer.OrdinalIgnoreCase);

        internal SolidWorksFaceToFaceFrameReadOperations(IModelDoc2 totalAssemblyModel)
            : this(totalAssemblyModel, true) { }

        internal SolidWorksFaceToFaceFrameReadOperations(IModelDoc2 totalAssemblyModel, bool readLegacyBoxCenters, bool businessReadback = false,
            Component2[] businessComponents=null,Component2[] businessTop=null, Func<string, bool> allowUnsavedBusinessDocument = null)
        {
            _readLegacyBoxCenters = readLegacyBoxCenters;
            _businessReadback = businessReadback;
            _allowUnsavedBusinessDocument = allowUnsavedBusinessDocument;
            _businessComponents=businessComponents; _businessTop=businessTop;
            _totalAssemblyModel = totalAssemblyModel
                ?? throw new ArgumentNullException(nameof(totalAssemblyModel));
            _totalAssembly = totalAssemblyModel as IAssemblyDoc;
            if (_totalAssembly == null
                || totalAssemblyModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new InvalidOperationException("面对面基架参数只能从有效总装配体读取。" );
            }
        }

        public int ResolveLightweightComponents()
        {
            if (_businessReadback)
            {
                foreach (var component in _businessComponents ?? (_totalAssembly.GetComponents(false) as object[] ?? Array.Empty<object>()).OfType<Component2>().ToArray())
                {
                    if (component.IsSuppressed()) continue;
                    var type = component.GetConfiguredRdSystemPartType();
                    if ((type == SystemPartType_e.Frame || type == SystemPartType_e.Base) && component.GetModelDoc2() == null)
                        component.SetSuppression2((int)swComponentSuppressionState_e.swComponentResolved);
                }
                return (int)swComponentResolveStatus_e.swResolveOk;
            }
            // 正式生成后的重复反读不应再次触发已解析模型的上下文更新。
            var components = _totalAssembly.GetComponents(false) as object[] ?? Array.Empty<object>();
            if (!components.OfType<Component2>().Any(component =>
            {
                var state = component.GetSuppression();
                return state == (int)swComponentSuppressionState_e.swComponentLightweight
                    || state == (int)swComponentSuppressionState_e.swComponentFullyLightweight;
            }))
                return (int)swComponentResolveStatus_e.swResolveOk;
            return _totalAssembly.ResolveAllLightWeightComponents(false);
        }

        public IReadOnlyList<FaceToFaceFrameNativeComponent> GetTopLevelComponents()
        {
            if (_businessReadback)
            {
                var top = (_businessTop ?? (_totalAssembly.GetComponents(true) as object[] ?? Array.Empty<object>()).OfType<Component2>().ToArray())
                    .Where(c => c.GetConfiguredRdSystemPartType() == SystemPartType_e.Base);
                var frames = (_businessComponents ?? (_totalAssembly.GetComponents(false) as object[] ?? Array.Empty<object>()).OfType<Component2>().ToArray())
                    .Where(c => c.GetConfiguredRdSystemPartType() == SystemPartType_e.Frame);
                return BuildNativeComponents(top.Concat(frames).Cast<object>().ToArray(), clearComponentCache: true);
            }
            var nativeComponents = _totalAssembly.GetComponents(true) as object[];
            return BuildNativeComponents(nativeComponents, clearComponentCache: true);
        }

        public IReadOnlyList<FaceToFaceFrameNativeComponent> GetAllComponents()
        {
            var nativeComponents = _totalAssembly.GetComponents(false) as object[];
            return BuildNativeComponents(nativeComponents, clearComponentCache: true);
        }

        private IReadOnlyList<FaceToFaceFrameNativeComponent> BuildNativeComponents(
            object[] nativeComponents,
            bool clearComponentCache)
        {
            var result = new List<FaceToFaceFrameNativeComponent>();
            if (clearComponentCache)
            {
                _componentById.Clear();
            }
            if (nativeComponents == null)
            {
                return result.AsReadOnly();
            }

            foreach (var item in nativeComponents)
            {
                if (!(item is Component2 component))
                {
                    continue;
                }

                var instanceId = component.Name2;
                if (string.IsNullOrWhiteSpace(instanceId))
                {
                    throw new InvalidOperationException("装配组件缺少实例名称，无法读取拓扑。" );
                }

                var configuredRdSystemPartType = component.GetConfiguredRdSystemPartType();
                var suppressionState = (int)component.GetSuppression();
                var componentBoxCenter = _readLegacyBoxCenters ? ReadBoxCenter(component) : Array.Empty<double>();
                _componentById[instanceId] = component;
                result.Add(new FaceToFaceFrameNativeComponent(
                    instanceId,
                    instanceId,
                    component.GetPathName(),
                    suppressionState == (int)swComponentSuppressionState_e.swComponentSuppressed,
                    suppressionState == (int)swComponentSuppressionState_e.swComponentLightweight
                        || suppressionState == (int)swComponentSuppressionState_e.swComponentFullyLightweight,
                    ReadHeightAxisCoordinate(component, componentBoxCenter),
                    configuredRdSystemPartType,
                    componentBoxCenter));
            }

            return result.AsReadOnly();
        }

        private static double? ReadHeightAxisCoordinate(
            Component2 component,
            IReadOnlyList<double> componentBoxCenter)
        {
            if (component == null)
            {
                return null;
            }

            if (componentBoxCenter != null && componentBoxCenter.Count == 3)
            {
                return componentBoxCenter[1];
            }

            try
            {
                var transformData = component.Transform2?.ArrayData as double[];
                if (transformData != null && transformData.Length > 10)
                {
                    return transformData[10];
                }
            }
            catch
            {
                // 缺少高度轴位置时由上层唯一定位错误统一报告。
            }

            return null;
        }

        private static IReadOnlyList<double> ReadBoxCenter(Component2 component)
        {
            try
            {
                var box = component.GetBox(false, false) as double[];
                if (box == null
                    || box.Length != 6
                    || box.Any(value => double.IsNaN(value) || double.IsInfinity(value))
                    || box[0] > box[3]
                    || box[1] > box[4]
                    || box[2] > box[5])
                {
                    return Array.Empty<double>();
                }

                return new[]
                {
                    (box[0] + box[3]) / 2d,
                    (box[1] + box[4]) / 2d,
                    (box[2] + box[5]) / 2d
                };
            }
            catch
            {
                // 非面对面场景不依赖 Box；面对面入口会用含组件身份的明确错误终止。
                return Array.Empty<double>();
            }
        }

        public IReadOnlyList<FaceToFaceFrameNativeMate> GetMates(string componentInstanceId)
        {
            var component = GetCapturedComponent(componentInstanceId);
            IReadOnlyDictionary<IMate2,bool> suppression=null;
            if(_businessReadback)
            {
                var parent=component.GetParent();
                var owner=parent?.GetModelDoc2() as IModelDoc2 ?? _totalAssemblyModel;
                var configuration=parent?.ReferencedConfiguration;
                if(string.IsNullOrWhiteSpace(configuration)) configuration=owner.ConfigurationManager.ActiveConfiguration.Name;
                var key=owner.GetPathName()+"|"+configuration;
                if(!_mateSuppression.TryGetValue(key,out suppression))
                    _mateSuppression[key]=suppression=BridgeReferencedModelParameters.MateSuppression(owner,configuration,true);
            }
            var nativeMates = component.GetMates() as object[];
            var result = new List<FaceToFaceFrameNativeMate>();
            if (nativeMates == null)
            {
                return result.AsReadOnly();
            }

            foreach (var item in nativeMates)
            {
                var mate = item as IMate2;
                if (mate == null || mate.GetMateEntityCount() != 2)
                {
                    continue;
                }
                if (_businessReadback && mate.Type != (int)swMateType_e.swMateCOORDINATE) continue;
                if(suppression!=null && suppression.TryGetValue(mate,out var suppressed) && suppressed) continue;

                result.Add(new FaceToFaceFrameNativeMate(
                    ReadMateEndpoint(mate.MateEntity(0)),
                    ReadMateEndpoint(mate.MateEntity(1))));
            }

            return result.AsReadOnly();
        }

        public FaceToFaceFramePhysicalSnapshot ReadPhysicalModel(
            string componentInstanceId,
            FaceToWallFrameTemplateKind frameTemplateKind)
        {
            var component = GetCapturedComponent(componentInstanceId);
            SolidWorksFaceToFaceFrameModelOperations.RequireDefaultReference(
                component.GetModelDoc2() as IModelDoc2, component.ReferencedConfiguration, componentInstanceId);
            if (_businessReadback)
            {
                // 物理文档可以共享，实际安装点必须逐实例核验，不能复用另一个实例的验收结果。
                var key = componentInstanceId + "|" + component.GetPathName() + "|" + component.ReferencedConfiguration;
                if (!_businessPhysical.TryGetValue(key, out var snapshot))
                    _businessPhysical.Add(key, snapshot = new BridgeReferencedModelParameters(component, _allowUnsavedBusinessDocument).Frame(component, frameTemplateKind));
                return snapshot;
            }
            var values = ReferencedModelValueReader.FromComponent(component);
            var model = values.Model;
            if (model == null
                || model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new InvalidOperationException(
                    $"基架模型未解析为装配体：组件={componentInstanceId}。" );
            }

            var templateIdentityText = values.TryProperty(FrameTemplateIdentityMetadata.PropertyName);
            var explicitTemplateIdentity = FaceToFaceFramePathClassifier.ResolveStoredTemplateIdentity(
                component.GetPathName(), templateIdentityText);

            var useLargeFrameTemplate =
                frameTemplateKind != FaceToWallFrameTemplateKind.Regular;
            var widthVariableName = useLargeFrameTemplate
                ? FaceToWallFrameDrivePlanner.LargeFrameWidthVariableName
                : FaceToWallFrameDrivePlanner.RegularFrameWidthVariableName;
            var lengthVariableName = useLargeFrameTemplate
                ? FaceToWallFrameDrivePlanner.LargeFrameLengthVariableName
                : FaceToWallFrameDrivePlanner.RegularFrameLengthVariableName;
            var heightVariableName = useLargeFrameTemplate
                ? FaceToWallFrameDrivePlanner.LargeFrameHeightVariableName
                : FaceToWallFrameDrivePlanner.RegularFrameHeightVariableName;
            var width = ReadRequiredGlobalVariable(values, widthVariableName, componentInstanceId);
            var length = ReadRequiredGlobalVariable(values, lengthVariableName, componentInstanceId);
            var height = ReadRequiredGlobalVariable(values, heightVariableName, componentInstanceId);
            var wallEntryDistance = 0d;
            var hasWallEntryDistanceProperty = false;
            if (frameTemplateKind == FaceToWallFrameTemplateKind.Large1)
            {
                var entryText = values.TryProperty(FaceToWallFrameDrivePlanner.WallEntryDistancePropertyName);
                hasWallEntryDistanceProperty = !string.IsNullOrWhiteSpace(entryText);
                wallEntryDistance = ParseWallEntryDistance(entryText, componentInstanceId);
            }
            double? sideCoverPlateWidth = null;
            if (frameTemplateKind == FaceToWallFrameTemplateKind.Large2)
            {
                sideCoverPlateWidth = ReadRequiredGlobalVariable(
                    values,
                    FaceToWallFrameDrivePlanner.SideCoverPlateWidthVariableName,
                    componentInstanceId);
            }
            if (!FaceToFaceFrameTemplateRules.SupportsInternalInsulators(frameTemplateKind))
            {
                // 仅不支持内部绝缘子的模板跳过；026、027 和 028 均继续反读布局。
                return CreatePhysicalSnapshot(
                    width,
                    length,
                    height,
                    Array.Empty<double>(),
                    null,
                    explicitTemplateIdentity,
                    sideCoverPlateWidth,
                    wallEntryDistance,
                    hasWallEntryDistanceProperty);
            }

            var smallOffsetFrame = frameTemplateKind == FaceToWallFrameTemplateKind.Regular
                ? ReadSmallOffsetFramePlan(
                    values,
                    componentInstanceId,
                    width)
                : null;

            var positions = new FrameInsulatorPositionReadback().Read(
                new SolidWorksFrameInsulatorPositionSource(component, length, name => values.Dimension(name)));

            return CreatePhysicalSnapshot(
                width,
                length,
                height,
                positions,
                smallOffsetFrame,
                explicitTemplateIdentity,
                sideCoverPlateWidth,
                wallEntryDistance,
                hasWallEntryDistanceProperty);
        }

        // 两种布局分支共用出口，安装参数必须显式传递，避免构造函数默认值掩盖漏传。
        internal static FaceToFaceFramePhysicalSnapshot CreatePhysicalSnapshot(
            double width,
            double length,
            double height,
            IEnumerable<double> insulatorPositions,
            FaceToFaceSmallOffsetFrameDrivePlan smallOffsetFrame,
            FrameTemplateIdentity? templateIdentity,
            double? sideCoverPlateWidth,
            double wallEntryDistance,
            bool hasWallEntryDistanceProperty)
        {
            return new FaceToFaceFramePhysicalSnapshot(
                width, length, height, insulatorPositions, smallOffsetFrame,
                templateIdentity, sideCoverPlateWidth, wallEntryDistance,
                hasWallEntryDistanceProperty);
        }

        internal static double ParseWallEntryDistance(string text, string componentInstanceId)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0d;

            if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
                || double.IsNaN(value) || double.IsInfinity(value))
                throw new InvalidOperationException($"大基架模型自定义属性“进墙距离”无效：组件={componentInstanceId}，值={text}。");
            return value;
        }

        /// <summary>
        /// 只读模型级/当前配置自定义属性；解析失败时返回原文本，由上层保留“未明确身份”状态。
        /// </summary>
        private static string ReadCustomProperty(
            IModelDoc2 model,
            string propertyName)
        {
            var extension = model?.Extension;
            if (extension == null)
            {
                return null;
            }

            var value = ReadCustomProperty(
                extension.CustomPropertyManager[string.Empty],
                propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            var configurationName = model.ConfigurationManager
                ?.ActiveConfiguration
                ?.Name;
            return string.IsNullOrWhiteSpace(configurationName)
                ? null
                : ReadCustomProperty(
                    extension.CustomPropertyManager[configurationName],
                    propertyName);
        }

        private static string ReadCustomProperty(
            CustomPropertyManager manager,
            string propertyName)
        {
            if (manager == null)
            {
                return null;
            }

            string rawValue;
            string resolvedValue;
            manager.Get2(propertyName, out rawValue, out resolvedValue);
            return !string.IsNullOrWhiteSpace(rawValue)
                ? rawValue.Trim()
                : string.IsNullOrWhiteSpace(resolvedValue)
                    ? null
                    : resolvedValue.Trim();
        }

        private static FaceToFaceSmallOffsetFrameDrivePlan ReadSmallOffsetFramePlan(
            ReferencedModelValueReader model,
            string componentInstanceId,
            double frameWidth)
        {
            var offsetVariableNames = new[]
            {
                "进孔偏移方向",
                "进孔偏移尺寸",
                "出孔偏移方向",
                "出孔偏移尺寸"
            };
            var presentVariableNames = offsetVariableNames
                .Where(variableName => CabinetAssemblyDimensionReader.HasGlobalVariable(
                    model.Model,
                    variableName))
                .ToArray();
            if (presentVariableNames.Length == 0)
            {
                return null;
            }

            if (presentVariableNames.Length != offsetVariableNames.Length)
            {
                throw new InvalidOperationException(
                    $"基架模型的小偏移全局变量不完整：组件={componentInstanceId}；"
                    + $"已找到={string.Join("、", presentVariableNames)}。" );
            }

            var values = new Dictionary<string, double>();
            foreach (var variableName in offsetVariableNames)
            {
                if (!model.TryGlobal(variableName, out var value))
                {
                    throw new InvalidOperationException(
                        $"基架模型全局变量“{CabinetHoleParameters.GetParameterDisplayName(variableName)}”的数值无效："
                        + $"组件={componentInstanceId}。" );
                }

                values.Add(variableName, value);
            }

            var inletDirection = ReadOffsetDirection(
                values["进孔偏移方向"],
                "进孔偏移方向",
                componentInstanceId);
            var outletDirection = ReadOffsetDirection(
                values["出孔偏移方向"],
                "出孔偏移方向",
                componentInstanceId);
            var inletSize = ReadOffsetSize(
                values["进孔偏移尺寸"],
                "进孔偏移尺寸",
                componentInstanceId);
            var outletSize = ReadOffsetSize(
                values["出孔偏移尺寸"],
                "出孔偏移尺寸",
                componentInstanceId);
            return new FaceToFaceSmallOffsetFrameDrivePlan(
                frameWidth,
                inletDirection,
                inletSize,
                outletDirection,
                outletSize);
        }

        private static int ReadOffsetDirection(
            double value,
            string variableName,
            string componentInstanceId)
        {
            var rounded = Math.Round(value);
            if (double.IsNaN(value)
                || double.IsInfinity(value)
                || Math.Abs(value - rounded) > 0.001
                || (rounded != 0 && rounded != 1))
            {
                throw new InvalidOperationException(
                    $"基架模型全局变量“{CabinetHoleParameters.GetParameterDisplayName(variableName)}”必须为 0 或 1："
                    + $"组件={componentInstanceId}；数值={value}。" );
            }

            return (int)rounded;
        }

        private static double ReadOffsetSize(
            double value,
            string variableName,
            string componentInstanceId)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            {
                throw new InvalidOperationException(
                    $"基架模型全局变量“{CabinetHoleParameters.GetParameterDisplayName(variableName)}”必须为有限非负值："
                    + $"组件={componentInstanceId}；数值={value}。" );
            }

            return Math.Round(value, 3, MidpointRounding.AwayFromZero);
        }

        private Component2 GetCapturedComponent(string componentInstanceId)
        {
            if (string.IsNullOrWhiteSpace(componentInstanceId)
                || !_componentById.TryGetValue(componentInstanceId, out var component))
            {
                throw new InvalidOperationException(
                    "未找到总装顶层组件实例：" + componentInstanceId + "。" );
            }

            return component;
        }

        private FaceToFaceFrameNativeMateEndpoint ReadMateEndpoint(MateEntity2 entity)
        {
            if (entity == null)
            {
                return new FaceToFaceFrameNativeMateEndpoint(null, null);
            }

            var referenceComponent = entity.ReferenceComponent;
            var referenceFeature = entity.Reference as IFeature;
            // SolidWorks 2022 会把总装根坐标系端点的 ReferenceComponent 返回为总装自身；
            // 统一按模型路径折叠为 null，使纯拓扑层不依赖版本相关的 COM 表示。
            var componentInstanceId = NormalizeMateComponentInstanceId(
                referenceComponent?.Name2,
                referenceComponent?.GetPathName(),
                _totalAssemblyModel.GetPathName());
            return new FaceToFaceFrameNativeMateEndpoint(
                componentInstanceId,
                referenceFeature?.Name);
        }

        internal static string NormalizeMateComponentInstanceId(
            string componentInstanceId,
            string componentModelPath,
            string totalAssemblyModelPath)
        {
            // 实现迁入未加密的界面运行时，保留原调用入口。
            return BridgeModelReadOperations.NormalizeMateComponentInstanceId(componentInstanceId, componentModelPath, totalAssemblyModelPath);
        }

        private static double ReadRequiredGlobalVariable(
            ReferencedModelValueReader values, string name, string identity) => values.Global(name);

        private static double ReadRequiredGlobalVariable(
            IModelDoc2 model,
            string variableName,
            string componentInstanceId)
        {
            if (!CabinetAssemblyDimensionReader.TryGetGlobalVariableValue(
                    model,
                    variableName,
                    out var value))
            {
                throw new InvalidOperationException(
                    $"基架模型缺少全局变量“{CabinetHoleParameters.GetParameterDisplayName(variableName)}”：组件={componentInstanceId}。" );
            }

            return value;
        }

        private static bool PathsEqual(string firstPath, string secondPath)
        {
            // 实现迁入未加密的界面运行时，保留原调用入口。
            return BridgeModelReadOperations.PathsEqual(firstPath, secondPath);
        }
    }
}
