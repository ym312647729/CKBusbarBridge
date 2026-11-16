using System.ComponentModel;

namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 面对墙场景穿墙套管及桩头参数。
    /// 参数值写入穿墙套管子零部件的全局变量 φ、C1～C10。
    /// </summary>
    public class FaceToWallBushingTerminalParameters : CabinetParameterNotificationObject
    {
        private double _busbarThickness;
        private double _c8;
        private double _c9;
        private double _c10;
        private bool _c8ManuallyModified;
        private bool _distancePairManuallyModified;

        public FaceToWallBushingTerminalParameters()
        {
            _busbarThickness = FaceToFaceBusbarCenterDistanceHelper.ResolveBusbarThickness(
                CabinetGeneralParameters.DefaultBusbarSpec);
            _c8 = _busbarThickness;
            _c9 = _busbarThickness + _c8;
            _c10 = _c9;
        }

        private double _faceToWallBushingTerminalDiameter  = 200;
        public double Diameter
        {
            get => _faceToWallBushingTerminalDiameter;
            set => SetParameterValue(ref _faceToWallBushingTerminalDiameter, value, nameof(Diameter));
        }

        private double _faceToWallBushingTerminalC1  = 100;
        public double C1
        {
            get => _faceToWallBushingTerminalC1;
            set => SetParameterValue(ref _faceToWallBushingTerminalC1, value, nameof(C1));
        }

        private double _faceToWallBushingTerminalC2  = 100;
        public double C2
        {
            get => _faceToWallBushingTerminalC2;
            set => SetParameterValue(ref _faceToWallBushingTerminalC2, value, nameof(C2));
        }

        private double _faceToWallBushingTerminalC3  = 20;
        public double C3
        {
            get => _faceToWallBushingTerminalC3;
            set => SetParameterValue(ref _faceToWallBushingTerminalC3, value, nameof(C3));
        }

        private double _faceToWallBushingTerminalC4  = 50;
        public double C4
        {
            get => _faceToWallBushingTerminalC4;
            set => SetParameterValue(ref _faceToWallBushingTerminalC4, value, nameof(C4));
        }

        private double _faceToWallBushingTerminalC5  = 25;
        public double C5
        {
            get => _faceToWallBushingTerminalC5;
            set => SetParameterValue(ref _faceToWallBushingTerminalC5, value, nameof(C5));
        }

        private double _faceToWallBushingTerminalC6  = 50;
        public double C6
        {
            get => _faceToWallBushingTerminalC6;
            set => SetParameterValue(ref _faceToWallBushingTerminalC6, value, nameof(C6));
        }

        private double _faceToWallBushingTerminalC7  = 11;
        public double C7
        {
            get => _faceToWallBushingTerminalC7;
            set => SetParameterValue(ref _faceToWallBushingTerminalC7, value, nameof(C7));
        }

        public double C8
        {
            get { return _c8; }
            set
            {
                _c8ManuallyModified = true;
                SetC8(value);

                if (!_distancePairManuallyModified)
                {
                    SetDistancePair(_busbarThickness + _c8);
                }
            }
        }

        public double C9
        {
            get { return _c9; }
            set
            {
                _distancePairManuallyModified = true;
                SetDistancePair(value);
            }
        }

        public double C10
        {
            get { return _c10; }
            set
            {
                _distancePairManuallyModified = true;
                SetDistancePair(value);
            }
        }

        /// <summary>
        /// 铜排规格中的排厚变化后，更新尚未被用户手动确定的桩头参数。
        /// </summary>
        /// <param name="busbarThickness">当前铜排排厚。</param>
        internal void ApplyBusbarThickness(double busbarThickness)
        {
            if (busbarThickness <= 0)
            {
                return;
            }

            _busbarThickness = busbarThickness;

            if (!_c8ManuallyModified)
            {
                SetC8(busbarThickness);
            }

            if (!_distancePairManuallyModified)
            {
                SetDistancePair(_busbarThickness + _c8);
            }
        }

        private void SetC8(double value)
        {
            if (_c8.Equals(value))
            {
                return;
            }

            _c8 = value;
            OnPropertyChanged(nameof(C8));
        }

        private void SetDistancePair(double value)
        {
            if (!_c9.Equals(value))
            {
                _c9 = value;
                OnPropertyChanged(nameof(C9));
            }

            if (!_c10.Equals(value))
            {
                _c10 = value;
                OnPropertyChanged(nameof(C10));
            }
        }


        private void OnPropertyChanged(string propertyName)
        {
            RaisePropertyChanged(propertyName);
        }
    }
}
