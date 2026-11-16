using SolidWorks.Interop.sldworks;

namespace CKBusbarBridge.Helpers
{
    public static class TransformHelper
    {
        public static MathTransform TranslateTransformX(ISldWorks swApp, MathTransform baseTransform, double moveXmm)
        {
            return TranslateTransformXY(swApp, baseTransform, moveXmm, 0);
        }

        public static MathTransform TranslateTransformXY(ISldWorks swApp, MathTransform baseTransform, double moveXmm, double moveYmm)
        {
            return TranslateTransformXYZ(swApp, baseTransform, moveXmm, moveYmm, 0);
        }

        public static MathTransform TranslateTransformXYZ(ISldWorks swApp, MathTransform baseTransform, double moveXmm, double moveYmm, double moveZmm)
        {
            if (swApp == null) return baseTransform;
            if (baseTransform == null) return null;

            // SolidWorks MathTransform 平移单位为“米”
            var moveXm = moveXmm / 1000.0;
            var moveYm = moveYmm / 1000.0;
            var moveZm = moveZmm / 1000.0;

            var mathUtil = swApp.GetMathUtility() as IMathUtility;
            if (mathUtil == null) return baseTransform;

            var data = new double[16];
            data[0] = 1;
            data[4] = 1;
            data[8] = 1;
            data[9] = moveXm;
            data[10] = moveYm;
            data[11] = moveZm;
            data[12] = 1;

            var moveTf = mathUtil.CreateTransform(data) as MathTransform;
            if (moveTf == null) return baseTransform;

            return (MathTransform)baseTransform.Multiply(moveTf);
        }
    }
}
