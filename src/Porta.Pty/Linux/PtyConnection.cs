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
        private readonly ManualResetEvent terminalProcessTerminatedEvent = new ManualResetEvent(false);
        private int exitCode;
        private bool isDisposed;

        public PtyConnection(int controller, int pid)
        {
            this.ReaderStream = new PtyStream(controller, FileAccess.Read);
            this.WriterStream = new PtyStream(controller, FileAccess.Write);
            this.controller = controller;
            this.pid = pid;

            var childWatcherThread = new Thread(this.ChildWatcherThreadProc)
            {
                IsBackground = true,
                Priority = ThreadPriority.Lowest,
                Name = $"Watcher thread for child process {pid}",
            };

            childWatcherThread.Start();
        }

        public event EventHandler<PtyExitedEventArgs>? ProcessExited;

        public Stream ReaderStream { get; }

        public Stream WriterStream { get; }

        public int Pid => this.pid;

        public int ExitCode => this.exitCode;

        public void Dispose()
        {
            if (this.isDisposed)
            {
                return;
            }

            this.isDisposed = true;
            this.ReaderStream.Dispose();
            this.WriterStream.Dispose();

            // Both streams wrap the same non-owning fd. Kill first, then close it exactly once here.
            this.TryKill();
            this.TryClose();
        }

        public void Kill()
        {
            if (pty_kill(this.pid, SIGHUP) == -1)
            {
                int errno = Marshal.GetLastWin32Error();
                if (errno != ESRCH)
                {
                    throw new InvalidOperationException($"Killing terminal failed with error {errno}");
                }
            }
        }

        public void Resize(int cols, int rows)
        {
            if (pty_resize(this.controller, (ushort)rows, (ushort)cols) == -1)
            {
                throw new InvalidOperationException(
                    $"Resizing terminal failed with error {Marshal.GetLastWin32Error()}");
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
                this.Kill();
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
