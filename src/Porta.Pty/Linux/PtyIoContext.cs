// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
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
        private readonly LinkedList<ReadOperation> reads = new();
        private readonly LinkedList<WriteOperation> writes = new();
        private readonly object stoppedReactorGate = new();
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

        private PtyIoContext(int fileDescriptor, EpollReactor reactor)
        {
            this.FileDescriptor = fileDescriptor;
            this.reactor = reactor;
        }

        internal int FileDescriptor { get; }

        internal ulong ActiveToken { get; set; }

        internal uint ActiveInterest { get; set; }

        internal bool IsStoppedOnReactor => this.stoppedOnReactor;

        internal static PtyIoContext Create(int fileDescriptor)
        {
            EpollReactor reactor = EpollReactor.Shared;
            var context = new PtyIoContext(fileDescriptor, reactor);
            reactor.Register(context);
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

            return new ValueTask<int>(operation.Task);
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

            return new ValueTask(operation.Task);
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

        internal void Stop()
        {
            Interlocked.Exchange(ref this.accepting, 0);
            if (Interlocked.Exchange(ref this.stopRequested, 1) != 0)
            {
                return;
            }

            this.reactor.Stop(this);
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

            bool wasEmpty = this.reads.Count == 0;
            operation.Node = this.reads.AddLast(operation);
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

            bool wasEmpty = this.writes.Count == 0;
            operation.Node = this.writes.AddLast(operation);
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
                    if (operation.Node is { } node)
                    {
                        this.reads.Remove(node);
                        operation.Node = null;
                    }

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
                    if (operation.Node is { } node)
                    {
                        this.writes.Remove(node);
                        operation.Node = null;
                    }

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
            while (this.reads.First is { } node
                && bytesProcessed < MaxBytesPerDispatch
                && calls < MaxCallsPerDispatch
                && Volatile.Read(ref this.stopRequested) == 0
                && Volatile.Read(ref this.readCloseRequested) == 0)
            {
                ReadOperation operation = node.Value;
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
            while (this.writes.First is { } node
                && bytesProcessed < MaxBytesPerDispatch
                && calls < MaxCallsPerDispatch
                && Volatile.Read(ref this.stopRequested) == 0
                && Volatile.Read(ref this.writeCloseRequested) == 0)
            {
                WriteOperation operation = node.Value;
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
                && this.reads.Count != 0
                && this.readError == null
                && !this.endOfFile)
            {
                interest |= ReactorRead;
            }

            if (Volatile.Read(ref this.stopRequested) == 0
                && Volatile.Read(ref this.writeCloseRequested) == 0
                && this.writes.Count != 0
                && this.writeError == null)
            {
                interest |= ReactorWrite;
            }

            this.reactor.UpdateInterest(this, interest);
        }

        private unsafe int Read(Memory<byte> buffer, int count, out int transferred)
        {
            using MemoryHandle handle = buffer.Pin();
            return pty_io_read(
                this.FileDescriptor,
                (IntPtr)handle.Pointer,
                count,
                out transferred);
        }

        private unsafe int Write(ReadOnlyMemory<byte> buffer, int count, out int transferred)
        {
            using MemoryHandle handle = buffer.Pin();
            return pty_io_write(
                this.FileDescriptor,
                (IntPtr)handle.Pointer,
                count,
                out transferred);
        }

        private void RemoveRead(ReadOperation operation)
        {
            if (operation.Node is { } node)
            {
                this.reads.Remove(node);
                operation.Node = null;
            }
        }

        private void RemoveWrite(WriteOperation operation)
        {
            if (operation.Node is { } node)
            {
                this.writes.Remove(node);
                operation.Node = null;
            }
        }

        private void CompleteReadsWithResult(int result)
        {
            while (this.reads.First is { } node)
            {
                ReadOperation operation = node.Value;
                this.reads.RemoveFirst();
                operation.Node = null;
                operation.CompleteResult(result);
            }
        }

        private void CompleteReadsWithException(Func<Exception?> exceptionFactory)
        {
            while (this.reads.First is { } node)
            {
                ReadOperation operation = node.Value;
                this.reads.RemoveFirst();
                operation.Node = null;
                operation.CompleteException(
                    exceptionFactory() ?? new IOException("Reading from the PTY failed."));
            }
        }

        private void CompleteWritesWithException(Func<Exception?> exceptionFactory)
        {
            while (this.writes.First is { } node)
            {
                WriteOperation operation = node.Value;
                this.writes.RemoveFirst();
                operation.Node = null;
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
            private readonly object completionGate = new();
            private CancellationTokenRegistration cancellationRegistration;
            private bool cancellationRegistrationAssigned;
            private bool completed;
            private int cancellationRequested;

            protected IoOperation(PtyIoContext context, CancellationToken cancellationToken)
            {
                this.Context = context;
                this.CancellationToken = cancellationToken;
            }

            protected PtyIoContext Context { get; }

            protected CancellationToken CancellationToken { get; }

            internal bool IsCancellationRequested => Volatile.Read(ref this.cancellationRequested) != 0;

            internal bool IsCompleted
            {
                get
                {
                    lock (this.completionGate)
                    {
                        return this.completed;
                    }
                }
            }

            internal void RegisterCancellation()
            {
                if (!this.CancellationToken.CanBeCanceled)
                {
                    return;
                }

                CancellationTokenRegistration registration = this.CancellationToken.Register(
                    static state => ((IoOperation)state!).CancellationCallback(),
                    this);

                bool disposeRegistration;
                lock (this.completionGate)
                {
                    this.cancellationRegistration = registration;
                    this.cancellationRegistrationAssigned = true;
                    disposeRegistration = this.completed;
                }

                if (disposeRegistration)
                {
                    registration.Dispose();
                }
            }

            protected bool TryBeginCompletion(out CancellationTokenRegistration registration)
            {
                lock (this.completionGate)
                {
                    if (this.completed)
                    {
                        registration = default;
                        return false;
                    }

                    this.completed = true;
                    registration = this.cancellationRegistrationAssigned
                        ? this.cancellationRegistration
                        : default;
                    return true;
                }
            }

            private void CancellationCallback()
            {
                Interlocked.Exchange(ref this.cancellationRequested, 1);
                this.RequestCancellation();
            }

            protected abstract void RequestCancellation();
        }

        private sealed class ReadOperation : IoOperation
        {
            private readonly TaskCompletionSource<int> completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            internal ReadOperation(
                PtyIoContext context,
                Memory<byte> buffer,
                CancellationToken cancellationToken)
                : base(context, cancellationToken)
            {
                this.Buffer = buffer;
            }

            internal Memory<byte> Buffer { get; }

            internal LinkedListNode<ReadOperation>? Node { get; set; }

            internal Task<int> Task => this.completion.Task;

            internal void CompleteResult(int result)
            {
                if (this.TryBeginCompletion(out CancellationTokenRegistration registration))
                {
                    registration.Dispose();
                    this.completion.TrySetResult(result);
                }
            }

            internal void CompleteCanceled()
            {
                if (this.TryBeginCompletion(out CancellationTokenRegistration registration))
                {
                    registration.Dispose();
                    this.completion.TrySetCanceled(this.CancellationToken);
                }
            }

            internal void CompleteException(Exception exception)
            {
                if (this.TryBeginCompletion(out CancellationTokenRegistration registration))
                {
                    registration.Dispose();
                    this.completion.TrySetException(exception);
                }
            }

            protected override void RequestCancellation()
            {
                this.Context.RequestCancellation(this);
            }
        }

        private sealed class WriteOperation : IoOperation
        {
            private readonly TaskCompletionSource<object?> completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

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

            internal LinkedListNode<WriteOperation>? Node { get; set; }

            internal Task Task => this.completion.Task;

            internal void CompleteResult()
            {
                if (this.TryBeginCompletion(out CancellationTokenRegistration registration))
                {
                    registration.Dispose();
                    this.completion.TrySetResult(null);
                }
            }

            internal void CompleteCanceled()
            {
                if (this.TryBeginCompletion(out CancellationTokenRegistration registration))
                {
                    registration.Dispose();
                    this.completion.TrySetCanceled(this.CancellationToken);
                }
            }

            internal void CompleteException(Exception exception)
            {
                if (this.TryBeginCompletion(out CancellationTokenRegistration registration))
                {
                    registration.Dispose();
                    this.completion.TrySetException(exception);
                }
            }

            protected override void RequestCancellation()
            {
                this.Context.RequestCancellation(this);
            }
        }
    }
}
