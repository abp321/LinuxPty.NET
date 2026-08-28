// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A directional stream backed by the shared Linux PTY epoll reactor.
    /// </summary>
    internal sealed class PtyStream : Stream
    {
        private readonly PtyIoContext context;
        private readonly FileAccess access;
        private int disposed;

        internal PtyStream(PtyIoContext context, FileAccess access)
        {
            this.context = context;
            this.access = access;
        }

        public override bool CanRead => Volatile.Read(ref this.disposed) == 0
            && this.access == FileAccess.Read;

        public override bool CanSeek => false;

        public override bool CanWrite => Volatile.Read(ref this.disposed) == 0
            && this.access == FileAccess.Write;

        public override long Length
        {
            get
            {
                this.ThrowIfDisposed();
                throw new NotSupportedException("PTY streams do not support length.");
            }
        }

        public override long Position
        {
            get
            {
                this.ThrowIfDisposed();
                throw new NotSupportedException("PTY streams do not support positioning.");
            }

            set
            {
                this.ThrowIfDisposed();
                throw new NotSupportedException("PTY streams do not support positioning.");
            }
        }

        public override void Flush()
        {
            this.ThrowIfDisposed();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            this.ThrowIfDisposed();
            return cancellationToken.IsCancellationRequested
                ? Task.FromCanceled(cancellationToken)
                : Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            this.EnsureReadable();
            ArgumentNullException.ThrowIfNull(buffer);
            Memory<byte> memory = buffer.AsMemory(offset, count);
            if (memory.Length == 0)
            {
                return 0;
            }

            ValueTask<int> read = this.context.ReadAsync(memory, CancellationToken.None);

            // The backing IValueTaskSource has no blocking GetResult, so consuming the
            // ValueTask directly is valid only once completed; otherwise block through AsTask.
            return read.IsCompleted ? read.GetAwaiter().GetResult() : read.AsTask().GetAwaiter().GetResult();
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            this.EnsureReadable();
            ArgumentNullException.ThrowIfNull(buffer);
            Memory<byte> memory = buffer.AsMemory(offset, count);
            return memory.Length == 0
                ? Task.FromResult(0)
                : this.context.ReadAsync(memory, cancellationToken).AsTask();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            this.EnsureReadable();
            return buffer.Length == 0
                ? new ValueTask<int>(0)
                : this.context.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            this.ThrowIfDisposed();
            throw new NotSupportedException("PTY streams do not support seeking.");
        }

        public override void SetLength(long value)
        {
            this.ThrowIfDisposed();
            throw new NotSupportedException("PTY streams do not support length.");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            this.EnsureWritable();
            ArgumentNullException.ThrowIfNull(buffer);
            ReadOnlyMemory<byte> memory = buffer.AsMemory(offset, count);
            if (memory.Length == 0)
            {
                return;
            }

            ValueTask write = this.context.WriteAsync(memory, CancellationToken.None);

            // The backing IValueTaskSource has no blocking GetResult, so consuming the
            // ValueTask directly is valid only once completed; otherwise block through AsTask.
            if (write.IsCompleted)
            {
                write.GetAwaiter().GetResult();
            }
            else
            {
                write.AsTask().GetAwaiter().GetResult();
            }
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            this.EnsureWritable();
            ArgumentNullException.ThrowIfNull(buffer);
            ReadOnlyMemory<byte> memory = buffer.AsMemory(offset, count);
            return memory.Length == 0
                ? Task.CompletedTask
                : this.context.WriteAsync(memory, cancellationToken).AsTask();
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            this.EnsureWritable();
            return buffer.Length == 0
                ? ValueTask.CompletedTask
                : this.context.WriteAsync(buffer, cancellationToken);
        }

        public override ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            return new ValueTask(this.context.CloseSideAsync(this.access == FileAccess.Read));
        }

        internal void MarkDisposedByConnection()
        {
            Interlocked.Exchange(ref this.disposed, 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref this.disposed, 1) == 0)
            {
                this.context.CloseSideAsync(this.access == FileAccess.Read)
                    .GetAwaiter()
                    .GetResult();
            }

            base.Dispose(disposing);
        }

        private void EnsureReadable()
        {
            this.ThrowIfDisposed();
            if (this.access != FileAccess.Read)
            {
                throw new NotSupportedException("This PTY stream does not support reading.");
            }
        }

        private void EnsureWritable()
        {
            this.ThrowIfDisposed();
            if (this.access != FileAccess.Write)
            {
                throw new NotSupportedException("This PTY stream does not support writing.");
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);
        }
    }
}
