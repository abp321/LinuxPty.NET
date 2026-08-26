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
        private const int BasePollIntervalMilliseconds = 20;
        private const int MaxPollIntervalMilliseconds = 500;
        private const int PollIntervalGrowthFactor = 2;
        private static readonly Lock SharedGate = new();
        private static PtyProcessReaper? shared;

        private readonly Lock gate = new();
        private readonly HashSet<PtyProcessState> processes = new();
        private readonly AutoResetEvent wakeEvent;
        private readonly Thread thread;
        private int pollIntervalMilliseconds = BasePollIntervalMilliseconds;
        private bool registered;

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
                    this.pollIntervalMilliseconds = BasePollIntervalMilliseconds;
                    this.registered = true;
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
            var snapshot = new List<PtyProcessState>();
            for (;;)
            {
                lock (this.gate)
                {
                    snapshot.AddRange(this.processes);
                }

                bool reapedAny = false;
                foreach (PtyProcessState process in snapshot)
                {
                    if (!process.TryReap(out int exitCode, out Exception? failure))
                    {
                        continue;
                    }

                    reapedAny = true;
                    lock (this.gate)
                    {
                        this.processes.Remove(process);
                    }

                    process.FinishReapingFromFallback(exitCode, failure);
                }

                bool hasProcesses = snapshot.Count != 0;
                snapshot.Clear();

                int waitMilliseconds;
                lock (this.gate)
                {
                    // A registration during this scan already reset the interval; growing now would undo it.
                    if (reapedAny || this.registered)
                    {
                        this.registered = false;
                        this.pollIntervalMilliseconds = BasePollIntervalMilliseconds;
                    }
                    else
                    {
                        this.pollIntervalMilliseconds = Math.Min(
                            this.pollIntervalMilliseconds * PollIntervalGrowthFactor,
                            MaxPollIntervalMilliseconds);
                    }

                    waitMilliseconds = this.pollIntervalMilliseconds;
                }

                this.wakeEvent.WaitOne(
                    hasProcesses ? waitMilliseconds : Timeout.Infinite);
            }
        }
    }
}
