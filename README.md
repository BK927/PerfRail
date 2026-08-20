# PerfRail – Lightweight Windows 11 CPU, GPU & RAM Hardware Monitor

> **Status: early development.** Nothing here is releasable yet — there is no download,
> and the milestone table below is the honest state of the code. Star the repo if you
> want to know when the first build ships.

PerfRail is a thin, quiet strip along the top of your screen that shows CPU, RAM and GPU
usage at a glance. It is a **Windows 11 hardware monitor** built to stay out of your way:
it reserves its own sliver of screen edge instead of floating on top of your applications.

```
 CPU 14% │ RAM 53% │ GPU 28% │ VRAM 41%
──────────────────────────────────────────
```

## It is an AppBar, not a taskbar mod

Most "taskbar monitor" tools either inject code into Explorer or paint an always-on-top
overlay across your desktop. PerfRail does neither. It registers itself with Windows as an
**AppBar** — the same documented `SHAppBarMessage` API the taskbar itself uses — and asks
the shell to reserve roughly 20 device-independent pixels for it.

The practical difference: maximized windows stop *below* PerfRail instead of *behind* it.
Nothing is covered, nothing is intercepted, and your clicks always reach the app you aimed at.

PerfRail:

- does **not** inject into `explorer.exe`
- does **not** modify or replace the Windows taskbar
- does **not** use Electron, WebView, or a bundled browser
- does **not** install a kernel driver
- does **not** require administrator privileges

## Why there are no temperature readings

A **hardware temperature monitor** on Windows needs ring-0 access: CPU die temperature comes
from model-specific registers that can only be read by a signed kernel driver, which in turn
requires administrator rights to install and load. GPU temperature has no OS-level API at all —
it needs a different vendor SDK for NVIDIA, AMD and Intel.

PerfRail's core promise is that it runs as a standard user with no driver, so it reports what
it can actually measure and stays silent about the rest. It will never show you a fabricated
`0 °C`. CPU, RAM, GPU utilization and VRAM all work without elevation, and those are what
PerfRail ships.

## Milestones

| Milestone | Scope | Status |
|---|---|---|
| M0 | Repository, toolchain, project scaffolding | in progress |
| M1 | AppBar registration, per-monitor DPI, non-activating window | not started |
| M2 | **CPU monitor** and **RAM monitor** via `GetSystemTimes` / `GlobalMemoryStatusEx` | not started |
| M3 | Tray icon, persisted settings, start with Windows | not started |
| M4 | **GPU monitor** and VRAM via performance counters + DXGI | not started |
| M5 | Adaptive layout, warning thresholds, logging | not started |

## Design goals

PerfRail exists to watch your system's performance, so it must not waste it. The targets are
under 0.5% average CPU, under 100 MB of memory, one sample per second, no disk writes during
normal monitoring, and zero network traffic — a **system monitor** that behaves like a
**lightweight Windows utility** rather than a background service.

Reliability comes first: PerfRail must survive an Explorer restart, release its reserved screen
area cleanly on exit, and keep showing CPU and RAM even when optional GPU sensors are unavailable.

## Requirements

- Windows 11 (x64)
- No administrator privileges

## License

[MIT](LICENSE)
