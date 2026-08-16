using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using NvAPIWrapper.GPU;

namespace NvControl
{
    internal class Program
    {
        private const int UPDATE_INTERVAL_MS = 50;
        private static Config _config;
        private static NvApi.NV_ILLUM_PARAMS _illumParams;
        private static int _zoneIndex = -1;
        private static IntPtr _hGpu;
        private static bool _running = true;
        private static bool _showConsole;

        // ---- COLOR STATE ----
        private static double _currentR = -1, _currentG = -1, _currentB = -1, _currentBrightness = -1;
        private static int _lastSentR = -1, _lastSentG = -1, _lastSentB = -1, _lastSentBrightness = -1;
        private static string _lastColor = "";

        // ---- FAN STATE ----
        private static PhysicalGPU _physicalGpu = null;
        private static GPUCoolerInformation _coolerInfo = null;
        private static int _originalFanLevel = -1;
        private static int _fanCoolerId = 0;
        private static int _lastSentFanSpeed = -1;

        // ---- CONSOLE OUTPUT ----
        private static int _consoleStartRow = 0;

        // ---- CONSOLE CONTROL HANDLER ----
        private delegate bool ConsoleCtrlHandler(int dwCtrlType);
        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, bool add);
        private static ConsoleCtrlHandler _consoleHandler = null;

        [DllImport("kernel32.dll")] private static extern bool AllocConsole();
        [DllImport("kernel32.dll")] private static extern bool FreeConsole();

