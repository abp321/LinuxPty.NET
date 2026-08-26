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
    /// A connection to a Linux pseudoterminal.
    /// </summary>
    internal sealed class PtyConnection : IPtyConnection
    {
        private const int ESRCH = 3;

        private readonly int controller;
        private readonly int pid;
        private readonly PtyIoContext ioContext;
        private readonly PtyProcessState processState;
        private readonly PtyStream readerStream;
        private readonly PtyStream writerStream;
        private readonly Lock lifetimeGate = new();
        private readonly Lock exitEventGate = new();
        private EventHandler<PtyExitedEventArgs>? processExited;
        private bool exitEventRaised;
        private int raisedExitCode;
        private int masterClosed;
        private bool isDisposed;
        private Task? disposalTask;

        internal PtyConnection(
            int controller,
            int pid,
            PtyIoContext ioContext,
            PtyProcessState processState)
        {
            this.controller = controller;
            this.pid = pid;
            this.ioContext = ioContext;
            this.processState = processState;
            this.readerStream = new PtyStream(ioContext, FileAccess.Read);
            this.writerStream = new PtyStream(ioContext, FileAccess.Write);
            _ = this.ObserveProcessExitAsync();
        }

        public event EventHandler<PtyExitedEventArgs>? ProcessExited
        {
            add
            {
                bool replay;
                int exitCode;
                lock (this.exitEventGate)
                {
                    this.processExited += value;
                    replay = this.exitEventRaised && value is not null;
                    exitCode = this.raisedExitCode;
                }

                // The child can be reaped before the caller subscribes, so a late
                // handler is invoked here instead of never being called at all.
                if (replay)
                {
                    this.InvokeExitHandlers(value!.GetInvocationList(), exitCode);
                }
            }

            remove
            {
                lock (this.exitEventGate)
                {
                    this.processExited -= value;
                }
            }
        }

        public Stream ReaderStream => this.readerStream;

        public Stream WriterStream => this.writerStream;

        public int Pid => this.pid;

        public int ExitCode => this.processState.ExitCode;

        public void Dispose()
        {
            this.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        public ValueTask DisposeAsync()
        {
            lock (this.lifetimeGate)
            {
                if (this.disposalTask is not null)
                {
                    return new ValueTask(this.disposalTask);
                }

                this.isDisposed = true;
                this.readerStream.MarkDisposedByConnection();
                this.writerStream.MarkDisposedByConnection();
                this.disposalTask = this.DisposeCoreAsync();
                return new ValueTask(this.disposalTask);
            }
        }

        public void Kill()
        {
            lock (this.lifetimeGate)
            {
                ObjectDisposedException.ThrowIf(this.isDisposed, this);
                int error = this.processState.SendSignal(SIGKILL);
                if (error != 0)
                {
                    if (error != ESRCH)
                    {
                        throw new InvalidOperationException($"Killing terminal failed with error {error}");
                    }
                }
            }
        }

        public void Resize(int cols, int rows)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(cols);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(cols, ushort.MaxValue);
            ArgumentOutOfRangeException.ThrowIfNegative(rows);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, ushort.MaxValue);
            lock (this.lifetimeGate)
            {
                ObjectDisposedException.ThrowIf(this.isDisposed, this);
                if (pty_resize(this.controller, (ushort)rows, (ushort)cols) == -1)
                {
                    throw new InvalidOperationException(
                        $"Resizing terminal failed with error {Marshal.GetLastWin32Error()}");
                }
            }
        }

        public bool WaitForExit(int milliseconds)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(milliseconds, -1);
            Task<int> exitTask = this.processState.ExitTask;
            if (exitTask.IsCompleted)
            {
                _ = exitTask.GetAwaiter().GetResult();
                return true;
            }

            if (milliseconds == 0)
            {
                return false;
            }

            if (milliseconds == -1)
            {
                _ = exitTask.GetAwaiter().GetResult();
                return true;
            }

            using var timeout = new CancellationTokenSource(milliseconds);
            try
            {
                _ = exitTask.WaitAsync(timeout.Token).GetAwaiter().GetResult();
                return true;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!exitTask.IsCompleted)
                {
                    return false;
                }

                _ = exitTask.GetAwaiter().GetResult();
                return true;
            }
        }

        public ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            Task<int> exitTask = this.processState.ExitTask;
            if (!cancellationToken.CanBeCanceled || exitTask.IsCompleted)
            {
                return new ValueTask<int>(exitTask);
            }

            return new ValueTask<int>(exitTask.WaitAsync(cancellationToken));
        }

        private async Task DisposeCoreAsync()
        {
            this.TryKill(SIGHUP);
            try
            {
                await this.ioContext.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                this.TryCloseMaster();
            }

            if (!this.processState.IsExited)
            {
                // SIGHUP preserves the historical cleanup behavior; SIGKILL ensures an
                // ignored HUP cannot leave async disposal waiting forever.
                this.TryKill(SIGKILL);
            }

            try
            {
                await this.processState.ExitTask.ConfigureAwait(false);
            }
            catch
            {
                // The descriptor is retired and closed even if an external SIGCHLD
                // policy made the child's exit status unavailable.
            }
        }

        private async Task ObserveProcessExitAsync()
        {
            int processExitCode;
            try
            {
                processExitCode = await this.processState.ExitTask.ConfigureAwait(false);
            }
            catch
            {
                // Reaping failed, so the status is unknown: distinct from a clean 0.
                processExitCode = -1;
            }

            Delegate[]? handlers = null;
            lock (this.exitEventGate)
            {
                if (this.exitEventRaised)
                {
                    return;
                }

                this.exitEventRaised = true;
                this.raisedExitCode = processExitCode;
                handlers = this.processExited?.GetInvocationList();
            }

            if (handlers is not null)
            {
                this.InvokeExitHandlers(handlers, processExitCode);
            }
        }

        private void InvokeExitHandlers(Delegate[] handlers, int processExitCode)
        {
            var eventArgs = new PtyExitedEventArgs(processExitCode);
            foreach (Delegate handler in handlers)
            {
                try
                {
                    ((EventHandler<PtyExitedEventArgs>)handler)(this, eventArgs);
                }
                catch
                {
                    // A subscriber must not stop the process-wide reactor or reaper.
                }
            }
        }

        private void TryKill(int signal)
        {
            try
            {
                int error = this.processState.SendSignal(signal);
                if (error != 0 && error != ESRCH)
                {
                    throw new InvalidOperationException("Killing terminal failed during cleanup.");
                }
            }
            catch
            {
                // The process may already have exited during cleanup.
            }
        }

        private void TryCloseMaster()
        {
            if (Interlocked.Exchange(ref this.masterClosed, 1) != 0)
            {
                return;
            }

            try
            {
                // Never retry close after EINTR because Linux may already have reused the fd.
                _ = pty_close(this.controller);
            }
            catch
            {
                // Cleanup must not throw.
            }
        }
    }
}
