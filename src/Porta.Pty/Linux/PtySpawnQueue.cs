// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Serializes forkpty calls and emergency reaping work on one dedicated process-wide worker.
    /// </summary>
    internal sealed class PtySpawnQueue
    {
        private static readonly Lock SharedGate = new();
        private static PtySpawnQueue? shared;

        private readonly ConcurrentQueue<Action> workItems = new();
        private readonly AutoResetEvent wakeEvent;
        private readonly Thread thread;

        private PtySpawnQueue()
        {
            this.wakeEvent = new AutoResetEvent(false);
            try
            {
                this.thread = new Thread(this.Run)
                {
                    IsBackground = true,
                    Name = "LinuxPty.NET process spawn worker",
                };
                this.thread.Start();
            }
            catch
            {
                this.wakeEvent.Dispose();
                throw;
            }
        }

        internal static PtySpawnQueue Shared
        {
            get
            {
                lock (SharedGate)
                {
                    return shared ??= new PtySpawnQueue();
                }
            }
        }

        internal Task<IPtyConnection> Enqueue(
            PtyOptions options,
            CancellationToken cancellationToken)
        {
            var request = new SpawnRequest(options, cancellationToken);
            this.Post(request.Execute);
            return request.Task;
        }

        internal void Post(Action workItem)
        {
            this.workItems.Enqueue(workItem);
            this.wakeEvent.Set();
        }

        private void Run()
        {
            for (;;)
            {
                while (this.workItems.TryDequeue(out Action? workItem))
                {
                    try
                    {
                        workItem();
                    }
                    catch
                    {
                        // The process-wide worker must outlive any single work item.
                    }
                }

                this.wakeEvent.WaitOne();
            }
        }

        private sealed class SpawnRequest
        {
            private readonly Lock gate = new();
            private readonly PtyOptions options;
            private readonly CancellationToken cancellationToken;
            private readonly TaskCompletionSource<IPtyConnection> completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            private CancellationTokenRegistration cancellationRegistration;
            private int state;

            internal SpawnRequest(PtyOptions options, CancellationToken cancellationToken)
            {
                this.options = options;
                this.cancellationToken = cancellationToken;
                this.cancellationRegistration = cancellationToken.Register(
                    static request => ((SpawnRequest)request!).CancelQueuedRequest(),
                    this);
            }

            internal Task<IPtyConnection> Task => this.completion.Task;

            internal void Execute()
            {
                bool skip;
                lock (this.gate)
                {
                    if (this.state != 0)
                    {
                        skip = true;
                    }
                    else if (this.cancellationToken.IsCancellationRequested)
                    {
                        this.state = 2;
                        this.completion.TrySetCanceled(this.cancellationToken);
                        skip = true;
                    }
                    else
                    {
                        this.state = 1;
                        skip = false;
                    }
                }

                if (skip)
                {
                    this.cancellationRegistration.Dispose();
                    return;
                }

                _ = this.ExecuteAsync();
            }

            private async Task ExecuteAsync()
            {
                try
                {
                    IPtyConnection connection = await PtyProvider.StartTerminalAsync(
                        this.options,
                        this.cancellationToken).ConfigureAwait(false);

                    bool canceled;
                    lock (this.gate)
                    {
                        canceled = this.cancellationToken.IsCancellationRequested;
                        this.state = 2;
                    }

                    if (canceled)
                    {
                        await connection.DisposeAsync().ConfigureAwait(false);
                        this.completion.TrySetCanceled(this.cancellationToken);
                    }
                    else
                    {
                        this.completion.TrySetResult(connection);
                    }
                }
                catch (Exception exception)
                {
                    lock (this.gate)
                    {
                        this.state = 2;
                    }

                    if (this.cancellationToken.IsCancellationRequested)
                    {
                        this.completion.TrySetCanceled(this.cancellationToken);
                    }
                    else
                    {
                        this.completion.TrySetException(exception);
                    }
                }
                finally
                {
                    this.cancellationRegistration.Dispose();
                }
            }

            private void CancelQueuedRequest()
            {
                lock (this.gate)
                {
                    if (this.state != 0)
                    {
                        return;
                    }

                    this.state = 2;
                    this.completion.TrySetCanceled(this.cancellationToken);
                }
            }
        }
    }
}