        private static void Main(string[] args)
        {
            // KILL OTHER INSTANCES
            string name = Process.GetCurrentProcess().ProcessName;
            foreach (var p in Process.GetProcessesByName(name))
                if (p.Id != Process.GetCurrentProcess().Id) try { p.Kill(); p.WaitForExit(500); } catch { }

            // ARGUMENTS
            _showConsole = !args.Any(a => a.Equals("-s", StringComparison.OrdinalIgnoreCase) ||
                                          a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
            if (_showConsole)
            {
                AllocConsole();
                Console.Title = "NvControl";
            }

            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.cfg");
                _config = Config.Load(configPath) ?? new Config();
                if (!File.Exists(configPath)) _config.Save(configPath);

                // ---- CONSOLE CONTROL HANDLER ----
                _consoleHandler = new ConsoleCtrlHandler(OnConsoleCtrl);
                SetConsoleCtrlHandler(_consoleHandler, true);
                AppDomain.CurrentDomain.ProcessExit += (s, e) => RestoreFanControl();
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; _running = false; };

                // ---- NVAPI/RGB INITIALIZATION ----
                int paramsSize = Marshal.SizeOf(typeof(NvApi.NV_ILLUM_PARAMS));
                uint version = (uint)(paramsSize | (1u << 16));

                NvApi.Initialize();
                _hGpu = NvApi.GetPhysicalGpu(_config.GpuIndex);

                _illumParams = new NvApi.NV_ILLUM_PARAMS
                {
                    version = version,
                    bDefault = 0,
                    numIllumZonesControl = 0,
                    reserved = new byte[64],
                    zones = new NvApi.NV_ILLUM_ZONE[32]
                };
                for (int i = 0; i < 32; i++)
                    _illumParams.zones[i] = new NvApi.NV_ILLUM_ZONE { data = new byte[128], reserved = new byte[64] };

                NvApi.GetIllumination(_hGpu, ref _illumParams);

                // RGB ZONE SELECTION
                if (_config.IllumZoneIndex != -1)
                {
                    int idx = _config.IllumZoneIndex;
                    if (idx >= 0 && idx < _illumParams.numIllumZonesControl)
                        _zoneIndex = idx;
                    else
                        throw new Exception($"Specified zone index {idx} is invalid (zones count: {_illumParams.numIllumZonesControl})");
                }
                else
                {
                    for (int i = 0; i < Math.Min(_illumParams.numIllumZonesControl, 32); i++)
                    {
                        uint type = _illumParams.zones[i].type;
                        bool match = false;
                        if (_config.IllumZoneType == 0)
                            match = (type == 1 || type == 3);
                        else if (_config.IllumZoneType == 1)
                            match = (type == 1);
                        else if (_config.IllumZoneType == 3)
                            match = (type == 3);
                        else
                            match = (type == 1 || type == 3);
                        if (match)
                        {
                            _zoneIndex = i;
                            break;
                        }
                    }
                }
                if (_zoneIndex == -1)
                {
                    if (_showConsole) { Console.WriteLine("RGB ZONE NOT FOUND"); Console.ReadLine(); }
                    return;
                }

                // ---- FAN INITIALIZATION ----
                InitializeFanControl();

                // ---- CONSOLE OUTPUT (only if console is visible) ----
                if (_showConsole)
                {
                    Console.Clear();
                    Console.WriteLine(new string('-', 40));
                    Console.WriteLine($"NvControl v0.0.3 by FreeGen");
                    Console.WriteLine(new string('-', 40));
                    Console.WriteLine($"Requires Administrator privileges to control fan speed.");
                    Console.WriteLine($"For silent mode, use -s or --silent");
                    Console.WriteLine(new string('-', 40));
                    Console.WriteLine($"GPU INDEX        : {_config.GpuIndex}");
                    Console.WriteLine($"ZONE INDEX       : {_zoneIndex} (TYPE: {(_illumParams.zones[_zoneIndex].type == 1 ? "RGB" : "RGBW")})");
                    Console.WriteLine($"RGB MODE         : {_config.Mode}");
                    if (_config.FanControl)
                    {
                        Console.WriteLine($"FAN CONTROL      : ENABLED");
                        Console.WriteLine($"FAN MODE         : {_config.FanMode}");
                        Console.WriteLine($"FAN COOLER ID    : {_fanCoolerId}");
                        if (_config.FanMode == "STATIC")
                            Console.WriteLine($"FAN FIXED SPEED  : {_config.FanSpeed}%");
                    }
                    else
                        Console.WriteLine($"FAN CONTROL      : DISABLED");
                    Console.WriteLine(new string('-', 40));
                    _consoleStartRow = Console.CursorTop;
                }

                // ---- MAIN LOOP ----
                bool needLoop = (_config.Mode == "STATUS") || (_config.FanControl && _config.FanMode == "CURVE");

                if (!needLoop)
                {
                    SetColor(_config.StaticColor, _config.Brightness);
                    if (_showConsole)
                    {
                        Console.WriteLine("\nCOLOR SET. PRESS ENTER TO EXIT...");
                        Console.ReadLine();
                    }
                    return;
                }

                while (_running)
                {
                    int temp = GetGpuTemperature();

                    if (_config.Mode == "STATUS")
                        UpdateColor(temp);

                    if (_config.FanControl && _config.FanMode == "CURVE")
                        UpdateFanSpeed(temp);

                    if (_showConsole)
                        DisplayStatus(temp);

                    Thread.Sleep(UPDATE_INTERVAL_MS);
                }
            }
            catch (Exception ex)
            {
                if (_showConsole) { Console.WriteLine($"\nERROR: {ex.Message}\nPRESS ENTER TO EXIT..."); Console.ReadLine(); }
            }
            finally
            {
                RestoreFanControl();
                if (_showConsole) { Thread.Sleep(500); FreeConsole(); }
            }
        }

        // ---- CONSOLE CONTROL HANDLER ----
        private static bool OnConsoleCtrl(int dwCtrlType)
        {
            if (dwCtrlType == 2 || dwCtrlType == 3 || dwCtrlType == 4 || dwCtrlType == 0)
            {
                RestoreFanControl();
                Thread.Sleep(200);
            }
            return false;
        }

        // ---- FAN INITIALIZATION ----
        private static void InitializeFanControl()
        {
            if (!_config.FanControl) return;

            try
            {
                var gpus = PhysicalGPU.GetPhysicalGPUs();
                if (gpus == null || gpus.Length == 0)
                {
                    if (_showConsole) Console.WriteLine("NVIDIA GPU NOT FOUND.");
                    return;
                }

                int gpuIndex = _config.GpuIndex;
                if (gpuIndex >= gpus.Length)
                {
                    if (_showConsole) Console.WriteLine($"GPU INDEX {gpuIndex} OUT OF RANGE (0-{gpus.Length - 1}), USING 0.");
                    gpuIndex = 0;
                }
                _physicalGpu = gpus[gpuIndex];
                _coolerInfo = _physicalGpu.CoolerInformation;

                var coolers = _coolerInfo.Coolers;
                if (coolers == null || !coolers.Any())
                {
                    if (_showConsole) Console.WriteLine("NO COOLERS FOUND.");
                    return;
                }

                var targetCooler = coolers.FirstOrDefault(c => c.CoolerId == _config.FanCoolerId);
                if (targetCooler == null)
                {
                    targetCooler = coolers.First();
                    if (_showConsole) Console.WriteLine($"COOLER ID {_config.FanCoolerId} NOT FOUND, USING FIRST (ID={targetCooler.CoolerId})");
                }
                _fanCoolerId = targetCooler.CoolerId;
                _originalFanLevel = targetCooler.CurrentLevel;

                if (_showConsole)
                    Console.WriteLine($"FAN CONTROL INITIALIZED: COOLER ID={_fanCoolerId}, CURRENT LEVEL={_originalFanLevel}");

                if (_config.FanMode == "STATIC")
                {
                    int speed = Clamp(_config.FanSpeed, 0, 100);
                    _coolerInfo.SetCoolerSettings(_fanCoolerId, speed);
                    _lastSentFanSpeed = speed;
                    if (_showConsole) Console.WriteLine($"FAN SPEED SET TO {speed}% (STATIC MODE)");
                }
                else if (_config.FanMode == "CURVE")
                {
                    if (_showConsole) Console.WriteLine("FAN CURVE MODE ACTIVE.");
                }
                else
                {
                    if (_showConsole) Console.WriteLine($"UNKNOWN FANMODE: {_config.FanMode}, FAN CONTROL DISABLED.");
                    _config.FanControl = false; // отключаем, чтобы не мешал
                }
            }
            catch (Exception ex)
            {
                if (_showConsole) Console.WriteLine($"FAN INITIALIZATION ERROR: {ex.Message}");
            }
        }

        // ---- FAN RESTORE ----
        private static void RestoreFanControl()
        {
            if (!_config.FanRestoreOnExit || _coolerInfo == null) return;
            if (!_config.FanControl) return;

            try
            {
                _coolerInfo.RestoreCoolerSettingsToDefault(new[] { _fanCoolerId });
                if (_showConsole) Console.WriteLine($"FAN RESTORED TO AUTOMATIC CONTROL (ID={_fanCoolerId})");
            }
            catch (Exception ex)
            {
                if (_showConsole) Console.WriteLine($"FAN RESTORE ERROR: {ex.Message}");
            }
        }

        // ---- GET GPU TEMPERATURE ----
        private static int GetGpuTemperature()
        {
            try { return NvApi.GetGpuTemperature(_hGpu); }
            catch { return 0; } // fallback
        }

        // ---- COLOR UPDATE ----
        private static void UpdateColor(int temp)
        {
            try
            {
                var (hex, targetBrightness) = GetColorAndBrightnessForTemperature(temp);
                int tr = Convert.ToInt32(hex.Substring(0, 2), 16);
                int tg = Convert.ToInt32(hex.Substring(2, 2), 16);
                int tb = Convert.ToInt32(hex.Substring(4, 2), 16);
                tr = (int)Math.Round(tr * _config.RedGain);
                tg = (int)Math.Round(tg * _config.GreenGain);
                tb = (int)Math.Round(tb * _config.BlueGain);
                tr = Clamp(tr, 0, 255);
                tg = Clamp(tg, 0, 255);
                tb = Clamp(tb, 0, 255);
                targetBrightness = Clamp(targetBrightness, 0, 100);

                if (_currentR == -1) { _currentR = tr; _currentG = tg; _currentB = tb; _currentBrightness = targetBrightness; _lastColor = hex; }

                if (hex != _lastColor || Math.Abs(_currentBrightness - targetBrightness) > 0.5)
                {
                    _lastColor = hex;
                }

                double s = _config.Smoothing;
                _currentR += (tr - _currentR) * s;
                _currentG += (tg - _currentG) * s;
                _currentB += (tb - _currentB) * s;
                _currentBrightness += (targetBrightness - _currentBrightness) * s;

                if (Math.Abs(tr - _currentR) < 0.5 && Math.Abs(tg - _currentG) < 0.5 &&
                    Math.Abs(tb - _currentB) < 0.5 && Math.Abs(targetBrightness - _currentBrightness) < 0.5)
                {
                    _currentR = tr; _currentG = tg; _currentB = tb; _currentBrightness = targetBrightness;
                }

                int finalR = (int)Math.Round(_currentR);
                int finalG = (int)Math.Round(_currentG);
                int finalB = (int)Math.Round(_currentB);
                int finalBrightness = (int)Math.Round(_currentBrightness);

                if (finalR != _lastSentR || finalG != _lastSentG || finalB != _lastSentB || finalBrightness != _lastSentBrightness)
                {
                    try
                    {
                        SetColor(finalR, finalG, finalB, finalBrightness);
                        _lastSentR = finalR; _lastSentG = finalG; _lastSentB = finalB; _lastSentBrightness = finalBrightness;
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ---- SET COLOR ----
        private static void SetColor(string hex, int brightness)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length != 6 || !Regex.IsMatch(hex, "^[0-9A-Fa-f]{6}$"))
            {
                if (_showConsole) Console.WriteLine($"INVALID COLOR: {hex}");
                return;
            }
            int r = (int)Math.Round(Convert.ToInt32(hex.Substring(0, 2), 16) * _config.RedGain);
            int g = (int)Math.Round(Convert.ToInt32(hex.Substring(2, 2), 16) * _config.GreenGain);
            int b = (int)Math.Round(Convert.ToInt32(hex.Substring(4, 2), 16) * _config.BlueGain);
            SetColor(Clamp(r, 0, 255), Clamp(g, 0, 255), Clamp(b, 0, 255), Clamp(brightness, 0, 100));
        }

        // ---- COLOR SETUP ----
        private static void SetColor(int r, int g, int b, int brightness)
        {
            var zone = _illumParams.zones[_zoneIndex];
            zone.ctrlMode = (uint)_config.CtrlMode;

            if (zone.type == 3) // RGBW
            {
                zone.data[_config.RgbwROffset] = (byte)r;
                zone.data[_config.RgbwGOffset] = (byte)g;
                zone.data[_config.RgbwBOffset] = (byte)b;
                zone.data[_config.RgbwWOffset] = 0;
                zone.data[_config.RgbwBrightnessOffset] = (byte)brightness;
            }
            else // RGB
            {
                zone.data[_config.RgbROffset] = (byte)r;
                zone.data[_config.RgbGOffset] = (byte)g;
                zone.data[_config.RgbBOffset] = (byte)b;
                zone.data[_config.RgbBrightnessOffset] = (byte)brightness;
            }

            _illumParams.zones[_zoneIndex] = zone;
            NvApi.SetIllumination(_hGpu, ref _illumParams);
        }

        // ---- COLOR AND BRIGHTNESS CALCULATION ----
        private static (string Color, int Brightness) GetColorAndBrightnessForTemperature(int temp)
        {
            var pts = _config.TemperaturePoints.OrderBy(p => p.Temp).ToList();
            if (pts.Count == 0) return ("FF0000", _config.Brightness);
            if (temp <= pts[0].Temp) return (pts[0].Color, pts[0].Brightness > 0 ? pts[0].Brightness : _config.Brightness);
            if (temp >= pts[pts.Count - 1].Temp) return (pts[pts.Count - 1].Color, pts[pts.Count - 1].Brightness > 0 ? pts[pts.Count - 1].Brightness : _config.Brightness);

            for (int i = 0; i < pts.Count - 1; i++)
            {
                int t1 = pts[i].Temp, t2 = pts[i + 1].Temp;
                if (temp >= t1 && temp <= t2)
                {
                    float f = (float)(temp - t1) / (t2 - t1);
                    string c1 = pts[i].Color, c2 = pts[i + 1].Color;
                    int r1 = Convert.ToInt32(c1.Substring(0, 2), 16);
                    int g1 = Convert.ToInt32(c1.Substring(2, 2), 16);
                    int b1 = Convert.ToInt32(c1.Substring(4, 2), 16);
                    int r2 = Convert.ToInt32(c2.Substring(0, 2), 16);
                    int g2 = Convert.ToInt32(c2.Substring(2, 2), 16);
                    int b2 = Convert.ToInt32(c2.Substring(4, 2), 16);
                    int r = (int)(r1 + (r2 - r1) * f);
                    int g = (int)(g1 + (g2 - g1) * f);
                    int b = (int)(b1 + (b2 - b1) * f);
                    int b1v = pts[i].Brightness > 0 ? pts[i].Brightness : _config.Brightness;
                    int b2v = pts[i + 1].Brightness > 0 ? pts[i + 1].Brightness : _config.Brightness;
                    return ($"{r:X2}{g:X2}{b:X2}", (int)(b1v + (b2v - b1v) * f));
                }
            }
            return (pts[pts.Count - 1].Color, pts[pts.Count - 1].Brightness > 0 ? pts[pts.Count - 1].Brightness : _config.Brightness);
        }

        // ---- FAN SPEED CALCULATION ----
        private static int GetFanSpeedForTemperature(int temp)
        {
            var pts = _config.FanCurvePoints.OrderBy(p => p.Temp).ToList();
            if (pts.Count == 0) return 30; // значение по умолчанию
            if (temp <= pts[0].Temp) return Clamp(pts[0].Speed, 0, 100);
            if (temp >= pts[pts.Count - 1].Temp) return Clamp(pts[pts.Count - 1].Speed, 0, 100);

            for (int i = 0; i < pts.Count - 1; i++)
            {
                int t1 = pts[i].Temp, t2 = pts[i + 1].Temp;
                if (temp >= t1 && temp <= t2)
                {
                    float f = (float)(temp - t1) / (t2 - t1);
                    int s1 = pts[i].Speed, s2 = pts[i + 1].Speed;
                    int speed = (int)(s1 + (s2 - s1) * f);
                    return Clamp(speed, 0, 100);
                }
            }
            return Clamp(pts[pts.Count - 1].Speed, 0, 100);
        }

        // ---- FAN UPDATE ----
        private static void UpdateFanSpeed(int temp)
        {
            if (_coolerInfo == null) return;

            int targetSpeed = GetFanSpeedForTemperature(temp);
            targetSpeed = Clamp(targetSpeed, 0, 100);

            if (targetSpeed != _lastSentFanSpeed)
            {
                try
                {
                    _coolerInfo.SetCoolerSettings(_fanCoolerId, targetSpeed);
                    _lastSentFanSpeed = targetSpeed;
                }
                catch { }
            }
        }

        // ---- CONSOLE DISPLAY ----
        private static void DisplayStatus(int temp)
        {
            if (!_showConsole) return;

            // RGB STRING
            string colorHex = _lastColor;
            if (string.IsNullOrEmpty(colorHex))
                colorHex = "------";
            int brightness = (int)Math.Round(_currentBrightness);
            if (brightness < 0) brightness = 0;
            string rgbLine = $"RGB : {temp,2}°C --> {colorHex} {brightness,3}%";

            // FAN STRING
            string fanLine;
            if (_config.FanControl)
            {
                int speed = _lastSentFanSpeed >= 0 ? _lastSentFanSpeed : 0;
                fanLine = $"FAN : {temp,2}°C --> {speed,5}%";
            }
            else
            {
                fanLine = "FAN : DISABLED";
            }

            int left = 0;
            int top = _consoleStartRow;
            Console.SetCursorPosition(left, top);
            Console.Write(rgbLine.PadRight(Console.WindowWidth - 1));
            top++;
            Console.SetCursorPosition(left, top);
            Console.Write(fanLine.PadRight(Console.WindowWidth - 1));
        }

        // ---- HELPER CLAMP ----
        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }
}