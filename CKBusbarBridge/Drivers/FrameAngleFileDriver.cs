using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using CKBusbarBridge.Helpers;
using CKBusbarBridge.Prototype;

namespace CKBusbarBridge.Drivers
{
    internal sealed class FrameAngleParameters
    {
        internal double Height { get; }
        internal double Width { get; }
        internal double NotchWidth { get; }
        internal bool SuppressNotch { get; }
        internal FrameAngleParameters(double height, double width, double notchWidth, bool suppress)
        {
            if (new[] { height, width, notchWidth }.Any(v => double.IsNaN(v) || double.IsInfinity(v) || v <= 0d) || height <= 45.5d)
                throw new ArgumentException("角钢目标尺寸必须为有限正数且满足架高要求。");
            Height = height; Width = width; NotchWidth = notchWidth; SuppressNotch = suppress;
        }
        internal bool Same(FrameAngleParameters other) => other != null &&
            Math.Abs(Height - other.Height) <= BridgeDesignPrecision.LengthToleranceMm &&
            Math.Abs(Width - other.Width) <= BridgeDesignPrecision.LengthToleranceMm &&
            Math.Abs(NotchWidth - other.NotchWidth) <= BridgeDesignPrecision.LengthToleranceMm && SuppressNotch == other.SuppressNotch;
        public override string ToString() => string.Format(CultureInfo.InvariantCulture,
            "架高={0:R}mm/宽度={1:R}mm/豁口={2:R}mm/豁口压缩={3}", Height, Width, NotchWidth, SuppressNotch);
    }

    internal sealed class FrameAngleFileTarget
    {
        internal string Prefix { get; }
        internal int End { get; }
        internal int ComponentId { get; }
        internal string Path { get; }
        internal FrameAngleParameters Parameters { get; }
        internal double? NotchDepthMm { get; }

        private FrameAngleFileTarget(string prefix, int end, string path, int componentId)
        {
            if (string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(path) || (end != -1 && end != 1))
                throw new ArgumentException("角钢实例缺少有效型号、端位或实际文件路径。");
            Prefix = prefix; End = end; Path = path; ComponentId = componentId;
        }

        internal FrameAngleFileTarget(string prefix, int end, string path, int componentId, FrameAngleParameters parameters)
            : this(prefix, end, path, componentId)
        {
            Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        }

        internal FrameAngleFileTarget(string prefix, int end, string path, int componentId, double depth)
            : this(prefix, end, path, componentId)
        {
            if (!BridgeAngleNotchPlan.Valid(depth)) throw new ArgumentOutOfRangeException(nameof(depth));
            NotchDepthMm = depth;
        }

        internal bool SameDrive(FrameAngleFileTarget other) => Parameters != null ? Parameters.Same(other.Parameters) :
            other.NotchDepthMm.HasValue && NotchDepthMm.HasValue && (NotchDepthMm == 0d) == (other.NotchDepthMm == 0d) &&
            Math.Abs(NotchDepthMm.Value - other.NotchDepthMm.Value) <= BridgeDesignPrecision.LengthToleranceMm;

        public override string ToString() => Prefix + "/" + End + "/ID=" + ComponentId + "/" +
            (Parameters?.ToString() ?? FormattableString.Invariant($"豁口={NotchDepthMm:0.0} mm"));
    }

    internal interface IFrameAngleFileOperations
    {
        void MakeIndependent(FrameAngleFileTarget target, FrameAngleFileTarget retained, string drawingNumber, string suffix);
        void Drive(FrameAngleFileTarget target);
    }

    internal sealed class FrameAngleFileDriver
    {
        internal void Drive(IReadOnlyList<FrameAngleFileTarget> targets, IDrawingNumberGenerator numbers, IFrameAngleFileOperations operations,
            Action<string> trace = null)
        {
            if (targets == null || operations == null) throw new ArgumentNullException();
            if (targets.Any(t => t == null) || targets.GroupBy(t => t.ComponentId).Any(g => g.Count() != 1) ||
                targets.GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 2 || g.Select(t => t.Prefix).Distinct().Count() != 1))
                throw new InvalidOperationException("角钢实例或对角文件共用关系不唯一。");
            var representatives = new List<FrameAngleFileTarget>();
            // 所有独立化完成后才写参数，保证原生复制使用未被本轮驱动覆盖的文件。
            foreach (var file in targets.GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var group = file.OrderBy(t => t.End).ThenBy(t => t.ComponentId).ToArray();
                var retained = group[0];
                representatives.Add(retained);
                trace?.Invoke("文件=" + file.Key + "；实例目标={" + string.Join("；", group.Select(t => t.ToString())) + "}");
                if (group.Length == 1) continue;
                var target = group[1];
                if (retained.SameDrive(target))
                { trace?.Invoke("对角参数一致，继续共用：" + file.Key + "/" + target.End); continue; }
                if (numbers == null) throw new InvalidOperationException("角钢参数分歧需要独立化，但没有共享图号生成器。");
                var number = numbers.PeekNextDrawingNumber();
                trace?.Invoke("对角参数不同，原生使之独立：" + file.Key + "/" + target.End + "；新图号=" + number);
                operations.MakeIndependent(target, retained, number, numbers.PeekNextFileNameSuffix());
                numbers.CommitNext(number);
                representatives.Add(target);
            }
            foreach (var target in representatives) operations.Drive(target);
        }
    }
}
