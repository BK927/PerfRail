using System.Runtime.InteropServices;
using PerfRail.Interop;

namespace PerfRail.Sensors.Vendor;

/// <summary>
/// NVIDIA GPU temperature through NVML, the library behind <c>nvidia-smi</c>.
/// </summary>
/// <remarks>
/// <para>
/// Runs entirely in user mode with no elevation, which is why <c>nvidia-smi</c> works
/// from an ordinary command prompt. NVML ships with the display driver, so nothing needs
/// installing and nothing is redistributed.
/// </para>
/// <para>
/// Everything is resolved dynamically. A plain <c>[DllImport("nvml.dll")]</c> would throw
/// on the first call on every AMD and Intel machine, and because the runtime fails the
/// whole containing type, it would take unrelated methods down with it.
/// </para>
/// <para>
/// UNVERIFIED: written against NVML's documented ABI but not exercised on real NVIDIA
/// hardware - the development machine has integrated Intel graphics. Every call is
/// guarded so that being wrong degrades to "no temperature" rather than to a crash.
/// </para>
/// </remarks>
internal sealed class NvmlTemperatureSource : IGpuTemperatureSource
{
    /// <summary>NVML_TEMPERATURE_GPU: the die sensor.</summary>
    private const uint SensorGpuDie = 0;

    private const int NvmlSuccess = 0;

    private readonly nint _library;
    private readonly nint _device;
    private readonly DeviceGetTemperature _getTemperature;
    private readonly Shutdown _shutdown;

    private bool _disposed;

    private NvmlTemperatureSource(
        nint library, nint device, DeviceGetTemperature getTemperature, Shutdown shutdown, string status)
    {
        _library = library;
        _device = device;
        _getTemperature = getTemperature;
        _shutdown = shutdown;
        Status = status;
    }

    public GpuVendor Vendor => GpuVendor.Nvidia;

    public string Status { get; }

    /// <summary>
    /// Loads NVML and takes a handle to the first device, or returns null.
    /// </summary>
    public static NvmlTemperatureSource? TryCreate()
    {
        if (!TryLoadNvml(out nint library))
        {
            return null;
        }

        try
        {
            if (!TryBind(library, "nvmlInit_v2", out Init? init)
                || !TryBind(library, "nvmlShutdown", out Shutdown? shutdown)
                || !TryBind(library, "nvmlDeviceGetCount_v2", out DeviceGetCount? getCount)
                || !TryBind(library, "nvmlDeviceGetHandleByIndex_v2", out DeviceGetHandle? getHandle)
                || !TryBind(library, "nvmlDeviceGetTemperature", out DeviceGetTemperature? getTemperature))
            {
                NativeLibrary.Free(library);
                return null;
            }

            if (init!() != NvmlSuccess)
            {
                NativeLibrary.Free(library);
                return null;
            }

            if (getCount!(out uint count) != NvmlSuccess || count == 0
                || getHandle!(0, out nint device) != NvmlSuccess)
            {
                shutdown!();
                NativeLibrary.Free(library);
                return null;
            }

            // Device 0 rather than a match against the DXGI adapter: NVML identifies
            // devices by PCI bus address and DXGI does not report one, so joining them
            // would mean a second lookup for a case - two NVIDIA cards in one machine -
            // that is vanishingly rare outside workstations.
            string status = count == 1
                ? "NVML loaded"
                : $"NVML loaded, reporting device 0 of {count}";

            return new NvmlTemperatureSource(library, device, getTemperature!, shutdown!, status);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            NativeLibrary.Free(library);
            return null;
        }
    }

    public double? ReadCelsius()
    {
        if (_disposed)
        {
            return null;
        }

        try
        {
            if (_getTemperature(_device, SensorGpuDie, out uint celsius) != NvmlSuccess)
            {
                return null;
            }

            // NVML reports whole degrees. Anything outside this range means the call
            // succeeded but the value is not a temperature.
            return celsius is > 0 and < 150 ? celsius : null;
        }
        catch (SEHException)
        {
            return null;
        }
    }

    private static bool TryLoadNvml(out nint library)
    {
        // System32 first, which is where the modern DCH driver puts it and where the
        // default search path finds it. The NVSMI folder is the legacy location.
        if (NativeLibrary.TryLoad("nvml.dll", out library))
        {
            return true;
        }

        string? programFiles = Environment.GetEnvironmentVariable("ProgramW6432")
            ?? Environment.GetEnvironmentVariable("ProgramFiles");

        if (programFiles is null)
        {
            return false;
        }

        string legacy = Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvml.dll");
        return File.Exists(legacy) && NativeLibrary.TryLoad(legacy, out library);
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
            _shutdown();
        }
        catch (SEHException)
        {
        }

        NativeLibrary.Free(_library);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Init();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Shutdown();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DeviceGetCount(out uint deviceCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DeviceGetHandle(uint index, out nint device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DeviceGetTemperature(nint device, uint sensorType, out uint temperature);
}
