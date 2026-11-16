using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 面对面大偏移组中物理基架的固定角色。
    /// 显式数值会写入项目参数，后续不能调整顺序或复用数值。
    /// </summary>
    public enum FaceToFaceLargeOffsetFrameRole
    {
        None = 0,
        BSideCorner = 1,
        Connector = 2,
        ASideCorner = 3
    }

    /// <summary>
    /// 大偏移组内单个物理基架的纯计算结果，不携带文件或 SolidWorks 对象。
    /// </summary>
    internal sealed class FaceToFaceLargeOffsetFrameMemberPlan
    {
        internal FaceToFaceLargeOffsetFrameMemberPlan(
            FaceToFaceLargeOffsetFrameRole role,
            FaceToWallFrameTemplateKind frameTemplateKind,
            double width,
            double length,
            double height,
            string inletCoordinateName,
            string outletCoordinateName)
        {
            Role = role;
            FrameTemplateKind = frameTemplateKind;
            Width = width;
            Length = length;
            Height = height;
            InletCoordinateName = inletCoordinateName
                ?? throw new ArgumentNullException(nameof(inletCoordinateName));
            OutletCoordinateName = outletCoordinateName
                ?? throw new ArgumentNullException(nameof(outletCoordinateName));
        }

        internal FaceToFaceLargeOffsetFrameRole Role { get; }

        internal FaceToWallFrameTemplateKind FrameTemplateKind { get; }

        internal double Width { get; }

        internal double Length { get; }

        internal double Height { get; }

        internal string InletCoordinateName { get; }

        internal string OutletCoordinateName { get; }
    }

    /// <summary>
    /// 一个面对面大偏移逻辑段展开后的完整物理组计划。
    /// </summary>
    internal sealed class FaceToFaceLargeOffsetFramePlan
    {
        internal FaceToFaceLargeOffsetFramePlan(
            double signedEccentricity,
            double connectorSpan,
            double sharedLength,
            IEnumerable<FaceToFaceLargeOffsetFrameMemberPlan> members)
        {
            SignedEccentricity = signedEccentricity;
            ConnectorSpan = connectorSpan;
            SharedLength = sharedLength;
            Members = (members ?? Enumerable.Empty<FaceToFaceLargeOffsetFrameMemberPlan>())
                .ToList()
                .AsReadOnly();
        }

        internal double SignedEccentricity { get; }

        internal double ConnectorSpan { get; }

        internal double SharedLength { get; }

        internal IReadOnlyList<FaceToFaceLargeOffsetFrameMemberPlan> Members { get; }
    }
}
