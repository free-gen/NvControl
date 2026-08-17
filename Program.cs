using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NvControl
{
    internal static class Program
    {
        private const int RGB_TICK_MS = 50;
        private const int TEMPERATURE_POLL_MS = 500;
        private const int CONSOLE_REFRESH_MS = 250;
        private const long MAX_LOG_SIZE = 1024L * 1024L;

        private static volatile bool _running = true;
        private static bool _showConsole;
        private static bool _logEnabled;
        private static bool _consoleAllocated;

        private static Config _config;
        private static LedControl _lighting;
        private static FanControl _fan;
        private static Mutex _singleInstanceMutex;
        private static EventWaitHandle _shutdownEvent;
        private static ConsoleCtrlHandler _consoleHandler;

        private static readonly object LogLock = new object();

        private static string _logPath;
        private static int _consoleStartRow;

        private delegate bool ConsoleCtrlHandler(int ctrlType);

        private static void Main(string[] args)
        {
            bool ownsMutex = false;

            try
            {
                ownsMutex = AcquireSingleInstance();

                if (!ownsMutex)
                    return;

                _showConsole = !args.Any(arg =>
                    arg.Equals("-s", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("--silent", StringComparison.OrdinalIgnoreCase));

                _logEnabled = args.Any(arg =>
                    arg.Equals("-l", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("--log", StringComparison.OrdinalIgnoreCase));

                if (_logEnabled)
                    InitializeLogging();

                if (_showConsole)
                    InitializeConsole();

                Log("APP", "NvControl starting.");

                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.cfg");

                LoadConfiguration(configPath);
                InstallShutdownHandlers();
                InitializeHardware();
                ShowStartupInformation();

                bool needLoop = NeedsBackgroundLoop();

                if (!needLoop)
                {
                    if (_showConsole)
                    {
                        Console.WriteLine();
                        Console.WriteLine("Configuration applied.");
                        Console.WriteLine("Press ENTER to exit...");
                        Console.ReadLine();
                    }

                    return;
                }

                RunLoop();
            }
            catch (Exception ex)
            {
                Log("FATAL", ex.ToString());

                if (_showConsole)
                {
                    try
                    {
                        Console.WriteLine();
                        Console.WriteLine("ERROR: " + ex.Message);
                        Console.WriteLine();
                        if (_logEnabled)
                            Console.WriteLine("See NvControl.log for details.");

                        Console.WriteLine("Press ENTER to exit...");
                        Console.ReadLine();
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                SafeTurnOffLed();
                SafeRestoreFan();

                try
                {
                    NvApi.Shutdown();
                }
                catch (Exception ex)
                {
                    Log("NVAPI", "Shutdown failed: " + ex.Message);
                }

                Log("APP", "NvControl stopped.");

                if (_consoleAllocated)
                {
                    try
                    {
                        FreeConsole();
                    }
                    catch
                    {
                    }
                }

                if (ownsMutex && _singleInstanceMutex != null)
                {
                    try
                    {
                        _singleInstanceMutex.ReleaseMutex();
                    }
                    catch
                    {
                    }
                }

                if (_shutdownEvent != null)
                {
                    _shutdownEvent.Dispose();
                    _shutdownEvent = null;
                }

                if (_singleInstanceMutex != null)
                {
                    _singleInstanceMutex.Dispose();
                    _singleInstanceMutex = null;
                }
            }
        }

        private static bool AcquireSingleInstance()
        {
            bool createdNew;
            _singleInstanceMutex = new Mutex(true, "FreeGen.NvControl.SingleInstance", out createdNew);

            if (createdNew)
            {
                CreateShutdownEvent();
                return true;
            }

            SignalPreviousInstanceToExit();

            try
            {
                if (_singleInstanceMutex.WaitOne(3000))
                {
                    CreateShutdownEvent();
                    return true;
                }
            }
            catch (AbandonedMutexException)
            {
                CreateShutdownEvent();
                return true;
            }

            KillPreviousInstance();

            try
            {
                if (_singleInstanceMutex.WaitOne(2000))
                {
                    CreateShutdownEvent();
                    return true;
                }
            }
            catch (AbandonedMutexException)
            {
                CreateShutdownEvent();
                return true;
            }

            return false;
        }

        private static void CreateShutdownEvent()
        {
            _shutdownEvent = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                "FreeGen.NvControl.Shutdown");

            _shutdownEvent.Reset();
            StartShutdownWatcher();
        }

        private static void StartShutdownWatcher()
        {
            Thread thread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    _shutdownEvent.WaitOne();

                    _running = false;

                    // Give the main loop time to leave any current NVAPI call.
                    Thread.Sleep(100);

                    SafeTurnOffLed();
                    SafeRestoreFan();

                    Environment.Exit(0);
                }
                catch
                {
                }
            }));

            thread.IsBackground = true;
            thread.Start();
        }

        private static void SignalPreviousInstanceToExit()
        {
            try
            {
                using (EventWaitHandle shutdownEvent =
                    EventWaitHandle.OpenExisting("FreeGen.NvControl.Shutdown"))
                {
                    shutdownEvent.Set();
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // Older NvControl build without the shutdown event.
                // KillPreviousInstance() will handle it after the timeout.
            }
        }

        private static void KillPreviousInstance()
        {
            Process current = Process.GetCurrentProcess();
            string currentPath = current.MainModule.FileName;

            foreach (Process process in Process.GetProcessesByName(current.ProcessName))
            {
                if (process.Id == current.Id)
                    continue;

                try
                {
                    string processPath = process.MainModule.FileName;

                    if (!string.Equals(processPath, currentPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    process.Kill();
                    process.WaitForExit(2000);
                }
                catch
                {
                }
            }
        }

        private static void LoadConfiguration(string path)
        {
            if (File.Exists(path))
            {
                _config = Config.Load(path);
            }
            else
            {
                _config = new Config();
                _config.Validate();
                _config.Save(path);

                Log("CONFIG", "Created default config.cfg.");
            }

            if (_config == null)
                throw new InvalidOperationException("Configuration could not be loaded.");

            _config.Validate();

            Log("CONFIG", "Configuration loaded and validated.");
        }

        private static void InitializeHardware()
        {
            // NvAPIWrapper is needed for fan control and temperature monitoring.
            bool needWrapper = _config.FanControl || _config.Mode == LightingMode.Status;

            if (needWrapper)
            {
                try
                {
                    _fan = new FanControl(_config, message => Log("FAN", message));
                    _fan.Initialize();
                }
                catch (Exception ex)
                {
                    Log("FAN", "NvAPIWrapper initialization failed: " + ex);

                    if (_showConsole)
                        Console.WriteLine("FAN/TEMP ERROR: " + ex.Message);

                    _fan = null;
                }
            }

            // Illumination uses the direct NVAPI layer.
            try
            {
                NvApi.Initialize();

                IntPtr gpu = NvApi.GetPhysicalGpu(_config.GpuIndex);

                _lighting = new LedControl(_config, message => Log("RGB", message));
                _lighting.Initialize(gpu);

                if (_config.Mode == LightingMode.Static)
                    _lighting.ApplyStatic();
            }
            catch (Exception ex)
            {
                Log("RGB", "Lighting initialization failed: " + ex);

                if (_showConsole)
                    Console.WriteLine("RGB ERROR: " + ex.Message);

                _lighting = null;
            }
        }

        private static bool NeedsBackgroundLoop()
        {
            bool fanNeedsLoop = _fan != null && _fan.RequiresBackgroundLoop;
            bool rgbNeedsLoop = _lighting != null && _lighting.IsDynamic && _fan != null && _fan.IsInitialized;

            return fanNeedsLoop || rgbNeedsLoop;
        }

        private static void RunLoop()
        {
            Stopwatch clock = Stopwatch.StartNew();

            long nextTemperaturePoll = 0;
            long nextConsoleRefresh = 0;

            int currentTemperature = 0;
            bool haveTemperature = false;

            while (_running)
            {
                long now = clock.ElapsedMilliseconds;

                if (_fan != null && now >= nextTemperaturePoll)
                {
                    int temperature;
                    bool valid = _fan.TryReadTemperature(out temperature);

                    if (valid)
                    {
                        currentTemperature = temperature;
                        haveTemperature = true;

                        if (_lighting != null && _lighting.IsDynamic)
                            _lighting.UpdateTemperature(temperature);

                        _fan.UpdateFanForTemperature(temperature);
                    }

                    // Failed reads are never fed into RGB/fan curves.
                    nextTemperaturePoll = now + TEMPERATURE_POLL_MS;
                }

                if (_lighting != null && _lighting.IsDynamic)
                    _lighting.Tick();

                if (_showConsole && now >= nextConsoleRefresh)
                {
                    DisplayStatus(currentTemperature, haveTemperature);
                    nextConsoleRefresh = now + CONSOLE_REFRESH_MS;
                }

                Thread.Sleep(RGB_TICK_MS);
            }
        }

        private static void InitializeConsole()
        {
            if (!AllocConsole())
                return;

            _consoleAllocated = true;
            Console.Title = "NvControl";
        }

        private static void ShowStartupInformation()
        {
            if (!_showConsole || !_consoleAllocated)
                return;

            Console.Clear();

            string version = Assembly.GetExecutingAssembly().GetName().Version.ToString();

            Console.WriteLine(new string('-', 48));
            Console.WriteLine("NvControl v" + version + " by FreeGen");
            Console.WriteLine(new string('-', 48));
            Console.WriteLine("GPU INDEX       : " + _config.GpuIndex);

            if (_lighting != null)
            {
                Console.WriteLine("RGB ZONE        : " + _lighting.ZoneIndex + " (" + _lighting.ZoneTypeName + ")");
                Console.WriteLine("RGB MODE        : " + _config.Mode.ToString().ToUpperInvariant());
            }
            else
            {
                Console.WriteLine("RGB             : UNAVAILABLE");
            }

            if (!_config.FanControl)
            {
                Console.WriteLine("FAN             : DISABLED");
            }
            else if (_fan != null)
            {
                Console.WriteLine("FAN MODE        : " + _config.FanMode.ToString().ToUpperInvariant());
                Console.WriteLine("FAN COOLER ID   : " + _fan.CoolerId);
            }
            else
            {
                Console.WriteLine("FAN             : UNAVAILABLE");
            }

            Console.WriteLine("LOG             : " + (_logEnabled ? _logPath : "DISABLED"));
            Console.WriteLine(new string('-', 48));

            _consoleStartRow = Console.CursorTop;
        }

        private static void DisplayStatus(int temperature, bool haveTemperature)
        {
            if (!_showConsole || !_consoleAllocated)
                return;

            try
            {
                string temperatureText = haveTemperature ? temperature + " °C" : "-- °C";

                string rgbText = _lighting == null
                    ? "RGB  : UNAVAILABLE"
                    : "RGB  : " + _lighting.CurrentColorHex + "  " + _lighting.CurrentBrightness + "%";

                string fanText = _fan == null
                    ? (_config.FanControl ? "FAN  : UNAVAILABLE" : "FAN  : DISABLED")
                    : "FAN  : " + _fan.StatusText;

                WriteStatusLine(0, "TEMP : " + temperatureText);
                WriteStatusLine(1, rgbText);
                WriteStatusLine(2, fanText);
            }
            catch
            {
                // Console may disappear asynchronously during shutdown.
            }
        }

        private static void WriteStatusLine(int offset, string text)
        {
            int width = Math.Max(1, Console.WindowWidth - 1);

            if (text.Length > width)
                text = text.Substring(0, width);
            else
                text = text.PadRight(width);

            Console.SetCursorPosition(0, _consoleStartRow + offset);
            Console.Write(text);
        }

        private static void InstallShutdownHandlers()
        {
            _consoleHandler = new ConsoleCtrlHandler(OnConsoleControl);
            SetConsoleCtrlHandler(_consoleHandler, true);

            AppDomain.CurrentDomain.ProcessExit += delegate
            {
                SafeTurnOffLed();
                SafeRestoreFan();
            };

            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs eventArgs)
            {
                eventArgs.Cancel = true;
                _running = false;
            };
        }

        private static bool OnConsoleControl(int ctrlType)
        {
            // 0 = CTRL_C, 1 = CTRL_BREAK, 2 = CTRL_CLOSE, 5 = LOGOFF, 6 = SHUTDOWN
            if (ctrlType == 0 || ctrlType == 1 || ctrlType == 2 || ctrlType == 5 || ctrlType == 6)
            {
                _running = false;
                SafeTurnOffLed();
                SafeRestoreFan();
            }

            return false;
        }

        private static void SafeRestoreFan()
        {
            FanControl fan = _fan;

            if (fan == null)
                return;

            try
            {
                fan.RestoreOnExit();
            }
            catch (Exception ex)
            {
                Log("FAN", "Unexpected fan restore error: " + ex);
            }
        }

        private static void SafeTurnOffLed()
        {
            LedControl lighting = _lighting;

            if (lighting == null)
                return;

            try
            {
                lighting.TurnOff();
            }
            catch (Exception ex)
            {
                Log("RGB", "Unexpected LED shutdown error: " + ex);
            }
        }

        private static void InitializeLogging()
        {
            string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string preferred = Path.Combine(appDirectory, "NvControl.log");

            if (CanUseLogPath(preferred))
            {
                _logPath = preferred;
                return;
            }

            string fallbackDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NvControl");

            Directory.CreateDirectory(fallbackDirectory);
            _logPath = Path.Combine(fallbackDirectory, "NvControl.log");
        }

        private static bool CanUseLogPath(string path)
        {
            try
            {
                string directory = Path.GetDirectoryName(path);

                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                using (FileStream stream = new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.Write,
                    FileShare.ReadWrite))
                {
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void Log(string subsystem, string message)
        {
            if (!_logEnabled)
                return;

            try
            {
                lock (LogLock)
                {
                    RotateLogIfNeeded();

                    string line =
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                        " [" + subsystem + "] " +
                        message +
                        Environment.NewLine;

                    File.AppendAllText(_logPath, line, new UTF8Encoding(false));
                }
            }
            catch
            {
                // Logging must never crash hardware cleanup.
            }
        }

        private static void RotateLogIfNeeded()
        {
            if (string.IsNullOrEmpty(_logPath) || !File.Exists(_logPath))
                return;

            var info = new FileInfo(_logPath);

            if (info.Length < MAX_LOG_SIZE)
                return;

            string oldLog = _logPath + ".old";

            try
            {
                if (File.Exists(oldLog))
                    File.Delete(oldLog);

                File.Move(_logPath, oldLog);
            }
            catch
            {
                // Rotation failure should not prevent logging.
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, bool add);
    }
}