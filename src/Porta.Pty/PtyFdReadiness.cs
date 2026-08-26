// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty
{
    using System;

    /// <summary>
    /// Readiness conditions an external event loop reports back to the library for a descriptor.
    /// </summary>
    /// <remarks>
    /// <see cref="Error"/> and <see cref="Hangup"/> are deliverable regardless of the requested
    /// interests, matching epoll, which reports EPOLLERR and EPOLLHUP whatever the mask asked for.
    /// </remarks>
    [Flags]
    public enum PtyFdReadiness
    {
        /// <summary>
        /// No readiness condition is reported.
        /// </summary>
        None = 0,

        /// <summary>
        /// The descriptor is ready for reading.
        /// </summary>
        Read = 1,

        /// <summary>
        /// The descriptor is ready for writing.
        /// </summary>
        Write = 2,

        /// <summary>
        /// The descriptor reported an error condition.
        /// </summary>
        Error = 4,

        /// <summary>
        /// The descriptor's peer hung up.
        /// </summary>
        Hangup = 8,
    }
}
