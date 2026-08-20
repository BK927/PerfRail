using System.Runtime.InteropServices;

namespace PerfRail.Interop;

/// <summary>A locally unique identifier for a graphics adapter.</summary>
/// <remarks>
/// LowPart is unsigned and HighPart is signed, which matters when formatting the value
/// into a performance-counter instance name.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct LUID
{
    public uint LowPart;
    public int HighPart;
}

/// <summary>
/// What PerfRail needs to know about one graphics adapter.
/// </summary>
/// <param name="Description">Adapter name, for diagnostics.</param>
/// <param name="DedicatedVideoMemoryBytes">On-board VRAM. Zero on integrated graphics.</param>
/// <param name="SharedSystemMemoryBytes">System memory the adapter may borrow.</param>
/// <param name="Luid">Identity, used to join against performance-counter instances.</param>
/// <param name="IsSoftware">True for WARP and other software adapters, which are skipped.</param>
internal readonly record struct GraphicsAdapter(
    string Description,
    ulong DedicatedVideoMemoryBytes,
    ulong SharedSystemMemoryBytes,
    LUID Luid,
    bool IsSoftware);

/// <summary>
/// Enumerates graphics adapters through DXGI.
/// </summary>
/// <remarks>
/// <para>
/// DXGI is used ONLY for capacity and identity. Deliberately not for usage:
/// IDXGIAdapter3::QueryVideoMemoryInfo reports the CALLING PROCESS's video memory, not
/// the adapter's, so building a VRAM readout on it produces a number that sits at zero
/// while the GPU is full. Usage comes from the GPU Adapter Memory counters instead.
/// </para>
/// <para>
/// Called through raw vtable slots rather than [ComImport] interfaces. Declaring the
/// interfaces means restating every inherited method in exact order just to reach two of
/// them, and a single wrong signature corrupts the stack silently. The two indices used
/// here are the whole surface area.
/// </para>
/// </remarks>
internal static unsafe partial class Dxgi
{
    /// <summary>IDXGIFactory1::EnumAdapters1.</summary>
    /// <remarks>
    /// IUnknown 0-2, IDXGIObject 3-6, IDXGIFactory 7-11, then IDXGIFactory1 12-13.
    /// </remarks>
    private const int VtblEnumAdapters1 = 12;

    /// <summary>IDXGIAdapter1::GetDesc1.</summary>
    /// <remarks>IUnknown 0-2, IDXGIObject 3-6, IDXGIAdapter 7-9, then IDXGIAdapter1 10.</remarks>
    private const int VtblGetDesc1 = 10;

    private const int VtblRelease = 2;

    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(in Guid riid, out nint ppFactory);

    /// <summary>
    /// Lists the graphics adapters DXGI can see.
    /// </summary>
    /// <returns>
    /// An empty list when DXGI is unavailable, which is a normal outcome in some remote
    /// and virtualised sessions rather than an error.
    /// </returns>
    public static List<GraphicsAdapter> EnumerateAdapters()
    {
        var adapters = new List<GraphicsAdapter>();

        if (CreateDXGIFactory1(in IID_IDXGIFactory1, out nint factory) < 0 || factory == 0)
        {
            return adapters;
        }

        try
        {
            var enumAdapters = (delegate* unmanaged[Stdcall]<nint, uint, out nint, int>)
                (*(void***)factory)[VtblEnumAdapters1];

            for (uint index = 0; ; index++)
            {
                int hr = enumAdapters(factory, index, out nint adapter);
                if (hr == DXGI_ERROR_NOT_FOUND || hr < 0 || adapter == 0)
                {
                    break;
                }

                try
                {
                    var getDesc = (delegate* unmanaged[Stdcall]<nint, DXGI_ADAPTER_DESC1*, int>)
                        (*(void***)adapter)[VtblGetDesc1];

                    DXGI_ADAPTER_DESC1 desc;
                    if (getDesc(adapter, &desc) >= 0)
                    {
                        adapters.Add(new GraphicsAdapter(
                            new string(desc.Description, 0, DescriptionLength(&desc)),
                            desc.DedicatedVideoMemory,
                            desc.SharedSystemMemory,
                            desc.AdapterLuid,
                            (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0));
                    }
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }

        return adapters;
    }

    private static int DescriptionLength(DXGI_ADAPTER_DESC1* desc)
    {
        int length = 0;
        while (length < 128 && desc->Description[length] != '\0')
        {
            length++;
        }

        return length;
    }

    private static void Release(nint com)
    {
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)com)[VtblRelease];
        release(com);
    }
}
