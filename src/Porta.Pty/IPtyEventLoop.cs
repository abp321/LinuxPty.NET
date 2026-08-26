// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty
{
    using System;

    /// <summary>
    /// A host-owned event loop the library drives its descriptors through instead of its own
    /// reactor thread.
    /// </summary>
    /// <remarks>
    /// Readiness callbacks must be serialized and never run concurrently with each other. The loop
    /// must keep dispatching until every registration it handed out has been disposed, because
    /// disposal and child reaping complete through callbacks. Synchronous blocking connection
    /// operations (<see cref="IDisposable.Dispose"/>, <see cref="IPtyConnection.WaitForExit"/>, and
    /// synchronous stream reads and writes) must not run on the loop's dispatch thread: their
    /// completion may require callbacks only that thread can deliver.
    /// </remarks>
    public interface IPtyEventLoop
    {
        /// <summary>
        /// Registers a descriptor with the loop. Callable from any thread.
        /// </summary>
        /// <param name="fileDescriptor">The descriptor to watch. The loop never closes it.</param>
        /// <param name="interests">The initial readiness conditions.
        /// <see cref="PtyFdInterests.None"/> registers the descriptor disarmed.</param>
        /// <param name="onReady">Invoked with the observed readiness whenever the descriptor is
        /// ready, serialized against every other readiness callback of this loop.</param>
        /// <returns>The registration handle, used to re-arm and to unregister.</returns>
        IPtyFdRegistration Register(int fileDescriptor, PtyFdInterests interests, Action<PtyFdReadiness> onReady);
    }
}
