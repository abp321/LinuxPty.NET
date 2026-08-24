# LinuxPty.NET

LinuxPty.NET is a Linux-focused pseudoterminal (PTY) library for .NET 10.

It is derived from [Porta.Pty](https://github.com/tomlm/Porta.Pty) and keeps the inherited API shape for now while the fork moves toward a Linux-specific implementation with fewer cross-platform compromises.

[![Linux](https://github.com/abp321/LinuxPty.NET/actions/workflows/build-linux.yml/badge.svg?branch=main)](https://github.com/abp321/LinuxPty.NET/actions/workflows/build-linux.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## Status

This fork is at an early stage.

The repository currently still contains inherited Windows and macOS implementations from Porta.Pty. Linux is the target of this fork, and the planned specialization will remove those platform paths rather than preserve cross-platform compatibility indefinitely.

The current package keeps the `Porta.Pty` namespace so existing Linux-side call sites can migrate without an unnecessary source-level rename while the internals are being redesigned. The NuGet package identity is separate: `LinuxPty.NET`.

## Direction

LinuxPty.NET is intended to become a deliberately Linux-specific PTY implementation. The main goals are:

- true non-blocking/asynchronous PTY I/O rather than async-over-sync behavior;
- efficient handling of many long-lived PTY sessions without one blocked I/O thread per session;
- use of modern Linux primitives where they provide a measurable correctness, lifecycle, or performance benefit;
- simpler code by removing Windows/macOS compatibility layers once the Linux-specific implementation replaces them;
- explicit attention to cancellation, process ownership, descriptor lifetime, teardown, and restart-safe PTY workloads.

These are project goals, not claims about features already completed in the initial fork.

## Installation

The project is configured to publish as the NuGet package `LinuxPty.NET`, starting at version `0.1.0`.

After the first package is published to NuGet.org:

```bash
dotnet add package LinuxPty.NET
```

The package currently targets `net10.0`.

## Usage

The inherited API currently remains under the `Porta.Pty` namespace:

```csharp
using Porta.Pty;

var options = new PtyOptions
{
    Name = "bash",
    Cols = 120,
    Rows = 30,
    Cwd = Environment.CurrentDirectory,
    App = "/bin/bash",
    CommandLine = []
};

using IPtyConnection terminal = await PtyProvider.SpawnAsync(
    options,
    CancellationToken.None);

byte[] command = System.Text.Encoding.UTF8.GetBytes("echo hello\r");
await terminal.WriterStream.WriteAsync(command);
await terminal.WriterStream.FlushAsync();

byte[] buffer = new byte[4096];
int count = await terminal.ReaderStream.ReadAsync(buffer);
Console.WriteLine(System.Text.Encoding.UTF8.GetString(buffer, 0, count));
```

The current stream implementation is inherited from Porta.Pty. In particular, Linux PTY I/O has not yet been replaced by the planned true non-blocking implementation.

## Building

Linux builds require a native toolchain for the PTY shim:

```bash
sudo apt-get install cmake build-essential
cd src/Porta.Pty.Native
./build.sh
cd ../..
dotnet restore src/Porta.Pty.sln
dotnet build src/Porta.Pty.sln
```

The Linux GitHub Actions workflow also builds the native shim, builds the solution, runs the existing test suite, and verifies consumption through a locally packed NuGet package.

## Publishing

NuGet publication is manual through the inherited GitHub Actions release workflow. The repository must contain an Actions secret named `NUGET_KEY` with a NuGet.org API key authorized to push `LinuxPty.NET`.

The release workflow packs the version declared in `src/Porta.Pty/Porta.Pty.csproj`. A separate prerelease workflow publishes automatically numbered prereleases of the same base version.

## Credits and provenance

LinuxPty.NET is a fork of [Porta.Pty](https://github.com/tomlm/Porta.Pty), created and maintained by **Tom Laird-McConnell**. The fork preserves the original Git history and is distributed under the same MIT license.

Original code remains copyright its respective copyright holders. The original Porta.Pty copyright and MIT permission notice are preserved in [LICENSE](LICENSE), and the license file is included in the LinuxPty.NET NuGet package.

Some inherited source files also retain their original Microsoft copyright notices.

LinuxPty.NET is an independent fork and is not an official Porta.Pty distribution.

## License

LinuxPty.NET is distributed under the [MIT License](LICENSE).
