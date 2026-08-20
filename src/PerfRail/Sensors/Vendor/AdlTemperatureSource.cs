using System.Runtime.InteropServices;
using PerfRail.Interop;

namespace PerfRail.Sensors.Vendor;

/// <summary>
/// AMD GPU temperature through ADL, the library the Radeon driver installs.
/// </summary>
/// <remarks>
/// <para>
/// User mode, no elevation, ships with the driver - the same properties that make the
/// NVIDIA path viable.
/// </para>
/// <para>
/// AMD is messier than NVIDIA because the temperature entry point changed with the
/// Overdrive generation, and which one works depends on the card: OverdriveN covers
/// Vega and Navi, Overdrive6 covers older GCN parts, and PMLog covers the newest. There
/// is no single call that works everywhere, so all three are tried in order and the
/// first plausible answer wins.
/// </para>
/// <para>
/// Adapter selection is also simplified. Getting the true ADL adapter index means
/// marshalling ADLAdapterInfo, a large layout-sensitive struct that has changed across
/// SDK versions. Instead every index is probed and the first that yields a plausible
/// temperature is used. That is correct for a machine with one AMD GPU, which is nearly
/// all of them; on a multi-AMD-GPU system it may report the wrong card.
/// </para>
/// <para>
/// UNVERIFIED: written against ADL's documented ABI but not exercised on real AMD
/// hardware - the development machine has integrated Intel graphics. Every call is
/// guarded so being wrong degrades to "no temperature" rather than to a crash.
/// </para>
/// </remarks>
internal sealed class AdlTemperatureSource : IGpuTemperatureSource
{
    private const int AdlOk = 0;

    /// <summary>ADLOD n temperature type: edge/core sensor.</summary>
    private const int OdnTemperatureCore = 1;

    private readonly nint _library;
    private readonly nint _context;
    private readonly int _adapterIndex;
    private readonly Reader _read;
    private readonly MainControlDestroy _destroy;

    /// <summary>Held so the GC cannot collect the callback ADL keeps a pointer to.</summary>
    private readonly MallocCallback _malloc;

    private bool _disposed;

    private AdlTemperatureSource(
        nint library,
        nint context,
        int adapterIndex,
        Reader read,
        MainControlDestroy destroy,
        MallocCallback malloc,
        string status)
    {
        _library = library;
        _context = context;
        _adapterIndex = adapterIndex;
        _read = read;
        _destroy = destroy;
        _malloc = malloc;
        Status = status;
    }

    public GpuVendor Vendor => GpuVendor.Amd;

    public string Status { get; }

    public static AdlTemperatureSource? TryCreate()
    {
        if (!NativeLibrary.TryLoad("atiadlxx.dll", out nint library)
            && !NativeLibrary.TryLoad("atiadlxy.dll", out library))
        {
            return null;
        }

        MallocCallback malloc = size => Marshal.AllocHGlobal(size);

        try
        {
            if (!TryBind(library, "ADL2_Main_Control_Create", out MainControlCreate? create)
                || !TryBind(library, "ADL2_Main_Control_Destroy", out MainControlDestroy? destroy)
                || !TryBind(library, "ADL2_Adapter_NumberOfAdapters_Get", out NumberOfAdapters? numberOfAdapters))
            {
                NativeLibrary.Free(library);
                return null;
            }

            // 1 = enumerate only connected adapters.
            if (create!(malloc, 1, out nint context) != AdlOk || context == 0)
            {
                NativeLibrary.Free(library);
                return null;
            }

            if (numberOfAdapters!(context, out int adapterCount) != AdlOk || adapterCount <= 0)
            {
                destroy!(context);
                NativeLibrary.Free(library);
                return null;
            }

            if (TryFindWorkingReader(library, context, adapterCount, out Reader? reader, out int index, out string api))
            {
                return new AdlTemperatureSource(
                    library, context, index, reader!, destroy!, malloc, $"ADL loaded, using {api}");
            }

            destroy!(context);
            NativeLibrary.Free(library);
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or SEHException)
        {
            NativeLibrary.Free(library);
            return null;
        }
    }

