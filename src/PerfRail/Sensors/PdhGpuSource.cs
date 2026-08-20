using System.Runtime.InteropServices;
using PerfRail.Interop;

namespace PerfRail.Sensors;

/// <summary>
/// GPU utilisation and video memory, read from performance counters as a standard user.
/// </summary>
/// <remarks>
/// <para>
/// This is the same data Task Manager's GPU tab is built on, and it needs no elevation,
/// no vendor SDK and no kernel driver. DXGI supplies only the capacity to divide by,
/// because the counters expose usage but not the size of the pool.
/// </para>
/// <para>
/// The first PdhAddEnglishCounterW in a process costs seconds while Windows loads the
/// counter name table. All initialisation therefore happens on the sampling thread, and
/// the source simply reports nothing until it is ready. Doing this on the UI thread would
/// stall the AppBar handshake with Explorer, which is a synchronous SendMessage.
/// </para>
/// </remarks>
internal sealed class PdhGpuSource : ISensorSource
{
    private const string EngineCounter = @"\GPU Engine(*)\Utilization Percentage";

    private nint _query;
    private nint _engineCounter;
    private nint _dedicatedCounter;
    private nint _sharedCounter;

    private GraphicsAdapter _adapter;
    private bool _useSharedMemory;
    private ulong _memoryCapacity;

    private byte[] _buffer = [];
    private bool _initialized;
    private bool _primed;
    private bool _unavailable;

    public string Name => "gpu";

    /// <summary>
    /// False once the machine has been shown to have nothing to report.
    /// </summary>
    /// <remarks>
    /// Set when there is no usable adapter or the counters are absent - normal on a
    /// machine with no WDDM adapter, on a Basic Display Adapter, and in some remote
    /// sessions. The service then stops calling this source entirely.
    /// </remarks>
    public bool IsAvailable => !_unavailable;

    /// <summary>Adapter chosen for reporting. Diagnostics only.</summary>
    public string AdapterDescription => _adapter.Description ?? string.Empty;

    public void Contribute(ref HardwareSnapshot snapshot)
    {
        if (!_initialized && !Initialize())
        {
            return;
        }

        uint status = Pdh.PdhCollectQueryData(_query);
        if (status != Pdh.ERROR_SUCCESS)
        {
            if (Pdh.IsUnavailable(status))
            {
                _unavailable = true;
            }

            return;
        }

        if (!_primed)
        {
            // GPU Engine is a PERF_100NSEC_TIMER, a rate. A rate needs two collects at
            // least one interval apart before a formatted read means anything; asking
            // sooner returns PDH_INVALID_DATA or zeros.
            _primed = true;
            return;
        }

        double? usage = ReadEngineUsage();
        ulong? used = ReadMemoryUsed();

        snapshot = snapshot with
        {
            GpuUsage = usage,
            VramUsedBytes = used,
            VramTotalBytes = used is null ? null : _memoryCapacity,
        };
    }

    private bool Initialize()
    {
        _initialized = true;

        _adapter = SelectAdapter();
        if (_adapter.Description is null)
        {
            _unavailable = true;
            return false;
        }

        // Integrated graphics report zero dedicated video memory and borrow system RAM
        // instead, so a "dedicated" percentage there would sit at 0 forever. Below half
        // a gigabyte of dedicated memory, report against the shared pool instead.
        _useSharedMemory = _adapter.DedicatedVideoMemoryBytes < 512UL * 1024 * 1024;
        _memoryCapacity = _useSharedMemory
            ? _adapter.SharedSystemMemoryBytes
            : _adapter.DedicatedVideoMemoryBytes;

        if (Pdh.PdhOpenQuery(null, 0, out _query) != Pdh.ERROR_SUCCESS)
        {
            _unavailable = true;
            return false;
        }

        if (Pdh.PdhAddEnglishCounter(_query, EngineCounter, 0, out _engineCounter) != Pdh.ERROR_SUCCESS)
        {
            Close();
            _unavailable = true;
            return false;
        }

        // Memory counters are optional: losing them costs the VRAM cells, not the source.
        string instance = GpuInstanceParser.AdapterMemoryInstanceName(_adapter.Luid);
        Pdh.PdhAddEnglishCounter(
            _query, $@"\GPU Adapter Memory({instance})\Dedicated Usage", 0, out _dedicatedCounter);
        Pdh.PdhAddEnglishCounter(
            _query, $@"\GPU Adapter Memory({instance})\Shared Usage", 0, out _sharedCounter);

        return true;
    }

