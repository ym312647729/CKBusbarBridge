using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    internal interface ILBracketInsulatorDriveOperations
    {
        void Prepare();
        void DriveParts(LBracketInsulatorDrivePlan plan);
        void DrivePadAssemblies(LBracketInsulatorDrivePlan plan);
        void DriveAssembly(LBracketInsulatorDrivePlan plan);
        LBracketInsulatorReadback Readback();
    }

    /// <summary>经模板实测确认的写入顺序及验收条件；不依赖 SolidWorks。</summary>
    internal sealed class LBracketInsulatorDrivePlan
    {
        internal const double ToleranceMm = .01d;
        internal double PhaseSpacing { get; }
        internal double Thickness { get; }
        internal bool UseStackedPads => Math.Abs(Thickness - 12d) < ToleranceMm;
        internal double PadThickness => UseStackedPads ? 6d : Thickness;
        internal double BodyHeight { get; }
        internal double BracketLength { get; }
        internal IReadOnlyList<int> EnabledPadIndexes { get; }
        internal IReadOnlyList<int> SuppressedSupportIndexes { get; }

        internal LBracketInsulatorDrivePlan(double phaseSpacing, double thickness, double bodyHeight,
            double bracketLength, int rootCount, IEnumerable<int> suppressedSupportIndexes)
        {
            if (new[] { phaseSpacing, thickness, bodyHeight, bracketLength }.Any(v => !Finite(v) || v <= 0d))
                throw new ArgumentException("L型绝缘子的相间距、排厚、本体高度和支架长度必须为有限正数。");
            if (rootCount < 1 || rootCount > 4)
                throw new ArgumentOutOfRangeException(nameof(rootCount), "L型绝缘子单相根数必须在1到4之间。");
            var suppressed = (suppressedSupportIndexes ?? Enumerable.Empty<int>()).Distinct().OrderBy(i => i).ToArray();
            if (suppressed.Any(i => i < 1 || i > 3) || suppressed.Length == 3)
                throw new ArgumentException("L型绝缘子安装位置必须保留至少一个有效支承。", nameof(suppressedSupportIndexes));
            PhaseSpacing = phaseSpacing; Thickness = thickness; BodyHeight = bodyHeight; BracketLength = bracketLength;
            // 实际距离配合链是1→3→2，不能按COM枚举顺序或实例编号递增取前N−1片。
            EnabledPadIndexes = Array.AsReadOnly(new[] { 1, 3, 2 }.Take(rootCount - 1).ToArray());
            SuppressedSupportIndexes = Array.AsReadOnly(suppressed);
        }

        internal void Drive(ILBracketInsulatorDriveOperations operations)
        {
            if (operations == null) throw new ArgumentNullException(nameof(operations));
            operations.Prepare();
            operations.DriveParts(this);
            operations.DrivePadAssemblies(this);
            operations.DriveAssembly(this);
            Validate(operations.Readback());
        }

        internal void Validate(LBracketInsulatorReadback actual)
        {
            if (actual == null) throw new InvalidOperationException("L型绝缘子缺少驱动后反读结果。");
            RequireValue("顶层/相间距", actual.TopPhaseSpacing, PhaseSpacing);
            RequireValue("顶层/支架长度", actual.TopBracketLength, BracketLength);
            RequireValue("支架零件/相间距", actual.BracketPhaseSpacing, PhaseSpacing);
            RequireValue("支架零件/D3@阵列(线性)1", actual.BracketPitch1, PhaseSpacing);
            RequireValue("支架零件/D4@阵列(线性)1", actual.BracketPitch2, PhaseSpacing);
            RequireValue("支架零件/D2@草图2", actual.BracketLength, BracketLength);
            RequireValue("支架零件/固定板厚", actual.BracketThickness, 3d);
            if (actual.PadAssemblies == null || actual.PadAssemblies.Count == 0 ||
                actual.PadAssemblies.Any(p => p == null || string.IsNullOrWhiteSpace(p.Identity)) ||
                actual.PadAssemblies.GroupBy(p => p.Identity, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1))
                throw new InvalidOperationException("L型绝缘子垫块子装配体反读身份缺失或重复。");
            foreach (var pad in actual.PadAssemblies)
            {
                RequireValue(pad.Identity + "/绝缘子高度", pad.BodyHeight, BodyHeight);
                RequireValue(pad.Identity + "/排厚", pad.Thickness, Thickness);
                RequireValue(pad.Identity + "/D1@距离1", pad.Distance1, Thickness);
                RequireValue(pad.Identity + "/D1@距离2", pad.Distance2, Thickness * 2d);
                RequireValue(pad.Identity + "/D1@距离3", pad.Distance3, Thickness * 2d);
                if (UseStackedPads && !pad.StackPatternPresent)
                    throw new InvalidOperationException("L型12厚绝缘子缺少排厚12阵列：" + pad.Identity);
                if (pad.StackPatternPresent)
                {
                    var enableStackPattern = UseStackedPads && EnabledPadIndexes.Count > 0;
                    if (pad.StackPatternSuppressed == enableStackPattern
                        || pad.ActiveStackCopies != (UseStackedPads ? EnabledPadIndexes.Count : 0))
                        throw new InvalidOperationException("L型绝缘子排厚12阵列状态或叠加数量不一致：" + pad.Identity
                            + "；实际副本=" + pad.ActiveStackCopies + "；期望副本=" + (UseStackedPads ? EnabledPadIndexes.Count : 0));
                    if (UseStackedPads) RequireValue(pad.Identity + "/排厚12阵列间距", pad.StackSpacing, 6d);
                }
                if (pad.Members == null || pad.Members.Any(p => p == null || string.IsNullOrWhiteSpace(p.Identity)))
                    throw new InvalidOperationException("L型垫块组件反读身份缺失：" + pad.Identity);
                RequireIndexes(pad.Members.Select(p => p.Index), pad.Identity + "/垫块组件");
                foreach (var member in pad.Members)
                {
                    if (member.Suppressed == EnabledPadIndexes.Contains(member.Index))
                        throw new InvalidOperationException(pad.Identity + "/垫块<" + member.Index + ">压缩状态与N−1片规则不一致。");
                    RequireValue(member.Identity + "/基体-法兰1厚度", member.BaseThickness, PadThickness);
                    RequireValue(member.Identity + "/钣金8厚度", member.SheetThickness, PadThickness);
                    RequireValue(member.Identity + "/实体平面间距", member.PhysicalThickness, PadThickness);
                }
            }
            if (actual.Supports == null || actual.Supports.Any(s => s == null))
                throw new InvalidOperationException("L型绝缘子安装位置反读缺失。");
            RequireIndexes(actual.Supports.Select(s => s.Index), "顶层绝缘子子总成");
            foreach (var support in actual.Supports)
            {
                if (support.Suppressed != SuppressedSupportIndexes.Contains(support.Index))
                    throw new InvalidOperationException("L型绝缘子顶层<" + support.Index + ">安装布局与要求不一致。");
                if (support.Suppressed) continue;
                if (!actual.PadAssemblies.Any(p => string.Equals(p.Identity, support.PadAssemblyIdentity, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("L型绝缘子安装位置未唯一绑定已校验的垫块子装配体：" + support.Index);
                RequireValue("安装位置<" + support.Index + ">/连接圆X", support.X, 0d);
                RequireValue("安装位置<" + support.Index + ">/连接圆Y", support.Y, BodyHeight);
                RequireValue("安装位置<" + support.Index + ">/连接圆Z", support.Z, (2 - support.Index) * PhaseSpacing);
                RequireValue("安装位置<" + support.Index + ">/连接圆半径", support.Radius, 50d);
            }
        }

        internal void RequireValue(string source, double actual, double expected)
        {
            if (!Finite(actual) || Math.Abs(actual - expected) > ToleranceMm)
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "L型绝缘子驱动后反读不一致：{0}，实际={1:0.###} mm，期望={2:0.###} mm。", source, actual, expected));
        }

        private static void RequireIndexes(IEnumerable<int> indexes, string source)
        {
            if (indexes == null || !indexes.OrderBy(i => i).SequenceEqual(new[] { 1, 2, 3 }))
                throw new InvalidOperationException("L型绝缘子" + source + "必须逐件唯一反读1、2、3号实例。");
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    internal sealed class LBracketInsulatorReadback
    {
        internal double TopPhaseSpacing, TopBracketLength, BracketPhaseSpacing, BracketPitch1, BracketPitch2,
            BracketLength, BracketThickness;
        internal IReadOnlyList<LBracketPadAssemblyReadback> PadAssemblies;
        internal IReadOnlyList<LBracketSupportReadback> Supports;
    }

    internal sealed class LBracketPadAssemblyReadback
    {
        internal string Identity;
        internal double BodyHeight, Thickness, Distance1, Distance2, Distance3;
        internal bool StackPatternPresent, StackPatternSuppressed;
        internal double StackSpacing;
        internal int ActiveStackCopies;
        internal IReadOnlyList<LBracketPadMemberReadback> Members;
    }

    internal sealed class LBracketPadMemberReadback
    {
        internal int Index;
        internal string Identity;
        internal bool Suppressed;
        internal double BaseThickness, SheetThickness, PhysicalThickness;
    }

    internal sealed class LBracketSupportReadback
    {
        internal int Index;
        internal string PadAssemblyIdentity;
        internal bool Suppressed;
        internal double X, Y, Z, Radius;
    }
}
