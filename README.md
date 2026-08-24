# LinuxPty.NET

LinuxPty.NET is a Linux-only pseudoterminal (PTY) library for .NET 10. It ships native assets for glibc-based `linux-x64` and `linux-arm64`; musl assets are not provided.

[![Publish package](https://github.com/abp321/LinuxPty.NET/actions/workflows/publish-package.yml/badge.svg?branch=main)](https://github.com/abp321/LinuxPty.NET/actions/workflows/publish-package.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

The NuGet package ID is `LinuxPty.NET`. The inherited public API remains in the `Porta.Pty` namespace.

## Status

LinuxPty.NET publishes on the `1.0.x` package line. Process spawning, resize, kill, disposal, and environment handling remain based on Porta.Pty.

PTY stream I/O is non-blocking and readiness-driven on Linux. All connections in a process share one epoll reactor and one reactor thread; pending asynchronous reads and writes do not occupy blocked ThreadPool workers. Synchronous `Stream` methods still block their caller by definition. Despite the inherited `SpawnAsync` name, process creation itself remains synchronous and its cancellation token is not observed.

Reads and writes are queued FIFO per connection. Cancellation wakes the reactor promptly. If a write is cancelled after the kernel accepted part of it, those bytes cannot be rolled back; the remaining bytes are not written.

## Installation

LinuxPty.NET is distributed through GitHub Packages. Configure the `abp321` NuGet source with a GitHub token that has `read:packages`, then choose an available package version:

```bash
dotnet nuget add source \
  --username YOUR_GITHUB_USERNAME \
  --password YOUR_GITHUB_TOKEN \
  --store-password-in-clear-text \
  --name github-abp321 \
  https://nuget.pkg.github.com/abp321/index.json

dotnet add package LinuxPty.NET --version VERSION --source github-abp321
```

Published versions are deterministic: each commit maps to `1.0.<repository commit count>`.

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

using IPtyConnection terminal = await PtyProvider.SpawnAsync(
    options,
    CancellationToken.None);

byte[] command = Encoding.UTF8.GetBytes("echo hello\r");
await terminal.WriterStream.WriteAsync(command);

byte[] buffer = new byte[4096];
int count = await terminal.ReaderStream.ReadAsync(buffer);
Console.WriteLine(Encoding.UTF8.GetString(buffer, 0, count));
```

## Building

Install a C compiler, CMake, binutils, and the .NET 10 SDK on glibc Linux:

```bash
./src/Porta.Pty.Native/build.sh
dotnet restore LinuxPty.NET.slnx
dotnet build LinuxPty.NET.slnx -c Release --no-restore
```

The native build produces the asset for the machine it runs on. A distributable package requires both RID assets. Every push to `main` runs the GitHub Packages workflow, which builds both architectures, validates their ELF and glibc contracts, packs the library, verifies the package contents, and then publishes with `GITHUB_TOKEN`. Manual workflow dispatch remains available; rerunning the same commit reuses its version and safely skips a package that already exists.

## Provenance and license

LinuxPty.NET is an independent Linux-focused fork of [Porta.Pty](https://github.com/tomlm/Porta.Pty). The upstream project was created and maintained by Tom Laird-McConnell. The original copyright notice and MIT license are preserved in [LICENSE](LICENSE).

LinuxPty.NET is not an official Porta.Pty distribution.
