using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace OfflineChatter.Core.Diagnostics;

/// <param name="Vram">Данные процесса, которые сейчас лежат в видеопамяти (Local Usage), байт.</param>
/// <param name="SysRam">
/// Данные процесса в ОЗУ, к которой видеокарта ходит через PCIe: больший из Shared Usage (так выделяет CUDA при sysmem fallback)
/// и Non Local Usage (так Windows вытесняет данные при переполнении видеопамяти, в том числе у Vulkan), байт.
/// </param>
/// <param name="EnginePercent">Загрузка самого занятого движка GPU этим процессом, % (null — ещё нет второго замера).</param>
public sealed record GpuProcessSample(long Vram, long SysRam, double? EnginePercent);

/// <summary>
/// Память и загрузка GPU отдельного процесса — те же счётчики, что в Диспетчере задач
/// («GPU Process Memory», «GPU Engine»). Есть с Windows 10 1709; на более старых системах <see cref="Sample"/> возвращает null.
/// Чтение занимает десятки миллисекунд — вызывать не из UI-потока.
/// </summary>
public sealed class GpuProcessCounters
{
    private const string MemoryCategory = "GPU Process Memory";
    private const string EngineCategory = "GPU Engine";

    private bool? _available;
    private Dictionary<string, CounterSample> _lastEngine = new(StringComparer.Ordinal);
    private int _lastPid;

    public GpuProcessSample? Sample(int pid)
    {
        try
        {
            _available ??= PerformanceCounterCategory.Exists(MemoryCategory);
            if (_available != true) return null;
            var prefix = "pid_" + pid + "_";

            // «Dedicated Usage» у процесса учитывает и чужие поверхности, которыми он пользуется (у dwm — больше, чем занято
            // на всём адаптере), поэтому видеопамять — по Local Usage: что действительно лежит в VRAM
            var mem = new PerformanceCounterCategory(MemoryCategory).ReadCategory();
            long Sum(string counter)
            {
                long total = 0;
                foreach (InstanceData d in mem[counter]?.Values ?? EmptyValues)
                    if (d.InstanceName.StartsWith(prefix, StringComparison.Ordinal)) total += d.RawValue;
                return total;
            }
            var vram = Sum("Local Usage");
            if (vram == 0) vram = Sum("Dedicated Usage");
            var sysRam = Math.Max(Sum("Shared Usage"), Sum("Non Local Usage"));

            double? engine = null;
            var current = new Dictionary<string, CounterSample>(StringComparer.Ordinal);
            if (PerformanceCounterCategory.Exists(EngineCategory))
            {
                var eng = new PerformanceCounterCategory(EngineCategory).ReadCategory();
                foreach (InstanceData d in eng["Utilization Percentage"]?.Values ?? EmptyValues)
                {
                    if (!d.InstanceName.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    current[d.InstanceName] = d.Sample;
                    if (pid == _lastPid && _lastEngine.TryGetValue(d.InstanceName, out var prev))
                    {
                        var v = CounterSample.Calculate(prev, d.Sample);
                        if (!float.IsNaN(v)) engine = Math.Max(engine ?? 0, Math.Min(100, v));
                    }
                }
            }
            _lastEngine = current;
            _lastPid = pid;
            return new GpuProcessSample(vram, sysRam, engine);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException
                                       or ArgumentException or FormatException)
        {
            // Счётчики производительности повреждены или отключены — просто нет данных
            _available = false;
            return null;
        }
    }

    private static readonly System.Collections.ICollection EmptyValues = Array.Empty<InstanceData>();
}
