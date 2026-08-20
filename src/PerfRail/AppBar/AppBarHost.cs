using System.Diagnostics;
using System.Runtime.InteropServices;
using PerfRail.Interop;
using static PerfRail.AppBar.AppBarInterop;

namespace PerfRail.AppBar;

/// <summary>
/// Owns the AppBar registration and every <see cref="SHAppBarMessage"/> call.
/// </summary>
/// <remarks>
/// <para>
/// All calls happen on the UI thread by design. SHAppBarMessage is a synchronous
/// cross-process round trip to Explorer's Shell_TrayWnd; if our UI thread is blocked
/// when Explorer sends back to us, we are treated as hung.
/// </para>
/// <para>
/// The rail is topmost, and steps aside for full-screen apps rather than relying on the
/// reserved band to keep windows off it. Reserving work area stops other windows'
/// CLIENT areas from overlapping, but not their DWM extended frames: a maximized window
/// sits about 11 px higher than its visible edge at 150% scaling, and its drop shadow
/// falls across the bottom of the band. Measured on this machine, that darkened the
/// lower third of the rail and cut through the text.
/// </para>
/// <para>
/// The re-entrancy guard is three separate mechanisms and all of them are required.
/// ABM_SETPOS broadcasts ABN_POSCHANGED to every registered AppBar <em>including the
/// sender</em>, so a handler that repositions inline oscillates forever. This is the
/// one and only way this design can burn CPU.
/// </para>
/// </remarks>
internal sealed class AppBarHost : IDisposable
{
    /// <summary>Bar thickness in device-independent pixels at 96 DPI.</summary>
    /// <remarks>
    /// The spec said "18-22 physical/device-independent pixels"; those two units differ
    /// by 50% at 150% scaling. Fixed here as DIP. Everything downstream of
    /// <see cref="PhysicalHeightFor"/> is physical pixels and there is exactly one
    /// height value in the codebase.
    /// </remarks>
    public const int BarHeightDip = 20;

    /// <summary>Floor so the bar stays legible if DPI reports something absurd.</summary>
    public const int MinimumPhysicalHeight = 18;

    private const int DebounceMilliseconds = 200;

    private static readonly int[] RetryDelaysMs = [500, 1000, 2000];

    private readonly Form _form;
    private readonly uint _callbackMessage;
    private readonly uint _taskbarCreatedMessage;
    private readonly System.Windows.Forms.Timer _debounce;
    private readonly System.Windows.Forms.Timer _retry;

    private bool _registered;
    private bool _inFlow;
    private bool _standAside;
    private int _retryAttempt;
    private int _removed;
    private RECT _lastApplied;

    /// <summary>Raised when the bar settles on a monitor with a different DPI.</summary>
    public event Action<uint>? DpiChanged;

    /// <summary>Raised after the reserved rectangle actually changes. Diagnostics only.</summary>
    public event Action<RECT>? Repositioned;

    /// <summary>Raised when ABM_NEW is refused, with the retry attempt number.</summary>
    public event Action<int>? RegistrationFailed;

    public AppBarHost(Form form)
    {
        _form = form;

        // Both are per-session global atoms; register once and cache.
        _callbackMessage = User32.RegisterWindowMessage("PerfRailAppBarCallback");
        _taskbarCreatedMessage = User32.RegisterWindowMessage("TaskbarCreated");

        _debounce = new System.Windows.Forms.Timer { Interval = DebounceMilliseconds };
        _debounce.Tick += OnDebounceTick;

        _retry = new System.Windows.Forms.Timer();
        _retry.Tick += OnRetryTick;
    }

    public bool IsRegistered => _registered;

    /// <summary>The rectangle Explorer actually approved, in physical pixels.</summary>
    public RECT ReservedRect => _lastApplied;

    public uint CurrentDpi { get; private set; } = 96;

    /// <summary>
    /// Drops the rail out of the topmost band so a full-screen app can cover it.
    /// </summary>
    /// <remarks>
    /// Driven by ABN_FULLSCREENAPP for exclusive-fullscreen apps and by
    /// <see cref="Services.FullscreenWatcher"/> for borderless ones, which send no
    /// notification at all.
    /// </remarks>
    public void SetStandAside(bool standAside)
    {
        if (_standAside == standAside || !_form.IsHandleCreated)
        {
            return;
        }

        _standAside = standAside;
        ApplyZOrder();
    }

    private void ApplyZOrder() =>
        User32.SetWindowPos(
            _form.Handle,
            _standAside ? User32.HWND_BOTTOM : User32.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);

    /// <summary>Converts the DIP bar height to physical pixels for the given DPI.</summary>
    public static int PhysicalHeightFor(uint dpi) =>
        Math.Max(MinimumPhysicalHeight, (int)Math.Round(BarHeightDip * dpi / 96.0));

