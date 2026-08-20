using System.Runtime.InteropServices;

namespace PerfRail.Interop;

/// <summary>
/// A single instance's value from <c>PdhGetFormattedCounterArrayW</c>.
/// </summary>
/// <remarks>
/// 24 bytes on x64: szName (8), CStatus (4), 4 bytes of padding, then the value union (8).
/// The padding is real and load-bearing - the union is 8-byte aligned - so this layout
/// must not be "tidied up".
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct PDH_FMT_COUNTERVALUE_ITEM
{
    /// <summary>Pointer to the instance name. Owned by PDH, valid until the next collect.</summary>
    public nint szName;

    public uint CStatus;

    private readonly uint _padding;

    /// <summary>
    /// The value. Which field is meaningful depends on the format requested.
    /// </summary>
    /// <remarks>
    /// Declared as an explicit-layout union so a double and a long can share the same
    /// eight bytes, exactly as the native structure does.
    /// </remarks>
    public PDH_COUNTERVALUE_UNION Value;
}

[StructLayout(LayoutKind.Explicit, Size = 8)]
internal struct PDH_COUNTERVALUE_UNION
{
    [FieldOffset(0)]
    public long LargeValue;

    [FieldOffset(0)]
    public double DoubleValue;
}

/// <summary>
/// Performance Data Helper. The non-elevated route to GPU utilisation and video memory.
/// </summary>
/// <remarks>
/// <para>
/// Every import is explicitly <see cref="StringMarshalling.Utf16"/>. Without it the
/// default is ANSI, the W entry points receive bytes they read as UTF-16, and
/// PdhAddEnglishCounterW fails with PDH_CSTATUS_BAD_COUNTERNAME (0xC0000BC0) for a
/// counter path that is perfectly correct.
/// </para>
/// <para>
/// Access is gated on the INTERACTIVE SID rather than on being an administrator, so
/// PerfRail must run as a normal interactive user process. A service, or a scheduled
/// task set to run whether or not the user is logged on, would be denied.
/// </para>
/// </remarks>
internal static partial class Pdh
{
    // ---- Formats ---------------------------------------------------------
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_FMT_LARGE = 0x00000400;

    // ---- Status codes ----------------------------------------------------
    public const uint ERROR_SUCCESS = 0x00000000;

    /// <summary>Returned for an item whose value is valid but unchanged. Not an error.</summary>
    public const uint PDH_CSTATUS_NEW_DATA = 0x00000001;

    /// <summary>Expected from the sizing call, not a failure.</summary>
    public const uint PDH_MORE_DATA = 0x800007D2;

    public const uint PDH_CSTATUS_NO_INSTANCE = 0x800007D1;
    public const uint PDH_NO_DATA = 0x800007D5;
    public const uint PDH_CSTATUS_NO_OBJECT = 0xC0000BB8;
    public const uint PDH_CSTATUS_NO_COUNTER = 0xC0000BB9;
    public const uint PDH_CSTATUS_BAD_COUNTERNAME = 0xC0000BC0;
    public const uint PDH_INVALID_DATA = 0xC0000BC6;
    public const uint PDH_INVALID_HANDLE = 0xC0000BBC;

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhOpenQuery(string? szDataSource, nint dwUserData, out nint phQuery);

    /// <summary>
    /// Adds a counter using its English name.
    /// </summary>
    /// <remarks>
    /// Deliberately the English variant, not PdhAddCounterW. Counter names are localised,
    /// so "\GPU Engine(*)\Utilization Percentage" simply does not exist on a Korean or
    /// German Windows - this variant translates it for us.
    /// </remarks>
    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhAddEnglishCounter(nint hQuery, string szFullCounterPath, nint dwUserData, out nint phCounter);

    [LibraryImport("pdh.dll", EntryPoint = "PdhCollectQueryData")]
    public static partial uint PdhCollectQueryData(nint hQuery);

    /// <summary>
    /// Reads every instance of a wildcard counter.
    /// </summary>
    /// <param name="lpdwBufferSize">
    /// Size in BYTES, unlike PdhEnumObjectItems whose sizes are in characters. Call once
    /// with a zero size to learn the requirement; that call returns PDH_MORE_DATA.
    /// </param>
    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhGetFormattedCounterArray(
        nint hCounter,
        uint dwFormat,
        ref uint lpdwBufferSize,
        out uint lpdwItemCount,
        nint ItemBuffer);

    [LibraryImport("pdh.dll", EntryPoint = "PdhCloseQuery")]
    public static partial uint PdhCloseQuery(nint hQuery);

    /// <summary>
    /// True when a per-item CStatus means the value can be trusted.
    /// </summary>
    /// <remarks>
    /// A process that exits between two collects leaves an item whose union holds
    /// garbage, so items must be filtered on this rather than read unconditionally.
    /// </remarks>
    public static bool IsItemValid(uint cStatus) =>
        cStatus is ERROR_SUCCESS or PDH_CSTATUS_NEW_DATA;

    /// <summary>
    /// True when a failure means "this machine has no such counter" rather than a bug.
    /// </summary>
    /// <remarks>
    /// Expected on a machine with no WDDM adapter, in some remote sessions, and on a
    /// Basic Display Adapter. The correct response is to hide the GPU cells, not to throw.
    /// </remarks>
    public static bool IsUnavailable(uint status) =>
        status is PDH_NO_DATA
            or PDH_CSTATUS_NO_INSTANCE
            or PDH_CSTATUS_NO_OBJECT
            or PDH_CSTATUS_NO_COUNTER
            or PDH_INVALID_DATA;
}
