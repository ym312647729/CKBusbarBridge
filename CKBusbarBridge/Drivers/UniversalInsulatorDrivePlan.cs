using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    internal interface IUniversalInsulatorDriveOperations
    {
        void Prepare(UniversalInsulatorDrivePlan plan);
        void WriteTopVariables(UniversalInsulatorDrivePlan plan);
        void WriteSharedInsulator(UniversalInsulatorDrivePlan plan);
        void WriteSharedPad(UniversalInsulatorDrivePlan plan);
        void ConfigurePadInstances(UniversalInsulatorDrivePlan plan);
        void CompleteAndValidate(UniversalInsulatorDrivePlan plan);
    }

    internal sealed class UniversalInsulatorDrivePlan
    {
        internal UniversalInsulatorLayout Layout { get; }
        internal bool UseLBracket { get; }
        internal double BodyHeight { get; }
        internal double Thickness { get; }
        internal bool UseStackedPads => Math.Abs(Thickness - 12d) < UniversalInsulatorLayout.ToleranceMm;
        internal double PadThickness => UseStackedPads ? 6d : Thickness;
        internal IReadOnlyList<int> EnabledPadIndexes { get; }
        internal IReadOnlyList<int> SuppressedGroups { get; }

        internal UniversalInsulatorDrivePlan(UniversalInsulatorLayout layout, bool useLBracket,
            double bodyHeight, double thickness, int rootCount, IEnumerable<int> suppressedGroups)
        {
            Layout = layout ?? throw new ArgumentNullException(nameof(layout));
            if (double.IsNaN(bodyHeight) || double.IsInfinity(bodyHeight) || bodyHeight <= 0
                || double.IsNaN(thickness) || double.IsInfinity(thickness) || thickness <= 0)
                throw new ArgumentException("绝缘子本体高度和排厚必须为有限正数。");
            if (rootCount < 1 || rootCount > 4) throw new ArgumentOutOfRangeException(nameof(rootCount));
            var suppressed = (suppressedGroups ?? Enumerable.Empty<int>()).Distinct().OrderBy(v => v).ToArray();
            if (suppressed.Any(v => v < 1 || v > 3) || suppressed.Length == 3)
                throw new ArgumentException("通用绝缘子必须保留至少一个有效绝缘子组。", nameof(suppressedGroups));
            UseLBracket = useLBracket; BodyHeight = bodyHeight; Thickness = thickness;
            // 延续原距离配合链角色，适配器按配合识别实际实例，不能依赖重编号后的尾号。
            EnabledPadIndexes = Array.AsReadOnly(new[] { 1, 3, 2 }.Take(rootCount - 1).ToArray());
            SuppressedGroups = Array.AsReadOnly(suppressed);
        }

        internal void Drive(IUniversalInsulatorDriveOperations operations)
        {
            if (operations == null) throw new ArgumentNullException(nameof(operations));
            operations.Prepare(this);
            operations.WriteTopVariables(this);
            operations.WriteSharedInsulator(this);
            operations.WriteSharedPad(this);
            operations.ConfigurePadInstances(this);
            operations.CompleteAndValidate(this);
        }
    }
}
