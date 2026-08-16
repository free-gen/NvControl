using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NvControl
{
    public class Config
    {
        // ---- RGB ----
        public string Mode { get; set; } = "STATUS";
        public int Brightness { get; set; } = 15;
        public string StaticColor { get; set; } = "FF4000";
        public double RedGain { get; set; } = 1.00;
        public double GreenGain { get; set; } = 0.65;
        public double BlueGain { get; set; } = 0.90;
        public double Smoothing { get; set; } = 0.15;
        public List<(int Temp, string Color, int Brightness)> TemperaturePoints { get; set; } = new List<(int, string, int)>
        {
            (0, "FF4000", 15), (49, "FF4000", 15), (50, "FF8000", 30), (90, "FF0000", 60)
        };

        // ---- NVAPI ----
        public int GpuIndex { get; set; } = 0;
        public int IllumZoneIndex { get; set; } = -1;
        public int IllumZoneType { get; set; } = 0;          // 0=AUTO, 1=RGB, 3=RGBW
        public int RgbROffset { get; set; } = 0;
        public int RgbGOffset { get; set; } = 1;
        public int RgbBOffset { get; set; } = 2;
        public int RgbBrightnessOffset { get; set; } = 3;
        public int RgbwROffset { get; set; } = 0;
        public int RgbwGOffset { get; set; } = 1;
        public int RgbwBOffset { get; set; } = 2;
        public int RgbwWOffset { get; set; } = 3;
        public int RgbwBrightnessOffset { get; set; } = 4;
        public int CtrlMode { get; set; } = 0;

        // ---- FAN CONTROL ----
        public bool FanControl { get; set; } = true;
        public string FanMode { get; set; } = "CURVE";
        public int FanSpeed { get; set; } = 30;
        public int FanCoolerId { get; set; } = 0;
        public bool FanRestoreOnExit { get; set; } = true;

        public List<(int Temp, int Speed)> FanCurvePoints { get; set; } = new List<(int, int)>
        {
            (0, 0), (50, 30), (60, 40), (70, 50), (80, 60), (90, 70)
        };

        // ---- KEYS SAVE/LOAD ----
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
        private const string KEY_RGB_R_OFFSET = "RGB_R_OFFSET";
        private const string KEY_RGB_G_OFFSET = "RGB_G_OFFSET";
        private const string KEY_RGB_B_OFFSET = "RGB_B_OFFSET";
        private const string KEY_RGB_BRIGHTNESS_OFFSET = "RGB_BRIGHTNESS_OFFSET";
        private const string KEY_RGBW_R_OFFSET = "RGBW_R_OFFSET";
        private const string KEY_RGBW_G_OFFSET = "RGBW_G_OFFSET";
        private const string KEY_RGBW_B_OFFSET = "RGBW_B_OFFSET";
        private const string KEY_RGBW_W_OFFSET = "RGBW_W_OFFSET";
        private const string KEY_RGBW_BRIGHTNESS_OFFSET = "RGBW_BRIGHTNESS_OFFSET";
        private const string KEY_CTRL_MODE = "CTRL_MODE";

        // FAN KEYS
        private const string KEY_FAN_CONTROL = "FAN_CONTROL";
        private const string KEY_FAN_MODE = "FAN_MODE";
        private const string KEY_FAN_SPEED = "FAN_SPEED";
        private const string KEY_FAN_COOLER_ID = "FAN_COOLER_ID";
        private const string KEY_FAN_RESTORE_ON_EXIT = "FAN_RESTORE_ON_EXIT";
        private const string KEY_FAN_CURVE_PREFIX = "FAN_P";

        // OLD FAN KEYS
        private const string KEY_FAN_MANUAL_OLD = "FAN_MANUAL";
        private const string KEY_FAN_CURVE_ENABLED_OLD = "FAN_CURVE_ENABLED";

        public static Config Load(string path)
        {
            if (!File.Exists(path)) return null;
            var cfg = new Config();
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            var tempPoints = new Dictionary<int, (int Temp, string Color, int Brightness)>();
            var fanPoints = new Dictionary<int, (int Temp, int Speed)>();

            bool oldManual = false;
            bool oldCurve = false;

            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith(";") || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToUpperInvariant();
                string val = line.Substring(eq + 1).Trim();

                int commentIdx = val.IndexOfAny(new char[] { '#', ';' });
                if (commentIdx >= 0) val = val.Substring(0, commentIdx).Trim();

                switch (key)
                {
                    case KEY_MODE: cfg.Mode = val.ToUpperInvariant(); break;
                    case KEY_BRIGHTNESS: if (int.TryParse(val, out int b)) cfg.Brightness = b; break;
                    case KEY_STATIC_COLOR: cfg.StaticColor = val; break;
                    case KEY_R_GAIN: if (double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double rg)) cfg.RedGain = rg; break;
                    case KEY_G_GAIN: if (double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double gg)) cfg.GreenGain = gg; break;
                    case KEY_B_GAIN: if (double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double bg)) cfg.BlueGain = bg; break;
                    case KEY_SMOOTHING: if (double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double sm)) cfg.Smoothing = sm; break;
                    case KEY_GPU_INDEX: if (int.TryParse(val, out int gpuIdx)) cfg.GpuIndex = gpuIdx; break;
                    case KEY_ILLUM_ZONE_INDEX: if (int.TryParse(val, out int zoneIdx)) cfg.IllumZoneIndex = zoneIdx; break;
                    case KEY_ILLUM_ZONE_TYPE: if (int.TryParse(val, out int zoneType)) cfg.IllumZoneType = zoneType; break;
                    case KEY_RGB_R_OFFSET: if (int.TryParse(val, out int rOff)) cfg.RgbROffset = rOff; break;
                    case KEY_RGB_G_OFFSET: if (int.TryParse(val, out int gOff)) cfg.RgbGOffset = gOff; break;
                    case KEY_RGB_B_OFFSET: if (int.TryParse(val, out int bOff)) cfg.RgbBOffset = bOff; break;
                    case KEY_RGB_BRIGHTNESS_OFFSET: if (int.TryParse(val, out int brOff)) cfg.RgbBrightnessOffset = brOff; break;
                    case KEY_RGBW_R_OFFSET: if (int.TryParse(val, out int rwOff)) cfg.RgbwROffset = rwOff; break;
                    case KEY_RGBW_G_OFFSET: if (int.TryParse(val, out int gwOff)) cfg.RgbwGOffset = gwOff; break;
                    case KEY_RGBW_B_OFFSET: if (int.TryParse(val, out int bwOff)) cfg.RgbwBOffset = bwOff; break;
                    case KEY_RGBW_W_OFFSET: if (int.TryParse(val, out int wOff)) cfg.RgbwWOffset = wOff; break;
                    case KEY_RGBW_BRIGHTNESS_OFFSET: if (int.TryParse(val, out int bwBrOff)) cfg.RgbwBrightnessOffset = bwBrOff; break;
                    case KEY_CTRL_MODE: if (int.TryParse(val, out int ctrl)) cfg.CtrlMode = ctrl; break;

                    case KEY_FAN_CONTROL: bool.TryParse(val, out bool fc); cfg.FanControl = fc; break;
                    case KEY_FAN_MODE: cfg.FanMode = val.ToUpperInvariant(); break;
                    case KEY_FAN_SPEED: int.TryParse(val, out int fs); cfg.FanSpeed = fs; break;
                    case KEY_FAN_COOLER_ID: int.TryParse(val, out int fci); cfg.FanCoolerId = fci; break;
                    case KEY_FAN_RESTORE_ON_EXIT: bool.TryParse(val, out bool froe); cfg.FanRestoreOnExit = froe; break;

                    case KEY_FAN_MANUAL_OLD: bool.TryParse(val, out bool fm); oldManual = fm; break;
                    case KEY_FAN_CURVE_ENABLED_OLD: bool.TryParse(val, out bool fce); oldCurve = fce; break;

                    default:
                        if (key.StartsWith(KEY_STATUS_PREFIX) && int.TryParse(key.Substring(KEY_STATUS_PREFIX.Length), out int idx))
                        {
                            var p = ParsePoint(val);
                            if (p.HasValue) tempPoints[idx] = p.Value;
                        }
                        else if (key.StartsWith(KEY_FAN_CURVE_PREFIX) && int.TryParse(key.Substring(KEY_FAN_CURVE_PREFIX.Length), out int fidx))
                        {
                            var fp = ParseFanPoint(val);
                            if (fp.HasValue) fanPoints[fidx] = fp.Value;
                        }
                        break;
                }
            }

            if (!lines.Any(l => l.Trim().StartsWith(KEY_FAN_CONTROL + "=", StringComparison.OrdinalIgnoreCase)))
            {
                if (oldManual && !oldCurve)
                {
                    cfg.FanControl = true;
                    cfg.FanMode = "STATIC";
                }
                else if (oldCurve)
                {
                    cfg.FanControl = true;
                    cfg.FanMode = "CURVE";
                }
            }

            if (tempPoints.Count > 0)
                cfg.TemperaturePoints = tempPoints.OrderBy(k => k.Key).Select(k => k.Value).ToList();

            if (fanPoints.Count > 0)
                cfg.FanCurvePoints = fanPoints.OrderBy(k => k.Key).Select(k => k.Value).ToList();

            return cfg;
        }

        private static (int Temp, string Color, int Brightness)? ParsePoint(string val)
        {
            var parts = val.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0].Trim(), out int t) && !string.IsNullOrWhiteSpace(parts[1]))
            {
                string c = parts[1].Trim().ToUpperInvariant();
                if (c.Length == 6 && Regex.IsMatch(c, "^[0-9A-F]{6}$"))
                {
                    int b = parts.Length >= 3 && int.TryParse(parts[2].Trim(), out int br) ? br : 0;
                    return (t, c, b);
                }
            }
            return null;
        }

        private static (int Temp, int Speed)? ParseFanPoint(string val)
        {
            var parts = val.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0].Trim(), out int t) && int.TryParse(parts[1].Trim(), out int s))
            {
                s = Math.Max(0, Math.Min(100, s));
                return (t, s);
            }
            return null;
        }

        public void Save(string path)
        {
            var sb = new StringBuilder();

            // =============== NVAPI / HARDWARE SETTINGS ===============
            sb.AppendLine("# [NVAPI / HARDWARE SETTINGS]");
            sb.AppendLine();

            sb.AppendLine($"{KEY_GPU_INDEX}={GpuIndex}");
            sb.AppendLine($"{KEY_ILLUM_ZONE_INDEX}={IllumZoneIndex}");
            sb.AppendLine($"{KEY_ILLUM_ZONE_TYPE}={IllumZoneType}");
            sb.AppendLine($"{KEY_RGB_R_OFFSET}={RgbROffset}");
            sb.AppendLine($"{KEY_RGB_G_OFFSET}={RgbGOffset}");
            sb.AppendLine($"{KEY_RGB_B_OFFSET}={RgbBOffset}");
            sb.AppendLine($"{KEY_RGB_BRIGHTNESS_OFFSET}={RgbBrightnessOffset}");
            sb.AppendLine($"{KEY_RGBW_R_OFFSET}={RgbwROffset}");
            sb.AppendLine($"{KEY_RGBW_G_OFFSET}={RgbwGOffset}");
            sb.AppendLine($"{KEY_RGBW_B_OFFSET}={RgbwBOffset}");
            sb.AppendLine($"{KEY_RGBW_W_OFFSET}={RgbwWOffset}");
            sb.AppendLine($"{KEY_RGBW_BRIGHTNESS_OFFSET}={RgbwBrightnessOffset}");
            sb.AppendLine($"{KEY_CTRL_MODE}={CtrlMode}");
            sb.AppendLine();

            // =============== RGB SETTINGS ===============
            sb.AppendLine("# [RGB SETTINGS]");
            sb.AppendLine();

            sb.AppendLine("# CALIBRATION: R,G,B");
            sb.AppendLine($"{KEY_R_GAIN}={RedGain.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
            sb.AppendLine($"{KEY_G_GAIN}={GreenGain.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
            sb.AppendLine($"{KEY_B_GAIN}={BlueGain.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
            sb.AppendLine();

            sb.AppendLine($"{KEY_MODE}={Mode} # STATIC / STATUS");
            sb.AppendLine($"{KEY_BRIGHTNESS}={Brightness} # BRIGHTNESS 0-100");
            sb.AppendLine($"{KEY_SMOOTHING}={Smoothing.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} # SMOOTHING 0.01-0.50");
            sb.AppendLine($"{KEY_STATIC_COLOR}={StaticColor} # STATIC COLOR (HEX)");
            sb.AppendLine();

            sb.AppendLine("# TEMP STATUS POINTS | MAX 9 POINTS");
            sb.AppendLine("# TEMPERATURE,COLOR,BRIGHTNESS");
            for (int i = 0; i < TemperaturePoints.Count && i < 9; i++)
            {
                var pt = TemperaturePoints[i];
                sb.AppendLine($"{KEY_STATUS_PREFIX}{i + 1}={pt.Temp},{pt.Color},{pt.Brightness}");
            }
            sb.AppendLine();

            // =============== FAN SETTINGS ===============
            sb.AppendLine("# [FAN SETTINGS]");
            sb.AppendLine();

            sb.AppendLine($"{KEY_FAN_CONTROL}={(FanControl ? "TRUE" : "FALSE")} # TRUE / FALSE");
            sb.AppendLine($"{KEY_FAN_MODE}={FanMode} # CURVE / STATIC");
            sb.AppendLine($"{KEY_FAN_SPEED}={FanSpeed} # STATIC FAN SPEED 0-100");
            sb.AppendLine($"{KEY_FAN_COOLER_ID}={FanCoolerId} # FAN COOLER ID");
            sb.AppendLine($"{KEY_FAN_RESTORE_ON_EXIT}={(FanRestoreOnExit ? "TRUE" : "FALSE")} # TRUE / FALSE");
            sb.AppendLine();

            sb.AppendLine("# FAN CURVE POINTS | MAX 9 POINTS");
            sb.AppendLine("# TEMPERATURE,SPEED");
            for (int i = 0; i < FanCurvePoints.Count && i < 9; i++)
            {
                var pt = FanCurvePoints[i];
                sb.AppendLine($"{KEY_FAN_CURVE_PREFIX}{i + 1}={pt.Temp},{pt.Speed}");
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
    }
}