using System.Runtime.InteropServices;

namespace PerfRail.Interop;

/// <summary>
/// Detects whether this process is running from an MSIX package.
/// </summary>
/// <remarks>
/// <para>
/// The single runtime difference between the GitHub build and the Store build. It is
/// deliberately a P/Invoke rather than <c>Windows.ApplicationModel.Package.Current</c>,
/// which throws when unpackaged - exception-driven control flow on the startup path is
/// wasteful when a three-line call answers exactly.
/// </para>
/// <para>
/// Cached: the answer cannot change during the process's lifetime.
/// </para>
/// </remarks>
internal static partial class PackageIdentity
{
    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>True when running from an MSIX package.</summary>
    public static bool IsPackaged { get; } = Probe();

    // The buffer is declared as a raw pointer because we only ever pass null: the
    // length probe is all we need, and LibraryImport cannot marshal char[] without
    // disabling runtime marshalling for the whole assembly.
    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentPackageFullName")]
    private static partial int GetCurrentPackageFullName(ref uint packageFullNameLength, nint packageFullName);

    private static bool Probe()
    {
        uint length = 0;

        // Asking for the length with a null buffer returns ERROR_INSUFFICIENT_BUFFER
        // when there is a package, and APPMODEL_ERROR_NO_PACKAGE when there is not.
        int result = GetCurrentPackageFullName(ref length, 0);
        return result switch
        {
            ErrorInsufficientBuffer => true,
            AppModelErrorNoPackage => false,

            // Anything else is unexpected; treating it as unpackaged keeps the
            // registry path, which is the one that works outside a package.
            _ => false,
        };
    }
}
