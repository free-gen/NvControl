using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NvControl
{
    public enum LightingMode
    {
        Static,
        Status
    }

    public enum FanControlMode
    {
        Static,
        Curve
    }

    public sealed class LightingCurvePoint
    {
        public int Temperature { get; set; }
        public string Color { get; set; }

        // -1 = use global BRIGHTNESS, 0..100 = literal brightness
        public int Brightness { get; set; }

        public LightingCurvePoint()
        {
        }

        public LightingCurvePoint(int temperature, string color, int brightness)
        {
            Temperature = temperature;
            Color = color;
            Brightness = brightness;
        }
    }

    public sealed class FanCurvePoint
    {
        public int Temperature { get; set; }
        public int Speed { get; set; }

        public FanCurvePoint()
        {
        }

        public FanCurvePoint(int temperature, int speed)
        {
            Temperature = temperature;
            Speed = speed;
        }
    }

    public sealed class Config
    {
        // RGB
        public LightingMode Mode { get; set; } = LightingMode.Status;
        public int Brightness { get; set; } = 15;
        public string StaticColor { get; set; } = "FF4000";
        public double RedGain { get; set; } = 1.00;
        public double GreenGain { get; set; } = 0.65;
        public double BlueGain { get; set; } = 0.90;

        // 0 < Smoothing <= 1. Lower = slower, higher = faster.
        public double Smoothing { get; set; } = 0.15;

        public List<LightingCurvePoint> TemperaturePoints { get; set; } = new List<LightingCurvePoint>
        {
            new LightingCurvePoint(0, "FF4000", 15),
            new LightingCurvePoint(49, "FF4000", 15),
            new LightingCurvePoint(50, "FF8000", 30),
            new LightingCurvePoint(90, "FF0000", 60)
        };

        // GPU / illumination
        public int GpuIndex { get; set; } = 0;
        public int IllumZoneIndex { get; set; } = -1; // -1 = automatic
        public int IllumZoneType { get; set; } = 0;   // 0 = auto, 1 = RGB, 3 = RGBW

        // Fan
        public bool FanControl { get; set; } = true;
        public FanControlMode FanMode { get; set; } = FanControlMode.Curve;
        public int FanSpeed { get; set; } = 30;
        public int MinFanSpeed { get; set; } = 30;
        public int FanCoolerId { get; set; } = 0;
        public bool FanRestoreOnExit { get; set; } = true;

        public List<FanCurvePoint> FanCurvePoints { get; set; } = new List<FanCurvePoint>
        {
            new FanCurvePoint(0, 0),
            new FanCurvePoint(40, 30),
            new FanCurvePoint(60, 40),
            new FanCurvePoint(70, 50),
            new FanCurvePoint(80, 60),
            new FanCurvePoint(90, 70)
        };

        private const string KEY_MODE = "MODE";
        private const string KEY_BRIGHTNESS = "BRIGHTNESS";
        private const string KEY_STATIC_COLOR = "STATIC_COLOR";
        private const string KEY_R_GAIN = "R_GAIN";
        private const string KEY_G_GAIN = "G_GAIN";
        private const string KEY_B_GAIN = "B_GAIN";
        private const string KEY_SMOOTHING = "SMOOTHING";
        private const string KEY_STATUS_PREFIX = "STATUS_P";

        private const string KEY_GPU_INDEX = "GPU_INDEX";
        private const string KEY_ILLUM_ZONE_INDEX = "ILLUM_ZONE_INDEX";
        private const string KEY_ILLUM_ZONE_TYPE = "ILLUM_ZONE_TYPE";

        private const string KEY_FAN_CONTROL = "FAN_CONTROL";
        private const string KEY_FAN_MODE = "FAN_MODE";
        private const string KEY_FAN_SPEED = "FAN_SPEED";
        private const string KEY_MIN_SPEED = "MIN_SPEED";
        private const string KEY_FAN_COOLER_ID = "FAN_COOLER_ID";
        private const string KEY_FAN_RESTORE_ON_EXIT = "FAN_RESTORE_ON_EXIT";
        private const string KEY_FAN_CURVE_PREFIX = "FAN_P";

        // Legacy fan keys
        private const string KEY_FAN_MANUAL_OLD = "FAN_MANUAL";
        private const string KEY_FAN_CURVE_ENABLED_OLD = "FAN_CURVE_ENABLED";

        public static Config Load(string path)
        {
            if (!File.Exists(path))
                return null;

            var config = new Config();
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);

            var lightingPoints = new Dictionary<int, LightingCurvePoint>();
            var fanPoints = new Dictionary<int, FanCurvePoint>();

            bool oldManual = false;
            bool oldCurve = false;
            bool sawFanControl = false;

            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string line = lines[lineIndex].Trim();

                if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith(";"))
                    continue;

                int equalsIndex = line.IndexOf('=');
                if (equalsIndex <= 0)
                    continue;

                string key = line.Substring(0, equalsIndex).Trim().ToUpperInvariant();
                string value = RemoveInlineComment(line.Substring(equalsIndex + 1).Trim());
                int configLine = lineIndex + 1;

                switch (key)
                {
                    case KEY_MODE:
                        config.Mode = ParseLightingMode(value, configLine);
                        break;

                    case KEY_BRIGHTNESS:
                        config.Brightness = ParseInt(value, key, configLine);
                        break;

                    case KEY_STATIC_COLOR:
                        config.StaticColor = value.ToUpperInvariant();
                        break;

                    case KEY_R_GAIN:
                        config.RedGain = ParseDouble(value, key, configLine);
                        break;

                    case KEY_G_GAIN:
                        config.GreenGain = ParseDouble(value, key, configLine);
                        break;

                    case KEY_B_GAIN:
                        config.BlueGain = ParseDouble(value, key, configLine);
                        break;

                    case KEY_SMOOTHING:
                        config.Smoothing = ParseDouble(value, key, configLine);
                        break;

                    case KEY_GPU_INDEX:
                        config.GpuIndex = ParseInt(value, key, configLine);
                        break;

                    case KEY_ILLUM_ZONE_INDEX:
                        config.IllumZoneIndex = ParseInt(value, key, configLine);
                        break;

                    case KEY_ILLUM_ZONE_TYPE:
                        config.IllumZoneType = ParseInt(value, key, configLine);
                        break;

                    case KEY_FAN_CONTROL:
                        config.FanControl = ParseBool(value, key, configLine);
                        sawFanControl = true;
                        break;

                    case KEY_FAN_MODE:
                        config.FanMode = ParseFanMode(value, configLine);
                        break;

                    case KEY_FAN_SPEED:
                        config.FanSpeed = ParseInt(value, key, configLine);
                        break;

                    case KEY_FAN_COOLER_ID:
                        config.FanCoolerId = ParseInt(value, key, configLine);
                        break;

                    case KEY_MIN_SPEED:
                        config.MinFanSpeed = ParseInt(value, key, configLine);
                        break;

                    case KEY_FAN_RESTORE_ON_EXIT:
                        config.FanRestoreOnExit = ParseBool(value, key, configLine);
                        break;

                    case KEY_FAN_MANUAL_OLD:
                        oldManual = ParseBool(value, key, configLine);
                        break;

                    case KEY_FAN_CURVE_ENABLED_OLD:
                        oldCurve = ParseBool(value, key, configLine);
                        break;

                    default:
                        if (key.StartsWith(KEY_STATUS_PREFIX, StringComparison.OrdinalIgnoreCase))
                        {
                            string suffix = key.Substring(KEY_STATUS_PREFIX.Length);
                            int pointIndex;

                            if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out pointIndex))
                                lightingPoints[pointIndex] = ParseLightingPoint(value, configLine);
                        }
                        else if (key.StartsWith(KEY_FAN_CURVE_PREFIX, StringComparison.OrdinalIgnoreCase))
                        {
                            string suffix = key.Substring(KEY_FAN_CURVE_PREFIX.Length);
                            int pointIndex;

                            if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out pointIndex))
                                fanPoints[pointIndex] = ParseFanPoint(value, configLine);
                        }

                        // Unknown and old RGB offset keys are intentionally ignored.
                        break;
                }
            }

            if (!sawFanControl)
            {
                if (oldCurve)
                {
                    config.FanControl = true;
                    config.FanMode = FanControlMode.Curve;
                }
                else if (oldManual)
                {
                    config.FanControl = true;
                    config.FanMode = FanControlMode.Static;
                }
            }

            if (lightingPoints.Count > 0)
                config.TemperaturePoints = lightingPoints.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList();

            if (fanPoints.Count > 0)
                config.FanCurvePoints = fanPoints.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList();

            return config;
        }

        public void Validate()
        {
            if (GpuIndex < 0 || GpuIndex >= 64)
                throw new InvalidDataException("GPU_INDEX MUST BE BETWEEN 0 AND 63.");

            if (IllumZoneIndex < -1 || IllumZoneIndex >= 32)
                throw new InvalidDataException("ILLUM_ZONE_INDEX MUST BE -1 OR BETWEEN 0 AND 31.");

            if (IllumZoneType != 0 && IllumZoneType != 1 && IllumZoneType != 3)
                throw new InvalidDataException("ILLUM_ZONE_TYPE MUST BE 0 (AUTO), 1 (RGB), OR 3 (RGBW).");

            if (Brightness < 0 || Brightness > 100)
                throw new InvalidDataException("BRIGHTNESS MUST BE BETWEEN 0 AND 100.");

            StaticColor = NormalizeColor(StaticColor, "STATIC_COLOR");

            ValidateGain(RedGain, "R_GAIN");
            ValidateGain(GreenGain, "G_GAIN");
            ValidateGain(BlueGain, "B_GAIN");

            if (double.IsNaN(Smoothing) || double.IsInfinity(Smoothing) || Smoothing <= 0.0 || Smoothing > 1.0)
                throw new InvalidDataException("SMOOTHING MUST BE GREATER THAN 0 AND LESS THAN OR EQUAL TO 1.");

            ValidateLightingCurve();

            if (FanSpeed < 0 || FanSpeed > 100)
                throw new InvalidDataException("FAN_SPEED MUST BE BETWEEN 0 AND 100.");

            if (MinFanSpeed < 0 || MinFanSpeed > 100)
                throw new InvalidDataException("MIN_SPEED MUST BE BETWEEN 0 AND 100.");

            if (FanCoolerId < 0)
                throw new InvalidDataException("FAN_COOLER_ID MUST BE A NON-NEGATIVE INTEGER.");

            ValidateFanCurve();
        }

        private void ValidateLightingCurve()
        {
            if (TemperaturePoints == null)
                TemperaturePoints = new List<LightingCurvePoint>();

            if (TemperaturePoints.Count > 9)
                throw new InvalidDataException("A MAXIMUM OF 9 STATUS_P POINTS IS SUPPORTED.");

            if (Mode == LightingMode.Status && TemperaturePoints.Count == 0)
                throw new InvalidDataException("STATUS MODE REQUIRES AT LEAST ONE STATUS_P POINT.");

            foreach (LightingCurvePoint point in TemperaturePoints)
            {
                if (point == null)
                    throw new InvalidDataException("STATUS CURVE CONTAINS A NULL POINT.");

                if (point.Temperature < -100 || point.Temperature > 200)
                    throw new InvalidDataException("STATUS TEMPERATURE MUST BE BETWEEN -100 AND 200 °C.");

                point.Color = NormalizeColor(point.Color, "STATUS color");

                if (point.Brightness < -1 || point.Brightness > 100)
                    throw new InvalidDataException("STATUS BRIGHTNESS MUST BE -1 OR BETWEEN 0 AND 100.");
            }

            TemperaturePoints = TemperaturePoints.OrderBy(point => point.Temperature).ToList();

            for (int i = 1; i < TemperaturePoints.Count; i++)
            {
                if (TemperaturePoints[i].Temperature <= TemperaturePoints[i - 1].Temperature)
                    throw new InvalidDataException("STATUS TEMPERATURES MUST BE UNIQUE.");
            }
        }

        private void ValidateFanCurve()
        {
            if (FanCurvePoints == null)
                FanCurvePoints = new List<FanCurvePoint>();

            if (FanCurvePoints.Count > 9)
                throw new InvalidDataException("A MAXIMUM OF 9 FAN_P POINTS IS SUPPORTED.");

            if (FanControl && FanMode == FanControlMode.Curve && FanCurvePoints.Count == 0)
                throw new InvalidDataException("CURVE FAN MODE REQUIRES AT LEAST ONE FAN_P POINT.");

            foreach (FanCurvePoint point in FanCurvePoints)
            {
                if (point == null)
                    throw new InvalidDataException("FAN CURVE CONTAINS A NULL POINT.");

                if (point.Temperature < -100 || point.Temperature > 200)
                    throw new InvalidDataException("FAN TEMPERATURE MUST BE BETWEEN -100 AND 200 °C.");

                if (point.Speed < 0 || point.Speed > 100)
                    throw new InvalidDataException("FAN SPEED MUST BE BETWEEN 0 AND 100.");
            }

            FanCurvePoints = FanCurvePoints.OrderBy(point => point.Temperature).ToList();

            for (int i = 1; i < FanCurvePoints.Count; i++)
            {
                if (FanCurvePoints[i].Temperature <= FanCurvePoints[i - 1].Temperature)
                    throw new InvalidDataException("FAN CURVE TEMPERATURES MUST BE UNIQUE.");
            }
        }

        private static void ValidateGain(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value > 10.0)
                throw new InvalidDataException(name + " MUST BE BETWEEN 0.0 AND 10.0.");
        }

        public void Save(string path)
        {
            Validate();

            var builder = new StringBuilder();

            builder.AppendLine("# [NVAPI / HARDWARE SETTINGS]");
            builder.AppendLine();
            builder.AppendLine(KEY_GPU_INDEX + "=" + GpuIndex.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine(KEY_ILLUM_ZONE_INDEX + "=" + IllumZoneIndex.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine(KEY_ILLUM_ZONE_TYPE + "=" + IllumZoneType.ToString(CultureInfo.InvariantCulture) + " # 0=AUTO, 1=RGB, 3=RGBW");
            builder.AppendLine();

            builder.AppendLine("# [RGB SETTINGS]");
            builder.AppendLine();
            builder.AppendLine(KEY_MODE + "=" + LightingModeToString(Mode) + " # STATIC / STATUS");
            builder.AppendLine(KEY_BRIGHTNESS + "=" + Brightness.ToString(CultureInfo.InvariantCulture) + " # 0-100");
            builder.AppendLine(KEY_STATIC_COLOR + "=" + StaticColor + " # RRGGBB");
            builder.AppendLine();
            builder.AppendLine(KEY_R_GAIN + "=" + RedGain.ToString("0.00", CultureInfo.InvariantCulture));
            builder.AppendLine(KEY_G_GAIN + "=" + GreenGain.ToString("0.00", CultureInfo.InvariantCulture));
            builder.AppendLine(KEY_B_GAIN + "=" + BlueGain.ToString("0.00", CultureInfo.InvariantCulture));
            builder.AppendLine();
            builder.AppendLine(KEY_SMOOTHING + "=" + Smoothing.ToString("0.00", CultureInfo.InvariantCulture) + " # >0..1; LOWER = SLOWER");
            builder.AppendLine();
            builder.AppendLine("# TEMP,COLOR,BRIGHTNESS");
            builder.AppendLine("# BRIGHTNESS=-1 MEANS USE GLOABAL BRIGHTNESS.");
            builder.AppendLine("# BRIGHTNESS=0 REALLY TURNS ILLUMINATION BRIGHTNESS to 0.");

            for (int i = 0; i < TemperaturePoints.Count; i++)
            {
                LightingCurvePoint point = TemperaturePoints[i];
                builder.AppendLine(
                    KEY_STATUS_PREFIX +
                    (i + 1).ToString(CultureInfo.InvariantCulture) + "=" +
                    point.Temperature.ToString(CultureInfo.InvariantCulture) + "," +
                    point.Color + "," +
                    point.Brightness.ToString(CultureInfo.InvariantCulture));
            }

            builder.AppendLine();
            builder.AppendLine("# [FAN SETTINGS]");
            builder.AppendLine();
            builder.AppendLine(KEY_FAN_CONTROL + "=" + (FanControl ? "TRUE" : "FALSE"));
            builder.AppendLine(KEY_FAN_MODE + "=" + FanModeToString(FanMode) + " # STATIC / CURVE");
            builder.AppendLine(KEY_FAN_SPEED + "=" + FanSpeed.ToString(CultureInfo.InvariantCulture) + " # STATIC SPEED 0-100");
            builder.AppendLine(KEY_MIN_SPEED + "=" + MinFanSpeed.ToString(CultureInfo.InvariantCulture) + " # VALUES BELOW THIS MEAN AUTO/STOP");
            builder.AppendLine(KEY_FAN_COOLER_ID + "=" + FanCoolerId.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine(KEY_FAN_RESTORE_ON_EXIT + "=" + (FanRestoreOnExit ? "TRUE" : "FALSE"));
            builder.AppendLine();
            builder.AppendLine("# POINT --> TEMPERATURE,SPEED");

            for (int i = 0; i < FanCurvePoints.Count; i++)
            {
                FanCurvePoint point = FanCurvePoints[i];
                builder.AppendLine(
                    KEY_FAN_CURVE_PREFIX +
                    (i + 1).ToString(CultureInfo.InvariantCulture) + "=" +
                    point.Temperature.ToString(CultureInfo.InvariantCulture) + "," +
                    point.Speed.ToString(CultureInfo.InvariantCulture));
            }

            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }

        private static LightingCurvePoint ParseLightingPoint(string value, int line)
        {
            string[] parts = value.Split(new[] { ',' }, StringSplitOptions.None);

            if (parts.Length < 2 || parts.Length > 3)
                throw new InvalidDataException("INVALID STATUS POINT AT CONFIG LINE " + line + ".");

            int temperature = ParseInt(parts[0].Trim(), "STATUS TEMPERATURE", line);
            string color = parts[1].Trim().ToUpperInvariant();
            int brightness = -1;

            if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]))
                brightness = ParseInt(parts[2].Trim(), "STATUS BRIGHTNESS", line);

            return new LightingCurvePoint(temperature, color, brightness);
        }

        private static FanCurvePoint ParseFanPoint(string value, int line)
        {
            string[] parts = value.Split(new[] { ',' }, StringSplitOptions.None);

            if (parts.Length != 2)
                throw new InvalidDataException("INVALID FAN POINT AT CONFIG LINE " + line + ".");

            int temperature = ParseInt(parts[0].Trim(), "FAN TEMPERATURE", line);
            int speed = ParseInt(parts[1].Trim(), "FAN SPEED", line);

            return new FanCurvePoint(temperature, speed);
        }

        private static int ParseInt(string value, string key, int line)
        {
            int result;

            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                throw new InvalidDataException("INVALID INTEGER FOR " + key + " AT CONFIG LINE " + line + ".");

            return result;
        }

        private static double ParseDouble(string value, string key, int line)
        {
            double result;

            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                throw new InvalidDataException("INVALID NUMBER FOR " + key + " AT CONFIG LINE " + line + ".");

            return result;
        }

        private static bool ParseBool(string value, string key, int line)
        {
            bool result;

            if (!bool.TryParse(value, out result))
                throw new InvalidDataException("INVALID BOOLEAN FOR " + key + " AT CONFIG LINE " + line + ". USE TRUE OR FALSE.");

            return result;
        }

        private static LightingMode ParseLightingMode(string value, int line)
        {
            if (value.Equals("STATIC", StringComparison.OrdinalIgnoreCase))
                return LightingMode.Static;

            if (value.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
                return LightingMode.Status;

            throw new InvalidDataException("INVALID MODE AT CONFIG LINE " + line + ". USE STATIC OR STATUS.");
        }

        private static FanControlMode ParseFanMode(string value, int line)
        {
            if (value.Equals("STATIC", StringComparison.OrdinalIgnoreCase))
                return FanControlMode.Static;

            if (value.Equals("CURVE", StringComparison.OrdinalIgnoreCase))
                return FanControlMode.Curve;

            throw new InvalidDataException("INVALID FAN_MODE AT CONFIG LINE " + line + ". USE STATIC OR CURVE.");
        }

        private static string LightingModeToString(LightingMode mode)
        {
            return mode == LightingMode.Static ? "STATIC" : "STATUS";
        }

        private static string FanModeToString(FanControlMode mode)
        {
            return mode == FanControlMode.Static ? "STATIC" : "CURVE";
        }

        private static string RemoveInlineComment(string value)
        {
            int commentIndex = value.IndexOfAny(new[] { '#', ';' });

            if (commentIndex >= 0)
                value = value.Substring(0, commentIndex).Trim();

            return value;
        }

        private static string NormalizeColor(string color, string name)
        {
            if (string.IsNullOrWhiteSpace(color))
                throw new InvalidDataException(name + " CANNOT BE EMPTY.");

            color = color.Trim().ToUpperInvariant();

            if (!Regex.IsMatch(color, "^[0-9A-F]{6}$"))
                throw new InvalidDataException(name + " MUST CONTAIN EXACTLY SIX HEXADECIMAL CHARACTERS.");

            return color;
        }
    }
}