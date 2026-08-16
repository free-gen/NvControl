# NvControl

Utility for controlling NVIDIA GPU RGB lighting and fan speed via NVAPI.

## Features

- **RGB/RGBW zone control** – supports both RGB (3‑channel) and RGBW (4‑channel with white) illumination zones.
- **Two lighting modes**:
  - `STATIC` – fixed color and brightness.
  - `STATUS` – color and brightness dynamically change based on GPU temperature (configurable temperature → color mapping).
- **Smooth transitions** – configurable smoothing factor for gradual color changes.
- **Per‑channel calibration** – independent gain settings for Red, Green, and Blue channels.
- **Fan control**:
  - `STATIC` – fixed fan speed (0‑100%).
  - `CURVE` – speed automatically follows a user‑defined temperature → speed curve.
- **Automatic fan restore** – reverts to default (automatic) fan control on exit (optional).
- **Multiple GPU and zone support** – choose GPU index and illumination zone manually or let the app auto‑detect.
- **Self‑contained executable** – `NvAPIWrapper.dll` is embedded into the `.exe` using Costura.Fody; no extra DLLs needed.
- **Console or silent mode** – by default runs with a console window showing live status; use `-s` or `--silent` to run in the background without a console.

## Requirements

- **Windows 10 / 11** (x64)
- **NVIDIA GPU** with NVAPI support (GeForce, Quadro, etc.)
- **[.NET Framework 4.7.2](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net472)** (usually pre‑installed on modern Windows)
- **Administrator privileges** – required for fan speed control (if enabled)

## Installation

1. Download the latest release or build the project yourself.
2. Place `NvControl.exe` in any folder.
3. Run the executable. On first launch, it creates a default `config.cfg` file next to the `.exe`.
4. Edit `config.cfg` to adjust settings (see [Configuration](#configuration)).

## Usage

```bash
NvControl.exe          # starts with console window (live status display)
NvControl.exe -s       # silent mode – no console, runs in background
NvControl.exe --silent # same as -s
```

## Configuration

The `config.cfg` file is a plain text file with `KEY=VALUE` pairs. Below are the most important settings.

### [NVAPI / HARDWARE SETTINGS]

| Key | Description |
|-----|-------------|
| `GPU_INDEX` | Index of the GPU to control (0 = first, 1 = second, etc.) |
| `ILLUM_ZONE_INDEX` | Zone index to use. Set to `-1` for auto‑detection. |
| `ILLUM_ZONE_TYPE` | `0` = auto (RGB or RGBW), `1` = RGB only, `3` = RGBW only. |
| `RGB_R_OFFSET` / `RGB_G_OFFSET` / `RGB_B_OFFSET` | Byte offsets for RGB data (default 0,1,2). |
| `RGB_BRIGHTNESS_OFFSET` | Byte offset for brightness (default 3). |
| `RGBW_*_OFFSET` | Similar offsets for RGBW zones (white channel and brightness). |
| `CTRL_MODE` | Control mode (usually 0). |

### [RGB SETTINGS]

| Key | Description |
|-----|-------------|
| `MODE` | `STATIC` or `STATUS`. |
| `BRIGHTNESS` | Default brightness (0‑100) when not overridden by temperature points. |
| `STATIC_COLOR` | Hex color (e.g., `FF4000`) used in `STATIC` mode. |
| `R_GAIN`, `G_GAIN`, `B_GAIN` | Channel gains (floating point, e.g., `1.00`, `0.65`). |
| `SMOOTHING` | Transition smoothing factor (0.01 – 0.50). Higher = slower but smoother changes. |
| `STATUS_P1` … `STATUS_P9` | Temperature points for `STATUS` mode. Format: `temperature,HEXcolor,brightness` (e.g., `50,FF8000,30`). |

### [FAN SETTINGS]

| Key | Description |
|-----|-------------|
| `FAN_CONTROL` | `TRUE` / `FALSE` – enable/disable fan control. |
| `FAN_MODE` | `CURVE` or `STATIC`. |
| `FAN_SPEED` | Fixed speed (0‑100) used in `STATIC` mode. |
| `FAN_COOLER_ID` | Cooler ID to control (0 = first cooler). Auto‑detects if not found. |
| `FAN_RESTORE_ON_EXIT` | `TRUE` / `FALSE` – restore automatic fan control on exit. |
| `FAN_P1` … `FAN_P9` | Temperature‑speed curve points. Format: `temperature,speed` (e.g., `50,30`). |

## Building from source

The project uses .NET SDK and targets .NET Framework 4.7.2. To build:

```bash
dotnet build -c Release
```
