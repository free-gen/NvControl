using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native.GPU;

namespace NvControl
{
    internal sealed class FanControl
    {
        // Five failed reads at 500 ms polling = ~2.5 seconds without reliable temperature data.
        private const int TEMPERATURE_FAILURE_LIMIT = 5;

        private readonly Config _config;
        private readonly Action<string> _log;
        private readonly List<FanCurvePoint> _curve;

        private PhysicalGPU _physicalGpu;
        private GPUCoolerInformation _coolerInfo;

        private int _fanCoolerId;
        private int _lastCommandedSpeed = -1;
        private int _consecutiveTemperatureFailures;
        private int? _lastValidTemperature;

        private bool _fanControlAvailable;
        private bool _fanWasModified;
        private bool _fanRestored;
        private bool _fanStoppedByMinSpeed;
        private bool _failsafeTriggered;

        // 0 = restore not started, 1 = restored / restore in progress
        private int _restoreGate;

        public FanControl(Config config, Action<string> log)
        {
            if (config == null)
                throw new ArgumentNullException("config");

            _config = config;
            _log = log ?? delegate { };
            _curve = config.FanCurvePoints.OrderBy(point => point.Temperature).ToList();
        }

        public bool IsInitialized
        {
            get { return _physicalGpu != null; }
        }

        public bool IsFanControlActive
        {
            get { return _fanControlAvailable && !_fanRestored && !_failsafeTriggered; }
        }

        public bool RequiresBackgroundLoop
        {
            get { return _config.FanControl && _fanControlAvailable && !_fanRestored; }
        }

        public bool FailsafeTriggered
        {
            get { return _failsafeTriggered; }
        }

        public int CoolerId
        {
            get { return _fanCoolerId; }
        }

        public int LastCommandedSpeed
        {
            get { return _lastCommandedSpeed; }
        }

        public int? LastValidTemperature
        {
            get { return _lastValidTemperature; }
        }

        public string StatusText
        {
            get
            {
                if (!_config.FanControl)
                    return "DISABLED";

                if (!_fanControlAvailable && !_fanRestored)
                    return "UNAVAILABLE";

                if (_failsafeTriggered)
                    return "FAILSAFE / AUTO";

                if (_fanStoppedByMinSpeed)
                    return "AUTO / STOP";

                if (_fanRestored)
                    return "AUTO";

                if (_lastCommandedSpeed >= 0)
                    return _lastCommandedSpeed + "%";

                return "READY";
            }
        }

        public void Initialize()
        {
            PhysicalGPU[] gpus = PhysicalGPU.GetPhysicalGPUs();

            if (gpus == null || gpus.Length == 0)
                throw new InvalidOperationException("NvAPIWrapper found no NVIDIA GPUs.");

            if (_config.GpuIndex < 0 || _config.GpuIndex >= gpus.Length)
            {
                throw new InvalidOperationException(
                    "GPU_INDEX " + _config.GpuIndex + " is outside NvAPIWrapper GPU range 0.." + (gpus.Length - 1) + ".");
            }

            _physicalGpu = gpus[_config.GpuIndex];
            _log("NvAPIWrapper GPU initialized: " + _physicalGpu.FullName + ".");

            if (!_config.FanControl)
                return;

            try
            {
                InitializeFanControl();
            }
            catch (Exception ex)
            {
                _fanControlAvailable = false;
                _log("Fan control initialization failed: " + ex.Message);
            }
        }

        private void InitializeFanControl()
        {
            _coolerInfo = _physicalGpu.CoolerInformation;

            if (_coolerInfo == null)
                throw new InvalidOperationException("GPU cooler information is unavailable.");

            List<GPUCooler> coolers = _coolerInfo.Coolers.ToList();

            if (coolers.Count == 0)
                throw new InvalidOperationException("No GPU coolers were reported.");

            GPUCooler target = coolers.FirstOrDefault(cooler => cooler.CoolerId == _config.FanCoolerId);

            if (target == null)
            {
                target = coolers[0];
                _log("Requested FAN_COOLER_ID " + _config.FanCoolerId + " not found; using cooler " + target.CoolerId + ".");
            }

            _fanCoolerId = target.CoolerId;
            _fanControlAvailable = true;

            _log("Fan controller initialized: cooler=" + _fanCoolerId + ", current=" + target.CurrentLevel + "%.");

            if (_config.FanMode == FanControlMode.Static)
                SetManualSpeed(_config.FanSpeed);
        }

        public bool TryReadTemperature(out int temperature)
        {
            temperature = _lastValidTemperature.HasValue ? _lastValidTemperature.Value : 0;

            if (_physicalGpu == null)
                return false;

            try
            {
                GPUThermalSensor sensor = _physicalGpu.ThermalInformation.ThermalSensors
                    .FirstOrDefault(item => item.Target == ThermalSettingsTarget.GPU);

                if (sensor == null)
                    throw new InvalidOperationException("GPU thermal sensor was not found.");

                int current = sensor.CurrentTemperature;

                if (current < -50 || current > 200)
                    throw new InvalidOperationException("GPU returned implausible temperature " + current + " °C.");

                int previousFailures = _consecutiveTemperatureFailures;

                _consecutiveTemperatureFailures = 0;
                _lastValidTemperature = current;
                temperature = current;

                if (previousFailures > 0 && !_failsafeTriggered)
                    _log("GPU temperature reading recovered after " + previousFailures + " failed read(s).");

                return true;
            }
            catch (Exception ex)
            {
                HandleTemperatureFailure(ex);
                return false;
            }
        }

