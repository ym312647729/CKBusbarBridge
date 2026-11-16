using CKBusbarBridge.Helpers;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;

namespace CKBusbarBridge.viewmodels
{
    /// <summary>
    /// 柜体参数窗口共用的示意图尺寸红框逻辑。
    /// </summary>
    public abstract class DimensionHighlightViewModel : NotificationObject
    {
        private const string DimensionCoordinateFileName = "示意图尺寸坐标.json";

        private readonly Dictionary<string, List<DimensionHighlightInfo>> _dimensionHighlights =
            new Dictionary<string, List<DimensionHighlightInfo>>(StringComparer.OrdinalIgnoreCase);

        private DimensionHighlightInfo _currentDimensionHighlight;
        private string _activeDimensionImageName;
        private BitmapImage _activeDimensionImageSource;
        private bool _isDimensionHighlightActive;
        private double _dimensionHighlightLeft;
        private double _dimensionHighlightTop;
        private double _dimensionHighlightWidth;
        private double _dimensionHighlightHeight;
        private Visibility _dimensionHighlightVisibility = Visibility.Collapsed;

        protected DimensionHighlightViewModel()
        {
            LoadDimensionCoordinates();
        }

        public string ActiveDimensionImageName
        {
            get => _activeDimensionImageName;
            private set
            {
                if (string.Equals(_activeDimensionImageName, value, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _activeDimensionImageName = value;
                RaisePropertyChanged(nameof(ActiveDimensionImageName));
            }
        }

        public BitmapImage ActiveDimensionImageSource
        {
            get => _activeDimensionImageSource;
            private set
            {
                if (ReferenceEquals(_activeDimensionImageSource, value))
                {
                    return;
                }

                _activeDimensionImageSource = value;
                RaisePropertyChanged(nameof(ActiveDimensionImageSource));
            }
        }

        public bool IsDimensionHighlightActive
        {
            get => _isDimensionHighlightActive;
            private set
            {
                if (_isDimensionHighlightActive == value)
                {
                    return;
                }

                _isDimensionHighlightActive = value;
                RaisePropertyChanged(nameof(IsDimensionHighlightActive));
            }
        }

        public double DimensionHighlightLeft => _dimensionHighlightLeft;

        public double DimensionHighlightTop => _dimensionHighlightTop;

        public double DimensionHighlightWidth => _dimensionHighlightWidth;

        public double DimensionHighlightHeight => _dimensionHighlightHeight;

        public Visibility DimensionHighlightVisibility => _dimensionHighlightVisibility;

        /// <summary>
        /// 激活当前示意图上的尺寸框。当前图没有该参数时不切换到其他示意图。
        /// </summary>
        public bool ActivateDimensionHighlight(string dimensionName, string currentImageName)
        {
            List<DimensionHighlightInfo> highlights;
            if (string.IsNullOrWhiteSpace(dimensionName) ||
                string.IsNullOrWhiteSpace(currentImageName) ||
                !_dimensionHighlights.TryGetValue(dimensionName, out highlights))
            {
                ClearDimensionHighlight();
                return false;
            }

            var selectedHighlight = highlights.FirstOrDefault(highlight =>
                string.Equals(
                    highlight.ImageName,
                    currentImageName,
                    StringComparison.OrdinalIgnoreCase));
            if (selectedHighlight == null)
            {
                ClearDimensionHighlight();
                return false;
            }

            var previewImage = LoadDimensionPreviewImage(selectedHighlight.ImageName);
            if (previewImage == null)
            {
                ClearDimensionHighlight();
                return false;
            }

            _currentDimensionHighlight = selectedHighlight;
            ActiveDimensionImageName = selectedHighlight.ImageName;
            ActiveDimensionImageSource = previewImage;
            IsDimensionHighlightActive = true;
            SetDimensionHighlightVisibility(Visibility.Collapsed);
            return true;
        }

        public void UpdateDimensionHighlightLayout(
            double viewportLeft,
            double viewportTop,
            double viewportWidth,
            double viewportHeight,
            int imagePixelWidth,
            int imagePixelHeight)
        {
            if (_currentDimensionHighlight == null ||
                viewportWidth <= 0 ||
                viewportHeight <= 0 ||
                imagePixelWidth <= 0 ||
                imagePixelHeight <= 0)
            {
                SetDimensionHighlightVisibility(Visibility.Collapsed);
                return;
            }

            var scale = Math.Min(
                viewportWidth / imagePixelWidth,
                viewportHeight / imagePixelHeight);
            var renderedWidth = imagePixelWidth * scale;
            var renderedHeight = imagePixelHeight * scale;
            var offsetX = viewportLeft + (viewportWidth - renderedWidth) / 2.0;
            var offsetY = viewportTop + (viewportHeight - renderedHeight) / 2.0;

            var coordinate1 = _currentDimensionHighlight.Coordinate1;
            var coordinate2 = _currentDimensionHighlight.Coordinate2;
            SetDimensionHighlightLayout(
                offsetX + Math.Min(coordinate1.X, coordinate2.X) * scale,
                offsetY + Math.Min(coordinate1.Y, coordinate2.Y) * scale,
                Math.Max(1.0, Math.Abs(coordinate2.X - coordinate1.X) * scale),
                Math.Max(1.0, Math.Abs(coordinate2.Y - coordinate1.Y) * scale));
            SetDimensionHighlightVisibility(Visibility.Visible);
        }

        public void ClearDimensionHighlight()
        {
            _currentDimensionHighlight = null;
            IsDimensionHighlightActive = false;
            ActiveDimensionImageSource = null;
            ActiveDimensionImageName = null;
            SetDimensionHighlightVisibility(Visibility.Collapsed);
        }

        private static BitmapImage LoadDimensionPreviewImage(string imageName)
        {
            try
            {
                var imageUri = new Uri(
                    "pack://application:,,,/CKBusbarBridge;component/Images/" + imageName,
                    UriKind.Absolute);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = imageUri;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("加载尺寸提示图片失败：" + ex);
                return null;
            }
        }

        private void LoadDimensionCoordinates()
        {
            var settingsPath = Path.Combine(
                CKBusbarBridge.BusBridgeDriver.RegDllPath(),
                "Settings",
                DimensionCoordinateFileName);
            if (!File.Exists(settingsPath))
            {
                System.Diagnostics.Debug.WriteLine("未找到示意图尺寸坐标配置：" + settingsPath);
                return;
            }

            try
            {
                var imageConfigurations = JsonConvert.DeserializeObject<List<DimensionImageConfiguration>>(
                    File.ReadAllText(settingsPath));
                if (imageConfigurations == null)
                {
                    return;
                }

                foreach (var imageConfiguration in imageConfigurations)
                {
                    if (imageConfiguration == null ||
                        string.IsNullOrWhiteSpace(imageConfiguration.ImageName) ||
                        imageConfiguration.Dimension == null)
                    {
                        continue;
                    }

                    foreach (var dimension in imageConfiguration.Dimension)
                    {
                        Point coordinate1;
                        Point coordinate2;
                        if (dimension == null ||
                            string.IsNullOrWhiteSpace(dimension.Name) ||
                            !TryParseCoordinate(dimension.Coordinate1, out coordinate1) ||
                            !TryParseCoordinate(dimension.Coordinate2, out coordinate2))
                        {
                            continue;
                        }

                        List<DimensionHighlightInfo> highlights;
                        if (!_dimensionHighlights.TryGetValue(dimension.Name, out highlights))
                        {
                            highlights = new List<DimensionHighlightInfo>();
                            _dimensionHighlights.Add(dimension.Name, highlights);
                        }

                        highlights.Add(new DimensionHighlightInfo
                        {
                            ImageName = imageConfiguration.ImageName,
                            Coordinate1 = coordinate1,
                            Coordinate2 = coordinate2
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("读取示意图尺寸坐标配置失败：" + ex);
            }
        }

        private static bool TryParseCoordinate(string coordinateText, out Point coordinate)
        {
            coordinate = new Point();
            if (string.IsNullOrWhiteSpace(coordinateText))
            {
                return false;
            }

            var parts = coordinateText.Split(',');
            double x;
            double y;
            if (parts.Length != 2 ||
                !double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y))
            {
                return false;
            }

            coordinate = new Point(x, y);
            return true;
        }

        private void SetDimensionHighlightLayout(double left, double top, double width, double height)
        {
            _dimensionHighlightLeft = left;
            _dimensionHighlightTop = top;
            _dimensionHighlightWidth = width;
            _dimensionHighlightHeight = height;
            RaisePropertyChanged(nameof(DimensionHighlightLeft));
            RaisePropertyChanged(nameof(DimensionHighlightTop));
            RaisePropertyChanged(nameof(DimensionHighlightWidth));
            RaisePropertyChanged(nameof(DimensionHighlightHeight));
        }

        private void SetDimensionHighlightVisibility(Visibility visibility)
        {
            if (_dimensionHighlightVisibility == visibility)
            {
                return;
            }

            _dimensionHighlightVisibility = visibility;
            RaisePropertyChanged(nameof(DimensionHighlightVisibility));
        }

        private sealed class DimensionImageConfiguration
        {
            public string ImageName { get; set; }

            public List<DimensionCoordinateConfiguration> Dimension { get; set; }
        }

        private sealed class DimensionCoordinateConfiguration
        {
            public string Name { get; set; }

            public string Coordinate1 { get; set; }

            public string Coordinate2 { get; set; }
        }

        private sealed class DimensionHighlightInfo
        {
            public string ImageName { get; set; }

            public Point Coordinate1 { get; set; }

            public Point Coordinate2 { get; set; }
        }
    }
}
