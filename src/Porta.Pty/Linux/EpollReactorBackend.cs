// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    using static Porta.Pty.Linux.NativeMethods;

    /// <summary>
    /// Readiness backend over a dedicated epoll instance with its own wake eventfd and poll timerfd.
    /// </summary>
    internal sealed class EpollReactorBackend : IReactorBackend
    {
        private readonly int epollFd;
        private readonly int wakeFd;
        private readonly int timerFd;

        internal EpollReactorBackend(ulong wakeToken, ulong timerToken)
        {
            int error = pty_reactor_create(
                wakeToken,
                timerToken,
                out this.epollFd,
                out this.wakeFd,
                out this.timerFd);
            if (error != 0)
            {
                throw EpollReactor.CreateIOException("Creating the PTY epoll reactor", error);
            }
        }

        public int AddInterest(int fileDescriptor, ulong token, uint interests)
        {
            return pty_reactor_control(this.epollFd, ReactorAdd, fileDescriptor, token, interests);
        }

        public int ModifyInterest(int fileDescriptor, ulong token, uint interests)
        {
            return pty_reactor_control(this.epollFd, ReactorModify, fileDescriptor, token, interests);
        }

        public int RemoveInterest(int fileDescriptor, ulong token)
        {
            return pty_reactor_control(this.epollFd, ReactorDelete, fileDescriptor, token, 0);
        }

        public int ArmTimer(int milliseconds)
        {
            return pty_reactor_set_timer(this.timerFd, milliseconds);
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
            return pty_reactor_wait(this.epollFd, events, capacity, out count);
        }

        public void Close()
        {
            _ = pty_close(this.timerFd);
            _ = pty_close(this.wakeFd);
            _ = pty_close(this.epollFd);
        }
    }
}
