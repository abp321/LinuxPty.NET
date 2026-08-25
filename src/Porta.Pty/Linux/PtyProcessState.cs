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
        private readonly int pid;
        private readonly EpollReactor reactor;
        private readonly Lock reapGate = new();
        private readonly TaskCompletionSource<int> exitCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int pidFileDescriptor;
        private ulong activeToken;
        private LifetimeState lifetimeState;
        private int exitCode;

        internal PtyProcessState(int pid, int pidFileDescriptor, int pidFileDescriptorError)
        {
            if ((pidFileDescriptor >= 0) != (pidFileDescriptorError == 0))
            {
                throw new InvalidOperationException(
                    "The native spawn returned an inconsistent pidfd result.");
            }

            this.pid = pid;
            this.pidFileDescriptor = pidFileDescriptor;
            this.reactor = EpollReactor.Shared;
        }

        private enum LifetimeState
        {
            Unowned,
            RegisteringEpoll,
            Epoll,
            RegisteringFallback,
            Fallback,
            Reaping,
            Completed,
        }

        internal Task<int> ExitTask => this.exitCompletion.Task;

        internal bool IsExited => this.exitCompletion.Task.IsCompleted;

        internal int ExitCode => Volatile.Read(ref this.exitCode);

        internal int SendSignal(int signal)
        {
            lock (this.reapGate)
            {
                if (this.lifetimeState is LifetimeState.Reaping or LifetimeState.Completed)
                {
                    return 0;
                }

                if (this.pidFileDescriptor >= 0)
                {
                    if (pty_pidfd_send_signal(this.pidFileDescriptor, signal) == 0)
                    {
                        return 0;
                    }

                    // Once a pidfd has identified the child, falling back to kill(pid)
                    // could target a reused PID after external reaping.
                    return Marshal.GetLastWin32Error();
                }

                return pty_kill(this.pid, signal) == 0
                    ? 0
                    : Marshal.GetLastWin32Error();
            }
        }

        internal async Task StartAsync()
        {
            if (this.pidFileDescriptor >= 0)
            {
                lock (this.reapGate)
                {
                    this.lifetimeState = LifetimeState.RegisteringEpoll;
                }

                try
                {
                    await this.reactor.RegisterProcessAsync(this).ConfigureAwait(false);
                    return;
                }
                catch
                {
                    lock (this.reapGate)
                    {
                        if (this.lifetimeState != LifetimeState.RegisteringEpoll)
                        {
                            throw new InvalidOperationException(
                                "The pidfd registration did not return ownership.");
                        }

                        this.lifetimeState = LifetimeState.Unowned;
                    }
                }
            }

            this.RegisterFallback();
        }

        internal int RegisterWithReactor(ulong token, Func<int, int> register)
        {
            lock (this.reapGate)
            {
                if (this.lifetimeState != LifetimeState.RegisteringEpoll
                    || this.pidFileDescriptor < 0)
                {
                    throw new InvalidOperationException("The pidfd is not available for registration.");
                }

                int error = register(this.pidFileDescriptor);
                if (error == 0)
                {
                    this.activeToken = token;
                    this.lifetimeState = LifetimeState.Epoll;
                }

                return error;
            }
        }

        internal void RollBackReactorRegistration(ulong token, Action<int> unregister)
        {
            lock (this.reapGate)
            {
                if (this.lifetimeState != LifetimeState.Epoll || this.activeToken != token)
                {
                    return;
                }

                try
                {
                    unregister(this.pidFileDescriptor);
                }
                finally
                {
                    this.activeToken = 0;
                    this.lifetimeState = LifetimeState.RegisteringEpoll;
                }
            }
        }

        internal bool HasReactorToken(ulong token)
        {
            lock (this.reapGate)
            {
                return this.lifetimeState == LifetimeState.Epoll && this.activeToken == token;
            }
        }

        internal void UseFallbackAfterReactorFailure()
        {
            lock (this.reapGate)
            {
                if (this.lifetimeState != LifetimeState.Epoll)
                {
                    return;
                }

                this.activeToken = 0;
                this.lifetimeState = LifetimeState.Unowned;
            }

            try
            {
                this.RegisterFallback();
            }
            catch
            {
                // A failed process-wide reaper cannot leave an unowned child. The
                // reactor thread performs the exceptional blocking cleanup itself.
                _ = this.SendSignal(SIGKILL);
                this.ReapSynchronouslyAfterTrackingFailure();
            }
        }

        internal bool TryReap(out int exitCode, out Exception? failure)
        {
            lock (this.reapGate)
            {
                exitCode = 0;
                failure = null;
                if (this.lifetimeState is not (LifetimeState.Epoll or LifetimeState.Fallback))
                {
                    return false;
                }

                PtyWaitResult result = pty_wait_child(
                    this.pid,
                    this.pidFileDescriptor,
                    NonBlockingWait);
                if (result.State == PtyWaitState.Running)
                {
                    return false;
                }

                this.lifetimeState = LifetimeState.Reaping;
                if (result.State == PtyWaitState.Exited)
                {
                    exitCode = result.ExitCode;
                }
                else if (result.State is PtyWaitState.Signaled or PtyWaitState.Unavailable)
                {
                    exitCode = 0;
                }
                else
                {
                    failure = EpollReactor.CreateIOException(
                        $"Reaping PTY child process {this.pid}",
                        result.Error);
                }

                return true;
            }
        }

        internal Exception? DetachAfterReapingFromReactor(
            ulong token,
            Action<int> unregister,
            Exception? failure)
        {
            lock (this.reapGate)
            {
                if (this.lifetimeState != LifetimeState.Reaping || this.activeToken != token)
                {
                    return failure ?? new InvalidOperationException(
                        "The pidfd reaping ownership changed before detachment.");
                }

                try
                {
                    unregister(this.pidFileDescriptor);
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }

                this.activeToken = 0;
                this.ClosePidFileDescriptorLocked();
                this.lifetimeState = LifetimeState.Completed;
                return failure;
            }
        }

        internal void FinishReapingFromFallback(int exitCode, Exception? failure)
        {
            lock (this.reapGate)
            {
                if (this.lifetimeState != LifetimeState.Reaping)
                {
                    return;
                }

                this.ClosePidFileDescriptorLocked();
                this.lifetimeState = LifetimeState.Completed;
            }

            this.CompleteReaping(exitCode, failure);
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

        internal void ReapSynchronouslyAfterTrackingFailure()
        {
            int pidFd;
            lock (this.reapGate)
            {
                if (this.lifetimeState != LifetimeState.Unowned)
                {
                    return;
                }

                // Claim reaping only after every signal operation that was already
                // using the pidfd has left the same gate.
                this.lifetimeState = LifetimeState.Reaping;
                pidFd = this.pidFileDescriptor;
            }

            PtyWaitResult result = pty_wait_child(this.pid, pidFd, nonBlocking: 0);

            int exitCode = 0;
            Exception? failure = null;
            if (result.State == PtyWaitState.Exited)
            {
                exitCode = result.ExitCode;
            }
            else if (result.State is PtyWaitState.Signaled or PtyWaitState.Unavailable)
            {
                exitCode = 0;
            }
            else
            {
                failure = EpollReactor.CreateIOException(
                    $"Reaping untracked PTY child process {this.pid}",
                    result.Error);
            }

            lock (this.reapGate)
            {
                this.ClosePidFileDescriptorLocked();
                this.lifetimeState = LifetimeState.Completed;
            }

            this.CompleteReaping(exitCode, failure);
        }

        private void RegisterFallback()
        {
            lock (this.reapGate)
            {
                if (this.lifetimeState is LifetimeState.Fallback
                    or LifetimeState.Reaping
                    or LifetimeState.Completed)
                {
                    return;
                }

                if (this.lifetimeState != LifetimeState.Unowned)
                {
                    throw new InvalidOperationException(
                        "The PTY child already has an exit-observation owner.");
                }

                this.lifetimeState = LifetimeState.RegisteringFallback;
                try
                {
                    PtyProcessReaper.Shared.Register(this);
                    this.lifetimeState = LifetimeState.Fallback;
                }
                catch
                {
                    this.lifetimeState = LifetimeState.Unowned;
                    throw;
                }
            }
        }

        private void ClosePidFileDescriptorLocked()
        {
            if (this.pidFileDescriptor < 0)
            {
                return;
            }

            int pidFd = this.pidFileDescriptor;
            this.pidFileDescriptor = -1;
            _ = pty_close(pidFd);
        }
    }
}
