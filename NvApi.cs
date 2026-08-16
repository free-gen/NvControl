using System;
using System.Runtime.InteropServices;

namespace NvControl
{
    internal static class NvApi
    {
        private const uint NVAPI_INITIALIZE_ID = 0x0150E828;
        private const uint NVAPI_ENUM_PHYSICAL_GPUS_ID = 0xE5AC921F;
        private const uint NVAPI_ILLUM_GET_ID = 0x3DBF5764;
        private const uint NVAPI_ILLUM_SET_ID = 0x197D065E;
        private const uint NVAPI_GPU_GETTHERMALSETTINGS_ID = 0xE3640A56;
        private const uint NVAPI_THERMAL_TARGET_ALL = 15;

        private static IntPtr _nvapiModule = IntPtr.Zero;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr NvAPI_QueryInterface(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_Initialize();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_EnumPhysicalGPUs([Out] IntPtr[] gpuHandles, ref int gpuCount);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_GetIllumination(IntPtr hPhysicalGpu, ref NV_ILLUM_PARAMS pParams);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_SetIllumination(IntPtr hPhysicalGpu, ref NV_ILLUM_PARAMS pParams);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_GPU_GetThermalSettings(IntPtr hPhysicalGpu, uint sensorIndex, ref NV_GPU_THERMAL_SETTINGS pThermalSettings);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct NV_ILLUM_ZONE
        {
            public uint type;
            public uint ctrlMode;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] data;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] reserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct NV_ILLUM_PARAMS
        {
            public uint version;
            public uint bDefault;
            public uint numIllumZonesControl;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] reserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32, ArraySubType = UnmanagedType.Struct)]
            public NV_ILLUM_ZONE[] zones;
        }

        public enum NV_THERMAL_CONTROLLER : int { NONE = 0, GPU_INTERNAL, ADM1032, MAX6649, MAX1617, LM99, LM89, LM64, ADT7473, SBMAX6649, VBIOSEVT, OS, UNKNOWN }
        public enum NV_THERMAL_TARGET : int { NONE = 0, GPU = 1, MEMORY = 2, POWER_SUPPLY = 4, BOARD = 8, VCD_BOARD = 9, VCD_INLET = 10, VCD_OUTLET = 11, ALL = 15, UNKNOWN = -1 }

        [StructLayout(LayoutKind.Sequential)]
        public struct NV_GPU_THERMAL_SENSOR
        {
            public NV_THERMAL_CONTROLLER controller;
            public int defaultMinTemp;
            public int defaultMaxTemp;
            public int currentTemp;
            public NV_THERMAL_TARGET target;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NV_GPU_THERMAL_SETTINGS
        {
            public uint version;
            public uint count;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public NV_GPU_THERMAL_SENSOR[] sensor;
        }

        private static IntPtr QueryInterface(uint id)
        {
            if (_nvapiModule == IntPtr.Zero)
            {
                _nvapiModule = LoadLibrary("nvapi64.dll");
                if (_nvapiModule == IntPtr.Zero) throw new Exception("Not found nvapi64.dll");
            }
            IntPtr p = GetProcAddress(_nvapiModule, "nvapi_QueryInterface");
            if (p == IntPtr.Zero) throw new Exception("nvapi_QueryInterface not found");
            var qi = (NvAPI_QueryInterface)Marshal.GetDelegateForFunctionPointer(p, typeof(NvAPI_QueryInterface));
            return qi(id);
        }

        private static T GetFunction<T>(uint id)
        {
            IntPtr p = QueryInterface(id);
            if (p == IntPtr.Zero) throw new Exception($"QueryInterface(0x{id:X8}) returned NULL");
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        private static string NvApiStatusName(int status)
        {
            switch (status)
            {
                case 0: return "NVAPI_OK";
                case -1: return "NVAPI_ERROR";
                case -5: return "NVAPI_HANDLE_INVALIDATED";
                case -6: return "NVAPI_INCOMPATIBLE_STRUCT_VERSION";
                case -8: return "NVAPI_INVALID_ARGUMENT";
                default: return $"UNKNOWN (0x{status:X8})";
            }
        }

        public static void Initialize()
        {
            var init = GetFunction<NvAPI_Initialize>(NVAPI_INITIALIZE_ID);
            int ret = init();
            if (ret != 0) throw new Exception($"NvAPI_Initialize returned {ret}");
        }

        public static IntPtr GetPhysicalGpu(int index = 0)
        {
            var enumGpu = GetFunction<NvAPI_EnumPhysicalGPUs>(NVAPI_ENUM_PHYSICAL_GPUS_ID);
            IntPtr[] handles = new IntPtr[64];
            int count = 0;
            int ret = enumGpu(handles, ref count);
            if (ret != 0 || count == 0) throw new Exception($"NvAPI_EnumPhysicalGPUs returned {ret}");
            if (index < 0 || index >= count) throw new Exception($"GPU index {index} out of range (0..{count - 1})");
            return handles[index];
        }

        public static void GetIllumination(IntPtr hGpu, ref NV_ILLUM_PARAMS p)
        {
            var get = GetFunction<NvAPI_GetIllumination>(NVAPI_ILLUM_GET_ID);
            int ret = get(hGpu, ref p);
            if (ret != 0) throw new Exception($"NvAPI_GetIllumination returned {ret}");
        }

        public static void SetIllumination(IntPtr hGpu, ref NV_ILLUM_PARAMS p)
        {
            var set = GetFunction<NvAPI_SetIllumination>(NVAPI_ILLUM_SET_ID);
            int ret = set(hGpu, ref p);
            if (ret != 0) throw new Exception($"NvAPI_SetIllumination returned {ret}");
        }

        public static int GetGpuTemperature(IntPtr hGpu)
        {
            var getThermal = GetFunction<NvAPI_GPU_GetThermalSettings>(NVAPI_GPU_GETTHERMALSETTINGS_ID);
            uint version = (2u << 16) | (uint)Marshal.SizeOf<NV_GPU_THERMAL_SETTINGS>();
            var settings = new NV_GPU_THERMAL_SETTINGS { version = version, count = 0, sensor = new NV_GPU_THERMAL_SENSOR[3] };
            int ret = getThermal(hGpu, NVAPI_THERMAL_TARGET_ALL, ref settings);
            if (ret != 0) throw new Exception($"NvAPI_GPU_GetThermalSettings returned {ret}");
            for (int i = 0; i < settings.count && i < 3; i++)
                if (settings.sensor[i].target == NV_THERMAL_TARGET.GPU)
                    return settings.sensor[i].currentTemp;
            throw new Exception("Sensor GPU not found");
        }

        [DllImport("kernel32.dll")] private static extern IntPtr LoadLibrary(string lpFileName);
        [DllImport("kernel32.dll")] private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
    }
}