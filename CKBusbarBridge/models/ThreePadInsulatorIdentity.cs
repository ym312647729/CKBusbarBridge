using System.Globalization;

namespace CKBusbarBridge.Prototype
{
    internal static class ThreePadInsulatorIdentity
    {
        internal static string DirectoryName(double thickness, double spacing, double height, double hostSpan, int roots)
            => string.Format(CultureInfo.InvariantCulture,
                "T{0:0.###}-P{1:0.###}-H{2:0.###}-W{3:0.###}-R{4}", thickness, spacing, height, hostSpan - 20, roots);
    }
}
