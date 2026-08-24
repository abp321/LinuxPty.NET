# LinuxPty.NET

LinuxPty.NET is a Linux-only pseudoterminal (PTY) library for .NET 10. It ships native assets for glibc-based `linux-x64` and `linux-arm64`; musl assets are not provided.

[![Linux CI](https://github.com/abp321/LinuxPty.NET/actions/workflows/build-linux.yml/badge.svg?branch=main)](https://github.com/abp321/LinuxPty.NET/actions/workflows/build-linux.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

The NuGet package ID is `LinuxPty.NET`. The inherited public API remains in the `Porta.Pty` namespace.

## Status

This is a pre-1.0 fork. Process spawning, resize, kill, disposal, environment handling, and stream behavior remain based on Porta.Pty.

The current PTY streams use blocking file descriptors. Despite the inherited `SpawnAsync` name, process creation is synchronous, the cancellation token is not observed, and stream async methods run over synchronous handles. The planned non-blocking and epoll-based I/O redesign is not implemented yet.

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

The first published preview was `0.1.0-preview.1`. It predates this Linux-only cleanup.

## Usage

```csharp
using System;
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

byte[] command = System.Text.Encoding.UTF8.GetBytes("echo hello\r");
terminal.WriterStream.Write(command);
terminal.WriterStream.Flush();

byte[] buffer = new byte[4096];
int count = terminal.ReaderStream.Read(buffer);
Console.WriteLine(System.Text.Encoding.UTF8.GetString(buffer, 0, count));
```

## Building

Install a C compiler, CMake, binutils, and the .NET 10 SDK on glibc Linux:

```bash
./src/Porta.Pty.Native/build.sh
dotnet restore LinuxPty.NET.slnx
dotnet build LinuxPty.NET.slnx -c Release --no-restore
```

The native build produces the asset for the machine it runs on. A distributable package requires both RID assets. The manual GitHub Packages workflow builds both architectures, validates their ELF and glibc contracts, packs the library, verifies the package contents, and then publishes with `GITHUB_TOKEN`.

## Provenance and license

LinuxPty.NET is an independent Linux-focused fork of [Porta.Pty](https://github.com/tomlm/Porta.Pty). The upstream project was created and maintained by Tom Laird-McConnell. The original copyright notice and MIT license are preserved in [LICENSE](LICENSE).

LinuxPty.NET is not an official Porta.Pty distribution.
