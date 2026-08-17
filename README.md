# NvControl

Lightweight Windows utility for controlling NVIDIA GPU RGB lighting and fan speed via NVAPI.

Tested on **Palit Dual GeForce RTX 4060**.

## Features

- RGB / RGBW lighting control
- `STATIC` and temperature-based `STATUS` lighting modes
- Smooth RGB transitions
- Per-channel RGB calibration
- Static fan speed
- Temperature-based fan curve
- Automatic fan restore on exit
- Fan fail-safe on GPU temperature read errors
- Multiple GPU / illumination zone selection
- Console and silent modes
- Single executable with embedded `NvAPIWrapper.dll`
- LED shutdown on exit
- Automatic replacement of an already running instance
- Optional logging with `-l` / `--log`
- Configurable minimum manual fan speed with AUTO/STOP fallback

## Requirements

- Windows 10 / 11 x64
- NVIDIA GPU with NVAPI support
- .NET Framework 4.7.2
- Administrator privileges for fan control

Other NVIDIA GPUs may work if their firmware and driver expose compatible NVAPI illumination and fan interfaces.

## Usage

```bat
NvControl.exe
```

Silent mode:

```bat
NvControl.exe -s
```

Enable logging (Logging is disabled by default.):

```bat
NvControl.exe -l
```

Starting NvControl while another instance is already running will gracefully stop the old instance and start the new one.

On shutdown, NvControl restores automatic fan control and turns GPU lighting off.

On first launch, `config.cfg` is created next to the executable.

Example:

```ini
GPU_INDEX=0
ILLUM_ZONE_INDEX=-1
ILLUM_ZONE_TYPE=0

MODE=STATUS
BRIGHTNESS=15
STATIC_COLOR=FF4000

R_GAIN=1.00
G_GAIN=0.65
B_GAIN=0.90

SMOOTHING=0.15

STATUS_P1=0,FF4000,15
STATUS_P2=49,FF4000,15
STATUS_P3=50,FF8000,30
STATUS_P4=90,FF0000,60

FAN_CONTROL=TRUE
FAN_MODE=CURVE
FAN_SPEED=30
MIN_SPEED=30 # defines the minimum usable manual fan speed. Fan curve values below this threshold are treated as AUTO/STOP.
FAN_COOLER_ID=0
FAN_RESTORE_ON_EXIT=TRUE

FAN_P1=0,0
FAN_P2=40,30
FAN_P3=60,40
FAN_P4=70,50
FAN_P5=80,60
FAN_P6=90,70
```

## Build

```bat
dotnet build -c Release
```

`NvAPIWrapper.dll` is embedded into the final executable using Costura.Fody.

## License

MIT