    /// <summary>
    /// Registers the window as an AppBar and reserves its band. Call once, after the
    /// handle exists.
    /// </summary>
    public bool Register()
    {
        Debug.Assert(
            Marshal.SizeOf<APPBARDATA>() == ExpectedAppBarDataSize,
            "APPBARDATA must be 48 bytes on x64. A Pack=1 layout packs it to 44 and " +
            "silently corrupts every AppBar call.");

        if (_registered || !_form.IsHandleCreated)
        {
            return _registered;
        }

        APPBARDATA data = NewData();
        data.uCallbackMessage = _callbackMessage;

        // ABM_NEW returns FALSE when Explorer is down or still starting, and also when
        // this HWND is already registered. On failure QUERYPOS/SETPOS are dropped, so
        // there is no point continuing.
        _registered = SHAppBarMessage(ABM_NEW, ref data) != 0;

        if (_registered)
        {
            _retryAttempt = 0;
            ApplyPosition();
        }
        else
        {
            RegistrationFailed?.Invoke(_retryAttempt);
            ScheduleRetry();
        }

        return _registered;
    }

    /// <summary>
    /// The only code path that moves the window. Recomputes the band, asks the shell to
    /// approve it, and applies exactly what came back.
    /// </summary>
    public void ApplyPosition()
    {
        if (!_registered || _inFlow || !_form.IsHandleCreated)
        {
            return;
        }

        _inFlow = true;
        try
        {
            nint hwnd = _form.Handle;

            if (!User32.TryGetMonitorRect(hwnd, out RECT monitor))
            {
                return;
            }

            uint dpi = User32.GetDpiForWindow(hwnd);
            if (dpi == 0)
            {
                dpi = (uint)_form.DeviceDpi;
            }

            int height = PhysicalHeightFor(dpi);

            APPBARDATA data = NewData();
            data.uEdge = ABE_TOP;
            data.rc = new RECT
            {
                Left = monitor.Left,
                Top = monitor.Top,
                Right = monitor.Right,
                Bottom = monitor.Top + height,
            };

            // The shell subtracts other AppBars' rectangles from ours. It does NOT
            // preserve our thickness, so the band has to be re-imposed afterwards.
            SHAppBarMessage(ABM_QUERYPOS, ref data);

            data.rc.Bottom = data.rc.Top + height;
            data.rc.Left = monitor.Left;
            data.rc.Right = monitor.Right;

            // Broadcasts ABN_POSCHANGED to every AppBar, us included. _inFlow is what
            // stops that from recursing straight back into here.
            SHAppBarMessage(ABM_SETPOS, ref data);

            RECT approved = data.rc;

            // Early-out on an unchanged rectangle. This is what actually breaks the
            // ABM_SETPOS -> ABN_POSCHANGED -> ApplyPosition feedback loop, and why the
            // bar costs nothing while sitting idle.
            if (approved == _lastApplied)
            {
                return;
            }

            _lastApplied = approved;

            // Never position from our own desired rectangle: the approved one is what
            // Explorer actually reserved, and any divergence is a permanent gap or overlap.
            // Positioned AND raised in one call. A non-activating window steadily sinks
            // in z-order as other windows are used, so the z-order has to be re-asserted
            // whenever the band moves rather than preserved with SWP_NOZORDER.
            User32.SetWindowPos(
                hwnd,
                _standAside ? User32.HWND_BOTTOM : User32.HWND_TOPMOST,
                approved.Left,
                approved.Top,
                approved.Width,
                approved.Height,
                User32.SWP_NOACTIVATE);

            if (dpi != CurrentDpi)
            {
                CurrentDpi = dpi;
                DpiChanged?.Invoke(dpi);
            }


            Repositioned?.Invoke(approved);
            _form.Invalidate();
        }
        finally
        {
            _inFlow = false;
        }
    }

    /// <summary>
    /// Drops and re-establishes the registration. Idempotent by construction, which is
    /// what makes it safe to run on the TaskbarCreated broadcast even when we are still
    /// registered.
    /// </summary>
    public void ReRegister()
    {
        if (!_form.IsHandleCreated || Volatile.Read(ref _removed) != 0)
        {
            return;
        }

        APPBARDATA remove = NewData();
        SHAppBarMessage(ABM_REMOVE, ref remove);
        _registered = false;

        APPBARDATA add = NewData();
        add.uCallbackMessage = _callbackMessage;
        _registered = SHAppBarMessage(ABM_NEW, ref add) != 0;

        if (_registered)
        {
            _retryAttempt = 0;
            _lastApplied = default;
            ApplyPosition();
        }
        else
        {
            RegistrationFailed?.Invoke(_retryAttempt);
            ScheduleRetry();
        }
    }

    /// <summary>
    /// Releases the reserved band. Safe to call any number of times from any shutdown path.
    /// </summary>
    /// <remarks>
    /// Must run while the HWND is still alive, which is why WM_ENDSESSION is handled
    /// synchronously rather than deferred. If this never runs, the desktop stays short by
    /// the height of the bar until Explorer restarts.
    /// </remarks>
    public void Unregister()
    {
        if (Interlocked.Exchange(ref _removed, 1) != 0)
        {
            return;
        }

        _debounce.Stop();
        _retry.Stop();

        if (_registered && _form.IsHandleCreated)
        {
            APPBARDATA data = NewData();
            SHAppBarMessage(ABM_REMOVE, ref data);
        }

        _registered = false;
    }

