using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 面对墙基架规划输入。常规基架的绝缘子布置进入模型身份；
    /// 026/027同样保存有效支撑套数和定位，不将大基架布局静默清零。
    /// </summary>
    internal sealed class FaceToWallFramePlanInput
    {
        internal FaceToWallFramePlanInput(
            string sourceName,
            double width,
            double length,
            double height,
            bool isReversed,
            bool useLargeFrameTemplate)
            : this(
                sourceName,
                width,
                length,
                height,
                isReversed,
                useLargeFrameTemplate
                    ? FaceToWallFrameTemplateKind.Large1
                    : FaceToWallFrameTemplateKind.Regular,
                0,
                Array.Empty<double>())
        {
        }

        internal FaceToWallFramePlanInput(
            string sourceName,
            double width,
            double length,
            double height,
            bool isReversed,
            FaceToWallFrameTemplateKind templateKind)
            : this(
                sourceName,
                width,
                length,
                height,
                isReversed,
                templateKind,
                0,
                Array.Empty<double>())
        {
        }

        internal FaceToWallFramePlanInput(
            string sourceName,
            double width,
            double length,
            double height,
            bool isReversed,
            bool useLargeFrameTemplate,
            int insulatorCount,
            IEnumerable<double> insulatorPositions)
            : this(
                sourceName,
                width,
                length,
                height,
                isReversed,
                useLargeFrameTemplate
                    ? FaceToWallFrameTemplateKind.Large1
                    : FaceToWallFrameTemplateKind.Regular,
                insulatorCount,
                insulatorPositions)
        {
        }

        internal FaceToWallFramePlanInput(
            string sourceName,
            double width,
            double length,
            double height,
            bool isReversed,
            FaceToWallFrameTemplateKind templateKind,
            int insulatorCount,
            IEnumerable<double> insulatorPositions)
            : this(
                sourceName,
                width,
                length,
                height,
                isReversed,
                templateKind,
                insulatorCount,
                insulatorPositions,
                0d,
                0d,
                0d)
        {
        }

        internal FaceToWallFramePlanInput(
            string sourceName,
            double width,
            double length,
            double height,
            bool isReversed,
            FaceToWallFrameTemplateKind templateKind,
            int insulatorCount,
            IEnumerable<double> insulatorPositions,
            double wallHorizontalClearance,
            double wallVerticalClearance,
            double wallEntryDistance)
        {
            SourceName = sourceName;
            Width = width;
            Length = length;
            Height = height;
            IsReversed = isReversed;
            TemplateKind = templateKind;
            InsulatorCount = insulatorCount;
            InsulatorPositions = (insulatorPositions ?? Array.Empty<double>()).ToArray();
            WallHorizontalClearance = wallHorizontalClearance;
            WallVerticalClearance = wallVerticalClearance;
            WallEntryDistance = wallEntryDistance;
        }

        internal string SourceName { get; }

        internal double Width { get; }

        internal double Length { get; }

        internal double Height { get; }

        internal bool IsReversed { get; }

        internal FaceToWallFrameTemplateKind TemplateKind { get; }

        internal bool UseLargeFrameTemplate => TemplateKind != FaceToWallFrameTemplateKind.Regular;

        internal int InsulatorCount { get; }

        internal IReadOnlyList<double> InsulatorPositions { get; }

        internal double WallHorizontalClearance { get; }

        internal double WallVerticalClearance { get; }

        internal double WallEntryDistance { get; }
    }
}
