// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Threading;
    using static Porta.Pty.Linux.NativeMethods;

    /// <summary>
    /// A connection to a Linux pseudoterminal.
    /// </summary>
    internal sealed class PtyConnection : IPtyConnection
    {
        private const int EINTR = 4;
        private const int ESRCH = 3;

        private readonly int controller;
        private readonly int pid;
        private readonly PtyIoContext ioContext;
        private readonly PtyStream readerStream;
        private readonly PtyStream writerStream;
        private readonly object lifetimeGate = new();
        private readonly ManualResetEvent terminalProcessTerminatedEvent = new ManualResetEvent(false);
        private int exitCode;
        private bool isDisposed;

        public PtyConnection(int controller, int pid, PtyIoContext ioContext)
        {
            this.controller = controller;
            this.pid = pid;
            this.ioContext = ioContext;
            this.readerStream = new PtyStream(ioContext, FileAccess.Read);
            this.writerStream = new PtyStream(ioContext, FileAccess.Write);

            var childWatcherThread = new Thread(this.ChildWatcherThreadProc)
            {
                IsBackground = true,
                Priority = ThreadPriority.Lowest,
                Name = $"Watcher thread for child process {pid}",
            };

            childWatcherThread.Start();
        }

        public event EventHandler<PtyExitedEventArgs>? ProcessExited;

        public Stream ReaderStream => this.readerStream;

        public Stream WriterStream => this.writerStream;

        public int Pid => this.pid;

        public int ExitCode => this.exitCode;

        public void Dispose()
        {
            lock (this.lifetimeGate)
            {
                if (this.isDisposed)
                {
                    return;
                }

                this.isDisposed = true;
                this.readerStream.MarkDisposedByConnection();
                this.writerStream.MarkDisposedByConnection();

                // Retirement is acknowledged before close, so the reactor can no longer
                // issue a syscall against a descriptor that Linux may immediately reuse.
                this.ioContext.Stop();
                this.TryKill();
                this.TryClose();
            }
        }

        public void Kill()
        {
            lock (this.lifetimeGate)
            {
                ObjectDisposedException.ThrowIf(this.isDisposed, this);
                if (pty_kill(this.pid, SIGHUP) == -1)
                {
                    int errno = Marshal.GetLastWin32Error();
                    if (errno != ESRCH)
                    {
                        throw new InvalidOperationException($"Killing terminal failed with error {errno}");
                    }
                }
            }
        }

        public void Resize(int cols, int rows)
        {
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
            return this.terminalProcessTerminatedEvent.WaitOne(milliseconds);
        }

        private void TryKill()
        {
            try
            {
                if (pty_kill(this.pid, SIGHUP) == -1
                    && Marshal.GetLastWin32Error() != ESRCH)
                {
                    throw new InvalidOperationException("Killing terminal failed during cleanup.");
                }
            }
            catch
            {
                // The process may already have exited during cleanup.
            }
        }

        private void TryClose()
        {
            try
            {
                pty_close(this.controller);
            }
            catch
            {
                // Cleanup must not throw.
            }
        }

        private void ChildWatcherThreadProc()
        {
            Debug.WriteLine($"Waiting on {this.pid}");
            const int SignalMask = 127;
            const int ExitCodeMask = 255;

            int status = 0;
            if (pty_waitpid(this.pid, ref status, 0) == -1)
            {
                int errno = Marshal.GetLastWin32Error();
                Debug.WriteLine($"Wait failed with {errno}");
                if (errno == EINTR)
                {
                    this.ChildWatcherThreadProc();
                }

                return;
            }

            Debug.WriteLine("Wait succeeded");
            int exitSignal = status & SignalMask;
            this.exitCode = exitSignal == 0 ? (status >> 8) & ExitCodeMask : 0;
            this.terminalProcessTerminatedEvent.Set();
            this.ProcessExited?.Invoke(this, new PtyExitedEventArgs(this.exitCode));
        }
    }
}