    /// <summary>
    /// Handles AppBar and window messages. Returns true when the message is fully handled
    /// and <c>base.WndProc</c> must not run.
    /// </summary>
    public bool ProcessMessage(ref Message m)
    {
        if (m.Msg == _callbackMessage)
        {
            HandleAppBarNotification((int)m.WParam, m.LParam);
            m.Result = 0;
            return true;
        }

        if (m.Msg == _taskbarCreatedMessage)
        {
            // Explorer restarted, or the primary display's DPI changed (which also
            // broadcasts this). Defer: Explorer is still building itself, and an inline
            // SETPOS gets clobbered by its own work-area recompute.
            TryBeginInvoke(ReRegister);
            return false;
        }

        switch (m.Msg)
        {
            case User32.WM_MOUSEACTIVATE:
                // Never take focus, but still receive the click.
                m.Result = User32.MA_NOACTIVATE;
                return true;

            case User32.WM_DISPLAYCHANGE:
                RestartDebounce();
                return false;

            case User32.WM_ENDSESSION when m.WParam != 0:
                Unregister();
                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// Re-applies our geometry to a WM_WINDOWPOSCHANGING that default handling has
    /// already seen. Must be called after <c>base.WndProc</c>.
    /// </summary>
    /// <param name="lParam">Pointer to the live WINDOWPOS.</param>
    /// <param name="requested">The WINDOWPOS as it looked before default handling.</param>
    /// <remarks>
    /// <para>
    /// WinForms enforces a minimum window height that is far taller than the rail.
    /// Measured on a 144 DPI display: we ask for 30 px and Form's handling rewrites it
    /// to 56, leaving the window overhanging the band it reserved by 26 px and covering
    /// the desktop below. Neither MinimumSize nor overriding ptMinTrackSize in
    /// WM_GETMINMAXINFO prevents this, so the size is simply restored afterwards.
    /// </para>
    /// <para>
    /// For any move that did NOT come from ApplyPosition, the move and size bits are
    /// stripped instead: our geometry belongs to the shell, not to whoever called.
    /// </para>
    /// </remarks>
    public void AfterWindowPosChanging(nint lParam, in WINDOWPOS requested)
    {
        if (!_registered)
        {
            return;
        }

        WINDOWPOS current = Marshal.PtrToStructure<WINDOWPOS>(lParam);

        if (_inFlow)
        {
            current.x = requested.x;
            current.y = requested.y;
            current.cx = requested.cx;
            current.cy = requested.cy;
            current.flags = requested.flags;
        }
        else
        {
            current.flags |= User32.SWP_NOMOVE | User32.SWP_NOSIZE;
        }

        Marshal.StructureToPtr(current, lParam, false);
    }

    private void HandleAppBarNotification(int notification, nint lParam)
    {
        switch (notification)
        {
            case ABN_POSCHANGED:
                // Never reposition inline here: ABM_SETPOS re-broadcasts this to us.
                RestartDebounce();
                break;

            case ABN_FULLSCREENAPP:
                // Covers exclusive-fullscreen apps. Borderless-fullscreen ones send
                // nothing and are handled by FullscreenWatcher instead.
                SetStandAside(lParam != 0);
                break;

            case ABN_WINDOWARRANGE:
                // TRUE arrives before the arrange (hide), FALSE after it (show).
                _form.Visible = lParam == 0;
                break;

            case ABN_STATECHANGE:
                // Intentionally ignored. The MSDN sample for this case reads an
                // uninitialised uState, is missing a break, and sinks the bar to
                // HWND_BOTTOM whenever ABS_ALWAYSONTOP is absent, which on Win7+ is
                // always. Do not copy it.
                break;
        }
    }

    private void RestartDebounce()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce.Stop();
        ApplyPosition();
    }

    private void ScheduleRetry()
    {
        if (_retryAttempt >= RetryDelaysMs.Length)
        {
            return;
        }

        _retry.Interval = RetryDelaysMs[_retryAttempt++];
        _retry.Stop();
        _retry.Start();
    }

    private void OnRetryTick(object? sender, EventArgs e)
    {
        _retry.Stop();

        if (Volatile.Read(ref _removed) != 0)
        {
            return;
        }

        ReRegister();
    }

    private void TryBeginInvoke(Action action)
    {
        // IsHandleCreated is inherently racy against disposal, and an unhandled exception
        // on a pool thread kills the process on .NET Core.
        try
        {
            if (_form.IsHandleCreated)
            {
                _form.BeginInvoke(action);
            }
        }
        catch (InvalidOperationException)
        {
            // Covers ObjectDisposedException too - it derives from
            // InvalidOperationException, so listing it separately is unreachable.
        }
    }

    private APPBARDATA NewData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
        hWnd = _form.Handle,
    };

    public void Dispose()
    {
        Unregister();

        _debounce.Tick -= OnDebounceTick;
        _retry.Tick -= OnRetryTick;
        _debounce.Dispose();
        _retry.Dispose();
    }
}
