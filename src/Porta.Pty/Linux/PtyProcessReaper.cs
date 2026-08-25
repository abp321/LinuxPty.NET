// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    using System;
    using System.Collections.Generic;
    using System.Threading;

    /// <summary>
    /// Process-wide fallback for kernels where pidfds are unavailable.
    /// </summary>
    internal sealed class PtyProcessReaper
    {
        private const int PollIntervalMilliseconds = 20;
        private static readonly Lock SharedGate = new();
        private static PtyProcessReaper? shared;

        private readonly Lock gate = new();
        private readonly HashSet<PtyProcessState> processes = new();
        private readonly AutoResetEvent wakeEvent;
        private readonly Thread thread;

        private PtyProcessReaper()
        {
            this.wakeEvent = new AutoResetEvent(false);
            try
            {
                this.thread = new Thread(this.Run)
                {
                    IsBackground = true,
                    Name = "LinuxPty.NET process reaper",
                };
                this.thread.Start();
            }
            catch
            {
                this.wakeEvent.Dispose();
                throw;
            }
        }

        internal static PtyProcessReaper Shared
        {
            get
            {
                lock (SharedGate)
                {
                    return shared ??= new PtyProcessReaper();
                }
            }
        }

        internal void Register(PtyProcessState process)
        {
            lock (this.gate)
            {
                if (!this.processes.Add(process))
                {
                    throw new InvalidOperationException("The PTY child is already registered.");
                }

                try
                {
                    this.wakeEvent.Set();
                }
                catch
                {
                    this.processes.Remove(process);
                    throw;
                }
            }
        }

        private void Run()
        {
            for (;;)
            {
                PtyProcessState[] snapshot;
                lock (this.gate)
                {
                    snapshot = new PtyProcessState[this.processes.Count];
                    this.processes.CopyTo(snapshot);
                }

                foreach (PtyProcessState process in snapshot)
                {
                    if (!process.TryReap(out int exitCode, out Exception? failure))
                    {
                        continue;
                    }

                    lock (this.gate)
                    {
                        this.processes.Remove(process);
                    }

                    process.FinishReapingFromFallback(exitCode, failure);
                }

                this.wakeEvent.WaitOne(
                    snapshot.Length == 0 ? Timeout.Infinite : PollIntervalMilliseconds);
            }
        }
    }
}
