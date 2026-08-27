# LinuxPty.NET

LinuxPty.NET is a Linux-only pseudoterminal (PTY) library for .NET 10. It spawns a process under a fresh PTY and exposes read and write streams plus asynchronous exit observation. Native assets ship for glibc-based `linux-x64` and `linux-arm64`; musl-based distributions (such as Alpine) are not supported.

[![Publish package](https://github.com/abp321/LinuxPty.NET/actions/workflows/publish-package.yml/badge.svg?branch=main)](https://github.com/abp321/LinuxPty.NET/actions/workflows/publish-package.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

The NuGet package ID is `LinuxPty.NET`. The public API lives in the `Porta.Pty` namespace, inherited from the upstream project.

## Features

- Spawn a child process under a new PTY with an initial size, working directory, and environment overrides.
- Readiness-driven stream I/O: asynchronous reads and writes do not tie up a thread while waiting. By default the library is self-contained: all connections in a process share one epoll reactor and one reactor thread, and there is no thread per connection. An advanced `SpawnAsync` overload instead accepts a caller-owned event loop, so the library owns no long-lived thread at all.
- Exit observation through pidfds on kernels that support them (Linux 5.4+). On older kernels the fallback poll runs on the reactor's own timer in the default mode, or on the caller's event loop in external mode, with a lazily created emergency reaper thread only if the reactor itself fails.
- Race-free child tracking: the spawned child opens its own pidfd and transfers it to the parent over a close-on-exec `AF_UNIX` socketpair before it is released to `chdir` and `exec`, so exit observation never resolves a numeric PID that could already have been recycled.
- Precise spawn failures: a failed `chdir` or `exec` in the child reports its errno back to the parent through a control channel, so a mistyped executable path surfaces as an exception with the real errno instead of a healthy-looking spawn that exits immediately.
- Awaitable lifecycle: `WaitForExitAsync` and `DisposeAsync` are genuinely asynchronous; synchronous `WaitForExit` and `Dispose` remain available.
- Deterministic exit codes: a normal exit reports the raw exit code, and death by signal reports 128 plus the signal number.

## Requirements

- Linux with glibc, on x64 or arm64. Calling `SpawnAsync` on any other OS throws `PlatformNotSupportedException`.
- .NET 10.

## Installation

```bash
dotnet add package LinuxPty.NET
```

Published versions are deterministic: each commit on `main` maps to `1.0.<repository commit count>`.

## Usage

```csharp
using System;
using System.Text;
using System.Threading;
using Porta.Pty;

var options = new PtyOptions
{
    App = "/bin/bash",
    CommandLine = ["--noprofile", "--norc"],
    Cwd = Environment.CurrentDirectory,
    Cols = 120,
    Rows = 30,
};

await using IPtyConnection terminal = await PtyProvider.SpawnAsync(
    options,
    CancellationToken.None);

byte[] command = Encoding.UTF8.GetBytes("echo hello\r");
await terminal.WriterStream.WriteAsync(command);

byte[] buffer = new byte[4096];
int count = await terminal.ReaderStream.ReadAsync(buffer);
Console.WriteLine(Encoding.UTF8.GetString(buffer, 0, count));
```

`IPtyConnection` also exposes `Resize(cols, rows)`, `Kill()` (immediate `SIGKILL`), the `Pid` and `ExitCode` properties, and a `ProcessExited` event. The event is delivered exactly once per handler, including handlers subscribed after the child has already exited.

## Behavior notes

**Spawning.** `SpawnAsync` validates a snapshot of the options, then runs the inherently synchronous native spawn on a transient thread-pool worker under a process-wide gate that covers only the native section, so spawns serialize without a dedicated thread and never block the caller. Cancellation is observed before queued work begins; cancellation during native process creation completes only after the resulting child has been killed, its master descriptor closed, and the child reaped.

**Environment.** The child inherits the managed process environment captured immediately before the native spawn (`Environment.GetEnvironmentVariables()`), not libc's potentially stale `environ`. The native shim then sets `TERM=xterm-256color` and unsets `TMUX`, `TMUX_PANE`, `STY`, `WINDOW`, `WINDOWID`, `TERMCAP`, `COLUMNS`, and `LINES`. Entries from `PtyOptions.Environment` are applied last, so they can override anything, including `TERM`; an empty value means unset. Executable lookup uses the resulting effective `PATH`, including one changed through `PtyOptions.Environment`.

**I/O.** Reads and writes are queued FIFO per connection, and cancellation wakes the reactor promptly. An operation the descriptor can satisfy immediately completes on the calling thread without a reactor round-trip; only operations that must wait for readiness park in the reactor. If a write is cancelled after the kernel accepted part of it, those bytes cannot be rolled back; the remaining bytes are not written. The synchronous `Read` and `Write` stream methods block until the operation completes; prefer the asynchronous methods.

**Exit and disposal.** `WaitForExitAsync` returns the exit code once the child has been reaped. `Kill()` sends `SIGKILL` immediately. Disposal is gentler: it sends `SIGHUP` first and escalates to `SIGKILL`, then waits for the child to be reaped; `DisposeAsync` does this without blocking. `Resize` and `Kill` are synchronous because the underlying `ioctl` and signal syscalls complete immediately.

**External event loop (advanced).** `PtyProvider.SpawnAsync(options, eventLoop, cancellationToken)` drives the connection through a caller-owned `IPtyEventLoop` instead of the library's reactor thread. Registration is one-shot: after a readiness callback fires, that registration is disarmed and delivers nothing further until the library re-arms it through `IPtyFdRegistration.UpdateInterests`; a registration created with `PtyFdInterests.None` starts disarmed. Arming a registration whose descriptor is already ready must deliver readiness immediately, and `Dispose` unregisters the descriptor so that no callback runs after it returns. The loop must serialize readiness callbacks so that no two ever run concurrently, and it must keep dispatching until every registration it handed out has been disposed, because disposal and child reaping complete through callbacks. Synchronous blocking connection operations (`Dispose`, `WaitForExit`, and the synchronous stream `Read` and `Write`) must never run on the loop's dispatch thread: their completion depends on callbacks only that thread can deliver.

**Performance posture.** The library favors interactivity and bounded resource use over peak throughput, and two internal choices record deliberate tradeoffs. First, bulk transfers move in small quanta: an operation the descriptor can satisfy immediately completes after a single native syscall on the calling thread, and a parked operation completes after a few KB per reactor visit, so concurrent interactive traffic stays responsive while other connections stream; measured on an 8-core host, this configuration streams 10 concurrent connections at about 40 percent above the pre-quanta design while keeping a loaded interactive echo at sub-millisecond medians. Under full load a small tail of round-trips (roughly the worst percentile) still waits on an OS scheduling quantum; that tail comes from genuine CPU saturation, not from library queuing, and shrinking it further would require giving throughput back. Second, `SpawnAsync` does not return until the child's `exec` has been confirmed over the control channel, so a mistyped `App` or `Cwd` surfaces as an exception carrying the real errno instead of a healthy-looking connection that dies immediately. That confirmation costs a few hundred microseconds per spawn relative to fire-and-forget spawning; it is the price of the error contract and is kept intentionally.

## Building from source

Install a C compiler, CMake, binutils, and the .NET 10 SDK on glibc Linux:

```bash
./src/Porta.Pty.Native/build.sh
dotnet restore LinuxPty.NET.slnx
dotnet build LinuxPty.NET.slnx -c Release --no-restore
```

The native build produces the asset for the machine it runs on. Release packages containing both RID assets are built and published by CI, so building from source is only needed for development.

## Provenance and license

LinuxPty.NET is an independent Linux-focused fork of [Porta.Pty](https://github.com/tomlm/Porta.Pty) by Tom Laird-McConnell, which itself derives from Microsoft's [Pty.Net](https://github.com/microsoft/vs-pty.net). Original copyright notices are preserved in the source headers and in [LICENSE](LICENSE) (MIT).

LinuxPty.NET is not an official Porta.Pty or Microsoft distribution.
