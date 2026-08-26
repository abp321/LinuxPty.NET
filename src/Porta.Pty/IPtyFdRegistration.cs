// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty
{
    using System;

    /// <summary>
    /// A descriptor registered with an external event loop on the library's behalf.
    /// </summary>
    /// <remarks>
    /// Registrations are one-shot: after a readiness delivery the registration is disarmed and no
    /// further readiness may be delivered until <see cref="UpdateInterests"/> is called again.
    /// <see cref="IDisposable.Dispose"/> unregisters the descriptor, and no readiness callback may
    /// run after it returns. The library calls <see cref="UpdateInterests"/> and
    /// <see cref="IDisposable.Dispose"/> only from within the loop's serialized callback context or
    /// on a registration that has never been armed, so implementations need no additional
    /// cross-thread fencing.
    /// </remarks>
    public interface IPtyFdRegistration : IDisposable
    {
        /// <summary>
        /// Arms the registration for the given readiness conditions, replacing any previous request.
        /// </summary>
        /// <param name="interests">The readiness conditions to watch for.
        /// <see cref="PtyFdInterests.None"/> leaves the registration disarmed.</param>
        void UpdateInterests(PtyFdInterests interests);
    }
}
