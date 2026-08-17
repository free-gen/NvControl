using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NvControl
{
    internal sealed class LedControl
    {
        private sealed class PreparedPoint
        {
            public int Temperature;
            public int R;
            public int G;
            public int B;
            public int Brightness;
        }

        private readonly Config _config;
        private readonly Action<string> _log;
        private readonly List<PreparedPoint> _points;

        private IntPtr _gpu;
        private NvApi.NV_GPU_CLIENT_ILLUM_ZONE_CONTROL_PARAMS _illumination;

        private int _zoneIndex = -1;

        private bool _initialized;
        private bool _targetValid;
        private bool _currentValid;

        private double _currentR;
        private double _currentG;
        private double _currentB;
        private double _currentBrightness;

        private double _targetR;
        private double _targetG;
        private double _targetB;
        private double _targetBrightness;

        private int _lastSentR = -1;
        private int _lastSentG = -1;
        private int _lastSentB = -1;
        private int _lastSentBrightness = -1;

        private DateTime _lastErrorLogUtc = DateTime.MinValue;

        public LedControl(Config config, Action<string> log)
        {
            if (config == null)
                throw new ArgumentNullException("config");

            _config = config;
            _log = log ?? delegate { };
            _points = config.TemperaturePoints.Select(PreparePoint).OrderBy(point => point.Temperature).ToList();
        }

        public bool IsInitialized
        {
            get { return _initialized; }
        }

        public bool IsDynamic
        {
            get { return _config.Mode == LightingMode.Status; }
        }

        public int ZoneIndex
        {
            get { return _zoneIndex; }
        }

        public uint ZoneType
        {
            get
            {
                if (!_initialized || _zoneIndex < 0)
                    return 0;

                return _illumination.zones[_zoneIndex].type;
            }
        }

        public string ZoneTypeName
        {
            get
            {
                if (ZoneType == NvApi.ILLUM_ZONE_TYPE_RGB)
                    return "RGB";

                if (ZoneType == NvApi.ILLUM_ZONE_TYPE_RGBW)
                    return "RGBW";

                return "UNKNOWN";
            }
        }

        public string CurrentColorHex
        {
            get
            {
                if (!_currentValid)
                    return "------";

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:X2}{1:X2}{2:X2}",
                    ClampToByte((int)Math.Round(_currentR)),
                    ClampToByte((int)Math.Round(_currentG)),
                    ClampToByte((int)Math.Round(_currentB)));
            }
        }

        public int CurrentBrightness
        {
            get
            {
                if (!_currentValid)
                    return 0;

                return Clamp((int)Math.Round(_currentBrightness), 0, 100);
            }
        }

        public void Initialize(IntPtr gpu)
        {
            _gpu = gpu;
            _illumination = NvApi.CreateIlluminationControlParameters(false);

            NvApi.GetIlluminationControl(_gpu, ref _illumination);

            int count = Math.Min((int)_illumination.numIllumZonesControl, 32);

            if (count <= 0)
                throw new InvalidOperationException("GPU exposes no NVAPI illumination zones.");

            if (_config.IllumZoneIndex >= 0)
            {
                int requested = _config.IllumZoneIndex;

                if (requested >= count)
                {
                    throw new InvalidOperationException(
                        "ILLUM_ZONE_INDEX " + requested + " is outside available range 0.." + (count - 1) + ".");
                }

                uint type = _illumination.zones[requested].type;

                if (!IsSupportedZoneType(type))
                    throw new InvalidOperationException("ILLUM_ZONE_INDEX " + requested + " is not RGB/RGBW.");

                _zoneIndex = requested;
            }
            else
            {
                _zoneIndex = FindAutomaticZone(count);
            }

            if (_zoneIndex < 0)
                throw new InvalidOperationException("No compatible RGB/RGBW illumination zone was found.");

            _initialized = true;

            _log("Illumination initialized: zone=" + _zoneIndex + ", type=" + ZoneTypeName + ".");
        }

        private int FindAutomaticZone(int count)
        {
            for (int i = 0; i < count; i++)
            {
                uint type = _illumination.zones[i].type;

                if (_config.IllumZoneType == 1)
                {
                    if (type == NvApi.ILLUM_ZONE_TYPE_RGB)
                        return i;

                    continue;
                }

                if (_config.IllumZoneType == 3)
                {
                    if (type == NvApi.ILLUM_ZONE_TYPE_RGBW)
                        return i;

                    continue;
                }

                if (IsSupportedZoneType(type))
                    return i;
            }

            return -1;
        }

        private static bool IsSupportedZoneType(uint type)
        {
            return type == NvApi.ILLUM_ZONE_TYPE_RGB || type == NvApi.ILLUM_ZONE_TYPE_RGBW;
        }

        public void ApplyStatic()
        {
            EnsureInitialized();

            int r;
            int g;
            int b;

            ParseColor(_config.StaticColor, out r, out g, out b);
            ApplyCalibration(ref r, ref g, ref b);

            int brightness = Clamp(_config.Brightness, 0, 100);

            SendColorCore(r, g, b, brightness);

            _currentR = r;
            _currentG = g;
            _currentB = b;
            _currentBrightness = brightness;

            _targetR = r;
            _targetG = g;
            _targetB = b;
            _targetBrightness = brightness;

            _targetValid = true;
            _currentValid = true;

            _lastSentR = r;
            _lastSentG = g;
            _lastSentB = b;
            _lastSentBrightness = brightness;
        }

        public void TurnOff()
        {
            if (!_initialized)
                return;

            SendColorCore(0, 0, 0, 0);

            _currentR = 0;
            _currentG = 0;
            _currentB = 0;
            _currentBrightness = 0;

            _targetR = 0;
            _targetG = 0;
            _targetB = 0;
            _targetBrightness = 0;

            _lastSentR = 0;
            _lastSentG = 0;
            _lastSentB = 0;
            _lastSentBrightness = 0;

            _currentValid = true;
            _targetValid = true;
        }

        public void UpdateTemperature(int temperature)
        {
            if (!_initialized || _config.Mode != LightingMode.Status)
                return;

            int r;
            int g;
            int b;
            int brightness;

            GetColorForTemperature(temperature, out r, out g, out b, out brightness);
            ApplyCalibration(ref r, ref g, ref b);

            _targetR = r;
            _targetG = g;
            _targetB = b;
            _targetBrightness = brightness;
            _targetValid = true;
        }

        public void Tick()
        {
            if (!_initialized || !_targetValid || _config.Mode != LightingMode.Status)
                return;

            if (!_currentValid)
            {
                _currentR = _targetR;
                _currentG = _targetG;
                _currentB = _targetB;
                _currentBrightness = _targetBrightness;
                _currentValid = true;
            }
            else
            {
                double smoothing = _config.Smoothing;

                _currentR += (_targetR - _currentR) * smoothing;
                _currentG += (_targetG - _currentG) * smoothing;
                _currentB += (_targetB - _currentB) * smoothing;
                _currentBrightness += (_targetBrightness - _currentBrightness) * smoothing;

                SnapNearTarget();
            }

            int r = ClampToByte((int)Math.Round(_currentR));
            int g = ClampToByte((int)Math.Round(_currentG));
            int b = ClampToByte((int)Math.Round(_currentB));
            int brightness = Clamp((int)Math.Round(_currentBrightness), 0, 100);

            if (r == _lastSentR &&
                g == _lastSentG &&
                b == _lastSentB &&
                brightness == _lastSentBrightness)
            {
                return;
            }

            try
            {
                SendColorCore(r, g, b, brightness);

                _lastSentR = r;
                _lastSentG = g;
                _lastSentB = b;
                _lastSentBrightness = brightness;
            }
            catch (Exception ex)
            {
                LogRuntimeError("Illumination update failed: " + ex.Message);
            }
        }

        private void SnapNearTarget()
        {
            if (Math.Abs(_targetR - _currentR) < 0.5)
                _currentR = _targetR;

            if (Math.Abs(_targetG - _currentG) < 0.5)
                _currentG = _targetG;

            if (Math.Abs(_targetB - _currentB) < 0.5)
                _currentB = _targetB;

            if (Math.Abs(_targetBrightness - _currentBrightness) < 0.5)
                _currentBrightness = _targetBrightness;
        }

        private void GetColorForTemperature(int temperature, out int r, out int g, out int b, out int brightness)
        {
            if (_points.Count == 0)
            {
                ParseColor(_config.StaticColor, out r, out g, out b);
                brightness = _config.Brightness;
                return;
            }

            PreparedPoint first = _points[0];
            PreparedPoint last = _points[_points.Count - 1];

            if (temperature <= first.Temperature)
            {
                r = first.R;
                g = first.G;
                b = first.B;
                brightness = ResolveBrightness(first);
                return;
            }

            if (temperature >= last.Temperature)
            {
                r = last.R;
                g = last.G;
                b = last.B;
                brightness = ResolveBrightness(last);
                return;
            }

            for (int i = 0; i < _points.Count - 1; i++)
            {
                PreparedPoint left = _points[i];
                PreparedPoint right = _points[i + 1];

                if (temperature < left.Temperature || temperature > right.Temperature)
                    continue;

                double range = right.Temperature - left.Temperature;
                double factor = (temperature - left.Temperature) / range;

                r = Interpolate(left.R, right.R, factor);
                g = Interpolate(left.G, right.G, factor);
                b = Interpolate(left.B, right.B, factor);
                brightness = Interpolate(ResolveBrightness(left), ResolveBrightness(right), factor);
                return;
            }

            r = last.R;
            g = last.G;
            b = last.B;
            brightness = ResolveBrightness(last);
        }

        private int ResolveBrightness(PreparedPoint point)
        {
            return point.Brightness < 0 ? _config.Brightness : point.Brightness;
        }

        private static int Interpolate(int from, int to, double factor)
        {
            return (int)Math.Round(from + (to - from) * factor);
        }

        private void SendColorCore(int r, int g, int b, int brightness)
        {
            EnsureInitialized();

            NvApi.NV_GPU_CLIENT_ILLUM_ZONE_CONTROL zone = _illumination.zones[_zoneIndex];

            if (zone.data == null || zone.data.Length < 128)
                throw new InvalidOperationException("Invalid NVAPI illumination zone data buffer.");

            zone.ctrlMode = NvApi.ILLUM_CTRL_MODE_MANUAL;

            if (zone.type == NvApi.ILLUM_ZONE_TYPE_RGB)
            {
                // R, G, B, brightnessPct
                zone.data[0] = (byte)r;
                zone.data[1] = (byte)g;
                zone.data[2] = (byte)b;
                zone.data[3] = (byte)brightness;
            }
            else if (zone.type == NvApi.ILLUM_ZONE_TYPE_RGBW)
            {
                // R, G, B, W, brightnessPct
                zone.data[0] = (byte)r;
                zone.data[1] = (byte)g;
                zone.data[2] = (byte)b;
                zone.data[3] = 0;
                zone.data[4] = (byte)brightness;
            }
            else
            {
                throw new InvalidOperationException("Unsupported illumination zone type " + zone.type + ".");
            }

            _illumination.zones[_zoneIndex] = zone;
            NvApi.SetIlluminationControl(_gpu, ref _illumination);
        }

        private PreparedPoint PreparePoint(LightingCurvePoint point)
        {
            int r;
            int g;
            int b;

            ParseColor(point.Color, out r, out g, out b);

            return new PreparedPoint
            {
                Temperature = point.Temperature,
                R = r,
                G = g,
                B = b,
                Brightness = point.Brightness
            };
        }

        private static void ParseColor(string hex, out int r, out int g, out int b)
        {
            r = int.Parse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            g = int.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            b = int.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private void ApplyCalibration(ref int r, ref int g, ref int b)
        {
            r = ClampToByte((int)Math.Round(r * _config.RedGain));
            g = ClampToByte((int)Math.Round(g * _config.GreenGain));
            b = ClampToByte((int)Math.Round(b * _config.BlueGain));
        }

        private void EnsureInitialized()
        {
            if (!_initialized)
                throw new InvalidOperationException("Lighting controller has not been initialized.");
        }

        private void LogRuntimeError(string message)
        {
            DateTime now = DateTime.UtcNow;

            if ((now - _lastErrorLogUtc).TotalSeconds < 5)
                return;

            _lastErrorLogUtc = now;
            _log(message);
        }

        private static int ClampToByte(int value)
        {
            return Clamp(value, 0, 255);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;

            if (value > max)
                return max;

            return value;
        }
    }
}