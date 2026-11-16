using System;
using System.Globalization;
using System.Linq;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// 图号（合同号.四位流水号）解析、格式化与递增。
    /// </summary>
    public static class DrawingNumberHelper
    {
        /// <summary>按最后一个“.”拆分：前缀为合同号，后缀为四位数字流水号。</summary>
        public static bool TryParse(string raw, out string contractPrefix, out int serial)
        {
            contractPrefix = null;
            serial = 0;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var trimmed = raw.Trim().ToUpperInvariant();
            var lastDot = trimmed.LastIndexOf('.');
            if (lastDot <= 0 || lastDot >= trimmed.Length - 1)
            {
                return false;
            }

            var prefix = trimmed.Substring(0, lastDot).Trim();
            var serialText = trimmed.Substring(lastDot + 1).Trim();
            if (serialText.Length != 4 || !serialText.All(char.IsDigit))
            {
                return false;
            }

            if (!IsValidContractBody(prefix))
            {
                return false;
            }

            if (!int.TryParse(serialText, NumberStyles.None, CultureInfo.InvariantCulture, out serial))
            {
                return false;
            }

            contractPrefix = prefix;
            return true;
        }

        public static string Format(string contractPrefix, int serial)
        {
            if (string.IsNullOrWhiteSpace(contractPrefix))
            {
                throw new ArgumentException("合同号前缀不能为空。", nameof(contractPrefix));
            }

            if (serial < 0 || serial > 9999)
            {
                throw new ArgumentOutOfRangeException(nameof(serial), serial, "图号流水号须在 0000~9999 范围内。");
            }

            return $"{contractPrefix.Trim().ToUpperInvariant()}.{serial.ToString("D4", CultureInfo.InvariantCulture)}";
        }

        /// <summary>
        /// 在基准图号上偏移流水号（delta 可为 0）。
        /// </summary>
        public static string Offset(string baseDrawingNumber, int delta)
        {
            if (!TryParse(baseDrawingNumber, out var prefix, out var serial))
            {
                throw new FormatException($"图号格式无效，应为：合同号.四位流水号（当前：{baseDrawingNumber}）");
            }

            var next = serial + delta;
            if (next < 0 || next > 9999)
            {
                throw new InvalidOperationException($"图号流水号超出范围：{Format(prefix, next)}");
            }

            return Format(prefix, next);
        }

        /// <summary>PackAndGo / 文件改名用的后缀（含前导点）。</summary>
        public static string ToFileNameSuffix(string drawingNumber)
        {
            if (string.IsNullOrWhiteSpace(drawingNumber))
            {
                throw new ArgumentException("图号不能为空。", nameof(drawingNumber));
            }

            return "." + drawingNumber.Trim().ToUpperInvariant();
        }

        /// <summary>
        /// 铜排零件文件名（不含扩展名）：8+图号。
        /// </summary>
        public static string ToBusbarPartFileName(string drawingNumber)
        {
            if (string.IsNullOrWhiteSpace(drawingNumber))
            {
                throw new ArgumentException("图号不能为空。", nameof(drawingNumber));
            }

            return "8" + drawingNumber.Trim().ToUpperInvariant();
        }

        /// <summary>
        /// 铜排装配体文件名（不含扩展名）：6+图号。
        /// </summary>
        public static string ToBusbarAssemblyFileName(string drawingNumber)
        {
            if (string.IsNullOrWhiteSpace(drawingNumber))
            {
                throw new ArgumentException("图号不能为空。", nameof(drawingNumber));
            }

            return "6" + drawingNumber.Trim().ToUpperInvariant();
        }

        /// <summary>
        /// 总装配体文件名（不含扩展名）：5+图号。
        /// </summary>
        public static string ToMainAssemblyFileName(string drawingNumber)
        {
            if (string.IsNullOrWhiteSpace(drawingNumber))
            {
                throw new ArgumentException("图号不能为空。", nameof(drawingNumber));
            }

            return "5" + drawingNumber.Trim().ToUpperInvariant();
        }

        private static bool IsValidContractBody(string candidate)
        {
            return !string.IsNullOrEmpty(candidate)
                && candidate.Length >= 6
                && candidate.All(char.IsLetterOrDigit);
        }
    }

    /// <summary>
    /// 按段分配图号：同一段内共用，段序号每 +1 流水号 +1。
    /// </summary>
    public sealed class DrawingNumberAllocator
    {
        private readonly string _baseDrawingNumber;

        public DrawingNumberAllocator(string baseDrawingNumber)
        {
            if (string.IsNullOrWhiteSpace(baseDrawingNumber))
            {
                throw new ArgumentException("起始图号不能为空。", nameof(baseDrawingNumber));
            }

            if (!DrawingNumberHelper.TryParse(baseDrawingNumber, out _, out _))
            {
                throw new FormatException($"起始图号格式无效：{baseDrawingNumber}");
            }

            _baseDrawingNumber = baseDrawingNumber.Trim().ToUpperInvariant();
        }

        /// <param name="segmentIndex">段序号，从 1 开始。</param>
        public string ForSegment(int segmentIndex)
        {
            if (segmentIndex <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(segmentIndex), segmentIndex, "段序号须大于 0。");
            }

            return DrawingNumberHelper.Offset(_baseDrawingNumber, segmentIndex - 1);
        }
    }
}
