using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OfflineChatter.Core.Diagnostics;

public sealed record ResourceSample(double? CpuPercent, long? WorkingSet, long? VramUsed, long? VramTotal);

/// <summary>
/// Нагрузка процесса llama-server: CPU (по времени процессора), ОЗУ (рабочий набор),
/// видеопамять NVIDIA через NVML (на Win7 тоже работает). Для остальных GPU VRAM не показывается.
/// </summary>
public sealed class ResourceMonitor
{
    private int? _pid;
    private TimeSpan _lastCpu;
    private DateTime _lastTime;
    private bool _nvmlTried;
    private bool _nvmlOk;
    private IntPtr _device;

    public ResourceSample Sample(int? pid)
    {
        double? cpu = null;
        long? ws = null;
        if (pid is { } id)
        {
            try
            {
                using var p = Process.GetProcessById(id);
                ws = p.WorkingSet64;
                var now = DateTime.UtcNow;
                var total = p.TotalProcessorTime;
                if (_pid == id && _lastTime != default)
                {
                    var wall = (now - _lastTime).TotalMilliseconds;
                    if (wall > 0) cpu = Math.Min(100, (total - _lastCpu).TotalMilliseconds / wall / Environment.ProcessorCount * 100);
                }
                _pid = id;
                _lastCpu = total;
                _lastTime = now;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        var (used, totalVram) = Vram();
        return new ResourceSample(cpu, ws, used, totalVram);
    }

    private (long?, long?) Vram()
    {
        if (!_nvmlTried)
        {
            _nvmlTried = true;
            try
            {
                if (Nvml.LoadLibrary("nvml.dll") == IntPtr.Zero)
                    Nvml.LoadLibrary(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVSMI\nvml.dll"));
                _nvmlOk = Nvml.Init() == 0 && Nvml.GetHandle(0, out _device) == 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _nvmlOk = false;
            }
        }
        if (!_nvmlOk) return (null, null);
        try
        {
            return Nvml.MemoryInfo(_device, out var m) == 0 ? ((long)m.Used, (long)m.Total) : (null, null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (null, null);
        }
    }

    private static class Nvml
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Memory
        {
            public ulong Total, Free, Used;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr LoadLibrary(string name);
        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] public static extern int Init();
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] public static extern int GetHandle(uint index, out IntPtr device);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")] public static extern int MemoryInfo(IntPtr device, out Memory memory);
    }
}