        private void HandleTemperatureFailure(Exception exception)
        {
            _consecutiveTemperatureFailures++;

            if (_consecutiveTemperatureFailures == 1)
                _log("GPU temperature read failed: " + exception.Message);

            if (_failsafeTriggered || !_fanWasModified)
                return;

            if (_consecutiveTemperatureFailures < TEMPERATURE_FAILURE_LIMIT)
                return;

            TriggerFailsafe(
                "GPU temperature could not be read for " +
                _consecutiveTemperatureFailures +
                " consecutive attempts.");
        }

        public void UpdateFanForTemperature(int temperature)
        {
            if (!_config.FanControl || _config.FanMode != FanControlMode.Curve)
                return;

            if (!_fanControlAvailable || _failsafeTriggered || _fanRestored)
                return;

            int target = GetFanSpeedForTemperature(temperature);
            bool shouldStop = target < _config.MinFanSpeed;

            if (!shouldStop && !_fanStoppedByMinSpeed && target == _lastCommandedSpeed)
                return;

            if (shouldStop && _fanStoppedByMinSpeed)
                return;

            try
            {
                SetManualSpeed(target);
            }
            catch (Exception ex)
            {
                _log("Fan speed update failed: " + ex.Message);
            }
        }

        private int GetFanSpeedForTemperature(int temperature)
        {
            if (_curve.Count == 0)
                return _config.FanSpeed;

            FanCurvePoint first = _curve[0];
            FanCurvePoint last = _curve[_curve.Count - 1];

            if (temperature <= first.Temperature)
                return Clamp(first.Speed, 0, 100);

            if (temperature >= last.Temperature)
                return Clamp(last.Speed, 0, 100);

            for (int i = 0; i < _curve.Count - 1; i++)
            {
                FanCurvePoint left = _curve[i];
                FanCurvePoint right = _curve[i + 1];

                if (temperature < left.Temperature || temperature > right.Temperature)
                    continue;

                double range = right.Temperature - left.Temperature;
                double factor = (temperature - left.Temperature) / range;
                int result = (int)Math.Round(left.Speed + (right.Speed - left.Speed) * factor);

                return Clamp(result, 0, 100);
            }

            return Clamp(last.Speed, 0, 100);
        }

        private void SetManualSpeed(int speed)
        {
            if (!_fanControlAvailable || _coolerInfo == null)
                throw new InvalidOperationException("Fan controller is unavailable.");

            speed = Clamp(speed, 0, 100);

            // Speeds below the hardware minimum are interpreted as fan stop.
            // On cards such as Palit Dual RTX 4060, manual 0..29% still means 30%,
            // so switching the cooler back to AUTO is the only way to allow 0 RPM.
            if (speed < _config.MinFanSpeed)
            {
                SetAutomaticStop();
                return;
            }

            _coolerInfo.SetCoolerSettings(_fanCoolerId, speed);

            _fanWasModified = true;
            _fanRestored = false;
            _fanStoppedByMinSpeed = false;
            _lastCommandedSpeed = speed;
        }

        private void SetAutomaticStop()
        {
            if (_fanStoppedByMinSpeed)
                return;

            _coolerInfo.RestoreCoolerSettingsToDefault(new[] { _fanCoolerId });

            _fanStoppedByMinSpeed = true;
            _fanWasModified = false;
            _fanRestored = false;
            _lastCommandedSpeed = 0;

            _log("Fan target is below MIN_SPEED=" + _config.MinFanSpeed + "%; switched cooler to AUTO/STOP.");
        }

        private void TriggerFailsafe(string reason)
        {
            if (_failsafeTriggered)
                return;

            _failsafeTriggered = true;
            _log("FAN FAILSAFE: " + reason + " Restoring automatic fan control.");

            RestoreToAuto(true, "failsafe");
        }

        public void RestoreOnExit()
        {
            RestoreToAuto(false, "shutdown");
        }

        private void RestoreToAuto(bool force, string reason)
        {
            if (_coolerInfo == null)
                return;

            if (_fanStoppedByMinSpeed)
                return;

            if (!_fanWasModified)
                return;

            if (!force && !_config.FanRestoreOnExit)
                return;

            if (Interlocked.CompareExchange(ref _restoreGate, 1, 0) != 0)
                return;

            try
            {
                _coolerInfo.RestoreCoolerSettingsToDefault(new[] { _fanCoolerId });

                _fanRestored = true;
                _fanControlAvailable = false;

                _log("Fan cooler " + _fanCoolerId + " restored to AUTO (" + reason + ").");
            }
            catch (Exception ex)
            {
                // Allow another shutdown path to retry.
                Interlocked.Exchange(ref _restoreGate, 0);
                _log("Fan restore failed: " + ex.Message);
            }
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