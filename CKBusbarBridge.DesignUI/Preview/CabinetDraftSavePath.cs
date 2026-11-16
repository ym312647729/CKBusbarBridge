using System;
using System.IO;
using CKBusbarBridge.Prototype;

namespace CKBusbarBridge.DesignPreview
{
    internal static class CabinetDraftSavePath
    {
        internal static void ValidateDrawingNumber(string drawingNumber)
        {
            // The drawing-number input permits only letters/digits and its separating dot.
            // This also excludes SW's @ separator and Windows' reserved filename symbols.
            if (string.IsNullOrWhiteSpace(drawingNumber) || drawingNumber.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                drawingNumber.IndexOf('@') >= 0 || drawingNumber.EndsWith(".", StringComparison.Ordinal) ||
                System.Linq.Enumerable.Any(drawingNumber, character => !char.IsLetterOrDigit(character) && character != '.'))
                throw new InvalidOperationException("图号只能包含字母、数字和分隔点，不能包含 SolidWorks 或文件名限制的符号。");
        }
        internal static string ForDrawing(string directory, string drawingMetadata)
        {
            var drawingNumber = CabinetParameterPropertyCodec.DrawingInputs(drawingMetadata)["图号"];
            ValidateDrawingNumber(drawingNumber);
            return Path.Combine(directory, drawingNumber + ".SLDASM");
        }
    }
}
