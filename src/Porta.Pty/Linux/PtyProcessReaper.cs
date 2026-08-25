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
        private static readonly Lazy<PtyProcessReaper> LazyShared = new(
            static () => new PtyProcessReaper(),
            LazyThreadSafetyMode.ExecutionAndPublication);

        private readonly object gate = new();
        private readonly HashSet<PtyProcessState> processes = new();
        private readonly AutoResetEvent wakeEvent = new(false);

        private PtyProcessReaper()
        {
            var thread = new Thread(this.Run)
            {
                IsBackground = true,
                Name = "LinuxPty.NET process reaper",
            };
            thread.Start();
        }

        internal static PtyProcessReaper Shared => LazyShared.Value;

        internal void Register(PtyProcessState process)
        {
            lock (this.gate)
            {
                this.processes.Add(process);
            }

            this.wakeEvent.Set();
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

                    process.ClosePidFileDescriptor();
                    process.CompleteReaping(exitCode, failure);
                }

                this.wakeEvent.WaitOne(
                    snapshot.Length == 0 ? Timeout.Infinite : PollIntervalMilliseconds);
            }
        }
    }
}
