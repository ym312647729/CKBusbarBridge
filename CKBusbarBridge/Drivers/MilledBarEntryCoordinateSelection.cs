using System;

namespace CKBusbarBridge.Drivers
{
    internal sealed class MilledBarEntryCoordinateSelection
    {
        internal const string EntryComponentPresetName = "1-1";
        internal const int EntryComponentInstanceNumber = 1;
        internal const string EntryComponentPattern = "1-1.*-1";

        internal MilledBarEntryCoordinateSelection(
            string enabledFolderName,
            string disabledFolderName,
            string inCoordinateName)
        {
            EnabledFolderName = enabledFolderName;
            DisabledFolderName = disabledFolderName;
            InCoordinateName = inCoordinateName;
        }

        internal string EnabledFolderName { get; }

        internal string DisabledFolderName { get; }

        internal string InCoordinateName { get; }
    }

    internal static class MilledBarEntryCoordinateSelector
    {
        internal static bool IsEntryComponentName(string componentName)
        {
            if (string.IsNullOrWhiteSpace(componentName))
            {
                return false;
            }

            var leafName = componentName.Trim();
            var separatorIndex = Math.Max(
                leafName.LastIndexOf('/'),
                leafName.LastIndexOf('\\'));
            if (separatorIndex >= 0)
            {
                leafName = leafName.Substring(separatorIndex + 1);
            }

            return System.Text.RegularExpressions.Regex.IsMatch(
                leafName,
                @"^1-1(?:\..+)?-1$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        internal static MilledBarEntryCoordinateSelection Resolve(int offsetDirection)
        {
            switch (offsetDirection)
            {
                case 0:
                    return new MilledBarEntryCoordinateSelection(
                        "右偏坐标系",
                        "左偏坐标系",
                        "右进");
                case 1:
                    return new MilledBarEntryCoordinateSelection(
                        "左偏坐标系",
                        "右偏坐标系",
                        "左进");
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(offsetDirection),
                        offsetDirection,
                        "铣排偏移方向只能为 0（右偏）或 1（左偏）。");
            }
        }
    }
}
