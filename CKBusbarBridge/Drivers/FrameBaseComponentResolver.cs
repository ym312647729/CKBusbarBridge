using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using System;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 解析首段基架所连接的 B 柜基座组件；装配器只依赖该边界，不感知场景映射差异。
    /// </summary>
    internal interface IFrameBaseComponentResolver
    {
        bool TryResolve(
            string mappingJson,
            string positionName,
            out string componentFullName);

        string Describe(string positionName);
    }

    /// <summary>
    /// 面对面场景的既有普通基座解析策略。
    /// </summary>
    internal sealed class FaceToFaceFrameBaseComponentResolver
        : IFrameBaseComponentResolver
    {
        public bool TryResolve(
            string mappingJson,
            string positionName,
            out string componentFullName)
        {
            return FaceToFaceFrameModelMappingHelper
                .TryGetOrdinaryBaseComponentFullName(
                    mappingJson,
                    positionName,
                    out componentFullName);
        }

        public string Describe(string positionName)
        {
            return NormalizePositionName(positionName) + "普通基座";
        }

        private static string NormalizePositionName(string positionName)
        {
            return string.IsNullOrWhiteSpace(positionName)
                ? "B柜"
                : positionName.Trim();
        }
    }

    /// <summary>
    /// 侧对侧场景的基座1/基座2解析策略；两者都读取映射中的“普通基座”节点，
    /// 但错误描述保留真实基座类型，避免把基座2误报成增高基座。
    /// </summary>
    internal sealed class SideToSideFrameBaseComponentResolver
        : IFrameBaseComponentResolver
    {
        private readonly SideToSideBaseModelKind _baseKind;

        internal SideToSideFrameBaseComponentResolver(
            SideToSideBaseModelKind baseKind)
        {
            if (baseKind != SideToSideBaseModelKind.Base1
                && baseKind != SideToSideBaseModelKind.Base2)
            {
                throw new ArgumentOutOfRangeException(nameof(baseKind));
            }

            _baseKind = baseKind;
        }

        public bool TryResolve(
            string mappingJson,
            string positionName,
            out string componentFullName)
        {
            return SideToSideBaseModelMappingHelper.TryGetComponentFullName(
                mappingJson,
                positionName,
                _baseKind,
                out componentFullName);
        }

        public string Describe(string positionName)
        {
            var normalizedPosition = string.IsNullOrWhiteSpace(positionName)
                ? "B柜"
                : positionName.Trim();
            return normalizedPosition
                + (_baseKind == SideToSideBaseModelKind.Base1
                    ? "基座1"
                    : "基座2");
        }
    }

    internal sealed class FaceToWallFrameBaseComponentResolver : IFrameBaseComponentResolver
    {
        public bool TryResolve(string mappingJson, string positionName, out string componentFullName)
        {
            if (positionName != FaceToWallBaseDrivePlanner.FrontPositionName && positionName != FaceToWallBaseDrivePlanner.RearPositionName)
                throw new ArgumentException("面对墙基座位置身份无效。", nameof(positionName));
            return FaceToFaceBaseModelMappingHelper.TryGetComponentFullName(mappingJson, positionName, false, out componentFullName);
        }
        public string Describe(string positionName) => positionName + "普通基座";
    }
}
