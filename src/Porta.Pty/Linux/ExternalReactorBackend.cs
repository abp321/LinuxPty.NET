// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    using System;
    using System.Collections.Generic;
    using static Porta.Pty.Linux.NativeMethods;

    /// <summary>
    /// Readiness backend that drives one reactor engine through a caller-supplied event loop
    /// instead of an owned epoll instance and reactor thread.
    /// </summary>
    internal sealed class ExternalReactorBackend : IReactorBackend
    {
        private readonly EpollReactor engine;
        private readonly IPtyEventLoop loop;
        private readonly Dictionary<ulong, IPtyFdRegistration> registrations = new();
        private readonly IPtyFdRegistration wakeRegistration;
        private IPtyFdRegistration? timerRegistration;
        private readonly int wakeFd;
        private int timerFd = -1;
        private bool closed;

        internal ExternalReactorBackend(EpollReactor engine, IPtyEventLoop loop)
        {
            this.engine = engine;
            this.loop = loop;

            int wakeError = pty_eventfd_create(out int wake);
            if (wakeError != 0)
            {
                throw EpollReactor.CreateIOException("Creating the PTY reactor wakeup", wakeError);
            }

            this.wakeFd = wake;

            IPtyFdRegistration? createdWake = null;
            try
            {
                // The registration starts unarmed: a never-armed one-shot registration has no
                // callback in flight, so the rollback below may dispose it from this thread.
                this.wakeRegistration = createdWake =
                    loop.Register(wake, PtyFdInterests.None, _ => this.OnWakeReady());
                createdWake.UpdateInterests(PtyFdInterests.Read);
            }
            catch
            {
                try
                {
                    // A throwing caller Dispose must not replace the setup exception rethrown below.
                    TryDispose(createdWake);
                }
                finally
                {
                    // The descriptor is this backend's alone, so a caller registration that
                    // throws from Dispose must not leak it.
                    _ = pty_close(wake);
                }

                throw;
            }
        }

        public int AddInterest(int fileDescriptor, ulong token, uint interests)
        {
            this.registrations.Add(
                token,
                this.loop.Register(
                    fileDescriptor,
                    MapInterests(interests),
                    readiness => this.OnReadiness(token, readiness)));
            return 0;
        }

        public int ModifyInterest(int fileDescriptor, ulong token, uint interests)
        {
            this.registrations[token].UpdateInterests(MapInterests(interests));
            return 0;
        }

        public int RemoveInterest(int fileDescriptor, ulong token)
        {
            if (this.registrations.Remove(token, out IPtyFdRegistration? registration))
            {
                registration.Dispose();
            }

            return 0;
        }

        public int ArmTimer(int milliseconds)
        {
            if (this.timerFd < 0)
            {
                // The timer serves only fallback reaping, so a pidfd-capable connection never
                // creates it. A throwing caller Register must not leak the descriptor, and the
                // field is assigned only once registration succeeded so a failed attempt retries.
                int createError = pty_timerfd_create(out int timer);
                if (createError != 0)
                {
                    return createError;
                }

                try
                {
                    this.timerRegistration =
                        this.loop.Register(timer, PtyFdInterests.None, _ => this.OnTimerReady());
                }
                catch
                {
                    _ = pty_close(timer);
                    throw;
                }

                this.timerFd = timer;
            }

            int error = pty_reactor_set_timer(this.timerFd, milliseconds);
            if (error != 0)
            {
                return error;
            }

            // A one-shot delivery disarms the timer registration, so every arm must re-arm it.
            this.timerRegistration!.UpdateInterests(PtyFdInterests.Read);
            return 0;
        }

        public int Wake()
        {
            return pty_reactor_wake(this.wakeFd);
        }

        public int DrainWake()
        {
            return pty_reactor_drain(this.wakeFd);
        }

        public int DrainTimer()
        {
            return pty_reactor_drain(this.timerFd);
        }

        public unsafe int Wait(PtyReactorEvent* events, int capacity, out int count)
        {
            // External mode has no Run loop: the caller's event loop does the waiting.
            throw new InvalidOperationException("The external PTY reactor backend does not wait.");
        }

        public void Close()
        {
            if (this.closed)
            {
                return;
            }

            this.closed = true;
            try
            {
                foreach (IPtyFdRegistration registration in this.registrations.Values)
                {
                    TryDispose(registration);
                }

                this.registrations.Clear();
                TryDispose(this.timerRegistration);
                TryDispose(this.wakeRegistration);
            }
            finally
            {
                if (this.timerFd >= 0)
                {
                    _ = pty_close(this.timerFd);
                }

                _ = pty_close(this.wakeFd);
            }
        }

        private static void TryDispose(IPtyFdRegistration? registration)
        {
            try
            {
                registration?.Dispose();
            }
            catch
            {
                // Teardown must not be interrupted by a throwing caller Dispose; the native
                // descriptors are closed in the finally regardless.
            }
        }

        private static PtyFdInterests MapInterests(uint interests)
        {
            // ReactorOneShot maps to nothing: the registration contract is already one-shot.
            PtyFdInterests mapped = PtyFdInterests.None;
            if ((interests & ReactorRead) != 0)
            {
                mapped |= PtyFdInterests.Read;
            }

            if ((interests & ReactorWrite) != 0)
            {
                mapped |= PtyFdInterests.Write;
            }

            return mapped;
        }

        private static uint MapReadiness(PtyFdReadiness readiness)
        {
            uint mapped = 0;
            if ((readiness & PtyFdReadiness.Read) != 0)
            {
                mapped |= ReactorRead;
            }

            if ((readiness & PtyFdReadiness.Write) != 0)
            {
                mapped |= ReactorWrite;
            }

            if ((readiness & PtyFdReadiness.Error) != 0)
            {
                mapped |= ReactorError;
            }

            if ((readiness & PtyFdReadiness.Hangup) != 0)
            {
                mapped |= ReactorHangup;
            }

            return mapped;
        }

        private void OnWakeReady()
        {
            try
            {
                int error = pty_reactor_drain(this.wakeFd);
                if (error != 0)
                {
                    throw EpollReactor.CreateIOException("Draining the PTY reactor wakeup", error);
                }

                this.engine.ExternalWake();
                if (!this.engine.IsStopped)
                {
                    // An eventfd stays readable until drained, so a Wake posted during this
                    // callback is still delivered by this late re-arm: arming a one-shot
                    // registration on an already-readable descriptor fires immediately. A
                    // stopped engine has closed this backend, so re-arming would touch a
                    // disposed registration.
                    this.wakeRegistration.UpdateInterests(PtyFdInterests.Read);
                }
            }
            catch (Exception exception)
            {
                this.engine.ExternalFail(exception);
            }
        }

        private void OnTimerReady()
        {
            try
            {
                int error = pty_reactor_drain(this.timerFd);
                if (error != 0)
                {
                    throw EpollReactor.CreateIOException(
                        "Draining the PTY fallback poll timer",
                        error);
                }

                this.engine.ExternalTimer();
            }
            catch (Exception exception)
            {
                this.engine.ExternalFail(exception);
            }
        }

        private void OnReadiness(ulong token, PtyFdReadiness readiness)
        {
            try
            {
                this.engine.ExternalReadiness(token, MapReadiness(readiness));
            }
            catch (Exception exception)
            {
                this.engine.ExternalFail(exception);
            }
        }
    }
}
