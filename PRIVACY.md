# PerfRail Privacy Policy

_Last updated: 2026-08-20_

PerfRail does not collect, transmit, or store any personal information.

## What PerfRail reads

PerfRail reads local hardware performance data from the operating system in order to
display it to you:

- CPU utilisation, from Windows kernel timing counters
- Physical memory usage, from the Windows memory status API
- GPU utilisation and video memory usage, from Windows performance counters
- Graphics adapter names and memory capacity, from DXGI

All of this is read on your machine, displayed on your screen, and discarded. None of it
is recorded to disk, and none of it leaves your computer.

## What PerfRail stores

Two things, both on your own machine and both under `%LOCALAPPDATA%\PerfRail`:

- `settings.json` — your preferences: update interval, which metrics to show, whether the
  rail is docked. No hardware readings are stored here.
- `logs\` — a small diagnostic log of startup, shutdown and errors, capped in size. It
  records no measurements and no personal data. You can delete this folder at any time.

## Network

PerfRail makes no network connections. It has no telemetry, no analytics, no crash
reporting, and no update checking. It does not need internet access and will not use it
if available.

## Third parties

PerfRail has no third-party dependencies and includes no third-party code beyond the
Microsoft .NET runtime it is built on. Nothing is shared with anyone, because nothing is
collected in the first place.

## Contact

Questions about this policy: https://github.com/BK927/PerfRail/issues
