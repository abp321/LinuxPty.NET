// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    using System;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using static Porta.Pty.Linux.NativeMethods;

    /// <summary>
    /// Tracks and reaps exactly one PTY child process.
    /// </summary>
    internal sealed class PtyProcessState
    {
        private const int EPERM = 1;
        private const int EINVAL = 22;
        private const int ENOSYS = 38;
        private const int EINTR = 4;
        private const int SignalMask = 127;
        private const int ExitCodeMask = 255;

        private static int pidFdsUnavailable;

        private readonly int pid;
        private readonly EpollReactor reactor;
        private readonly object reapGate = new();
        private readonly TaskCompletionSource<int> exitCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int pidFileDescriptor = -1;
        private int fallbackRegistered;
        private int reapingClaimed;
        private int exitCode;

        internal PtyProcessState(int pid)
        {
            this.pid = pid;
            this.reactor = EpollReactor.Shared;
        }

        internal ulong ActiveToken { get; set; }

        internal int PidFileDescriptor => Volatile.Read(ref this.pidFileDescriptor);

        internal Task<int> ExitTask => this.exitCompletion.Task;

        internal bool IsExited => this.exitCompletion.Task.IsCompleted;

        internal int ExitCode => Volatile.Read(ref this.exitCode);

        internal int SendSignal(int signal)
        {
            lock (this.reapGate)
            {
                if (this.reapingClaimed != 0)
                {
                    return 0;
                }

                int pidFd = this.PidFileDescriptor;
                if (pidFd >= 0)
                {
                    if (pty_pidfd_send_signal(pidFd, signal) == 0)
                    {
                        return 0;
                    }

                    int pidFdError = Marshal.GetLastWin32Error();
                    if (pidFdError != ENOSYS)
                    {
                        return pidFdError;
                    }
                }

                return pty_kill(this.pid, signal) == 0
                    ? 0
                    : Marshal.GetLastWin32Error();
            }
        }

        internal async Task StartAsync()
        {
            if (Volatile.Read(ref pidFdsUnavailable) == 0)
            {
                int pidFd = pty_pidfd_open(this.pid);
                if (pidFd >= 0)
                {
                    Volatile.Write(ref this.pidFileDescriptor, pidFd);
                    try
                    {
                        await this.reactor.RegisterProcessAsync(this).ConfigureAwait(false);
                        return;
                    }
                    catch
                    {
                        this.ActiveToken = 0;
                        this.ClosePidFileDescriptor();
                    }
                }
                else
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ENOSYS || error == EINVAL || error == EPERM)
                    {
                        Volatile.Write(ref pidFdsUnavailable, 1);
                    }
                }
            }

            this.RegisterFallback();
        }

        internal void UseFallbackAfterReactorFailure()
        {
            this.ActiveToken = 0;
            this.ClosePidFileDescriptor();
            this.RegisterFallback();
        }

        internal bool TryReap(out int exitCode, out Exception? failure)
        {
            lock (this.reapGate)
            {
                exitCode = 0;
                failure = null;
                if (this.reapingClaimed != 0)
                {
                    return false;
                }

                int status = 0;
                int result;
                do
                {
                    result = pty_waitpid(this.pid, ref status, WaitNoHang);
                }
                while (result == -1 && Marshal.GetLastWin32Error() == EINTR);

                if (result == 0)
                {
                    return false;
                }

                this.reapingClaimed = 1;
                if (result == this.pid)
                {
                    int exitSignal = status & SignalMask;
                    exitCode = exitSignal == 0 ? (status >> 8) & ExitCodeMask : 0;
                }
                else
                {
                    int error = Marshal.GetLastWin32Error();
                    failure = EpollReactor.CreateIOException(
                        $"Reaping PTY child process {this.pid}",
                        error);
                }

                return true;
            }
        }

        internal void CompleteReaping(int exitCode, Exception? failure)
        {
            if (failure is null)
            {
                Volatile.Write(ref this.exitCode, exitCode);
                this.exitCompletion.TrySetResult(exitCode);
            }
            else
            {
                this.exitCompletion.TrySetException(failure);
            }
        }

        internal void ClosePidFileDescriptor()
        {
            int pidFd = Interlocked.Exchange(ref this.pidFileDescriptor, -1);
            if (pidFd >= 0)
            {
                _ = pty_close(pidFd);
            }
        }

        private void RegisterFallback()
        {
            if (Interlocked.Exchange(ref this.fallbackRegistered, 1) == 0)
            {
                PtyProcessReaper.Shared.Register(this);
            }
        }
    }
}
