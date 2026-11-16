using CKBusbarBridge.Models;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 集中定义各基架模板允许出现的业务场景和内部结构，避免用“非普通模板”笼统判断。
    /// </summary>
    internal static class FaceToFaceFrameTemplateRules
    {
        internal static bool IsFaceToWallSupported(FaceToWallFrameTemplateKind templateKind)
        {
            return templateKind == FaceToWallFrameTemplateKind.Regular
                || templateKind == FaceToWallFrameTemplateKind.Large1
                || templateKind == FaceToWallFrameTemplateKind.Large2;
        }

        internal static bool IsFaceToWallLarge(FaceToWallFrameTemplateKind templateKind)
        {
            return templateKind == FaceToWallFrameTemplateKind.Large1
                || templateKind == FaceToWallFrameTemplateKind.Large2;
        }

        internal static bool IsFaceToFaceLargeOffsetCorner(
            FaceToWallFrameTemplateKind templateKind)
        {
            return templateKind == FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner;
        }

        /// <summary>
        /// 面对面仅允许常规 024 与大偏移拐角 028；面对墙 026/027 不得混入。
        /// </summary>
        internal static bool IsFaceToFaceSupported(FaceToWallFrameTemplateKind templateKind)
        {
            return templateKind == FaceToWallFrameTemplateKind.Regular
                || IsFaceToFaceLargeOffsetCorner(templateKind);
        }

        internal static bool SupportsInternalInsulators(
            FaceToWallFrameTemplateKind templateKind)
        {
            return templateKind == FaceToWallFrameTemplateKind.Regular
                || templateKind == FaceToWallFrameTemplateKind.Large1
                || templateKind == FaceToWallFrameTemplateKind.Large2
                || IsFaceToFaceLargeOffsetCorner(templateKind);
        }
    }
}
