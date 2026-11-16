using System;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// 生成流程内共享的图号来源。查看当前图号不会递增，
    /// 只有一棵模型树完整生成后调用提交才会推进流水号。
    /// </summary>
    internal interface IDrawingNumberGenerator
    {
        string PeekNextDrawingNumber();

        string PeekNextFileNameSuffix();

        void CommitNext(string drawingNumber);
    }

    /// <summary>
    /// 按“合同号.四位流水号”顺序分配图号，生命周期限定在一次生成流程内。
    /// </summary>
    internal sealed class DrawingNumberGenerator : IDrawingNumberGenerator
    {
        private readonly string _contractPrefix;
        private int _serial;
        private bool _exhausted;

        internal DrawingNumberGenerator(string startingDrawingNumber)
        {
            if (!DrawingNumberHelper.TryParse(
                startingDrawingNumber,
                out var contractPrefix,
                out var serial))
            {
                throw new FormatException(
                    $"起始图号格式无效，应为合同号.四位流水号：{startingDrawingNumber}");
            }

            _contractPrefix = contractPrefix;
            _serial = serial;
        }

        public string PeekNextDrawingNumber()
        {
            if (_exhausted)
            {
                throw new InvalidOperationException("图号四位流水号已耗尽。");
            }

            return DrawingNumberHelper.Format(_contractPrefix, _serial);
        }

        public string PeekNextFileNameSuffix()
        {
            return DrawingNumberHelper.ToFileNameSuffix(PeekNextDrawingNumber());
        }

        public void CommitNext(string drawingNumber)
        {
            var currentDrawingNumber = PeekNextDrawingNumber();
            var normalizedDrawingNumber = drawingNumber == null
                ? string.Empty
                : drawingNumber.Trim().ToUpperInvariant();
            if (!string.Equals(
                currentDrawingNumber,
                normalizedDrawingNumber,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"提交图号与当前待分配图号不一致：当前={currentDrawingNumber}，提交={drawingNumber}");
            }

            // 9999 本身仍可分配；成功提交后不再允许取得下一图号。
            if (_serial == 9999)
            {
                _exhausted = true;
                return;
            }

            _serial++;
        }
    }
}
