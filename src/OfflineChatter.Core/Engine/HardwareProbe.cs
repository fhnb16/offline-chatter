using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using OfflineChatter.Core.Diagnostics;

namespace OfflineChatter.Core.Engine;

public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel,
    Qualcomm,
    Microsoft,
}

public sealed record GpuInfo(string Name, GpuVendor Vendor, long DedicatedMemory, long SharedMemory, string? DriverVersion);

public sealed record HardwareInfo(
    string CpuName,
    int LogicalCores,
    int PhysicalCores,
    bool Avx,
    bool Avx2,
    bool Avx512,
    long TotalRam,
    long AvailableRam,
    IReadOnlyList<GpuInfo> Gpus,
    string? NvidiaDriver,
    int? CudaDriverVersion,
    bool VulkanAvailable,
    bool Is64BitOs,
    bool IsArm64)
{
    public GpuInfo? BestGpu => Gpus.Where(g => g.Vendor != GpuVendor.Microsoft).OrderByDescending(g => g.DedicatedMemory).FirstOrDefault();

    public long TotalVram => Gpus.Where(g => g.Vendor != GpuVendor.Microsoft).Sum(g => g.DedicatedMemory);

    /// <summary>"12.4" из 12040.</summary>
    public string? CudaVersionText => CudaDriverVersion is { } v ? $"{v / 1000}.{v % 1000 / 10}" : null;
}

/// <summary>
/// Сведения о железе: CPU (ядра, AVX), RAM, видеокарты через DXGI 1.1 (есть на Win7), NVIDIA — через NVML.
/// Все вызовы с запасным вариантом: на любой ошибке поле остаётся «неизвестно», исключения наружу не летят.
/// </summary>
public static class HardwareProbe
{
    public static HardwareInfo Probe()
    {
        var (logical, physical) = CpuCores();
        var (total, available) = Memory();
        var gpus = SafeGpus();
        var (driver, cuda) = Nvml.Query();
        return new HardwareInfo(
            CpuName(), logical, physical,
            Feature(39), Feature(40), Feature(41),
            total, available, gpus, driver, cuda,
            VulkanPresent(), Environment.Is64BitOperatingSystem, IsArm64());
    }

