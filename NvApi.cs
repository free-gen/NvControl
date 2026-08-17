using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NvControl
{
    internal sealed class NvApiException : Exception
    {
        public int Status { get; private set; }

        public NvApiException(string operation, int status, string description)
            : base(operation + " failed: " + description + " (" + status + ")")
        {
            Status = status;
        }
    }

    internal static class NvApi
    {
        private const uint NVAPI_INITIALIZE_ID = 0x0150E828;
        private const uint NVAPI_UNLOAD_ID = 0xD22BDD7E;
        private const uint NVAPI_GET_ERROR_MESSAGE_ID = 0x6C2D048C;
        private const uint NVAPI_ENUM_PHYSICAL_GPUS_ID = 0xE5AC921F;
        private const uint NVAPI_GPU_CLIENT_ILLUM_ZONES_GET_CONTROL_ID = 0x3DBF5764;
        private const uint NVAPI_GPU_CLIENT_ILLUM_ZONES_SET_CONTROL_ID = 0x197D065E;
        private const int NV_GPU_CLIENT_ILLUM_ZONE_NUM_ZONES_MAX = 32;

        public const uint ILLUM_ZONE_TYPE_RGB = 1;
        public const uint ILLUM_ZONE_TYPE_RGBW = 3;
        public const uint ILLUM_CTRL_MODE_MANUAL = 0;

        private static readonly object Sync = new object();

        private static IntPtr _module = IntPtr.Zero;
        private static NvAPI_QueryInterface _queryInterface;
        private static NvAPI_Initialize _initialize;
        private static NvAPI_Unload _unload;
        private static NvAPI_GetErrorMessage _getErrorMessage;
        private static NvAPI_EnumPhysicalGPUs _enumPhysicalGpus;
        private static NvAPI_GPU_ClientIllumZonesGetControl _illumZonesGetControl;
        private static NvAPI_GPU_ClientIllumZonesSetControl _illumZonesSetControl;
        private static bool _initialized;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr NvAPI_QueryInterface(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_Initialize();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_Unload();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private delegate int NvAPI_GetErrorMessage(int status, StringBuilder description);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_EnumPhysicalGPUs([Out] IntPtr[] gpuHandles, ref int gpuCount);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_GPU_ClientIllumZonesGetControl(
            IntPtr hPhysicalGpu,
            ref NV_GPU_CLIENT_ILLUM_ZONE_CONTROL_PARAMS parameters);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_GPU_ClientIllumZonesSetControl(
            IntPtr hPhysicalGpu,
            ref NV_GPU_CLIENT_ILLUM_ZONE_CONTROL_PARAMS parameters);

        // Official size: 200 bytes
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct NV_GPU_CLIENT_ILLUM_ZONE_CONTROL
        {
            public uint type;
            public uint ctrlMode;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
            public byte[] data;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] reserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct NV_GPU_CLIENT_ILLUM_ZONE_CONTROL_PARAMS
        {
            public uint version;

            // bit 0 = bDefault
            public uint flags;

            public uint numIllumZonesControl;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] reserved;

            [MarshalAs(
                UnmanagedType.ByValArray,
                SizeConst = NV_GPU_CLIENT_ILLUM_ZONE_NUM_ZONES_MAX,
                ArraySubType = UnmanagedType.Struct)]
            public NV_GPU_CLIENT_ILLUM_ZONE_CONTROL[] zones;
        }

        public static void Initialize()
        {
            lock (Sync)
            {
                if (_initialized)
                    return;

                try
                {
                    _module = LoadLibrary("nvapi64.dll");

                    if (_module == IntPtr.Zero)
                        throw new InvalidOperationException("nvapi64.dll was not found.");

                    IntPtr queryPointer = GetProcAddress(_module, "nvapi_QueryInterface");

                    if (queryPointer == IntPtr.Zero)
                        throw new InvalidOperationException("nvapi_QueryInterface was not found.");

                    _queryInterface = (NvAPI_QueryInterface)Marshal.GetDelegateForFunctionPointer(
                        queryPointer,
                        typeof(NvAPI_QueryInterface));

                    _initialize = GetRequiredFunction<NvAPI_Initialize>(NVAPI_INITIALIZE_ID);
                    _unload = GetRequiredFunction<NvAPI_Unload>(NVAPI_UNLOAD_ID);
                    _getErrorMessage = GetOptionalFunction<NvAPI_GetErrorMessage>(NVAPI_GET_ERROR_MESSAGE_ID);
                    _enumPhysicalGpus = GetRequiredFunction<NvAPI_EnumPhysicalGPUs>(NVAPI_ENUM_PHYSICAL_GPUS_ID);

                    _illumZonesGetControl =
                        GetRequiredFunction<NvAPI_GPU_ClientIllumZonesGetControl>(NVAPI_GPU_CLIENT_ILLUM_ZONES_GET_CONTROL_ID);

                    _illumZonesSetControl =
                        GetRequiredFunction<NvAPI_GPU_ClientIllumZonesSetControl>(NVAPI_GPU_CLIENT_ILLUM_ZONES_SET_CONTROL_ID);

                    ValidateStructureSizes();

                    int status = _initialize();
                    CheckStatus("NvAPI_Initialize", status);

                    _initialized = true;
                }
                catch
                {
                    ReleaseModule();
                    throw;
                }
            }
        }

        private static void ValidateStructureSizes()
        {
            int zoneSize = Marshal.SizeOf(typeof(NV_GPU_CLIENT_ILLUM_ZONE_CONTROL));
            int paramsSize = Marshal.SizeOf(typeof(NV_GPU_CLIENT_ILLUM_ZONE_CONTROL_PARAMS));

            if (zoneSize != 200)
            {
                throw new InvalidOperationException(
                    "Unexpected illumination zone structure size: " + zoneSize + ", expected 200.");
            }

            if (paramsSize != 6476)
            {
                throw new InvalidOperationException(
                    "Unexpected illumination parameter structure size: " + paramsSize + ", expected 6476.");
            }
        }

        public static IntPtr GetPhysicalGpu(int index)
        {
            EnsureInitialized();

            IntPtr[] handles = new IntPtr[64];
            int count = 0;

            int status = _enumPhysicalGpus(handles, ref count);
            CheckStatus("NvAPI_EnumPhysicalGPUs", status);

            if (count <= 0)
                throw new InvalidOperationException("No NVIDIA physical GPUs were reported by NVAPI.");

            if (index < 0 || index >= count)
            {
                throw new ArgumentOutOfRangeException(
                    "index",
                    "GPU index " + index + " is outside the available range 0.." + (count - 1) + ".");
            }

            return handles[index];
        }

        public static NV_GPU_CLIENT_ILLUM_ZONE_CONTROL_PARAMS CreateIlluminationControlParameters(bool defaultValues)
        {
            var parameters = new NV_GPU_CLIENT_ILLUM_ZONE_CONTROL_PARAMS
            {
                version = MakeVersion<NV_GPU_CLIENT_ILLUM_ZONE_CONTROL_PARAMS>(1),
                flags = defaultValues ? 1u : 0u,
                numIllumZonesControl = 0,
                reserved = new byte[64],
                zones = new NV_GPU_CLIENT_ILLUM_ZONE_CONTROL[NV_GPU_CLIENT_ILLUM_ZONE_NUM_ZONES_MAX]
            };

            for (int i = 0; i < parameters.zones.Length; i++)
            {
                parameters.zones[i] = new NV_GPU_CLIENT_ILLUM_ZONE_CONTROL
                {
                    type = 0,
                    ctrlMode = 0,
                    data = new byte[128],
                    reserved = new byte[64]
                };
            }

            return parameters;
        }

        public static void GetIlluminationControl(
            IntPtr gpu,
            ref NV_GPU_CLIENT_ILLUM_ZONE_CONTROL_PARAMS parameters)
        {
            EnsureInitialized();

            int status = _illumZonesGetControl(gpu, ref parameters);
            CheckStatus("NvAPI_GPU_ClientIllumZonesGetControl", status);
        }

        public static void SetIlluminationControl(
            IntPtr gpu,
            ref NV_GPU_CLIENT_ILLUM_ZONE_CONTROL_PARAMS parameters)
        {
            EnsureInitialized();

            int status = _illumZonesSetControl(gpu, ref parameters);
            CheckStatus("NvAPI_GPU_ClientIllumZonesSetControl", status);
        }

        public static void Shutdown()
        {
            lock (Sync)
            {
                if (_initialized)
                {
                    try
                    {
                        if (_unload != null)
                            _unload();
                    }
                    finally
                    {
                        _initialized = false;
                    }
                }

                ReleaseModule();
            }
        }

        private static T GetRequiredFunction<T>(uint id) where T : class
        {
            T function = GetOptionalFunction<T>(id);

            if (function == null)
                throw new InvalidOperationException("NvAPI interface 0x" + id.ToString("X8") + " is unavailable.");

            return function;
        }

        private static T GetOptionalFunction<T>(uint id) where T : class
        {
            if (_queryInterface == null)
                return null;

            IntPtr pointer = _queryInterface(id);

            if (pointer == IntPtr.Zero)
                return null;

            return (T)(object)Marshal.GetDelegateForFunctionPointer(pointer, typeof(T));
        }

        private static uint MakeVersion<T>(uint version)
        {
            uint size = (uint)Marshal.SizeOf(typeof(T));
            return size | (version << 16);
        }

        private static void EnsureInitialized()
        {
            if (!_initialized)
                throw new InvalidOperationException("NVAPI has not been initialized.");
        }

        private static void CheckStatus(string operation, int status)
        {
            if (status == 0)
                return;

            throw new NvApiException(operation, status, GetStatusDescription(status));
        }

        private static string GetStatusDescription(int status)
        {
            if (_getErrorMessage == null)
                return "NVAPI error";

            try
            {
                var text = new StringBuilder(64);
                int result = _getErrorMessage(status, text);

                if (result == 0 && text.Length > 0)
                    return text.ToString();
            }
            catch
            {
            }

            return "NVAPI error";
        }

        private static void ReleaseModule()
        {
            _initialize = null;
            _unload = null;
            _getErrorMessage = null;
            _enumPhysicalGpus = null;
            _illumZonesGetControl = null;
            _illumZonesSetControl = null;
            _queryInterface = null;

            if (_module != IntPtr.Zero)
            {
                FreeLibrary(_module);
                _module = IntPtr.Zero;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);
    }
}