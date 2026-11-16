using System;
using System.Collections.Generic;

namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 物理基架模板身份。该身份独立于结构能力，避免同类结构被文件前缀混同。
    /// </summary>
    internal enum FrameTemplateIdentity
    {
        Regular024 = 0,
        FaceToFaceSmallOffset032 = 1,
        FaceToFaceLargeOffsetCorner028 = 2,
        FaceToWallLarge1 = 3,
        FaceToWallLarge2 = 4,
        SideToSidePhaseChange029 = 5
    }

    internal static class FrameTemplateIdentityMetadata
    {
        internal const string PropertyName = "基架模板身份";
        internal const string SideToSidePhaseChangeValue = "侧对侧换相基架029";

        private static readonly IReadOnlyDictionary<FrameTemplateIdentity, string>
            SerializedValues = new Dictionary<FrameTemplateIdentity, string>
            {
                [FrameTemplateIdentity.Regular024] = "常规基架024",
                [FrameTemplateIdentity.FaceToFaceSmallOffset032] = "面对面小偏移基架032",
                [FrameTemplateIdentity.FaceToFaceLargeOffsetCorner028] = "面对面大偏移拐角基架028",
                [FrameTemplateIdentity.FaceToWallLarge1] = "面对墙大基架1",
                [FrameTemplateIdentity.FaceToWallLarge2] = "面对墙大基架2",
                [FrameTemplateIdentity.SideToSidePhaseChange029] =
                    SideToSidePhaseChangeValue
            };

        internal static string Serialize(FrameTemplateIdentity identity)
        {
            if (!SerializedValues.TryGetValue(identity, out var value))
            {
                throw new ArgumentOutOfRangeException(nameof(identity));
            }

            return value;
        }

        internal static bool TryParse(
            string value,
            out FrameTemplateIdentity identity)
        {
            identity = FrameTemplateIdentity.Regular024;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = value.Trim();
            foreach (var item in SerializedValues)
            {
                if (string.Equals(item.Value, normalized, StringComparison.Ordinal))
                {
                    identity = item.Key;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 从结构类型与小偏移标记推导模板身份。
        /// 侧对侧换相身份必须由调用方显式提供，不能仅从结构类型推导。
        /// </summary>
        internal static FrameTemplateIdentity FromLegacyStructure(
            FaceToWallFrameTemplateKind kind,
            bool isSmallOffsetFrame)
        {
            if (isSmallOffsetFrame)
            {
                return FrameTemplateIdentity.FaceToFaceSmallOffset032;
            }

            switch (kind)
            {
                case FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner:
                    return FrameTemplateIdentity.FaceToFaceLargeOffsetCorner028;
                case FaceToWallFrameTemplateKind.Large1:
                    return FrameTemplateIdentity.FaceToWallLarge1;
                case FaceToWallFrameTemplateKind.Large2:
                    return FrameTemplateIdentity.FaceToWallLarge2;
                case FaceToWallFrameTemplateKind.Regular:
                default:
                    return FrameTemplateIdentity.Regular024;
            }
        }
    }
}
