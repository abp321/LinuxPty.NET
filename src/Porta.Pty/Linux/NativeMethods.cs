// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    using System;
    using System.Runtime.InteropServices;
    using System.Runtime.InteropServices.Marshalling;

    internal static partial class NativeMethods
    {
        internal const int SIGHUP = 1;
        internal const int SIGKILL = 9;
        internal const int NonBlockingWait = 1;

        internal const int ReactorAdd = 1;
        internal const int ReactorModify = 2;
        internal const int ReactorDelete = 3;

        internal const uint ReactorRead = 1;
        internal const uint ReactorWrite = 2;
        internal const uint ReactorError = 4;
        internal const uint ReactorHangup = 8;

        private const string LibPortaPty = "libporta_pty";

        internal enum PtyWaitState
        {
            Running = 0,
            Exited = 1,
            Signaled = 2,
            Failed = 3,
        }

        [StructLayout(LayoutKind.Explicit, Size = 20)]
        internal struct PtySpawnResult
        {
            [FieldOffset(0)]
            public int MasterFd;

            [FieldOffset(4)]
            public int Pid;

            [FieldOffset(8)]
            public int PidFd;

            [FieldOffset(12)]
            public int PidFdError;

            [FieldOffset(16)]
            public int Error;
        }

        [StructLayout(LayoutKind.Explicit, Size = 16)]
        internal struct PtyWaitResult
        {
            [FieldOffset(0)]
            public PtyWaitState State;

            [FieldOffset(4)]
            public int ExitCode;

            [FieldOffset(8)]
            public int Signal;

            [FieldOffset(12)]
            public int Error;
        }

        [StructLayout(LayoutKind.Explicit, Size = 16)]
        internal struct PtyReactorEvent
        {
            [FieldOffset(0)]
            public ulong Token;

            [FieldOffset(8)]
            public uint Events;

            [FieldOffset(12)]
            public uint Reserved;
        }

        // The source-generated marshaller cannot preserve null-terminated UTF-8
        // char** argv and environment-mutation arrays without custom marshalling.
        [DllImport(LibPortaPty)]
        internal static extern PtySpawnResult pty_spawn(
            [MarshalAs(UnmanagedType.LPStr)] string file,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[] argv,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[]? environmentMutations,
            [MarshalAs(UnmanagedType.LPStr)] string? workingDir,
            ushort rows,
            ushort cols);

        [LibraryImport(LibPortaPty, SetLastError = true)]
        internal static partial int pty_resize(int masterFd, ushort rows, ushort cols);

        [LibraryImport(LibPortaPty, SetLastError = true)]
        internal static partial int pty_kill(int pid, int signal);

        [LibraryImport(LibPortaPty)]
        internal static partial PtyWaitResult pty_wait_child(int pid, int nonBlocking);

        [LibraryImport(LibPortaPty, SetLastError = true)]
        internal static partial int pty_pidfd_send_signal(int pidFd, int signal);

        [LibraryImport(LibPortaPty, SetLastError = true)]
        internal static partial int pty_close(int masterFd);

        [LibraryImport(LibPortaPty)]
        internal static partial int pty_cleanup_untracked(int masterFd, int pid, int pidFd);

        [LibraryImport(LibPortaPty)]
        internal static partial int pty_reactor_create(
            ulong wakeToken,
            out int epollFd,
            out int wakeFd);

        [LibraryImport(LibPortaPty)]
        internal static partial int pty_reactor_control(
            int epollFd,
            int operation,
            int monitoredFd,
            ulong token,
            uint interests);

        [LibraryImport(LibPortaPty)]
        internal static partial int pty_reactor_wait(
            int epollFd,
            [Out, MarshalUsing(CountElementName = nameof(capacity))] PtyReactorEvent[] events,
            int capacity,
            out int count);

        [LibraryImport(LibPortaPty)]
        internal static partial int pty_reactor_wake(int wakeFd);

        [LibraryImport(LibPortaPty)]
        internal static partial int pty_reactor_drain(int wakeFd);

        [LibraryImport(LibPortaPty)]
        internal static partial int pty_io_read(
            int masterFd,
            IntPtr buffer,
            int length,
            out int transferred);

        [LibraryImport(LibPortaPty)]
        internal static partial int pty_io_write(
            int masterFd,
            IntPtr buffer,
            int length,
            out int transferred);
    }
}
