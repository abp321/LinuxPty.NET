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
    /// <see cref="IDisposable.Dispose"/> unregisters the descriptor: no new readiness callback may
    /// begin after it returns. The library may dispose a registration from inside a readiness
    /// callback, including the registration's own, so Dispose must not wait for the current
    /// callback to return; serialized callbacks guarantee no other callback is in flight. The
    /// library calls <see cref="UpdateInterests"/> and <see cref="IDisposable.Dispose"/> only from
    /// within the loop's serialized callback context or on a registration that has never been
    /// armed, so implementations need no additional cross-thread fencing. Callbacks are also
    /// strictly non-reentrant: neither <see cref="IPtyEventLoop.Register"/> nor
    /// <see cref="UpdateInterests"/> may invoke a readiness callback inline before returning,
    /// because the library publishes registration state only after those calls return. Arming a
    /// registration whose descriptor is already ready must therefore deliver that readiness
    /// promptly as its own serialized dispatch; the library re-arms instead of re-probing and
    /// depends on no readiness being lost.
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
