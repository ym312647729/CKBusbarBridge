using GeometRi;

namespace RouteLinkBar.Algorithm.Geometry
{
    /// <summary>
    /// 算法程序集内部统一的 GeometRi 值复制与基础向量运算。
    /// 保持纯函数，不读取或修改 GeometRi3D 的全局容差。
    /// </summary>
    internal static class GeometRiMethod
    {
        internal static Point3d Copy(Point3d sourcePoint)
        {
            return new Point3d(
                sourcePoint.X,
                sourcePoint.Y,
                sourcePoint.Z);
        }

        internal static Vector3d Copy(Vector3d sourceVector)
        {
            return new Vector3d(
                sourceVector.X,
                sourceVector.Y,
                sourceVector.Z);
        }

        internal static Point3d Translate(
            Point3d sourcePoint,
            Vector3d direction,
            double distance)
        {
            return new Point3d(
                sourcePoint.X + direction.X * distance,
                sourcePoint.Y + direction.Y * distance,
                sourcePoint.Z + direction.Z * distance);
        }

        internal static Point3d TranslateAlongDirection(
            Point3d sourcePoint,
            Vector3d direction,
            double distance)
        {
            return Translate(sourcePoint, direction.Normalized, distance);
        }

        internal static Vector3d Add(
            Vector3d firstVector,
            Vector3d secondVector)
        {
            return new Vector3d(
                firstVector.X + secondVector.X,
                firstVector.Y + secondVector.Y,
                firstVector.Z + secondVector.Z);
        }

        internal static Vector3d Subtract(
            Vector3d firstVector,
            Vector3d secondVector)
        {
            return new Vector3d(
                firstVector.X - secondVector.X,
                firstVector.Y - secondVector.Y,
                firstVector.Z - secondVector.Z);
        }

        internal static Vector3d Scale(
            Vector3d sourceVector,
            double scaleFactor)
        {
            return new Vector3d(
                sourceVector.X * scaleFactor,
                sourceVector.Y * scaleFactor,
                sourceVector.Z * scaleFactor);
        }
    }
}
