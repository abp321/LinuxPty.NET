// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    using System;
    using System.Runtime.InteropServices;
    using System.Text;

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
        internal const uint ReactorOneShot = 16;

        private const string LibPortaPty = "libporta_pty";

        internal enum PtyWaitState
        {
            Running = 0,
            Exited = 1,
            Signaled = 2,
            Failed = 3,
            Unavailable = 4,
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

        internal static unsafe PtySpawnResult pty_spawn(
            string file,
            string?[] argv,
            string?[] inheritedEnvironment,
            string?[]? environmentMutations,
            string? workingDir,
            ushort rows,
            ushort cols)
        {
            byte** nativeArgv = null;
            byte** nativeInherited = null;
            byte** nativeMutations = null;
            try
            {
                nativeArgv = AllocateStringArray(argv);
                nativeInherited = AllocateStringArray(inheritedEnvironment);
                if (environmentMutations is not null)
                {
                    nativeMutations = AllocateStringArray(environmentMutations);
                }

                return PtySpawnCore(
                    file,
                    nativeArgv,
                    nativeInherited,
                    nativeMutations,
                    workingDir,
                    rows,
                    cols);
            }
            finally
            {
                FreeStringArray(nativeMutations, environmentMutations?.Length ?? 0);
                FreeStringArray(nativeInherited, inheritedEnvironment.Length);
                FreeStringArray(nativeArgv, argv.Length);
            }
        }

        [LibraryImport(LibPortaPty, SetLastError = true)]
        internal static partial int pty_resize(int masterFd, ushort rows, ushort cols);

        [LibraryImport(LibPortaPty, SetLastError = true)]
        internal static partial int pty_kill(int pid, int signal);

        [LibraryImport(LibPortaPty)]
        internal static partial PtyWaitResult pty_wait_child(
            int pid,
            int pidFd,
            int nonBlocking);

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
        internal static unsafe partial int pty_reactor_wait(
            int epollFd,
            PtyReactorEvent* events,
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

        [LibraryImport(LibPortaPty, EntryPoint = "pty_spawn", StringMarshalling = StringMarshalling.Utf8)]
        private static unsafe partial PtySpawnResult PtySpawnCore(
            string file,
            byte** argv,
            byte** inheritedEnvironment,
            byte** environmentMutations,
            string? workingDir,
            ushort rows,
            ushort cols);

        private static unsafe byte** AllocateStringArray(string?[] values)
        {
            // Zeroed so a partially built array is still safe to walk and free.
            byte** native = (byte**)NativeMemory.AllocZeroed((nuint)values.Length, (nuint)sizeof(byte*));
            try
            {
                for (int index = 0; index < values.Length; index++)
                {
                    if (values[index] is { } value)
                    {
                        native[index] = AllocateUtf8(value);
                    }
                }
            }
            catch
            {
                // The caller cannot free an array it never received a pointer to.
                FreeStringArray(native, values.Length);
                throw;
            }

            return native;
        }

        private static unsafe byte* AllocateUtf8(string value)
        {
            int byteCount = Encoding.UTF8.GetByteCount(value);
            byte* buffer = (byte*)NativeMemory.Alloc((nuint)byteCount + 1);
            int written = Encoding.UTF8.GetBytes(value, new Span<byte>(buffer, byteCount));
            buffer[written] = 0;
            return buffer;
        }

        private static unsafe void FreeStringArray(byte** native, int length)
        {
            if (native is null)
            {
                return;
            }

            for (int index = 0; index < length; index++)
            {
                NativeMemory.Free(native[index]);
            }

            NativeMemory.Free(native);
        }
    }
}