    /// <summary>
    /// Picks the adapter to report on.
    /// </summary>
    /// <remarks>
    /// Software adapters (WARP, Microsoft Basic Render Driver) are skipped outright.
    /// Among the rest the one with the most dedicated video memory wins, which picks the
    /// discrete GPU on a laptop that also has integrated graphics.
    /// </remarks>
    private static GraphicsAdapter SelectAdapter()
    {
        GraphicsAdapter best = default;

        foreach (GraphicsAdapter candidate in Dxgi.EnumerateAdapters())
        {
            if (candidate.IsSoftware)
            {
                continue;
            }

            if (best.Description is null
                || candidate.DedicatedVideoMemoryBytes > best.DedicatedVideoMemoryBytes)
            {
                best = candidate;
            }
        }

        return best;
    }

    private double? ReadEngineUsage()
    {
        List<(string Name, double Value)>? items = ReadArray(_engineCounter, Pdh.PDH_FMT_DOUBLE);
        return items is null ? null : GpuInstanceParser.Aggregate(items, _adapter.Luid);
    }

    private ulong? ReadMemoryUsed()
    {
        if (_memoryCapacity == 0)
        {
            return null;
        }

        nint counter = _useSharedMemory ? _sharedCounter : _dedicatedCounter;
        if (counter == 0)
        {
            return null;
        }

        List<(string Name, double Value)>? items = ReadArray(counter, Pdh.PDH_FMT_LARGE, asLarge: true);
        if (items is null || items.Count == 0)
        {
            return null;
        }

        // The counter path names a single adapter instance, so there is exactly one item.
        return (ulong)Math.Max(0, items[0].Value);
    }

    /// <summary>
    /// Reads every instance of a counter.
    /// </summary>
    /// <remarks>
    /// The buffer only ever grows, so the steady state allocates nothing. Wildcard
    /// counters re-enumerate their instances on each collect, which is why a process
    /// starting or exiting needs no query rebuild.
    /// </remarks>
    private List<(string Name, double Value)>? ReadArray(nint counter, uint format, bool asLarge = false)
    {
        uint size = 0;
        uint status = Pdh.PdhGetFormattedCounterArray(counter, format, ref size, out _, 0);

        if (status != Pdh.PDH_MORE_DATA)
        {
            // A sizing call is expected to fail with PDH_MORE_DATA. Anything else means
            // there is nothing to read.
            if (Pdh.IsUnavailable(status))
            {
                return null;
            }

            return null;
        }

        if (size == 0)
        {
            return null;
        }

        if (_buffer.Length < size)
        {
            _buffer = new byte[size];
        }

        var results = new List<(string, double)>();

        unsafe
        {
            fixed (byte* raw = _buffer)
            {
                uint capacity = size;
                if (Pdh.PdhGetFormattedCounterArray(counter, format, ref capacity, out uint count, (nint)raw)
                    != Pdh.ERROR_SUCCESS)
                {
                    return null;
                }

                var items = (PDH_FMT_COUNTERVALUE_ITEM*)raw;
                for (uint i = 0; i < count; i++)
                {
                    PDH_FMT_COUNTERVALUE_ITEM item = items[i];

                    // A process that exited between collects leaves an entry whose value
                    // union holds garbage.
                    if (!Pdh.IsItemValid(item.CStatus) || item.szName == 0)
                    {
                        continue;
                    }

                    string? name = Marshal.PtrToStringUni(item.szName);
                    if (name is null)
                    {
                        continue;
                    }

                    results.Add((name, asLarge ? item.Value.LargeValue : item.Value.DoubleValue));
                }
            }
        }

        return results;
    }

    private void Close()
    {
        if (_query != 0)
        {
            Pdh.PdhCloseQuery(_query);
            _query = 0;
        }

        _engineCounter = 0;
        _dedicatedCounter = 0;
        _sharedCounter = 0;
    }

    public void Dispose() => Close();
}
