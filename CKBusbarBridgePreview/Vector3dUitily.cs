using GeometRi;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CKBusbarBridgePreview
{
    public static class Vector3dUtility
    {
        public static Vector3d Vector3dToZero(this Vector3d v, double d = 0.001)
        {
            return new Vector3d(Math.Abs(v.X) > d ? Math.Round(v.X, 3) : 0,
                Math.Abs(v.Y) > d ? Math.Round(v.Y, 3) : 0, Math.Abs(v.Z) > d ? Math.Round(v.Z, 3) : 0);
        }

        public static double[] ToArrayData(this Vector3d vector3D)
        {
            return new[]
            {
               vector3D.X, vector3D.Y, vector3D.Z
           };
        }

        /// <summary>
        /// 向量取反
        /// </summary>
        /// <param name="vector3D"></param>
        /// <returns></returns>
        public static Vector3d Negate(this Vector3d vector3D)
        {
            if (vector3D != null)
            {
                return new Vector3d(vector3D.X * -1, vector3D.Y * -1, vector3D.Z * -1);

            }
            else
            {
                return vector3D;

            }

        }

        /// <summary>
        /// 判断两个向量是否相等
        /// </summary>
        /// <param name="varVec1"></param>
        /// <param name="varVec2"></param>
        /// <returns></returns>
        public static bool VectorNormalizedEqual(this Vector3d varVec1, Vector3d varVec2)
        {
            return varVec1.Normalized.Equals(varVec2.Normalized);
        }
    }
}
