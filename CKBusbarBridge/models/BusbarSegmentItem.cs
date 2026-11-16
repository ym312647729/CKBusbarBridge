using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CKBusbarBridge.models
{
    public class BusbarSegmentItem : INotifyPropertyChanged
    {
        private int _index;
        private string _title;
        private bool _isSelected;
        private string _lengthText;
        private string _rangeText;
        private double _absoluteStartPosition;
        private double _absoluteEndPosition;
        private double _previewLeft;
        private double _previewWidth;
        private int _leftBreakpointIndex;
        private int _rightBreakpointIndex;
        private bool _isPhaseSwitched;
        private bool _isSuppressedBySmallOffset;
        private bool _isAutomaticPhaseChange;

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

        public string LengthText
        {
            get => _lengthText;
            set => SetField(ref _lengthText, value);
        }

        public string RangeText
        {
            get => _rangeText;
            set => SetField(ref _rangeText, value);
        }

        public double AbsoluteStartPosition
        {
            get => _absoluteStartPosition;
            set => SetField(ref _absoluteStartPosition, value);
        }

        public double AbsoluteEndPosition
        {
            get => _absoluteEndPosition;
            set => SetField(ref _absoluteEndPosition, value);
        }

        public double PreviewLeft
        {
            get => _previewLeft;
            set => SetField(ref _previewLeft, value);
        }

        public double PreviewWidth
        {
            get => _previewWidth;
            set => SetField(ref _previewWidth, value);
        }

        public int LeftBreakpointIndex
        {
            get => _leftBreakpointIndex;
            set => SetField(ref _leftBreakpointIndex, value);
        }

        public int RightBreakpointIndex
        {
            get => _rightBreakpointIndex;
            set => SetField(ref _rightBreakpointIndex, value);
        }

        public bool IsPhaseSwitched
        {
            get => _isPhaseSwitched;
            set => SetField(ref _isPhaseSwitched, value);
        }

        /// <summary>
        /// 小偏移特殊基架固定空窗段不参与预览、编辑和正式铜排生成。
        /// </summary>
        public bool IsSuppressedBySmallOffset
        {
            get => _isSuppressedBySmallOffset;
            set => SetField(ref _isSuppressedBySmallOffset, value);
        }

        /// <summary>
        /// 是否为面对面根据两柜实际相序自动确定的换相段。
        /// </summary>
        public bool IsAutomaticPhaseChange
        {
            get => _isAutomaticPhaseChange;
            set => SetField(ref _isAutomaticPhaseChange, value);
        }

        public event PropertyChangedEventHandler PropertyChanged;

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