    private static string CpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return "CPU";
        }
    }

    /// <summary>PF_AVX_INSTRUCTIONS_AVAILABLE (39), AVX2 (40), AVX512F (41) — константы известны Win10+, на Win7 вернут false.</summary>
    private static bool Feature(int pf)
    {
        try { return Native.IsProcessorFeaturePresent(pf); }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static (int logical, int physical) CpuCores()
    {
        var logical = Environment.ProcessorCount;
        try
        {
            uint len = 0;
            Native.GetLogicalProcessorInformation(IntPtr.Zero, ref len);
            if (len == 0) return (logical, logical);
            var buffer = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!Native.GetLogicalProcessorInformation(buffer, ref len)) return (logical, logical);
                var size = Marshal.SizeOf<Native.SYSTEM_LOGICAL_PROCESSOR_INFORMATION>();
                var cores = 0;
                for (var off = 0; off + size <= len; off += size)
                {
                    var info = Marshal.PtrToStructure<Native.SYSTEM_LOGICAL_PROCESSOR_INFORMATION>(buffer + off);
                    if (info.Relationship == 0) cores++; // RelationProcessorCore
                }
                return (logical, cores > 0 ? cores : logical);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or ArgumentException)
        {
            return (logical, logical);
        }
    }

    public static (long total, long available) Memory()
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        return Native.GlobalMemoryStatusEx(ref m) ? ((long)m.ullTotalPhys, (long)m.ullAvailPhys) : (0, 0);
    }

    private static bool VulkanPresent()
    {
        var h = Native.LoadLibrary("vulkan-1.dll");
        if (h == IntPtr.Zero) return false;
        Native.FreeLibrary(h);
        return true;
    }

    private static bool IsArm64()
    {
        try
        {
            if (Native.IsWow64Process2(Native.GetCurrentProcess(), out _, out var machine))
                return machine == 0xAA64;
        }
        catch (EntryPointNotFoundException) { }
        return false;
    }

    private static IReadOnlyList<GpuInfo> SafeGpus()
    {
        try
        {
            return Dxgi.Enumerate();
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException)
        {
            Log.Warn("Hardware", "DXGI enumeration failed", ex);
            return Array.Empty<GpuInfo>();
        }
    }

    internal static GpuVendor VendorFromId(uint id) => id switch
    {
        0x10DE => GpuVendor.Nvidia,
        0x1002 or 0x1022 => GpuVendor.Amd,
        0x8086 => GpuVendor.Intel,
        0x5143 or 0x4D4F4351 => GpuVendor.Qualcomm,
        0x1414 => GpuVendor.Microsoft,
        _ => GpuVendor.Unknown,
    };

    // ---------------- DXGI 1.1 (Win7+) ----------------

    private static class Dxgi
    {
        [DllImport("dxgi.dll", PreserveSig = true)]
        private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId, DeviceId, SubSysId, Revision;
            public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public uint LuidLow;
            public int LuidHigh;
            public uint Flags;
        }

        // Порядок методов повторяет vtable: IDXGIObject → IDXGIFactory → IDXGIFactory1
        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1
        {
            void SetPrivateData();
            void SetPrivateDataInterface();
            void GetPrivateData();
            void GetParent();
            void EnumAdapters();
            void MakeWindowAssociation();
            void GetWindowAssociation();
            void CreateSwapChain();
            void CreateSoftwareAdapter();
            [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
            [PreserveSig] bool IsCurrent();
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter1
        {
            void SetPrivateData();
            void SetPrivateDataInterface();
            void GetPrivateData();
            void GetParent();
            void EnumOutputs();
            void GetDesc();
            void CheckInterfaceSupport();
            [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
        }

        public static IReadOnlyList<GpuInfo> Enumerate()
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out var factory) != 0 || factory == null) return Array.Empty<GpuInfo>();
            var drivers = DriverVersions();
            var list = new List<GpuInfo>();
            try
            {
                for (uint i = 0; factory.EnumAdapters1(i, out var adapter) == 0; i++)
                {
                    try
                    {
                        if (adapter.GetDesc1(out var d) != 0) continue;
                        if ((d.Flags & 2) != 0) continue; // DXGI_ADAPTER_FLAG_SOFTWARE
                        drivers.TryGetValue(d.Description.Trim(), out var drv);
                        list.Add(new GpuInfo(d.Description.Trim(), VendorFromId(d.VendorId),
                            (long)d.DedicatedVideoMemory.ToUInt64(), (long)d.SharedSystemMemory.ToUInt64(), drv));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
            return list;
        }

        /// <summary>Версии драйверов из класса устройств «Display adapters».</summary>
        private static Dictionary<string, string> DriverVersions()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (cls == null) return map;
                foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
                {
                    using var k = cls.OpenSubKey(sub);
                    if (k?.GetValue("DriverDesc") is string desc && k.GetValue("DriverVersion") is string ver)
                        map[desc.Trim()] = ver;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
            return map;
        }
    }

    // ---------------- NVML (драйвер NVIDIA) ----------------

    private static class Nvml
    {
        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] private static extern int Init();
        [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")] private static extern int Shutdown();
        [DllImport("nvml.dll", EntryPoint = "nvmlSystemGetCudaDriverVersion")] private static extern int CudaVersion(out int v);
        [DllImport("nvml.dll", EntryPoint = "nvmlSystemGetDriverVersion", CharSet = CharSet.Ansi)]
        private static extern int DriverVersion(StringBuilder buf, uint len);

        public static (string? driver, int? cuda) Query()
        {
            // nvml.dll лежит в System32 (новые драйверы) или в NVSMI (старые, в т.ч. Win7)
            if (Native.LoadLibrary("nvml.dll") == IntPtr.Zero)
            {
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (Native.LoadLibrary(System.IO.Path.Combine(pf, @"NVIDIA Corporation\NVSMI\nvml.dll")) == IntPtr.Zero)
                    return (null, null);
            }
            try
            {
                if (Init() != 0) return (null, null);
                try
                {
                    var sb = new StringBuilder(96);
                    string? driver = DriverVersion(sb, (uint)sb.Capacity) == 0 ? sb.ToString() : null;
                    int? cuda = CudaVersion(out var v) == 0 ? v : null;
                    return (driver, cuda);
                }
                finally
                {
                    Shutdown();
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                return (null, null);
            }
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION
        {
            public UIntPtr ProcessorMask;
            public int Relationship;
            public ulong Reserved1;
            public ulong Reserved2;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint length);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsProcessorFeaturePresent(int feature);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibrary(string name);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FreeLibrary(IntPtr h);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        /// <summary>Win10 1709+.</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
    }
}
