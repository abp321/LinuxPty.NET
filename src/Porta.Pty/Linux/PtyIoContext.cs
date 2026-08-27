// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Threading.Tasks.Sources;
    using static Porta.Pty.Linux.NativeMethods;

    /// <summary>
    /// Shared reactor-owned I/O state for one PTY master descriptor.
    /// </summary>
    internal sealed class PtyIoContext
    {
        private const int EIO = 5;
        private const int EAGAIN = 11;
        private const int EWOULDBLOCK = EAGAIN;
        private const int EPIPE = 32;
        private const int MaxBytesPerDispatch = 64 * 1024;
        private const int MaxCallsPerDispatch = 16;

        // An enqueue command that waited at least this long behind other reactor work probably has
        // data already buffered (the reactor was busy), so one immediate probe read beats a full
        // epoll round trip; a fresh command means an idle reactor and an almost-certainly-empty
        // descriptor, where the probe is a wasted syscall.
        private const int StaleHandoffMicroseconds = 80;

        // A parked operation completes at this much so one busy descriptor cannot hold the reactor for a full 64 KB fill while a small ready read waits; the consumer's next read drains the rest inline.
        private const int ReactorFillQuantum = 4 * 1024;

        private const int MaxWriteSize = 16 * 1024;
        private const int InlineFree = 0;
        private const int InlineHeld = 1;
        private const int InlineStopped = 2;

        private static readonly long StaleHandoffTicks = Stopwatch.Frequency * StaleHandoffMicroseconds / 1_000_000;

        private readonly EpollReactor reactor;
        private readonly Lock stoppedReactorGate = new();
        private readonly Lock stopGate = new();
        private ReadOperation? readsHead;
        private ReadOperation? readsTail;
        private WriteOperation? writesHead;
        private WriteOperation? writesTail;
        private int accepting = 1;
        private int readOpsInFlight;
        private int writeOpsInFlight;
        private int readInlineOwner;
        private int writeInlineOwner;
        private int readCloseRequested;
        private int writeCloseRequested;
        private int stopRequested;
        private bool readClosedOnReactor;
        private bool writeClosedOnReactor;
        private bool stoppedOnReactor;
        private bool hangupSeen;
        private bool endOfFile;
        private Exception? terminalError;
        private Exception? readError;
        private Exception? writeError;
        private Task? stopTask;

        // Deliberately synchronous continuations: the waiter is library code that must resume on
        // the releasing thread so a synchronously blocked Dispose never waits on a ThreadPool slot.
        private TaskCompletionSource? readRetirement;
        private TaskCompletionSource? writeRetirement;

        private PtyIoContext(int fileDescriptor, EpollReactor reactor)
        {
            this.FileDescriptor = fileDescriptor;
            this.reactor = reactor;
        }

        internal int FileDescriptor { get; }

        internal ulong ActiveToken { get; set; }

        internal bool IsStoppedOnReactor => this.stoppedOnReactor;

        internal static async Task<(PtyIoContext Context, Exception? ProcessFailure)> CreateAsync(
            int fileDescriptor,
            PtyProcessState? process,
            EpollReactor reactor)
        {
            var context = new PtyIoContext(fileDescriptor, reactor);
            Exception? processFailure =
                await reactor.RegisterConnectionAsync(context, process).ConfigureAwait(false);
            return (context, processFailure);
        }

        internal ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled<int>(cancellationToken);
            }

            if (Volatile.Read(ref this.accepting) == 0)
            {
                return ValueTask.FromException<int>(this.CreateUnavailableException());
            }

            if (Volatile.Read(ref this.readCloseRequested) != 0)
            {
                return ValueTask.FromException<int>(new ObjectDisposedException("PtyStream"));
            }

            // The slot is claimed before the count so the reactor gate already excludes this
            // caller's syscall; a count above 1 means someone is queued, so the slot goes back
            // and this read takes its place at the tail.
            bool inlineClaimed =
                Interlocked.CompareExchange(ref this.readInlineOwner, InlineHeld, InlineFree) == InlineFree;
            if (Interlocked.Increment(ref this.readOpsInFlight) == 1 && inlineClaimed)
            {
                return this.ReadInline(buffer, cancellationToken);
            }

            if (inlineClaimed)
            {
                this.SignalReadInlineReleased();
                this.KickReadsOnReactor();
            }

            var operation = new ReadOperation(this, buffer, cancellationToken);
            try
            {
                operation.RegisterCancellation();
                this.reactor.PostCommand(this, ReactorCommandKind.EnqueueRead, operation);
            }
            catch (Exception exception)
            {
                operation.CompleteException(exception);
            }

            return new ValueTask<int>(operation, operation.Version);
        }

        internal ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled(cancellationToken);
            }

            if (Volatile.Read(ref this.accepting) == 0)
            {
                return ValueTask.FromException(this.CreateUnavailableException());
            }

            if (Volatile.Read(ref this.writeCloseRequested) != 0)
            {
                return ValueTask.FromException(new ObjectDisposedException("PtyStream"));
            }

            bool inlineClaimed =
                Interlocked.CompareExchange(ref this.writeInlineOwner, InlineHeld, InlineFree) == InlineFree;
            if (Interlocked.Increment(ref this.writeOpsInFlight) == 1 && inlineClaimed)
            {
                return this.WriteInline(buffer, cancellationToken);
            }

            if (inlineClaimed)
            {
                this.SignalWriteInlineReleased();
                this.KickWritesOnReactor();
            }

            var operation = new WriteOperation(this, buffer, cancellationToken);
            try
            {
                operation.RegisterCancellation();
                this.reactor.PostCommand(this, ReactorCommandKind.EnqueueWrite, operation);
            }
            catch (Exception exception)
            {
                operation.CompleteException(exception);
            }

            return new ValueTask(operation, operation.Version);
        }

        internal Task CloseSideAsync(bool readSide)
        {
            int wasAlreadyRequested = readSide
                ? Interlocked.Exchange(ref this.readCloseRequested, 1)
                : Interlocked.Exchange(ref this.writeCloseRequested, 1);
            if (wasAlreadyRequested != 0)
            {
                return Task.CompletedTask;
            }

            Task retire = readSide
                ? this.RetireReadInlineOwnerAsync()
                : this.RetireWriteInlineOwnerAsync();
            if (!retire.IsCompletedSuccessfully)
            {
                return this.CloseSideAfterRetirementAsync(retire, readSide);
            }

            try
            {
                return this.reactor.CloseSideAsync(this, readSide);
            }
            catch (Exception exception)
            {
                this.StopAfterReactorFailure(exception);
                return Task.CompletedTask;
            }
        }

        internal Task StopAsync()
        {
            Interlocked.Exchange(ref this.accepting, 0);
            lock (this.stopGate)
            {
                if (this.stopTask is not null)
                {
                    return this.stopTask;
                }

                Interlocked.Exchange(ref this.stopRequested, 1);
                Task retire = this.RetireInlineOwnersAsync();
                this.stopTask = retire.IsCompletedSuccessfully
                    ? this.reactor.StopAsync(this)
                    : this.StopAfterRetirementAsync(retire);
                return this.stopTask;
            }
        }

        internal void ProcessReadyOnReactor(uint events)
        {
            if (Volatile.Read(ref this.stopRequested) != 0)
            {
                return;
            }

            if ((events & ReactorHangup) != 0)
            {
                this.hangupSeen = true;
            }

            if (Volatile.Read(ref this.readCloseRequested) == 0)
            {
                this.ProcessReadsOnReactor(events);
            }

            if (Volatile.Read(ref this.writeCloseRequested) == 0)
            {
                this.ProcessWritesOnReactor(events);
            }

            this.UpdateInterestOnReactor();
        }

        internal void ExecuteCommandOnReactor(ReactorCommandKind kind, object? state)
        {
            switch (kind)
            {
                case ReactorCommandKind.EnqueueRead:
                    this.EnqueueReadOnReactor((ReadOperation)state!);
                    break;
                case ReactorCommandKind.EnqueueReadFallback:
                    this.EnqueueReadFallbackOnReactor((ReadOperation)state!);
                    break;
                case ReactorCommandKind.EnqueueWrite:
                    this.EnqueueWriteOnReactor((WriteOperation)state!);
                    break;
                case ReactorCommandKind.EnqueueWriteFallback:
                    this.EnqueueWriteFallbackOnReactor((WriteOperation)state!);
                    break;
                case ReactorCommandKind.CancelRead:
                    this.CancelReadOnReactor((ReadOperation)state!);
                    break;
                case ReactorCommandKind.CancelWrite:
                    this.CancelWriteOnReactor((WriteOperation)state!);
                    break;
                case ReactorCommandKind.KickReads:
                    this.UpdateInterestOnReactor();
                    break;
                case ReactorCommandKind.KickWrites:
                    // The write half keeps its probe: a parked write is cold, so the extra
                    // syscall costs nothing that the read path's arm-only kick saves.
                    this.ProcessWritesOnReactor(0);
                    this.UpdateInterestOnReactor();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        internal void CloseSideOnReactor(bool readSide)
        {
            if (readSide)
            {
                this.readClosedOnReactor = true;
                this.CompleteReadsWithException(
                    static () => new ObjectDisposedException("PtyStream"));
            }
            else
            {
                this.writeClosedOnReactor = true;
                this.CompleteWritesWithException(
                    static () => new ObjectDisposedException("PtyStream"));
            }

            this.UpdateInterestOnReactor();
        }

        internal void StopOnReactor()
        {
            this.stoppedOnReactor = true;
            this.readClosedOnReactor = true;
            this.writeClosedOnReactor = true;
            this.CompleteReadsWithException(
                static () => new ObjectDisposedException("PtyConnection"));
            this.CompleteWritesWithException(
                static () => new ObjectDisposedException("PtyConnection"));
        }

        internal void FailOnReactor(Exception exception)
        {
            this.terminalError = exception;
            Interlocked.Exchange(ref this.accepting, 0);
            this.stoppedOnReactor = true;
            this.CompleteReadsWithException(() => exception);
            this.CompleteWritesWithException(() => exception);
        }

        internal void FailAfterReactorStopped(Exception exception)
        {
            lock (this.stoppedReactorGate)
            {
                this.FailAfterReactorStoppedCore(exception);
            }
        }

        internal void StopAfterReactorFailure(Exception exception)
        {
            lock (this.stoppedReactorGate)
            {
                this.FailAfterReactorStoppedCore(
                    new IOException("The PTY epoll reactor stopped before disposal completed.", exception));
            }
        }

        private Exception CreateUnavailableException()
        {
            return Volatile.Read(ref this.terminalError)
                ?? new ObjectDisposedException("PtyConnection");
        }

        private void EnqueueReadOnReactor(ReadOperation operation)
        {
            if (operation.IsCompleted)
            {
                return;
            }

            if (operation.IsCancellationRequested)
            {
                operation.CompleteCanceled();
                return;
            }

            if (this.stoppedOnReactor)
            {
                operation.CompleteException(this.CreateUnavailableException());
                return;
            }

            if (Volatile.Read(ref this.stopRequested) != 0)
            {
                operation.CompleteException(new ObjectDisposedException("PtyConnection"));
                return;
            }

            if (Volatile.Read(ref this.readCloseRequested) != 0 || this.readClosedOnReactor)
            {
                operation.CompleteException(new ObjectDisposedException("PtyStream"));
                return;
            }

            if (this.readError is { } failure)
            {
                operation.CompleteException(failure);
                return;
            }

            if (this.endOfFile)
            {
                operation.CompleteResult(0);
                return;
            }

            // Correctness rests on the arm alone: the caller already saw EAGAIN, master readiness is level-triggered,
            // and sticky HUP or ERR is delivered on the arm regardless of the mask, so the first
            // readiness dispatch classifies EIO against hangupSeen.
            this.AddRead(operation);
            if (Stopwatch.GetTimestamp() - operation.PostedTimestamp >= StaleHandoffTicks)
            {
                this.ProcessReadsOnReactor(0);
            }

            this.UpdateInterestOnReactor();
        }

        private void EnqueueWriteOnReactor(WriteOperation operation)
        {
            if (operation.IsCompleted)
            {
                return;
            }

            if (operation.IsCancellationRequested)
            {
                operation.CompleteCanceled();
                return;
            }

            if (this.stoppedOnReactor)
            {
                operation.CompleteException(this.CreateUnavailableException());
                return;
            }

            if (Volatile.Read(ref this.stopRequested) != 0)
            {
                operation.CompleteException(new ObjectDisposedException("PtyConnection"));
                return;
            }

            if (Volatile.Read(ref this.writeCloseRequested) != 0 || this.writeClosedOnReactor)
            {
                operation.CompleteException(new ObjectDisposedException("PtyStream"));
                return;
            }

            if (this.writeError is { } failure)
            {
                operation.CompleteException(failure);
                return;
            }

            bool wasEmpty = this.writesHead is null;
            this.AddWrite(operation);
            if (wasEmpty)
            {
                this.ProcessWritesOnReactor(0);
            }

            this.UpdateInterestOnReactor();
        }

        private void RequestCancellation(ReadOperation operation)
        {
            try
            {
                this.reactor.PostCommand(this, ReactorCommandKind.CancelRead, operation);
            }
            catch
            {
                // A stopped reactor fails the context and any abandoned add command.
            }
        }

        private void RequestCancellation(WriteOperation operation)
        {
            try
            {
                this.reactor.PostCommand(this, ReactorCommandKind.CancelWrite, operation);
            }
            catch
            {
                // A stopped reactor fails the context and any abandoned add command.
            }
        }

        private void CancelReadOnReactor(ReadOperation operation)
        {
            this.RemoveRead(operation);
            operation.CompleteCanceled();
            this.UpdateInterestOnReactor();
        }

        private void CancelWriteOnReactor(WriteOperation operation)
        {
            this.RemoveWrite(operation);
            operation.CompleteCanceled();
            this.UpdateInterestOnReactor();
        }

        private void SignalReadInlineReleased()
        {
            // A CAS, never an Exchange: a release must never overwrite InlineStopped.
            Interlocked.CompareExchange(ref this.readInlineOwner, InlineFree, InlineHeld);
            Volatile.Read(ref this.readRetirement)?.TrySetResult();
        }

        private void SignalWriteInlineReleased()
        {
            Interlocked.CompareExchange(ref this.writeInlineOwner, InlineFree, InlineHeld);
            Volatile.Read(ref this.writeRetirement)?.TrySetResult();
        }

        // Clear-and-loop: a late claimant that passed the entry guards before the close or stop
        // flags were written can re-claim between a release and our CAS. It is bounded to at most
        // one nonblocking syscall (the flags are already set, and the fd stays open until
        // StopAsync's task completes, so the straggler syscall is fd-safe) and its release signals
        // a fresh iteration.
        private async Task RetireReadInlineOwnerAsync()
        {
            while (true)
            {
                int prior = Interlocked.CompareExchange(ref this.readInlineOwner, InlineStopped, InlineFree);
                if (prior == InlineFree || prior == InlineStopped)
                {
                    return;
                }

                var tcs = new TaskCompletionSource();
                TaskCompletionSource? existing =
                    Interlocked.CompareExchange(ref this.readRetirement, tcs, null);
                TaskCompletionSource waiter = existing ?? tcs;
                prior = Interlocked.CompareExchange(ref this.readInlineOwner, InlineStopped, InlineFree);
                if (prior == InlineFree || prior == InlineStopped)
                {
                    Interlocked.CompareExchange(ref this.readRetirement, null, waiter);
                    return;
                }

                await waiter.Task.ConfigureAwait(false);
                Interlocked.CompareExchange(ref this.readRetirement, null, waiter);
            }
        }

        private async Task RetireWriteInlineOwnerAsync()
        {
            while (true)
            {
                int prior = Interlocked.CompareExchange(ref this.writeInlineOwner, InlineStopped, InlineFree);
                if (prior == InlineFree || prior == InlineStopped)
                {
                    return;
                }

                var tcs = new TaskCompletionSource();
                TaskCompletionSource? existing =
                    Interlocked.CompareExchange(ref this.writeRetirement, tcs, null);
                TaskCompletionSource waiter = existing ?? tcs;
                prior = Interlocked.CompareExchange(ref this.writeInlineOwner, InlineStopped, InlineFree);
                if (prior == InlineFree || prior == InlineStopped)
                {
                    Interlocked.CompareExchange(ref this.writeRetirement, null, waiter);
                    return;
                }

                await waiter.Task.ConfigureAwait(false);
                Interlocked.CompareExchange(ref this.writeRetirement, null, waiter);
            }
        }

        private async Task RetireInlineOwnersAsync()
        {
            await this.RetireReadInlineOwnerAsync().ConfigureAwait(false);
            await this.RetireWriteInlineOwnerAsync().ConfigureAwait(false);
        }

        private async Task StopAfterRetirementAsync(Task retire)
        {
            await retire.ConfigureAwait(false);
            await this.reactor.StopAsync(this).ConfigureAwait(false);
        }

        private async Task CloseSideAfterRetirementAsync(Task retire, bool readSide)
        {
            try
            {
                await retire.ConfigureAwait(false);
                await this.reactor.CloseSideAsync(this, readSide).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                this.StopAfterReactorFailure(exception);
            }
        }

        private ValueTask<int> ReadInline(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref this.accepting) == 0)
            {
                this.ReleaseReadInline();
                return ValueTask.FromException<int>(this.CreateUnavailableException());
            }

            if (Volatile.Read(ref this.readCloseRequested) != 0)
            {
                this.ReleaseReadInline();
                return ValueTask.FromException<int>(new ObjectDisposedException("PtyStream"));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                this.ReleaseReadInline();
                return ValueTask.FromCanceled<int>(cancellationToken);
            }

            int error;
            int transferred;
            try
            {
                error = this.Read(buffer, buffer.Length, out transferred);
            }
            catch (Exception exception)
            {
                this.ReleaseReadInline();
                return ValueTask.FromException<int>(exception);
            }

            if (error == 0)
            {
                // A zero transfer is EOF for this caller only; endOfFile is reactor-owned.
                return this.CompleteReadInline(transferred);
            }

            var operation = new ReadOperation(this, buffer, cancellationToken);
            try
            {
                operation.RegisterCancellation();
                this.reactor.PostCommand(this, ReactorCommandKind.EnqueueReadFallback, operation);
            }
            catch (Exception exception)
            {
                this.SignalReadInlineReleased();
                operation.CompleteException(exception);
                this.KickReadsOnReactor();
            }

            return new ValueTask<int>(operation, operation.Version);
        }

        private ValueTask WriteInline(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref this.accepting) == 0)
            {
                this.ReleaseWriteInline();
                return ValueTask.FromException(this.CreateUnavailableException());
            }

            if (Volatile.Read(ref this.writeCloseRequested) != 0)
            {
                this.ReleaseWriteInline();
                return ValueTask.FromException(new ObjectDisposedException("PtyStream"));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                this.ReleaseWriteInline();
                return ValueTask.FromCanceled(cancellationToken);
            }

            int error;
            int transferred;
            try
            {
                error = this.Write(buffer, Math.Min(buffer.Length, MaxWriteSize), out transferred);
            }
            catch (Exception exception)
            {
                this.ReleaseWriteInline();
                return ValueTask.FromException(exception);
            }

            if (error == 0 && transferred == buffer.Length)
            {
                return this.CompleteWriteInline();
            }

            // The reactor write loop owns error classification and its hangup knowledge.
            int offset = error == 0 && transferred > 0 ? transferred : 0;
            var operation = new WriteOperation(this, buffer, cancellationToken) { Offset = offset };
            try
            {
                operation.RegisterCancellation();
                this.reactor.PostCommand(this, ReactorCommandKind.EnqueueWriteFallback, operation);
            }
            catch (Exception exception)
            {
                this.SignalWriteInlineReleased();
                operation.CompleteException(exception);
                this.KickWritesOnReactor();
            }

            return new ValueTask(operation, operation.Version);
        }

        private ValueTask<int> CompleteReadInline(int result)
        {
            Interlocked.Decrement(ref this.readOpsInFlight);
            this.SignalReadInlineReleased();
            this.KickReadsOnReactor();
            return new ValueTask<int>(result);
        }

        private ValueTask CompleteWriteInline()
        {
            Interlocked.Decrement(ref this.writeOpsInFlight);
            this.SignalWriteInlineReleased();
            this.KickWritesOnReactor();
            return ValueTask.CompletedTask;
        }

        private void ReleaseReadInline()
        {
            this.SignalReadInlineReleased();
            Interlocked.Decrement(ref this.readOpsInFlight);
            this.KickReadsOnReactor();
        }

        private void ReleaseWriteInline()
        {
            this.SignalWriteInlineReleased();
            Interlocked.Decrement(ref this.writeOpsInFlight);
            this.KickWritesOnReactor();
        }

        private void KickReadsOnReactor()
        {
            // Decrement before release before this check: a racing caller either claims the
            // slot afterwards or is already counted here, so no parked operation is stranded.
            if (Volatile.Read(ref this.readOpsInFlight) == 0)
            {
                return;
            }

            try
            {
                this.reactor.PostCommand(this, ReactorCommandKind.KickReads, null);
            }
            catch
            {
                // A stopped reactor already fails parked operations.
            }
        }

        private void KickWritesOnReactor()
        {
            if (Volatile.Read(ref this.writeOpsInFlight) == 0)
            {
                return;
            }

            try
            {
                this.reactor.PostCommand(this, ReactorCommandKind.KickWrites, null);
            }
            catch
            {
                // A stopped reactor already fails parked operations.
            }
        }

        private void EnqueueReadFallbackOnReactor(ReadOperation operation)
        {
            this.SignalReadInlineReleased();
            if (!operation.IsCompleted)
            {
                if (operation.IsCancellationRequested)
                {
                    operation.CompleteCanceled();
                }
                else if (this.stoppedOnReactor)
                {
                    operation.CompleteException(this.CreateUnavailableException());
                }
                else if (Volatile.Read(ref this.stopRequested) != 0)
                {
                    operation.CompleteException(new ObjectDisposedException("PtyConnection"));
                }
                else if (Volatile.Read(ref this.readCloseRequested) != 0 || this.readClosedOnReactor)
                {
                    operation.CompleteException(new ObjectDisposedException("PtyStream"));
                }
                else if (this.readError is { } failure)
                {
                    operation.CompleteException(failure);
                }
                else if (this.endOfFile)
                {
                    operation.CompleteResult(0);
                }
                else
                {
                    // Stagers appended behind while the inline slot was held, but this
                    // operation was issued first.
                    this.AddReadFirst(operation);
                }
            }

            // The arm alone suffices for the same reason as EnqueueReadOnReactor: the inline read
            // already consumed the readiness it saw, and re-arming redelivers whatever remains.
            if (Stopwatch.GetTimestamp() - operation.PostedTimestamp >= StaleHandoffTicks)
            {
                this.ProcessReadsOnReactor(0);
            }

            this.UpdateInterestOnReactor();
        }

        private void EnqueueWriteFallbackOnReactor(WriteOperation operation)
        {
            this.SignalWriteInlineReleased();
            if (!operation.IsCompleted)
            {
                if (operation.IsCancellationRequested)
                {
                    operation.CompleteCanceled();
                }
                else if (this.stoppedOnReactor)
                {
                    operation.CompleteException(this.CreateUnavailableException());
                }
                else if (Volatile.Read(ref this.stopRequested) != 0)
                {
                    operation.CompleteException(new ObjectDisposedException("PtyConnection"));
                }
                else if (Volatile.Read(ref this.writeCloseRequested) != 0 || this.writeClosedOnReactor)
                {
                    operation.CompleteException(new ObjectDisposedException("PtyStream"));
                }
                else if (this.writeError is { } failure)
                {
                    operation.CompleteException(failure);
                }
                else
                {
                    this.AddWriteFirst(operation);
                }
            }

            this.ProcessWritesOnReactor(0);
            this.UpdateInterestOnReactor();
        }

        private void ProcessReadsOnReactor(uint events)
        {
            if (Volatile.Read(ref this.readInlineOwner) != InlineFree)
            {
                return;
            }

            int bytesProcessed = 0;
            int calls = 0;
            while (this.readsHead is { } operation
                && bytesProcessed < MaxBytesPerDispatch
                && calls < MaxCallsPerDispatch
                && Volatile.Read(ref this.stopRequested) == 0
                && Volatile.Read(ref this.readCloseRequested) == 0)
            {
                // Cancellation stops further consumption immediately; bytes already copied into
                // the caller's buffer are delivered as a short success rather than discarded.
                if (operation.IsCancellationRequested)
                {
                    this.RemoveRead(operation);
                    if (operation.Offset > 0)
                    {
                        operation.CompleteResult(operation.Offset);
                    }
                    else
                    {
                        operation.CompleteCanceled();
                    }

                    continue;
                }

                if (this.endOfFile)
                {
                    this.RemoveRead(operation);
                    operation.CompleteResult(operation.Offset);
                    continue;
                }

                int count = Math.Min(
                    operation.Buffer.Length - operation.Offset,
                    MaxBytesPerDispatch - bytesProcessed);
                int error;
                int transferred;
                try
                {
                    error = this.Read(operation.Buffer.Slice(operation.Offset), count, out transferred);
                }
                catch (Exception exception)
                {
                    this.RemoveRead(operation);
                    if (operation.Offset > 0)
                    {
                        // A persistent failure resurfaces on the next read at offset 0.
                        operation.CompleteResult(operation.Offset);
                    }
                    else
                    {
                        operation.CompleteException(exception);
                    }

                    continue;
                }

                calls++;
                if (error == 0)
                {
                    if (transferred == 0)
                    {
                        this.endOfFile = true;
                        this.RemoveRead(operation);
                        operation.CompleteResult(operation.Offset);
                        continue;
                    }

                    operation.Offset += transferred;
                    bytesProcessed += transferred;
                    if (operation.Offset == operation.Buffer.Length || operation.Offset >= ReactorFillQuantum)
                    {
                        this.RemoveRead(operation);
                        operation.CompleteResult(operation.Offset);
                    }

                    continue;
                }

                if (error == EAGAIN || error == EWOULDBLOCK)
                {
                    if (this.hangupSeen)
                    {
                        this.endOfFile = true;
                        this.RemoveRead(operation);
                        operation.CompleteResult(operation.Offset);
                        continue;
                    }

                    if ((events & ReactorError) != 0)
                    {
                        this.CompleteHeadWithAccumulatedBytes();
                        this.readError = EpollReactor.CreateIOException(
                            "Reading from a PTY after epoll reported an error",
                            error);
                        this.CompleteReadsWithException(() => this.readError);
                    }

                    break;
                }

                if (error == EIO)
                {
                    if (this.hangupSeen)
                    {
                        this.endOfFile = true;
                        this.RemoveRead(operation);
                        operation.CompleteResult(operation.Offset);
                        continue;
                    }

                    if ((events & ReactorError) == 0)
                    {
                        // A PTY can report EIO before the sticky HUP is dispatched. Wait
                        // for epoll to distinguish normal slave closure from an I/O fault.
                        break;
                    }
                }

                this.CompleteHeadWithAccumulatedBytes();
                this.readError = EpollReactor.CreateIOException("Reading from the PTY", error);
                this.CompleteReadsWithException(() => this.readError);
                break;
            }

            // No operation holding accumulated bytes may stay queued once a dispatch returns.
            this.CompleteHeadWithAccumulatedBytes();

            if (this.endOfFile)
            {
                this.CompleteReadsWithResult(0);
            }
        }

        private void ProcessWritesOnReactor(uint events)
        {
            if (Volatile.Read(ref this.writeInlineOwner) != InlineFree)
            {
                return;
            }

            int bytesProcessed = 0;
            int calls = 0;
            while (this.writesHead is { } operation
                && bytesProcessed < MaxBytesPerDispatch
                && calls < MaxCallsPerDispatch
                && Volatile.Read(ref this.stopRequested) == 0
                && Volatile.Read(ref this.writeCloseRequested) == 0)
            {
                if (operation.IsCancellationRequested)
                {
                    this.RemoveWrite(operation);
                    operation.CompleteCanceled();
                    continue;
                }

                int remaining = operation.Buffer.Length - operation.Offset;
                int count = Math.Min(
                    remaining,
                    Math.Min(MaxWriteSize, MaxBytesPerDispatch - bytesProcessed));
                int error;
                int transferred;
                try
                {
                    error = this.Write(operation.Buffer.Slice(operation.Offset), count, out transferred);
                }
                catch (Exception exception)
                {
                    this.RemoveWrite(operation);
                    operation.CompleteException(exception);
                    continue;
                }

                calls++;
                if (error == 0 && transferred > 0)
                {
                    operation.Offset += transferred;
                    bytesProcessed += transferred;
                    if (operation.Offset == operation.Buffer.Length)
                    {
                        this.RemoveWrite(operation);
                        operation.CompleteResult();
                    }

                    continue;
                }

                if (error == EAGAIN || error == EWOULDBLOCK)
                {
                    if (!this.hangupSeen)
                    {
                        if ((events & ReactorError) != 0)
                        {
                            this.writeError = EpollReactor.CreateIOException(
                                "Writing to a PTY after epoll reported an error",
                                error);
                            this.CompleteWritesWithException(() => this.writeError);
                        }

                        break;
                    }

                    error = EIO;
                }

                if (error == 0)
                {
                    error = EIO;
                }

                string operationDescription = error == EPIPE || error == EIO
                    ? "Writing to the closed PTY slave"
                    : "Writing to the PTY";
                this.writeError = EpollReactor.CreateIOException(operationDescription, error);
                this.CompleteWritesWithException(() => this.writeError);
                break;
            }
        }

        private void UpdateInterestOnReactor()
        {
            uint interest = 0;
            if (Volatile.Read(ref this.stopRequested) == 0
                && Volatile.Read(ref this.readCloseRequested) == 0
                && Volatile.Read(ref this.readInlineOwner) == InlineFree
                && this.readsHead is not null
                && this.readError == null
                && !this.endOfFile)
            {
                interest |= ReactorRead;
            }

            if (Volatile.Read(ref this.stopRequested) == 0
                && Volatile.Read(ref this.writeCloseRequested) == 0
                && Volatile.Read(ref this.writeInlineOwner) == InlineFree
                && this.writesHead is not null
                && this.writeError == null)
            {
                interest |= ReactorWrite;
            }

            this.reactor.UpdateInterest(this, interest);
        }

        private unsafe int Read(Memory<byte> buffer, int count, out int transferred)
        {
            // count is always at least 1, so the span is never empty and the pointer never null.
            fixed (byte* pointer = buffer.Span)
            {
                return pty_io_read(
                    this.FileDescriptor,
                    (IntPtr)pointer,
                    count,
                    out transferred);
            }
        }

        private unsafe int Write(ReadOnlyMemory<byte> buffer, int count, out int transferred)
        {
            fixed (byte* pointer = buffer.Span)
            {
                return pty_io_write(
                    this.FileDescriptor,
                    (IntPtr)pointer,
                    count,
                    out transferred);
            }
        }

        private void AddRead(ReadOperation operation)
        {
            operation.Previous = this.readsTail;
            if (this.readsTail is null)
            {
                this.readsHead = operation;
            }
            else
            {
                this.readsTail.Next = operation;
            }

            this.readsTail = operation;
            operation.IsQueued = true;
        }

        private void AddReadFirst(ReadOperation operation)
        {
            operation.Next = this.readsHead;
            if (this.readsHead is null)
            {
                this.readsTail = operation;
            }
            else
            {
                this.readsHead.Previous = operation;
            }

            this.readsHead = operation;
            operation.IsQueued = true;
        }

        private void AddWriteFirst(WriteOperation operation)
        {
            operation.Next = this.writesHead;
            if (this.writesHead is null)
            {
                this.writesTail = operation;
            }
            else
            {
                this.writesHead.Previous = operation;
            }

            this.writesHead = operation;
            operation.IsQueued = true;
        }

        private void AddWrite(WriteOperation operation)
        {
            operation.Previous = this.writesTail;
            if (this.writesTail is null)
            {
                this.writesHead = operation;
            }
            else
            {
                this.writesTail.Next = operation;
            }

            this.writesTail = operation;
            operation.IsQueued = true;
        }

        private void RemoveRead(ReadOperation operation)
        {
            if (!operation.IsQueued)
            {
                return;
            }

            if (operation.Previous is { } previous)
            {
                previous.Next = operation.Next;
            }
            else
            {
                this.readsHead = operation.Next;
            }

            if (operation.Next is { } next)
            {
                next.Previous = operation.Previous;
            }
            else
            {
                this.readsTail = operation.Previous;
            }

            operation.Previous = null;
            operation.Next = null;
            operation.IsQueued = false;
        }

        private void RemoveWrite(WriteOperation operation)
        {
            if (!operation.IsQueued)
            {
                return;
            }

            if (operation.Previous is { } previous)
            {
                previous.Next = operation.Next;
            }
            else
            {
                this.writesHead = operation.Next;
            }

            if (operation.Next is { } next)
            {
                next.Previous = operation.Previous;
            }
            else
            {
                this.writesTail = operation.Previous;
            }

            operation.Previous = null;
            operation.Next = null;
            operation.IsQueued = false;
        }

        private void CompleteHeadWithAccumulatedBytes()
        {
            if (this.readsHead is { Offset: > 0 } operation)
            {
                this.RemoveRead(operation);
                operation.CompleteResult(operation.Offset);
            }
        }

        private void CompleteReadsWithResult(int result)
        {
            while (this.readsHead is { } operation)
            {
                this.RemoveRead(operation);
                operation.CompleteResult(result);
            }
        }

        private void CompleteReadsWithException(Func<Exception?> exceptionFactory)
        {
            while (this.readsHead is { } operation)
            {
                this.RemoveRead(operation);
                operation.CompleteException(
                    exceptionFactory() ?? new IOException("Reading from the PTY failed."));
            }
        }

        private void CompleteWritesWithException(Func<Exception?> exceptionFactory)
        {
            while (this.writesHead is { } operation)
            {
                this.RemoveWrite(operation);
                operation.CompleteException(
                    exceptionFactory() ?? new IOException("Writing to the PTY failed."));
            }
        }

        private void FailAfterReactorStoppedCore(Exception exception)
        {
            this.terminalError ??= exception;
            Interlocked.Exchange(ref this.accepting, 0);
            this.stoppedOnReactor = true;
            this.readClosedOnReactor = true;
            this.writeClosedOnReactor = true;
            this.ActiveToken = 0;
            this.CompleteReadsWithException(() => this.terminalError);
            this.CompleteWritesWithException(() => this.terminalError);
        }

        private abstract class IoOperation
        {
            private const int RegistrationAssigned = 1;
            private const int RegistrationTaken = 2;

            private ManualResetValueTaskSourceCore<int> core;
            private CancellationTokenRegistration cancellationRegistration;
            private int registrationState;
            private int completed;
            private int cancellationRequested;

            protected IoOperation(PtyIoContext context, CancellationToken cancellationToken)
            {
                this.Context = context;
                this.CancellationToken = cancellationToken;
                this.core.RunContinuationsAsynchronously = true;
            }

            internal short Version => this.core.Version;

            internal bool IsCancellationRequested => Volatile.Read(ref this.cancellationRequested) != 0;

            internal bool IsCompleted => Volatile.Read(ref this.completed) != 0;

            protected PtyIoContext Context { get; }

            protected CancellationToken CancellationToken { get; }

            protected ref ManualResetValueTaskSourceCore<int> Core => ref this.core;

            internal void RegisterCancellation()
            {
                if (!this.CancellationToken.CanBeCanceled)
                {
                    return;
                }

                CancellationTokenRegistration registration = this.CancellationToken.Register(
                    static state => ((IoOperation)state!).CancellationCallback(),
                    this);

                this.cancellationRegistration = registration;
                Volatile.Write(ref this.registrationState, RegistrationAssigned);

                // A completion that ran before the registration was published could not claim it.
                if (Volatile.Read(ref this.completed) != 0 && this.TryTakeRegistration())
                {
                    registration.Dispose();
                }
            }

            protected bool TryBeginCompletion(out CancellationTokenRegistration registration)
            {
                if (Interlocked.CompareExchange(ref this.completed, 1, 0) != 0)
                {
                    registration = default;
                    return false;
                }

                this.OnCompletionClaimed();
                registration = this.TryTakeRegistration() ? this.cancellationRegistration : default;
                return true;
            }

            protected abstract void RequestCancellation();

            protected abstract void OnCompletionClaimed();

            private bool TryTakeRegistration()
            {
                return Interlocked.CompareExchange(
                    ref this.registrationState,
                    RegistrationTaken,
                    RegistrationAssigned) == RegistrationAssigned;
            }

            private void CancellationCallback()
            {
                Interlocked.Exchange(ref this.cancellationRequested, 1);
                this.RequestCancellation();
            }
        }

        private sealed class ReadOperation : IoOperation, IValueTaskSource<int>
        {
            internal ReadOperation(
                PtyIoContext context,
                Memory<byte> buffer,
                CancellationToken cancellationToken)
                : base(context, cancellationToken)
            {
                this.Buffer = buffer;
                this.PostedTimestamp = Stopwatch.GetTimestamp();
            }

            internal Memory<byte> Buffer { get; }

            internal long PostedTimestamp { get; }

            internal int Offset { get; set; }

            internal ReadOperation? Next { get; set; }

            internal ReadOperation? Previous { get; set; }

            internal bool IsQueued { get; set; }

            public int GetResult(short token)
            {
                return this.Core.GetResult(token);
            }

            public ValueTaskSourceStatus GetStatus(short token)
            {
                return this.Core.GetStatus(token);
            }

            public void OnCompleted(
                Action<object?> continuation,
                object? state,
                short token,
                ValueTaskSourceOnCompletedFlags flags)
            {
                this.Core.OnCompleted(continuation, state, token, flags);
            }

            internal void CompleteResult(int result)
            {
                if (this.TryBeginCompletion(out CancellationTokenRegistration registration))
                {
                    registration.Dispose();
                    this.Core.SetResult(result);
                }
            }

            internal void CompleteCanceled()
            {
                if (this.TryBeginCompletion(out CancellationTokenRegistration registration))
                {
                    registration.Dispose();
                    this.Core.SetException(new OperationCanceledException(this.CancellationToken));
                }
            }

            internal void CompleteException(Exception exception)
            {
                if (this.TryBeginCompletion(out CancellationTokenRegistration registration))
                {
                    registration.Dispose();
                    this.Core.SetException(exception);
                }
            }

            protected override void RequestCancellation()
            {
                this.Context.RequestCancellation(this);
            }

            protected override void OnCompletionClaimed()
            {
                Interlocked.Decrement(ref this.Context.readOpsInFlight);
            }
        }

        private sealed class WriteOperation : IoOperation, IValueTaskSource
        {
            internal WriteOperation(
                PtyIoContext context,
                ReadOnlyMemory<byte> buffer,
                CancellationToken cancellationToken)
                : base(context, cancellationToken)
            {
                this.Buffer = buffer;
            }

            internal ReadOnlyMemory<byte> Buffer { get; }

            internal int Offset { get; set; }

            internal WriteOperation? Next { get; set; }

            internal WriteOperation? Previous { get; set; }

            internal bool IsQueued { get; set; }

            public void GetResult(short token)
            {
                this.Core.GetResult(token);
            }

            public ValueTaskSourceStatus GetStatus(short token)
            {
                return this.Core.GetStatus(token);
            }

            public void OnCompleted(
                Action<object?> continuation,
                object? state,
                short token,
                ValueTaskSourceOnCompletedFlags flags)
            {
                this.Core.OnCompleted(continuation, state, token, flags);
            }

            internal void CompleteResult()
            {
                if (this.TryBeginCompletion(out CancellationTokenRegistration registration))
                {
                    registration.Dispose();
                    this.Core.SetResult(0);
                }
            }

            internal void CompleteCanceled()
            {
                if (this.TryBeginCompletion(out CancellationTokenRegistration registration))
                {
                    registration.Dispose();
                    this.Core.SetException(new OperationCanceledException(this.CancellationToken));
                }
            }

            internal void CompleteException(Exception exception)
            {
                if (this.TryBeginCompletion(out CancellationTokenRegistration registration))
                {
                    registration.Dispose();
                    this.Core.SetException(exception);
                }
            }

            protected override void RequestCancellation()
            {
                this.Context.RequestCancellation(this);
            }

            protected override void OnCompletionClaimed()
            {
                Interlocked.Decrement(ref this.Context.writeOpsInFlight);
            }
        }
    }
}
