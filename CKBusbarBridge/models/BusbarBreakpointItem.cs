using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace CKBusbarBridge.models
{
    public class BusbarBreakpointItem : INotifyPropertyChanged
    {
        private int _index;
        private string _title;
        private bool _isSelected;
        private string _previousBusbarLengthText;
        private string _breakPositionOffsetText;
        private bool _isBreakPositionReadOnly;
        private bool _isSmallOffsetFixedBoundary;
        private bool _isPhaseChangeFixedBoundary;
        private bool _isFaceToWallFixedEndBoundary;
        private bool _isSideToSideFixedBoundary;
        private bool _isLargeOffsetTurnBoundary;
        private string _frontDistanceLabelText;
        private string _rearBreakpointDistanceDisplayText;
        private string _rearBusbarLengthText;
        private double _absolutePosition;
        private double _previewLeft;

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

        public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? string.Empty : $"{Title}(位置{Index})";

        public bool IsSelected
        {
            get => _isSelected;
            set => SetField(ref _isSelected, value);
        }

        public string PreviousBusbarLengthText
        {
            get => _previousBusbarLengthText;
            set => SetField(ref _previousBusbarLengthText, value);
        }

        public string BreakPositionOffsetText
        {
            get => _breakPositionOffsetText;
            set => SetField(ref _breakPositionOffsetText, value);
        }

        public bool IsBreakPositionReadOnly
        {
            get => _isBreakPositionReadOnly;
            set => SetField(ref _isBreakPositionReadOnly, value);
        }

        /// <summary>
        /// 是否为小偏移特殊基架空窗的左右边界。
        /// 该身份用于识别不生成的中间过渡段，边界本身允许客户移动。
        /// </summary>
        public bool IsSmallOffsetFixedBoundary
        {
            get => _isSmallOffsetFixedBoundary;
            set => SetField(ref _isSmallOffsetFixedBoundary, value);
        }

        /// <summary>
        /// 是否为面对面自动换相区域的左右边界；默认位置由中间基架
        /// 确定，客户仍可调整。
        /// </summary>
        public bool IsPhaseChangeFixedBoundary
        {
            get => _isPhaseChangeFixedBoundary;
            set => SetField(ref _isPhaseChangeFixedBoundary, value);
        }

        /// <summary>
        /// 是否为面对墙场景中按最后基架和绝缘距离固定的墙端断点。
        /// </summary>
        public bool IsFaceToWallFixedEndBoundary
        {
            get => _isFaceToWallFixedEndBoundary;
            set => SetField(ref _isFaceToWallFixedEndBoundary, value);
        }

        /// <summary>
        /// 是否为侧对侧两端固定断点。侧对侧首尾断点由柜体端部
        /// 的固定搭接规则确定，不允许客户修改位置。
        /// </summary>
        public bool IsSideToSideFixedBoundary
        {
            get => _isSideToSideFixedBoundary;
            set => SetField(ref _isSideToSideFixedBoundary, value);
        }

        /// <summary>
        /// 是否为大偏移路径的固定转向断点。该断点参与真实分段，
        /// 但位置由转角基架确定，不允许客户移动。
        /// </summary>
        public bool IsLargeOffsetTurnBoundary
        {
            get => _isLargeOffsetTurnBoundary;
            set => SetField(ref _isLargeOffsetTurnBoundary, value);
        }

        public string FrontDistanceLabelText
        {
            get => _frontDistanceLabelText;
            set => SetField(ref _frontDistanceLabelText, value);
        }

        public string RearBreakpointDistanceDisplayText
        {
            get => _rearBreakpointDistanceDisplayText;
            set => SetField(ref _rearBreakpointDistanceDisplayText, value);
        }

        public string RearBusbarLengthText
        {
            get => _rearBusbarLengthText;
            set => SetField(ref _rearBusbarLengthText, value);
        }

        public double AbsolutePosition
        {
            get => _absolutePosition;
            set => SetField(ref _absolutePosition, value);
        }

        public double PreviewLeft
        {
            get => _previewLeft;
            set => SetField(ref _previewLeft, value);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 断点项内部的通用字段赋值与属性通知方法。
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
    }
}
