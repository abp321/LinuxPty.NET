// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Connection to a running pseudoterminal process.
    /// </summary>
    public interface IPtyConnection : IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// Event fired when the pty process exits.
        /// </summary>
        event EventHandler<PtyExitedEventArgs>? ProcessExited;

        /// <summary>
        /// Gets the stream for reading data from the pty.
        /// </summary>
        Stream ReaderStream { get; }

        /// <summary>
        /// Gets the stream for writing data to the pty.
        /// </summary>
        Stream WriterStream { get; }

        /// <summary>
        /// Gets the pty process ID.
        /// </summary>
        int Pid { get; }

        /// <summary>
        /// Gets the pty process exit code.
        /// </summary>
        int ExitCode { get; }

        /// <summary>
        /// Wait for the pty process to exit up to a given timeout.
        /// </summary>
        /// <param name="milliseconds">Timeout to wait for the process to exit.</param>
        /// <returns>True if the process exits within the timeout, false otherwise.</returns>
        bool WaitForExit(int milliseconds);

        /// <summary>
        /// Asynchronously waits for the pty process to exit and be reaped.
        /// </summary>
        /// <param name="cancellationToken">Cancels only this wait operation.</param>
        /// <returns>The pty process exit code.</returns>
        ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Immediately terminates the pty process.
        /// </summary>
        void Kill();

        /// <summary>
        /// Change the size of the pty.
        /// </summary>
        /// <param name="cols">The number of columns.</param>
        /// <param name="rows">The number of rows.</param>
        void Resize(int cols, int rows);
    }
}
