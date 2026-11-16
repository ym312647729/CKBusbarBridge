using CKBusbarBridge.models;
using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 面对面界面使用的单个逻辑基架段；大偏移物理组在这里折叠为一个段。
    /// </summary>
    internal sealed class FaceToFaceLogicalFrameSegment
    {
        internal FaceToFaceLogicalFrameSegment(
            int logicalIndex,
            double length,
            IReadOnlyList<FrameItem> physicalFrames,
            string largeOffsetGroupId)
        {
            LogicalIndex = logicalIndex;
            Length = length;
            PhysicalFrames = physicalFrames;
            LargeOffsetGroupId = largeOffsetGroupId;
        }

        internal int LogicalIndex { get; }

        internal double Length { get; }

        internal IReadOnlyList<FrameItem> PhysicalFrames { get; }

        internal string LargeOffsetGroupId { get; }

        internal bool IsLargeOffsetGroup => !string.IsNullOrWhiteSpace(LargeOffsetGroupId);
    }

    /// <summary>
    /// 将平铺的物理基架列表投影为界面和母排断点使用的逻辑段列表。
    /// </summary>
    internal static class FaceToFaceFrameLogicalProjection
    {
        /// <summary>
        /// 编辑时允许连接段暂未闭合；生成前使用实际物理尺寸检查柜间偏心值。
        /// </summary>
        internal static bool TryValidateOffsetLength(
            IReadOnlyList<FrameItem> frames,
            double signedEccentricity,
            out string validationMessage)
        {
            validationMessage = null;
            var members = frames.Where(frame => frame?.IsLargeOffsetMember == true).ToList();
            var bCorners = members.Where(frame => frame.LargeOffsetRole == FaceToFaceLargeOffsetFrameRole.BSideCorner).ToList();
            var aCorners = members.Where(frame => frame.LargeOffsetRole == FaceToFaceLargeOffsetFrameRole.ASideCorner).ToList();
            var connectors = members.Where(frame => frame.LargeOffsetRole == FaceToFaceLargeOffsetFrameRole.Connector).ToList();
            if (bCorners.Count != 1 || aCorners.Count != 1 || connectors.Count == 0
                || members.Select(frame => frame.LargeOffsetGroupId).Distinct(StringComparer.Ordinal).Count() != 1)
            {
                validationMessage = "大偏移组必须包含左右两个拐角基架和至少一个连接基架。";
                return false;
            }

            var dimensions = connectors.Select(frame => frame.Length)
                .Concat(new[] { bCorners[0].Width, aCorners[0].Width, Math.Abs(signedEccentricity) });
            if (dimensions.Any(value => value <= 0d || double.IsNaN(value) || double.IsInfinity(value)))
            {
                validationMessage = "连接基架长度、拐角基架宽度和柜间偏心值必须为有效的正数。";
                return false;
            }

            var connectorLength = connectors.Sum(frame => frame.Length);
            var cornerHalfWidths = (bCorners[0].Width + aCorners[0].Width) / 2d;
            var eccentricity = Math.Abs(signedEccentricity);
            var difference = connectorLength + cornerHalfWidths - eccentricity;
            if (double.IsInfinity(difference) || Math.Abs(difference) > 0.01d)
            {
                validationMessage = $"连接基架长度总和（{connectorLength:0.###} mm）+ "
                    + $"左右拐角基架宽度和的一半（{cornerHalfWidths:0.###} mm）"
                    + $"必须等于柜间偏心值（{eccentricity:0.###} mm）；"
                    + $"当前差值为 {difference:0.###} mm，请调整连接基架长度后再生成。";
                return false;
            }

            return true;
        }

        internal static IReadOnlyList<FaceToFaceLogicalFrameSegment> Project(
            IReadOnlyList<FrameItem> physicalFrames)
        {
            if (physicalFrames == null)
            {
                throw new ArgumentNullException(nameof(physicalFrames));
            }

            var groupIds = physicalFrames
                .Where(frame => frame?.IsLargeOffsetMember == true)
                .Select(frame => frame.LargeOffsetGroupId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (groupIds.Count > 1)
            {
                throw new InvalidOperationException("当前仅支持一个连续的面对面大偏移基架组。");
            }

            var result = new List<FaceToFaceLogicalFrameSegment>();
            var index = 0;
            while (index < physicalFrames.Count)
            {
                var frame = physicalFrames[index]
                    ?? throw new InvalidOperationException("物理基架列表中不能包含空项。");
                if (!frame.IsLargeOffsetMember)
                {
                    if (frame.LargeOffsetRole != FaceToFaceLargeOffsetFrameRole.None)
                    {
                        throw new InvalidOperationException("大偏移物理基架缺少组标识。");
                    }

                    result.Add(new FaceToFaceLogicalFrameSegment(
                        result.Count,
                        frame.Length,
                        new[] { frame },
                        null));
                    index++;
                    continue;
                }

                var groupId = frame.LargeOffsetGroupId;
                var members = new List<FrameItem>();
                while (index < physicalFrames.Count
                    && string.Equals(
                        physicalFrames[index]?.LargeOffsetGroupId,
                        groupId,
                        StringComparison.Ordinal))
                {
                    members.Add(physicalFrames[index]);
                    index++;
                }

                ValidateGroup(members, groupId);
                result.Add(new FaceToFaceLogicalFrameSegment(
                    result.Count,
                    members[0].Length,
                    members,
                    groupId));
            }

            return result;
        }

        private static void ValidateGroup(IReadOnlyList<FrameItem> members, string groupId)
        {
            if (members.Count < 3
                || members[0].LargeOffsetRole != FaceToFaceLargeOffsetFrameRole.BSideCorner
                || members[members.Count - 1].LargeOffsetRole != FaceToFaceLargeOffsetFrameRole.ASideCorner
                || members.Skip(1).Take(members.Count - 2)
                    .Any(frame => frame.LargeOffsetRole != FaceToFaceLargeOffsetFrameRole.Connector))
            {
                throw new InvalidOperationException(
                    $"大偏移组“{groupId}”必须按 B 柜侧拐角、一个或多个连接基架、A 柜侧拐角连续排列。");
            }

            var sharedLength = members[0].Length;
            if (Math.Abs(members[members.Count - 1].Length - sharedLength) > 0.001
                || members.Skip(1).Take(members.Count - 2)
                    .Any(frame => Math.Abs(frame.Width - sharedLength) > 0.001))
            {
                throw new InvalidOperationException(
                    $"大偏移组“{groupId}”的拐角长度与连接基架宽度必须一致。");
            }
        }
    }
}
