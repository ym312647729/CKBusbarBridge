using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CKBusbarBridge.Models;

namespace CKBusbarBridge.Models
{
    /// <summary>墙孔相对末端基架驱动尺寸的安装约束；符号不由界面模式转换。</summary>
    public sealed class FaceToWallInstallationParameters : NotificationObject
    {
        private double _top, _bottom, _left, _right, _entry;
        public double Top { get => _top; set => Set(ref _top, value, nameof(Top)); }
        public double Bottom { get => _bottom; set => Set(ref _bottom, value, nameof(Bottom)); }
        public double Left { get => _left; set => Set(ref _left, value, nameof(Left)); }
        public double Right { get => _right; set => Set(ref _right, value, nameof(Right)); }
        public double EntryDistance { get => _entry; set => Set(ref _entry, value, nameof(EntryDistance)); }
        private void Set(ref double field, double value, string name)
        {
            if (Math.Abs(field - value) < 1e-9) return;
            field = value;
            RaisePropertyChanged(name);
        }
        public FaceToWallInstallationValues Capture() => new FaceToWallInstallationValues(Top, Bottom, Left, Right, EntryDistance);
        public void Apply(FaceToWallInstallationValues value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            Top = value.Top; Bottom = value.Bottom; Left = value.Left; Right = value.Right; EntryDistance = value.EntryDistance;
        }
    }

    /// <summary>已冻结的驱动约束，在上游快照、规划和正式生成间传递，不持有可变界面对象。</summary>
    public sealed class FaceToWallInstallationValues
    {
        public double Top { get; }
        public double Bottom { get; }
        public double Left { get; }
        public double Right { get; }
        public double EntryDistance { get; }
        internal bool IsLegacyRead { get; }
        public double AcrossOffset => (Left - Right) / 2d;
        public double VerticalOffset => (Bottom - Top) / 2d;
        public FaceToWallInstallationValues(double top, double bottom, double left, double right, double entryDistance)
            : this(top, bottom, left, right, entryDistance, false)
        {
        }
        private FaceToWallInstallationValues(double top, double bottom, double left, double right, double entryDistance, bool legacyRead)
        {
            if (new[] { top, bottom, left, right, entryDistance }.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                throw new InvalidOperationException("墙孔安装参数必须为有效有限数值。");
            Top = top; Bottom = bottom; Left = left; Right = right; EntryDistance = entryDistance;
            IsLegacyRead = legacyRead;
        }
        internal FaceToWallInstallationValues AsLegacyRead() => new FaceToWallInstallationValues(Top, Bottom, Left, Right, EntryDistance, true);
        public double FrameWidth(double wallWidth) => wallWidth - Left - Right;
        public double FrameHeight(double wallHeight) => wallHeight - Top - Bottom;
        public void Validate(double wallWidth, double wallHeight)
        {
            if (new[] { wallWidth, wallHeight, FrameWidth(wallWidth), FrameHeight(wallHeight) }
                .Any(v => double.IsNaN(v) || double.IsInfinity(v) || v <= 0))
                throw new InvalidOperationException("墙孔安装参数导致基架驱动宽度或高度无效。");
            if (new[] { Top, Bottom, Left, Right }.Any(v => v < 0) && EntryDistance >= 0)
                throw new InvalidOperationException("四边距存在负值，伸入墙距离必须为负数，末端基架只能位于墙外。");
        }
        internal JObject ToJson() => new JObject { ["上间距"] = Top, ["下间距"] = Bottom,
            ["左间距"] = Left, ["右间距"] = Right, ["伸入墙距离"] = EntryDistance };
        internal static FaceToWallInstallationValues FromJson(JObject value)
        {
            if (value == null) throw new InvalidOperationException("墙孔安装参数记录缺失。");
            var names = new[] { "上间距", "下间距", "左间距", "右间距", "伸入墙距离" };
            var values = names.Select(name =>
            {
                var token = value[name];
                if (token == null || token.Type != JTokenType.Float && token.Type != JTokenType.Integer)
                    throw new InvalidOperationException("墙孔安装参数缺失或格式无效：" + name);
                return token.Value<double>();
            }).ToArray();
            return new FaceToWallInstallationValues(values[0], values[1], values[2], values[3], values[4]);
        }
    }

    internal sealed class FaceToWallInstallationMetadata
    {
        internal const string PropertyName = "面对墙墙孔安装参数";
        internal string Encode(FaceToWallCabinetDriverParameters parameters)
        {
            var values = parameters.General.WallInstallation.Capture();
            values.Validate(parameters.General.WallHoleWidth, parameters.General.WallHoleHeight);
            return new JObject { ["版本"] = 1, ["安装参数"] = values.ToJson(),
                ["柜前基座高已手改"] = parameters.Cabinet.FrontHole.IsBaseHeightManuallyModified,
                ["柜前基架高已手改"] = parameters.Cabinet.FrontHole.IsBaseOpeningHeightManuallyModified,
                ["柜后基座高已手改"] = parameters.Cabinet.BackHole.IsBaseHeightManuallyModified,
                ["柜后基架高已手改"] = parameters.Cabinet.BackHole.IsBaseOpeningHeightManuallyModified }.ToString(Formatting.None);
        }
        internal FaceToWallInstallationValues Read(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var document = JObject.Parse(text);
            if ((int?)document["版本"] != 1) throw new InvalidOperationException("不支持的墙孔安装参数记录版本。");
            return FaceToWallInstallationValues.FromJson(document["安装参数"] as JObject);
        }
        internal void RestoreManualState(string text, FaceToWallCabinetDriverParameters parameters)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var document = JObject.Parse(text);
            parameters.Cabinet.FrontHole.RestoreManualState(RequiredBoolean(document, "柜前基座高已手改"), RequiredBoolean(document, "柜前基架高已手改"));
            parameters.Cabinet.BackHole.RestoreManualState(RequiredBoolean(document, "柜后基座高已手改"), RequiredBoolean(document, "柜后基架高已手改"));
        }
        private bool RequiredBoolean(JObject document, string name)
        {
            var token = document[name];
            if (token?.Type != JTokenType.Boolean) throw new InvalidOperationException("墙孔安装参数缺少同步状态：" + name);
            return token.Value<bool>();
        }
    }
}