    /// <summary>
    /// Probes each Overdrive generation against each adapter until one answers sensibly.
    /// </summary>
    private static bool TryFindWorkingReader(
        nint library, nint context, int adapterCount, out Reader? reader, out int adapterIndex, out string api)
    {
        // Newest first: a card that supports several will give the most accurate reading
        // from the newest interface.
        if (TryBind(library, "ADL2_OverdriveN_Temperature_Get", out OverdriveNTemperature? odn))
        {
            for (int i = 0; i < adapterCount; i++)
            {
                if (odn!(context, i, OdnTemperatureCore, out int milli) == AdlOk && IsPlausible(milli / 1000.0))
                {
                    int captured = i;
                    reader = (ctx, _) => odn(ctx, captured, OdnTemperatureCore, out int m) == AdlOk ? m / 1000.0 : null;
                    adapterIndex = i;
                    api = "OverdriveN";
                    return true;
                }
            }
        }

        if (TryBind(library, "ADL2_Overdrive6_Temperature_Get", out Overdrive6Temperature? od6))
        {
            for (int i = 0; i < adapterCount; i++)
            {
                if (od6!(context, i, out int milli) == AdlOk && IsPlausible(milli / 1000.0))
                {
                    int captured = i;
                    reader = (ctx, _) => od6(ctx, captured, out int m) == AdlOk ? m / 1000.0 : null;
                    adapterIndex = i;
                    api = "Overdrive6";
                    return true;
                }
            }
        }

        if (TryBind(library, "ADL2_Overdrive5_Temperature_Get", out Overdrive5Temperature? od5))
        {
            for (int i = 0; i < adapterCount; i++)
            {
                var temperature = new AdlTemperature { Size = Marshal.SizeOf<AdlTemperature>() };
                if (od5!(context, i, 0, ref temperature) == AdlOk && IsPlausible(temperature.Temperature / 1000.0))
                {
                    int captured = i;
                    reader = (ctx, _) =>
                    {
                        var t = new AdlTemperature { Size = Marshal.SizeOf<AdlTemperature>() };
                        return od5(ctx, captured, 0, ref t) == AdlOk ? t.Temperature / 1000.0 : null;
                    };
                    adapterIndex = i;
                    api = "Overdrive5";
                    return true;
                }
            }
        }

        reader = null;
        adapterIndex = -1;
        api = string.Empty;
        return false;
    }

    /// <summary>
    /// A GPU that is powered on sits somewhere between room temperature and its throttle
    /// point. Values outside that mean the call succeeded but returned something else.
    /// </summary>
    private static bool IsPlausible(double celsius) => celsius is > 0 and < 150;

    public double? ReadCelsius()
    {
        if (_disposed)
        {
            return null;
        }

        try
        {
            double? value = _read(_context, _adapterIndex);
            return value is { } celsius && IsPlausible(celsius) ? celsius : null;
        }
        catch (SEHException)
        {
            return null;
        }
    }

    private static bool TryBind<TDelegate>(nint library, string export, out TDelegate? binding)
        where TDelegate : Delegate
    {
        if (NativeLibrary.TryGetExport(library, export, out nint address))
        {
            binding = Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
            return true;
        }

        binding = null;
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _destroy(_context);
        }
        catch (SEHException)
        {
        }

        NativeLibrary.Free(_library);
        GC.KeepAlive(_malloc);
    }

    private delegate double? Reader(nint context, int adapterIndex);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint MallocCallback(int size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MainControlCreate(MallocCallback callback, int enumConnectedAdapters, out nint context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MainControlDestroy(nint context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NumberOfAdapters(nint context, out int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int OverdriveNTemperature(nint context, int adapterIndex, int temperatureType, out int milliCelsius);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Overdrive6Temperature(nint context, int adapterIndex, out int milliCelsius);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Overdrive5Temperature(nint context, int adapterIndex, int thermalControllerIndex, ref AdlTemperature temperature);

    [StructLayout(LayoutKind.Sequential)]
    private struct AdlTemperature
    {
        public int Size;
        public int Temperature;
    }
}
