using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using CKBusbarBridge.Models;

namespace CKBusbarBridge.models
{
    /// <summary>
    /// 表示单个基架的编辑与预览模型。
    /// 同时负责把绝缘子定位文本转换为预览区中的标记位置。
    /// </summary>
    public class FrameItem : INotifyPropertyChanged
    {
        internal CKBusbarBridge.Drivers.FaceToFaceSmallOffsetFrameDrivePlan OffsetFramePlan { get; set; }
        private const double InsulatorMarkerWidth = 18;
        private int _index;
        private string _sourceName;
        private string _componentInstanceId;
        private string _title;
        private double _length;
        private double _width;
        private string _insulatorPosition;
        private string _insulatorCountText = "1";
        private bool _isUpdatingInsulatorEntries;
        private bool _isReversed;
        private bool _isOffsetFrame;
        private bool _isStandard;
        private FaceToWallFrameTemplateKind _templateKind;
        private FrameTemplateIdentity _templateIdentity = FrameTemplateIdentity.Regular024;
        private double _height;
        private double _displayWidth;
        private bool _canMoveLeft;
        private bool _canMoveRight;
        private string _largeOffsetGroupId;
        private FaceToFaceLargeOffsetFrameRole _largeOffsetRole;
        private string _inletCoordinateName;
        private string _outletCoordinateName;
        private double _topViewLeft;
        private double _topViewTop;
        private double _topViewWidth;
        private double _topViewHeight;
        private bool _isWidthLockedByLargeOffsetLayout;
        private bool _isFaceToWallFixedEndFrame;
        private bool _isPhaseChangeFrame;
        private double _wallHorizontalClearance;
        private double _wallVerticalClearance;
        private double _wallEntryDistance;
        private bool _requiresSecondaryDrive;
        private string _secondaryDriveReason;

        public ObservableCollection<InsulatorMarker> InsulatorMarkers { get; } = new ObservableCollection<InsulatorMarker>();

        public ObservableCollection<InsulatorPositionEntry> InsulatorPositionEntries { get; } = new ObservableCollection<InsulatorPositionEntry>();

        /// <summary>
        /// 大偏移俯视图中当前物理基架的三相绝缘子支架。
        /// </summary>
        public ObservableCollection<TopViewInsulatorMarker> TopViewInsulatorMarkers { get; }
            = new ObservableCollection<TopViewInsulatorMarker>();

