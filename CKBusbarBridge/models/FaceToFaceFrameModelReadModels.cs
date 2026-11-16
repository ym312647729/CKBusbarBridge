using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridge.Drivers;

namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 总装顶层组件的只读快照。实例身份与物理模型路径分开保存，
    /// 以便同一个基架模型在总装中被多次装配时仍能分别参与配合链分析。
    /// </summary>
    internal sealed class FaceToFaceFrameComponentSnapshot
    {
        internal FaceToFaceFrameComponentSnapshot(
            string instanceId,
            string componentName,
            string modelPath,
            string sourceName,
            bool isFrame,
            bool isOrdinaryBase,
            bool useLargeFrameTemplate = false)
            : this(
                instanceId,
                componentName,
                modelPath,
                sourceName,
                isFrame,
                isOrdinaryBase,
                useLargeFrameTemplate
                    ? FaceToWallFrameTemplateKind.Large1
                    : FaceToWallFrameTemplateKind.Regular)
        {
        }

        internal FaceToFaceFrameComponentSnapshot(
            string instanceId,
            string componentName,
            string modelPath,
            string sourceName,
            bool isFrame,
            bool isOrdinaryBase,
            FaceToWallFrameTemplateKind frameTemplateKind,
            IEnumerable<double> boxCenter = null)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                throw new ArgumentException("组件实例标识不能为空。", nameof(instanceId));
            }

            if (string.IsNullOrWhiteSpace(componentName))
            {
                throw new ArgumentException("组件名称不能为空。", nameof(componentName));
            }

            if (string.IsNullOrWhiteSpace(modelPath))
            {
                throw new ArgumentException("组件模型路径不能为空。", nameof(modelPath));
            }

            if (isFrame && string.IsNullOrWhiteSpace(sourceName))
            {
                throw new ArgumentException("基架 SourceName 不能为空。", nameof(sourceName));
            }

            if (isFrame == isOrdinaryBase)
            {
                throw new ArgumentException("组件必须且只能标识为基架或普通基座。", nameof(isFrame));
            }

            if (frameTemplateKind != FaceToWallFrameTemplateKind.Regular && !isFrame)
            {
                throw new ArgumentException("只有基架可以标识为大基架模板。", nameof(frameTemplateKind));
            }

            InstanceId = instanceId.Trim();
            ComponentName = componentName.Trim();
            ModelPath = modelPath.Trim();
            SourceName = sourceName?.Trim();
            IsFrame = isFrame;
            IsOrdinaryBase = isOrdinaryBase;
            FrameTemplateKind = frameTemplateKind;
            BoxCenter = (boxCenter ?? Enumerable.Empty<double>()).ToArray();
        }

        internal string InstanceId { get; }

        internal string ComponentName { get; }

        internal string ModelPath { get; }

        internal string SourceName { get; }

        internal bool IsFrame { get; }

        internal bool IsOrdinaryBase { get; }

        internal FaceToWallFrameTemplateKind FrameTemplateKind { get; }

        internal IReadOnlyList<double> BoxCenter { get; }

        internal bool UseLargeFrameTemplate =>
            FrameTemplateKind != FaceToWallFrameTemplateKind.Regular;
    }

    /// <summary>
    /// 一个配合中的两个组件及其引用实体名称；不保存任何 COM 对象。
    /// </summary>
    internal sealed class FaceToFaceFrameMateSnapshot
    {
        internal FaceToFaceFrameMateSnapshot(
            string firstComponentId,
            string firstEntityName,
            string secondComponentId,
            string secondEntityName)
        {
            FirstComponentId = RequireText(firstComponentId, nameof(firstComponentId));
            FirstEntityName = firstEntityName?.Trim() ?? string.Empty;
            SecondComponentId = RequireText(secondComponentId, nameof(secondComponentId));
            SecondEntityName = secondEntityName?.Trim() ?? string.Empty;
        }

        internal string FirstComponentId { get; }

        internal string FirstEntityName { get; }

        internal string SecondComponentId { get; }

        internal string SecondEntityName { get; }

        private static string RequireText(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("配合端点信息不能为空。", parameterName);
            }

            return value.Trim();
        }
    }

    internal sealed class FaceToFaceFrameTopologySnapshot
    {
        internal FaceToFaceFrameTopologySnapshot(
            IEnumerable<FaceToFaceFrameComponentSnapshot> components,
            IEnumerable<FaceToFaceFrameMateSnapshot> mates)
        {
            Components = (components ?? Enumerable.Empty<FaceToFaceFrameComponentSnapshot>())
                .Where(item => item != null)
                .ToList()
                .AsReadOnly();
            Mates = (mates ?? Enumerable.Empty<FaceToFaceFrameMateSnapshot>())
                .Where(item => item != null)
                .ToList()
                .AsReadOnly();
        }

        internal IReadOnlyList<FaceToFaceFrameComponentSnapshot> Components { get; }

        internal IReadOnlyList<FaceToFaceFrameMateSnapshot> Mates { get; }
    }

    internal sealed class FaceToFaceFrameOrderedInstance
    {
        internal FaceToFaceFrameOrderedInstance(
            FaceToFaceFrameComponentSnapshot component,
            bool isReversed,
            string inletCoordinateName,
            string outletCoordinateName)
        {
            Component = component ?? throw new ArgumentNullException(nameof(component));
            IsReversed = component.FrameTemplateKind != FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner && isReversed;
            InletCoordinateName = inletCoordinateName
                ?? throw new ArgumentNullException(nameof(inletCoordinateName));
            OutletCoordinateName = outletCoordinateName;
        }

        internal FaceToFaceFrameComponentSnapshot Component { get; }

        internal bool IsReversed { get; }

        internal string InletCoordinateName { get; }

        internal string OutletCoordinateName { get; }
    }

    /// <summary>
    /// 单一物理基架模型的只读参数快照，数值单位均为毫米。
    /// 参数合法性由读取服务统一校验，保证错误信息能够带上组件与路径上下文。
    /// </summary>
    internal sealed class FaceToFaceFramePhysicalSnapshot
    {
        internal FaceToFaceFramePhysicalSnapshot(
            double width,
            double length,
            double height,
            IEnumerable<double> insulatorPositions,
            bool isSmallOffsetFrame = false)
            : this(
                width,
                length,
                height,
                insulatorPositions,
                isSmallOffsetFrame
                    ? new FaceToFaceSmallOffsetFrameDrivePlan(width, 0, 0, 0, 0)
                    : null)
        {
        }

        internal FaceToFaceFramePhysicalSnapshot(
            double width,
            double length,
            double height,
            IEnumerable<double> insulatorPositions,
            FaceToFaceSmallOffsetFrameDrivePlan smallOffsetFrame,
            FrameTemplateIdentity? templateIdentity = null,
            double? sideCoverPlateWidth = null,
            double wallEntryDistance = 0d,
            bool hasWallEntryDistanceProperty = true)
        {
            Width = width;
            Length = length;
            Height = height;
            InsulatorPositions = (insulatorPositions ?? Enumerable.Empty<double>())
                .ToList()
                .AsReadOnly();
            SmallOffsetFrame = smallOffsetFrame;
            HasExplicitTemplateIdentity = templateIdentity.HasValue;
            TemplateIdentity = templateIdentity
                ?? FrameTemplateIdentity.Regular024;
            SideCoverPlateWidth = sideCoverPlateWidth;
            WallEntryDistance = wallEntryDistance;
            HasWallEntryDistanceProperty = hasWallEntryDistanceProperty;
        }

        internal double Width { get; }

        internal double Length { get; }

        internal double Height { get; }

        internal IReadOnlyList<double> InsulatorPositions { get; }

        internal FaceToFaceSmallOffsetFrameDrivePlan SmallOffsetFrame { get; }

        internal bool IsSmallOffsetFrame => SmallOffsetFrame != null;

        internal FrameTemplateIdentity TemplateIdentity { get; }

        internal bool HasExplicitTemplateIdentity { get; }

        /// <summary>
        /// 面对墙大基架2的“侧盖板宽度”物理反读值；其他模板为空。
        /// </summary>
        internal double? SideCoverPlateWidth { get; }

        internal double WallEntryDistance { get; }

        internal bool HasWallEntryDistanceProperty { get; }
    }

    internal sealed class FaceToFaceFrameReadItem
    {
        internal FaceToFaceFrameReadItem(
            string componentInstanceId,
            string sourceName,
            string modelPath,
            double width,
            double length,
            double height,
            bool isReversed,
            IEnumerable<double> insulatorPositions,
            bool useLargeFrameTemplate = false,
            bool isSmallOffsetFrame = false)
            : this(
                componentInstanceId,
                sourceName,
                modelPath,
                width,
                length,
                height,
                isReversed,
                insulatorPositions,
                useLargeFrameTemplate
                    ? FaceToWallFrameTemplateKind.Large1
                    : FaceToWallFrameTemplateKind.Regular,
                isSmallOffsetFrame)
        {
        }

        internal FaceToFaceFrameReadItem(
            string componentInstanceId,
            string sourceName,
            string modelPath,
            double width,
            double length,
            double height,
            bool isReversed,
            IEnumerable<double> insulatorPositions,
            FaceToWallFrameTemplateKind frameTemplateKind,
            bool isSmallOffsetFrame = false)
            : this(
                componentInstanceId,
                sourceName,
                modelPath,
                width,
                length,
                height,
                isReversed,
                insulatorPositions,
                frameTemplateKind,
                isSmallOffsetFrame
                    ? new FaceToFaceSmallOffsetFrameDrivePlan(width, 0, 0, 0, 0)
                    : null)
        {
        }

        internal FaceToFaceFrameReadItem(
            string componentInstanceId,
            string sourceName,
            string modelPath,
            double width,
            double length,
            double height,
            bool isReversed,
            IEnumerable<double> insulatorPositions,
            FaceToWallFrameTemplateKind frameTemplateKind,
            FaceToFaceSmallOffsetFrameDrivePlan smallOffsetFrame,
            string inletCoordinateName = null,
            string outletCoordinateName = null,
            string largeOffsetGroupId = null,
            FaceToFaceLargeOffsetFrameRole largeOffsetRole = FaceToFaceLargeOffsetFrameRole.None,
            FrameTemplateIdentity? templateIdentity = null,
            bool? hasExplicitTemplateIdentity = null,
            double? sideCoverPlateWidth = null,
            double wallEntryDistance = 0d,
            bool hasWallEntryDistanceProperty = true)
        {
            ComponentInstanceId = componentInstanceId
                ?? throw new ArgumentNullException(nameof(componentInstanceId));
            SourceName = sourceName ?? throw new ArgumentNullException(nameof(sourceName));
            ModelPath = modelPath ?? throw new ArgumentNullException(nameof(modelPath));
            Width = width;
            Length = length;
            Height = height;
            IsReversed = frameTemplateKind != FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner && isReversed;
            InsulatorPositions = (insulatorPositions ?? Enumerable.Empty<double>())
                .ToList()
                .AsReadOnly();
            FrameTemplateKind = frameTemplateKind;
            SmallOffsetFrame = smallOffsetFrame;
            InletCoordinateName = inletCoordinateName;
            OutletCoordinateName = outletCoordinateName;
            LargeOffsetGroupId = largeOffsetGroupId;
            LargeOffsetRole = largeOffsetRole;
            HasExplicitTemplateIdentity = hasExplicitTemplateIdentity
                ?? templateIdentity.HasValue;
            TemplateIdentity = templateIdentity
                ?? FrameTemplateIdentityMetadata.FromLegacyStructure(
                    frameTemplateKind,
                    smallOffsetFrame != null);
            SideCoverPlateWidth = sideCoverPlateWidth;
            WallEntryDistance = frameTemplateKind == FaceToWallFrameTemplateKind.Large1 ? wallEntryDistance : 0d;
            HasWallEntryDistanceProperty = hasWallEntryDistanceProperty;
        }

        internal string ComponentInstanceId { get; }

        internal string SourceName { get; }

        internal string ModelPath { get; }

        internal double Width { get; }

        internal double Length { get; }

        internal double Height { get; }

        internal bool IsReversed { get; }

        internal IReadOnlyList<double> InsulatorPositions { get; }

        internal int InsulatorCount => InsulatorPositions.Count;

        internal FaceToWallFrameTemplateKind FrameTemplateKind { get; }

        internal FrameTemplateIdentity TemplateIdentity { get; }

        internal bool HasExplicitTemplateIdentity { get; }

        /// <summary>
        /// 面对墙大基架2的“侧盖板宽度”物理反读值；其他模板为空。
        /// </summary>
        internal double? SideCoverPlateWidth { get; }

        internal double WallEntryDistance { get; }

        internal bool HasWallEntryDistanceProperty { get; }

        internal bool UseLargeFrameTemplate =>
            FrameTemplateKind != FaceToWallFrameTemplateKind.Regular;

        internal FaceToFaceSmallOffsetFrameDrivePlan SmallOffsetFrame { get; }

        internal bool IsSmallOffsetFrame => SmallOffsetFrame != null;

        internal string InletCoordinateName { get; }

        internal string OutletCoordinateName { get; }

        internal string LargeOffsetGroupId { get; }

        internal FaceToFaceLargeOffsetFrameRole LargeOffsetRole { get; }

        internal bool IsLargeOffsetMember =>
            LargeOffsetRole != FaceToFaceLargeOffsetFrameRole.None
            && !string.IsNullOrWhiteSpace(LargeOffsetGroupId);

        /// <summary>
        /// 保留物理参数与实际配合路由，仅附加经真实 028 链验证后的组元数据。
        /// </summary>
        internal FaceToFaceFrameReadItem WithLargeOffsetMetadata(
            string groupId,
            FaceToFaceLargeOffsetFrameRole role)
        {
            return new FaceToFaceFrameReadItem(
                ComponentInstanceId,
                SourceName,
                ModelPath,
                Width,
                Length,
                Height,
                IsReversed,
                InsulatorPositions,
                FrameTemplateKind,
                SmallOffsetFrame,
                InletCoordinateName,
                OutletCoordinateName,
                groupId,
                role,
                TemplateIdentity,
                HasExplicitTemplateIdentity,
                SideCoverPlateWidth,
                WallEntryDistance,
                HasWallEntryDistanceProperty);
        }
    }

    internal sealed class FaceToFaceFrameReadResult
    {
        internal FaceToFaceFrameReadResult(
            IEnumerable<FaceToFaceFrameReadItem> frames,
            double unifiedWidth,
            double totalLength,
            double signedEccentricity = 0d)
        {
            Frames = (frames ?? Enumerable.Empty<FaceToFaceFrameReadItem>())
                .Where(item => item != null)
                .ToList()
                .AsReadOnly();
            UnifiedWidth = unifiedWidth;
            TotalLength = totalLength;
            SignedEccentricity = signedEccentricity;
        }

        internal bool HasFrames => Frames.Count > 0;

        internal IReadOnlyList<FaceToFaceFrameReadItem> Frames { get; }

        internal double UnifiedWidth { get; }

        internal double TotalLength { get; }

        internal double SignedEccentricity { get; }

        internal bool HasLargeOffsetGroup =>
            Frames.Any(item => item.IsLargeOffsetMember);

        internal static FaceToFaceFrameReadResult Empty { get; }
            = new FaceToFaceFrameReadResult(
                Array.Empty<FaceToFaceFrameReadItem>(),
                0,
                0);
    }
}
