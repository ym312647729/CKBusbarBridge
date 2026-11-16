using System;
using System.IO;

namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 集中维护柜体朝向的属性值和模板目录映射，避免各调用点拼写不一致。
    /// </summary>
    public static class FaceToWallCabinetOrientationDefaults
    {
        public const string CabinetOrientationCustomPropertyName = "柜体朝向";
        public const string MainAssemblyFileName = "面对墙柜体装配.SLDASM";

        public static string GetDisplayName(FaceToWallCabinetOrientation orientation)
        {
            switch (orientation)
            {
                case FaceToWallCabinetOrientation.CabinetFrontToWall:
                    return "柜前向墙";
                case FaceToWallCabinetOrientation.CabinetRearToWall:
                    return "柜后向墙";
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(orientation),
                        orientation,
                        "不支持的面对墙柜体朝向。");
            }
        }

        /// <summary>
        /// 返回当前柜体朝向在驱动界面中显示的 A 参数名称。
        /// </summary>
        public static string GetWallDistanceDisplayName(FaceToWallCabinetOrientation orientation)
        {
            switch (orientation)
            {
                case FaceToWallCabinetOrientation.CabinetFrontToWall:
                    return "柜前距墙(A)";
                case FaceToWallCabinetOrientation.CabinetRearToWall:
                    return "柜后距墙(A)";
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(orientation),
                        orientation,
                        "不支持的面对墙柜体朝向。");
            }
        }

        /// <summary>
        /// 返回当前柜体朝向对应的总装配体全局变量名称。
        /// </summary>
        public static string GetWallDistanceGlobalVariableName(FaceToWallCabinetOrientation orientation)
        {
            switch (orientation)
            {
                case FaceToWallCabinetOrientation.CabinetFrontToWall:
                    return "柜前距墙距离";
                case FaceToWallCabinetOrientation.CabinetRearToWall:
                    return "柜后距墙距离";
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(orientation),
                        orientation,
                        "不支持的面对墙柜体朝向。");
            }
        }

        public static bool TryParse(
            string value,
            out FaceToWallCabinetOrientation orientation)
        {
            if (string.Equals(value?.Trim(), "柜前向墙", StringComparison.Ordinal))
            {
                orientation = FaceToWallCabinetOrientation.CabinetFrontToWall;
                return true;
            }

            if (string.Equals(value?.Trim(), "柜后向墙", StringComparison.Ordinal))
            {
                orientation = FaceToWallCabinetOrientation.CabinetRearToWall;
                return true;
            }

            orientation = default(FaceToWallCabinetOrientation);
            return false;
        }

        public static string GetTemplateFolderName(FaceToWallCabinetOrientation orientation)
        {
            return "面对墙模板(" + GetDisplayName(orientation) + ")";
        }

        public static string GetTemplateDirectoryPath(
            string templateRoot,
            FaceToWallCabinetOrientation orientation)
        {
            if (string.IsNullOrWhiteSpace(templateRoot))
            {
                throw new ArgumentException("模板根目录不能为空。", nameof(templateRoot));
            }

            return Path.Combine(templateRoot, GetTemplateFolderName(orientation));
        }
    }
}