        public int Index
        {
            get => _index;
            set
            {
                if (SetField(ref _index, value))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayTitle)));
                }
            }
        }

        public string Title
        {
            get => _title;
            set
            {
                if (SetField(ref _title, value))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayTitle)));
                }
            }
        }

        public string DisplayTitle
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Title))
                {
                    return string.Empty;
                }

                switch (TemplateKind)
                {
                    case FaceToWallFrameTemplateKind.Large1:
                        return $"{Title}(位置{Index}，大基架1)";
                    case FaceToWallFrameTemplateKind.Large2:
                        return $"{Title}(位置{Index}，大基架2)";
                    default:
                        return IsOffsetFrame
                            ? $"偏移基架(位置{Index})"
                            : $"{Title}(位置{Index})";
                }
            }
        }

        public string SourceName
        {
            get => _sourceName;
            set => SetField(ref _sourceName, value);
        }

        /// <summary>
        /// 组件实例ID。从反读结果填充，用于二次驱动时通过实例身份定位旧物理模型。
        /// 独立操作改变 SourceName 后，该值保持不变。
        /// </summary>
        public string ComponentInstanceId
        {
            get => _componentInstanceId;
            set => SetField(ref _componentInstanceId, value);
        }

        public double Length
        {
            get => _length;
            set
            {
                if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
                {
                    return;
                }

                if (SetField(ref _length, value))
                {
                    RefreshInsulatorMarkers();
                }
            }
        }

        public string InsulatorPosition
        {
            get => _insulatorPosition;
            set
            {
                if (SetField(ref _insulatorPosition, value))
                {
                    RefreshInsulatorMarkers();
                    if (!_isUpdatingInsulatorEntries)
                    {
                        RefreshInsulatorPositionEntriesFromPositionText();
                    }
                }
            }
        }

        public string InsulatorCountText
        {
            get => _insulatorCountText;
            set
            {
                if (SetField(ref _insulatorCountText, value))
                {
                    RebuildInsulatorPositionEntriesFromCount();
                }
            }
        }

        public double Width
        {
            get => _width;
            set
            {
                if (value <= 0)
                {
                    return;
                }

                if (SetField(ref _width, value))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidthDisplayText)));
                }
            }
        }

        public bool IsReversed
        {
            get => _isReversed;
            set
            {
                if (value && (IsOffsetFrame || IsLargeOffsetCorner))
                {
                    return;
                }

                if (SetField(ref _isReversed, value))
                {
                    RefreshInsulatorMarkers();
                }
            }
        }

        /// <summary>
        /// 当前项是否为面对面偏移场景中的偏移基架。
        /// 该状态同时决定界面标记和小偏移模板身份；小偏移基架始终正装。
        /// </summary>
        public bool IsOffsetFrame
        {
            get => _isOffsetFrame;
            set
            {
                if (!SetField(ref _isOffsetFrame, value))
                {
                    return;
                }

                if (_isOffsetFrame && _isReversed)
                {
                    // 无论初始化还是参数恢复，转为小偏移模板时立即归一为正装。
                    _isReversed = false;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReversed)));
                    RefreshInsulatorMarkers();
                }

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanToggleReverse)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayTitle)));
            }
        }

        public bool IsStandard
        {
            get => _isStandard;
            set => SetField(ref _isStandard, value);
        }

        /// <summary>
        /// 面对墙场景下使用的显式模板种类。
        /// </summary>
        public FaceToWallFrameTemplateKind TemplateKind
        {
            get => _templateKind;
            set
            {
                if (SetField(ref _templateKind, value))
                {
                    if (IsLargeOffsetCorner && _isReversed)
                    {
                        _isReversed = false;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReversed)));
                        RefreshInsulatorMarkers();
                    }
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanToggleReverse)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UseLargeFrameTemplate)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFaceToWallLargeFrame1)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayTitle)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLargeOffsetCorner)));
                }
            }
        }

        /// <summary>
        /// 实际物理模板身份；不得仅据结构类型或文件名前缀合并模型。
        /// </summary>
        internal FrameTemplateIdentity TemplateIdentity
        {
            get => _templateIdentity;
            set => SetField(ref _templateIdentity, value);
        }

        /// <summary>
        /// 兼容旧参数和绑定；写入 true 时把常规基架映射为大基架1。
        /// </summary>
        public bool UseLargeFrameTemplate
        {
            get => TemplateKind != FaceToWallFrameTemplateKind.Regular;
            set => TemplateKind = value
                ? TemplateKind == FaceToWallFrameTemplateKind.Regular
                    ? FaceToWallFrameTemplateKind.Large1
                    : TemplateKind
                : FaceToWallFrameTemplateKind.Regular;
        }

        /// <summary>
        /// 是否为面对墙大基架1；用于仅在该模板上显示墙体间隙参数。
        /// </summary>
        public bool IsFaceToWallLargeFrame1 =>
            TemplateKind == FaceToWallFrameTemplateKind.Large1;

        /// <summary>
        /// 大基架1与墙左右间隙（mm），允许为负数。
        /// </summary>
        public double WallHorizontalClearance
        {
            get => _wallHorizontalClearance;
            set
            {
                if (!double.IsNaN(value) && !double.IsInfinity(value))
                {
                    SetField(ref _wallHorizontalClearance, value);
                }
            }
        }

        /// <summary>
        /// 大基架1与墙上下间隙（mm），允许为负数。
        /// </summary>
        public double WallVerticalClearance
        {
            get => _wallVerticalClearance;
            set
            {
                if (!double.IsNaN(value) && !double.IsInfinity(value))
                {
                    SetField(ref _wallVerticalClearance, value);
                }
            }
        }

        /// <summary>
        /// 大基架1进墙距离（mm），允许为负数。
        /// </summary>
        public double WallEntryDistance
        {
            get => _wallEntryDistance;
            set
            {
                if (!double.IsNaN(value) && !double.IsInfinity(value))
                {
                    SetField(ref _wallEntryDistance, value);
                }
            }
        }

        /// <summary>
        /// 基架预览中显示的宽度文本。
        /// </summary>
        public string WidthDisplayText => $"宽度 {_width:0.##} mm";

        /// <summary>
        /// 基架高度（mm）；未设置时由驱动逻辑按模板默认值处理。
        /// </summary>
        public double Height
        {
            get => _height;
            set
            {
                if (value <= 0)
                {
                    return;
                }

                SetField(ref _height, value);
            }
        }

        /// <summary>
        /// 真实模型尺寸与当前基座重新计算出的期望尺寸不一致，需要再次驱动基架。
        /// </summary>
        public bool RequiresSecondaryDrive
        {
            get => _requiresSecondaryDrive;
            private set => SetField(ref _requiresSecondaryDrive, value);
        }

        /// <summary>
        /// 二次驱动提示的详细尺寸差异。
        /// </summary>
        public string SecondaryDriveReason
        {
            get => _secondaryDriveReason;
            private set => SetField(ref _secondaryDriveReason, value);
        }

        internal void SetSecondaryDriveRequirement(string reason)
        {
            SecondaryDriveReason = reason;
            RequiresSecondaryDrive = !string.IsNullOrWhiteSpace(reason);
        }

        internal void AppendSecondaryDriveRequirement(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return;
            }

            SetSecondaryDriveRequirement(string.IsNullOrWhiteSpace(SecondaryDriveReason)
                ? reason
                : SecondaryDriveReason + Environment.NewLine + reason);
        }

        public double DisplayWidth
        {
            get => _displayWidth;
            set
            {
                if (SetField(ref _displayWidth, value))
                {
                    RefreshInsulatorMarkers();
                }
            }
        }

        public bool CanMoveLeft
        {
            get => _canMoveLeft;
            set => SetField(ref _canMoveLeft, value);
        }

        public bool CanMoveRight
        {
            get => _canMoveRight;
            set => SetField(ref _canMoveRight, value);
        }

        /// <summary>
        /// 面对面大偏移组三件或多件物理基架共享的稳定标识。
        /// </summary>
        public string LargeOffsetGroupId
        {
            get => _largeOffsetGroupId;
            set
            {
                if (SetField(ref _largeOffsetGroupId, value))
                {
                    RaiseLargeOffsetDerivedProperties();
                }
            }
        }

        /// <summary>
        /// 当前物理基架在面对面大偏移组中的固定角色。
        /// </summary>
        public FaceToFaceLargeOffsetFrameRole LargeOffsetRole
        {
            get => _largeOffsetRole;
            set
            {
                if (SetField(ref _largeOffsetRole, value))
                {
                    RaiseLargeOffsetDerivedProperties();
                }
            }
        }

        /// <summary>
        /// 当前物理基架装配时使用的入口坐标系名称。
        /// </summary>
        public string InletCoordinateName
        {
            get => _inletCoordinateName;
            set => SetField(ref _inletCoordinateName, value);
        }

        /// <summary>
        /// 当前物理基架装配时使用的出口坐标系名称。
        /// </summary>
        public string OutletCoordinateName
        {
            get => _outletCoordinateName;
            set => SetField(ref _outletCoordinateName, value);
        }

        /// <summary>
        /// 是否属于面对面大偏移物理组。
        /// </summary>
        public bool IsLargeOffsetMember =>
            LargeOffsetRole != FaceToFaceLargeOffsetFrameRole.None
            && !string.IsNullOrWhiteSpace(LargeOffsetGroupId);

        /// <summary>
        /// 是否为大偏移组的拐角基架。
        /// </summary>
        public bool IsLargeOffsetCorner =>
            TemplateKind == FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner;

        /// <summary>
        /// 是否为面对墙场景中承载墙端固定保护段的最后一节基架。
        /// </summary>
        public bool IsFaceToWallFixedEndFrame
        {
            get => _isFaceToWallFixedEndFrame;
            set => SetField(ref _isFaceToWallFixedEndFrame, value);
        }

        /// <summary>
        /// 是否为面对面无偏移自动换相所使用的唯一最中间基架。
        /// </summary>
        public bool IsPhaseChangeFrame
        {
            get => _isPhaseChangeFrame;
            set => SetField(ref _isPhaseChangeFrame, value);
        }

        /// <summary>
        /// 连接基架允许调整拆分长度，偏心尺寸闭合约束在生成前统一校验。
        /// </summary>
        public bool IsLengthEditable => true;

        public bool IsLengthReadOnly => !IsLengthEditable;

        /// <summary>
        /// 连接基架允许编辑宽度；拐角基架宽度固定为对应柜侧基座宽度。
        /// </summary>
        public bool IsWidthEditable => !IsWidthLockedByLargeOffsetLayout
            && (!IsLargeOffsetMember
                || LargeOffsetRole == FaceToFaceLargeOffsetFrameRole.Connector);

        public bool IsWidthReadOnly => !IsWidthEditable;

        /// <summary>
        /// 大偏移组外的常规基架宽度由所在 A/B 柜侧基座宽度锁定。
        /// </summary>
        public bool IsWidthLockedByLargeOffsetLayout
        {
            get => _isWidthLockedByLargeOffsetLayout;
            set
            {
                if (SetField(ref _isWidthLockedByLargeOffsetLayout, value))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsWidthEditable)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsWidthReadOnly)));
                }
            }
        }

        /// <summary>
        /// 大偏移组三件结构的高度统一由柜体参数驱动，不允许在界面修改。
        /// </summary>
        public bool IsHeightEditable => !IsLargeOffsetMember;

        /// <summary>
        /// 大偏移组作为一个逻辑段，禁止单独移动或删除其物理成员。
        /// </summary>
        public bool CanChangeStructure => !IsLargeOffsetMember;

        /// <summary>
        /// 拐角无正反装定义；连接架及小偏移基架的安装方式由布局决定。
        /// </summary>
        public bool CanToggleReverse => !IsLargeOffsetCorner && !IsLargeOffsetMember && !IsOffsetFrame;

        /// <summary>
        /// 俯视图画布中的物理基架矩形位置和尺寸（像素）。
        /// </summary>
        public double TopViewLeft
        {
            get => _topViewLeft;
            set => SetField(ref _topViewLeft, value);
        }

        public double TopViewTop
        {
            get => _topViewTop;
            set => SetField(ref _topViewTop, value);
        }

        public double TopViewWidth
        {
            get => _topViewWidth;
            set => SetField(ref _topViewWidth, value);
        }

        public double TopViewHeight
        {
            get => _topViewHeight;
            set => SetField(ref _topViewHeight, value);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 大偏移角色或组标识变化时，集中刷新所有界面约束属性。
        /// </summary>
        private void RaiseLargeOffsetDerivedProperties()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLargeOffsetMember)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLargeOffsetCorner)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLengthEditable)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLengthReadOnly)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsWidthEditable)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsWidthReadOnly)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHeightEditable)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanChangeStructure)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanToggleReverse)));
        }

        /// <summary>
        /// 根据长度、显示宽度、方向和定位文本重新生成绝缘子标记坐标。
        /// 数量为 0 时不绘制，即使定位文本里仍残留默认值。
        /// </summary>
        private void RefreshInsulatorMarkers()
        {
            InsulatorMarkers.Clear();

            if (_length <= 0
                || _displayWidth <= 0
                || string.IsNullOrWhiteSpace(_insulatorPosition)
                || string.Equals(_insulatorCountText, "0", StringComparison.Ordinal))
            {
                return;
            }

            var markerTrackWidth = Math.Max(0, _displayWidth - InsulatorMarkerWidth);

            foreach (var position in ParseInsulatorPositions(_insulatorPosition))
            {
                // 先归一化到 0~1，再结合反向显示状态映射到预览宽度。
                var normalizedPosition = Math.Max(0, Math.Min(position, _length)) / _length;
                var left = (_isReversed ? 1 - normalizedPosition : normalizedPosition) * markerTrackWidth;
                InsulatorMarkers.Add(new InsulatorMarker { Left = Math.Round(left, 1) });
            }
        }

        /// <summary>
        /// 根据当前绝缘子定位文本刷新数量与分项输入框。
        /// </summary>
        private void RefreshInsulatorPositionEntriesFromPositionText()
        {
            _isUpdatingInsulatorEntries = true;
            try
            {
                var positions = ParseInsulatorPositions(_insulatorPosition)
                    .Where(position => _length <= 0 || position >= 0 && position <= _length)
                    .Select(position => position.ToString("0.##"))
                    .ToList();

                // 数量 0 是面对面基架的有效业务值，不能像旧逻辑一样自动回退为 1。
                var count = string.Equals(_insulatorCountText, "0", StringComparison.Ordinal)
                    ? 0
                    : Math.Max(1, positions.Count);
                if (!string.Equals(_insulatorCountText, count.ToString(), StringComparison.Ordinal))
                {
                    _insulatorCountText = count.ToString();
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InsulatorCountText)));
                }

                ClearInsulatorPositionEntries();
                for (var index = 0; index < count; index++)
                {
                    var positionText = index < positions.Count
                        ? positions[index]
                        : BuildDefaultInsulatorPositionText(_length);
                    InsulatorPositionEntries.Add(CreateInsulatorPositionEntry(index + 1, count, positionText));
                }
            }
            finally
            {
                _isUpdatingInsulatorEntries = false;
            }
        }

        /// <summary>
        /// 根据绝缘子数量重建分项输入框，并回写定位文本。
        /// </summary>
        private void RebuildInsulatorPositionEntriesFromCount()
        {
            if (_isUpdatingInsulatorEntries)
            {
                return;
            }

            if (!int.TryParse(_insulatorCountText, out var count) || count < 0 || count > 3)
            {
                // 保留用户输入，让生成前校验给出明确错误；此处不创建超过模板上限的输入项。
                return;
            }

            _isUpdatingInsulatorEntries = true;
            try
            {
                var existingPositions = InsulatorPositionEntries
                    .Select(entry => entry.PositionText)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToList();

                if (existingPositions.Count == 0)
                {
                    existingPositions = ParseInsulatorPositions(_insulatorPosition)
                        .Where(position => _length <= 0 || position >= 0 && position <= _length)
                        .Select(position => position.ToString("0.##"))
                        .ToList();
                }

                while (existingPositions.Count < count)
                {
                    existingPositions.Add(BuildInsulatorPositionInLargestGap(existingPositions));
                }

                while (existingPositions.Count > count)
                {
                    existingPositions.RemoveAt(existingPositions.Count - 1);
                }

                ClearInsulatorPositionEntries();
                for (var index = 0; index < count; index++)
                {
                    InsulatorPositionEntries.Add(CreateInsulatorPositionEntry(index + 1, count, existingPositions[index]));
                }

                UpdateInsulatorPositionFromEntries();
            }
            finally
            {
                _isUpdatingInsulatorEntries = false;
            }
        }

        private InsulatorPositionEntry CreateInsulatorPositionEntry(int index, int totalCount, string positionText)
        {
            var entry = new InsulatorPositionEntry
            {
                Index = index,
                LabelText = totalCount <= 1 ? "绝缘子定位" : $"绝缘子定位{index}",
                PositionText = positionText
            };
            entry.PropertyChanged += InsulatorPositionEntry_PropertyChanged;
            return entry;
        }

        private void InsulatorPositionEntry_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(InsulatorPositionEntry.PositionText) && !_isUpdatingInsulatorEntries)
            {
                UpdateInsulatorPositionFromEntries();
            }
        }

        private void UpdateInsulatorPositionFromEntries()
        {
            _isUpdatingInsulatorEntries = true;
            try
            {
                var positions = InsulatorPositionEntries
                    .Select(entry => entry.PositionText)
                    .Where(text => !string.IsNullOrWhiteSpace(text));
                InsulatorPosition = string.Join(",", positions);
            }
            finally
            {
                _isUpdatingInsulatorEntries = false;
            }
        }

        private void ClearInsulatorPositionEntries()
        {
            foreach (var entry in InsulatorPositionEntries)
            {
                entry.PropertyChanged -= InsulatorPositionEntry_PropertyChanged;
            }

            InsulatorPositionEntries.Clear();
        }

        private static string BuildDefaultInsulatorPositionText(double frameLength)
        {
            return frameLength > 0
                ? (frameLength / 2d).ToString("0.##")
                : string.Empty;
        }

        private string BuildInsulatorPositionInLargestGap(IList<string> existingPositionTexts)
        {
            if (_length <= 0)
            {
                return BuildDefaultInsulatorPositionText(_length);
            }

            var positions = existingPositionTexts
                .SelectMany(text => ParseInsulatorPositions(text))
                .Where(position => position >= 0 && position <= _length)
                .OrderBy(position => position)
                .ToList();

            if (positions.Count == 0)
            {
                return BuildDefaultInsulatorPositionText(_length);
            }

            var newPosition = _length / 2d;
            var previousEdge = 0d;
            var maximumGap = -1d;

            foreach (var position in positions.Concat(new[] { _length }))
            {
                var gap = position - previousEdge;
                if (gap > maximumGap)
                {
                    maximumGap = gap;
                    newPosition = previousEdge + gap / 2d;
                }

                previousEdge = position;
            }

            return Math.Round(newPosition, 1).ToString("0.##");
        }

        /// <summary>
        /// 从文本中解析绝缘子定位点，支持中英文逗号、分号、顿号和空格分隔。
        /// </summary>
        public static IEnumerable<double> ParseInsulatorPositions(string positions)
        {
            if (string.IsNullOrWhiteSpace(positions))
            {
                yield break;
            }

            var parts = positions.Split(new[] { ',', '，', ';', '；', '、', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (double.TryParse(part, out var position))
                {
                    yield return position;
                }
            }
        }

        /// <summary>
        /// 基架项内部的通用字段赋值与属性通知方法。
        /// </summary>
        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        /// <summary>
        /// 预览区中单个绝缘子标记的横向位置。
        /// </summary>
        public class InsulatorMarker
        {
            public double Left { get; set; }
        }

        /// <summary>
        /// 大偏移俯视图中的三相绝缘子支架，坐标相对所属物理基架。
        /// </summary>
        public class TopViewInsulatorMarker
        {
            public double Left { get; set; }

            public double Top { get; set; }

            public double Width { get; set; }

            public double Height { get; set; }

            public ObservableCollection<TopViewInsulatorCircle> Circles { get; }
                = new ObservableCollection<TopViewInsulatorCircle>();
        }

        /// <summary>
        /// 三相绝缘子支架内的单个圆形符号，坐标相对支架左上角。
        /// </summary>
        public class TopViewInsulatorCircle
        {
            public double Left { get; set; }

            public double Top { get; set; }

            public double Diameter { get; set; }
        }

        /// <summary>
        /// 界面中单个绝缘子定位输入项。
        /// </summary>
        public class InsulatorPositionEntry : INotifyPropertyChanged
        {
            private string _labelText;
            private string _positionText;

            public int Index { get; set; }

            public string LabelText
            {
                get => _labelText;
                set => SetField(ref _labelText, value);
            }

            public string PositionText
            {
                get => _positionText;
                set => SetField(ref _positionText, value);
            }

            public event PropertyChangedEventHandler PropertyChanged;

            private void SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
            {
                if (EqualityComparer<T>.Default.Equals(field, value))
                {
                    return;
                }

                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
