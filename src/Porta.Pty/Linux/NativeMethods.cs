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

        private const string LibPortaPty = "liblinuxpty_net";

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

        internal static unsafe PtySpawnArguments PrepareSpawnArguments(
            string file,
            string?[] argv,
            string?[] inheritedEnvironment,
            string?[]? environmentMutations,
            string? workingDir)
        {
            var arguments = new PtySpawnArguments();
            try
            {
                arguments.File = AllocateUtf8(file);
                arguments.ArgvLength = argv.Length;
                arguments.Argv = AllocateStringArray(argv);
                arguments.InheritedLength = inheritedEnvironment.Length;
                arguments.InheritedEnvironment = AllocateStringArray(inheritedEnvironment);
                if (environmentMutations is not null)
                {
                    arguments.MutationsLength = environmentMutations.Length;
                    arguments.EnvironmentMutations = AllocateStringArray(environmentMutations);
                }

                if (workingDir is not null)
                {
                    arguments.WorkingDir = AllocateUtf8(workingDir);
                }

                return arguments;
            }
            catch
            {
                arguments.Dispose();
                throw;
            }
        }

        internal static unsafe PtySpawnResult pty_spawn(PtySpawnArguments arguments, ushort rows, ushort cols)
        {
            return PtySpawnCore(
                arguments.File,
                arguments.Argv,
                arguments.InheritedEnvironment,
                arguments.EnvironmentMutations,
                arguments.WorkingDir,
                rows,
                cols);
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
            ulong timerToken,
            out int epollFd,
            out int wakeFd,
            out int timerFd);

        [LibraryImport(LibPortaPty)]
        internal static partial int pty_eventfd_create(out int fd);

        [LibraryImport(LibPortaPty)]
        internal static partial int pty_timerfd_create(out int fd);

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
        internal static partial int pty_reactor_set_timer(int timerFd, int milliseconds);

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

        [LibraryImport(LibPortaPty, EntryPoint = "pty_spawn")]
        private static unsafe partial PtySpawnResult PtySpawnCore(
            byte* file,
            byte** argv,
            byte** inheritedEnvironment,
            byte** environmentMutations,
            byte* workingDir,
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

        internal sealed unsafe class PtySpawnArguments : IDisposable
        {
            internal byte* File;
            internal byte** Argv;
            internal int ArgvLength;
            internal byte** InheritedEnvironment;
            internal int InheritedLength;
            internal byte** EnvironmentMutations;
            internal int MutationsLength;
            internal byte* WorkingDir;

            public void Dispose()
            {
                FreeStringArray(this.EnvironmentMutations, this.MutationsLength);
                this.EnvironmentMutations = null;
                FreeStringArray(this.InheritedEnvironment, this.InheritedLength);
                this.InheritedEnvironment = null;
                FreeStringArray(this.Argv, this.ArgvLength);
                this.Argv = null;
                NativeMemory.Free(this.File);
                this.File = null;
                NativeMemory.Free(this.WorkingDir);
                this.WorkingDir = null;
            }
        }
    }
}
