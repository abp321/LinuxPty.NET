// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    using static Porta.Pty.Linux.NativeMethods;

    /// <summary>
    /// Readiness backend owning the descriptors the reactor engine drives. Every member returns an
    /// errno-style code like the native call it wraps.
    /// </summary>
    internal interface IReactorBackend
    {
        int AddInterest(int fileDescriptor, ulong token, uint interests);

        int ModifyInterest(int fileDescriptor, ulong token, uint interests);

        int RemoveInterest(int fileDescriptor, ulong token);

        int ArmTimer(int milliseconds);

        int Wake();

        int DrainWake();

        int DrainTimer();

        unsafe int Wait(PtyReactorEvent* events, int capacity, out int count);

        /// <summary>
        /// Releases the descriptors. The engine calls this exactly once, at teardown.
        /// </summary>
        void Close();
    }
}
