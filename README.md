# LinuxPty.NET

LinuxPty.NET is a Linux-only pseudoterminal (PTY) library for .NET 10. It spawns a process under a fresh PTY and exposes read and write streams plus asynchronous exit observation. Native assets ship for glibc-based `linux-x64` and `linux-arm64`; musl-based distributions (such as Alpine) are not supported.

[![Publish package](https://github.com/abp321/LinuxPty.NET/actions/workflows/publish-package.yml/badge.svg?branch=main)](https://github.com/abp321/LinuxPty.NET/actions/workflows/publish-package.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

The NuGet package ID is `LinuxPty.NET`. The public API lives in the `Porta.Pty` namespace, inherited from the upstream project.

## Features

- Spawn a child process under a new PTY with an initial size, working directory, and environment overrides.
- Readiness-driven stream I/O: asynchronous reads and writes do not tie up a thread while waiting. All connections in a process share one epoll reactor and one reactor thread; there is no thread per connection.
- Exit observation through pidfds on kernels that support them (Linux 5.4+), with one process-wide polling fallback reaper on older kernels.
- Race-free child tracking: the spawned child opens its own pidfd and transfers it to the parent over a close-on-exec `AF_UNIX` socketpair before it is released to `chdir` and `exec`, so exit observation never resolves a numeric PID that could already have been recycled.
- Precise spawn failures: a failed `chdir` or `exec` in the child reports its errno back to the parent through a control channel, so a mistyped executable path surfaces as an exception with the real errno instead of a healthy-looking spawn that exits immediately.
- Awaitable lifecycle: `WaitForExitAsync` and `DisposeAsync` are genuinely asynchronous; synchronous `WaitForExit` and `Dispose` remain available.
- Deterministic exit codes: a normal exit reports the raw exit code, and death by signal reports 128 plus the signal number.

## Requirements

- Linux with glibc, on x64 or arm64. Calling `SpawnAsync` on any other OS throws `PlatformNotSupportedException`.
- .NET 10.

## Installation

LinuxPty.NET is distributed through GitHub Packages, which requires an authenticated NuGet source even for public packages. Configure the `abp321` source with a GitHub token that has `read:packages`, then install:

```bash
dotnet nuget add source \
  --username YOUR_GITHUB_USERNAME \
  --password YOUR_GITHUB_TOKEN \
  --store-password-in-clear-text \
  --name github-abp321 \
  https://nuget.pkg.github.com/abp321/index.json

dotnet add package LinuxPty.NET --version VERSION --source github-abp321
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

**Spawning.** `SpawnAsync` validates a snapshot of the options, then queues the inherently synchronous native spawn on one dedicated process-wide worker, so it never blocks its caller or a ThreadPool thread. Cancellation is observed before queued work begins; cancellation during native process creation completes only after the resulting child has been killed, its master descriptor closed, and the child reaped.

**Environment.** The child inherits the managed process environment captured immediately before the native spawn (`Environment.GetEnvironmentVariables()`), not libc's potentially stale `environ`. The native shim then sets `TERM=xterm-256color` and unsets `TMUX`, `TMUX_PANE`, `STY`, `WINDOW`, `WINDOWID`, `TERMCAP`, `COLUMNS`, and `LINES`. Entries from `PtyOptions.Environment` are applied last, so they can override anything, including `TERM`; an empty value means unset. Executable lookup uses the resulting effective `PATH`, including one changed through `PtyOptions.Environment`.

**I/O.** Reads and writes are queued FIFO per connection, and cancellation wakes the reactor promptly. An operation the descriptor can satisfy immediately completes on the calling thread without a reactor round-trip; only operations that must wait for readiness park in the reactor. If a write is cancelled after the kernel accepted part of it, those bytes cannot be rolled back; the remaining bytes are not written. The synchronous `Read` and `Write` stream methods block until the operation completes; prefer the asynchronous methods.

**Exit and disposal.** `WaitForExitAsync` returns the exit code once the child has been reaped. `Kill()` sends `SIGKILL` immediately. Disposal is gentler: it sends `SIGHUP` first and escalates to `SIGKILL`, then waits for the child to be reaped; `DisposeAsync` does this without blocking. `Resize` and `Kill` are synchronous because the underlying `ioctl` and signal syscalls complete immediately.

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
