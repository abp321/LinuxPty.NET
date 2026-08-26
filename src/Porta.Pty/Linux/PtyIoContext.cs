// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    using System;
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
        private const int MaxWriteSize = 16 * 1024;

        private readonly EpollReactor reactor;
        private readonly Lock stoppedReactorGate = new();
        private readonly Lock stopGate = new();
        private ReadOperation? readsHead;
        private ReadOperation? readsTail;
        private WriteOperation? writesHead;
        private WriteOperation? writesTail;
        private int accepting = 1;
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

        private PtyIoContext(int fileDescriptor, EpollReactor reactor)
        {
            this.FileDescriptor = fileDescriptor;
            this.reactor = reactor;
        }

        internal int FileDescriptor { get; }

        internal ulong ActiveToken { get; set; }

        internal uint ActiveInterest { get; set; }

        internal bool IsStoppedOnReactor => this.stoppedOnReactor;

        internal static async Task<PtyIoContext> CreateAsync(int fileDescriptor)
        {
            EpollReactor reactor = EpollReactor.Shared;
            var context = new PtyIoContext(fileDescriptor, reactor);
            await reactor.RegisterAsync(context).ConfigureAwait(false);
            return context;
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

            var operation = new ReadOperation(this, buffer, cancellationToken);
            operation.RegisterCancellation();
            try
            {
                this.reactor.PostContextCommand(this, () => this.EnqueueReadOnReactor(operation));
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

            var operation = new WriteOperation(this, buffer, cancellationToken);
            operation.RegisterCancellation();
            try
            {
                this.reactor.PostContextCommand(this, () => this.EnqueueWriteOnReactor(operation));
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
                this.stopTask = this.reactor.StopAsync(this);
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

            bool wasEmpty = this.readsHead is null;
            this.AddRead(operation);
            if (wasEmpty)
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
                this.reactor.PostContextCommand(this, () =>
                {
                    this.RemoveRead(operation);
                    operation.CompleteCanceled();
                    this.UpdateInterestOnReactor();
                });
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
                this.reactor.PostContextCommand(this, () =>
                {
                    this.RemoveWrite(operation);
                    operation.CompleteCanceled();
                    this.UpdateInterestOnReactor();
                });
            }
            catch
            {
                // A stopped reactor fails the context and any abandoned add command.
            }
        }

        private void ProcessReadsOnReactor(uint events)
        {
            int bytesProcessed = 0;
            int calls = 0;
            while (this.readsHead is { } operation
                && bytesProcessed < MaxBytesPerDispatch
                && calls < MaxCallsPerDispatch
                && Volatile.Read(ref this.stopRequested) == 0
                && Volatile.Read(ref this.readCloseRequested) == 0)
            {
                if (operation.IsCancellationRequested)
                {
                    this.RemoveRead(operation);
                    operation.CompleteCanceled();
                    continue;
                }

                if (this.endOfFile)
                {
                    this.RemoveRead(operation);
                    operation.CompleteResult(0);
                    continue;
                }

                int count = Math.Min(operation.Buffer.Length, MaxBytesPerDispatch - bytesProcessed);
                int error;
                int transferred;
                try
                {
                    error = this.Read(operation.Buffer, count, out transferred);
                }
                catch (Exception exception)
                {
                    this.RemoveRead(operation);
                    operation.CompleteException(exception);
                    continue;
                }

                calls++;
                if (error == 0)
                {
                    this.RemoveRead(operation);
                    if (transferred == 0)
                    {
                        this.endOfFile = true;
                        operation.CompleteResult(0);
                    }
                    else
                    {
                        bytesProcessed += transferred;
                        operation.CompleteResult(transferred);
                    }

                    continue;
                }

                if (error == EAGAIN || error == EWOULDBLOCK)
                {
                    if (this.hangupSeen)
                    {
                        this.endOfFile = true;
                        this.RemoveRead(operation);
                        operation.CompleteResult(0);
                        continue;
                    }

                    if ((events & ReactorError) != 0)
                    {
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
                        operation.CompleteResult(0);
                        continue;
                    }

                    if ((events & ReactorError) == 0)
                    {
                        // A PTY can report EIO before the sticky HUP is dispatched. Wait
                        // for epoll to distinguish normal slave closure from an I/O fault.
                        break;
                    }
                }

                this.readError = EpollReactor.CreateIOException("Reading from the PTY", error);
                this.CompleteReadsWithException(() => this.readError);
                break;
            }

            if (this.endOfFile)
            {
                this.CompleteReadsWithResult(0);
            }
        }

        private void ProcessWritesOnReactor(uint events)
        {
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
                && this.readsHead is not null
                && this.readError == null
                && !this.endOfFile)
            {
                interest |= ReactorRead;
            }

            if (Volatile.Read(ref this.stopRequested) == 0
                && Volatile.Read(ref this.writeCloseRequested) == 0
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
            this.ActiveInterest = 0;
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

                registration = this.TryTakeRegistration() ? this.cancellationRegistration : default;
                return true;
            }

            protected abstract void RequestCancellation();

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
            }

            internal Memory<byte> Buffer { get; }

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
        }
    }
}
