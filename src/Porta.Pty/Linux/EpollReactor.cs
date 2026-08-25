// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using static Porta.Pty.Linux.NativeMethods;

    /// <summary>
    /// Process-wide readiness reactor for Linux PTY master descriptors.
    /// </summary>
    internal sealed class EpollReactor
    {
        private const ulong WakeToken = 0;
        private const int EventCapacity = 64;
        private const int MaxCommandsPerDrain = 16;
        private const int ENOENT = 2;

        private static readonly object SharedGate = new();
        private static EpollReactor? shared;

        private readonly int epollFd;
        private readonly int wakeFd;
        private readonly ConcurrentQueue<Action> commands = new();
        private readonly object commandGate = new();
        private readonly Dictionary<ulong, PtyIoContext> activeContexts = new();
        private readonly Dictionary<ulong, PtyProcessState> activeProcesses = new();
        private readonly HashSet<PtyIoContext> contexts = new();
        private readonly HashSet<PtyProcessState> processes = new();
        private readonly PtyReactorEvent[] events = new PtyReactorEvent[EventCapacity];
        private readonly Thread thread;
        private long nextToken;
        private Exception? fatalError;

        private EpollReactor()
        {
            int error = pty_reactor_create(WakeToken, out this.epollFd, out this.wakeFd);
            if (error != 0)
            {
                throw CreateIOException("Creating the PTY epoll reactor", error);
            }

            this.thread = new Thread(this.Run)
            {
                IsBackground = true,
                Name = "LinuxPty.NET epoll reactor",
            };

            try
            {
                this.thread.Start();
            }
            catch
            {
                pty_close(this.wakeFd);
                pty_close(this.epollFd);
                throw;
            }
        }

        internal static EpollReactor Shared
        {
            get
            {
                lock (SharedGate)
                {
                    return shared ??= new EpollReactor();
                }
            }
        }

        internal Task RegisterAsync(PtyIoContext context)
        {
            var completion = CreateCompletionSource();
            this.Post(() =>
            {
                try
                {
                    if (this.IsStopped)
                    {
                        throw new IOException("The PTY epoll reactor has stopped.", this.fatalError);
                    }

                    this.contexts.Add(context);
                    completion.TrySetResult(null);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });

            return completion.Task;
        }

        internal Task RegisterProcessAsync(PtyProcessState process)
        {
            var completion = CreateCompletionSource();
            this.Post(() =>
            {
                try
                {
                    if (this.IsStopped)
                    {
                        throw new IOException("The PTY epoll reactor has stopped.", this.fatalError);
                    }

                    ulong token = this.AllocateToken();
                    int error = pty_reactor_control(
                        this.epollFd,
                        ReactorAdd,
                        process.PidFileDescriptor,
                        token,
                        ReactorRead);
                    if (error != 0)
                    {
                        throw CreateIOException("Registering a pidfd with epoll", error);
                    }

                    process.ActiveToken = token;
                    this.activeProcesses.Add(token, process);
                    this.processes.Add(process);
                    completion.TrySetResult(null);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });

            return completion.Task;
        }

        internal void PostContextCommand(PtyIoContext context, Action action)
        {
            this.Post(() => this.ExecuteContextAction(context, action));
        }

        internal Task CloseSideAsync(PtyIoContext context, bool readSide)
        {
            var completion = CreateCompletionSource();
            this.Post(() =>
            {
                try
                {
                    this.ExecuteContextAction(context, () => context.CloseSideOnReactor(readSide));
                }
                finally
                {
                    completion.TrySetResult(null);
                }
            });

            return completion.Task;
        }

        internal Task StopAsync(PtyIoContext context)
        {
            var completion = CreateCompletionSource();
            try
            {
                this.Post(() =>
                {
                    try
                    {
                        this.Deactivate(context, ignoreErrors: true);
                        this.contexts.Remove(context);
                        context.StopOnReactor();
                    }
                    finally
                    {
                        completion.TrySetResult(null);
                    }
                });
            }
            catch (Exception exception)
            {
                context.StopAfterReactorFailure(exception);
                completion.TrySetResult(null);
            }

            return completion.Task;
        }

        internal void UpdateInterest(PtyIoContext context, uint desiredInterest)
        {
            if (context.ActiveToken == 0)
            {
                if (desiredInterest == 0)
                {
                    return;
                }

                ulong token = this.AllocateToken();
                int addError = pty_reactor_control(
                    this.epollFd,
                    ReactorAdd,
                    context.FileDescriptor,
                    token,
                    desiredInterest);
                if (addError != 0)
                {
                    throw CreateIOException("Adding PTY epoll interest", addError);
                }

                context.ActiveToken = token;
                context.ActiveInterest = desiredInterest;
                this.activeContexts.Add(token, context);
                return;
            }

            if (desiredInterest == 0)
            {
                this.Deactivate(context, ignoreErrors: false);
                return;
            }

            if (context.ActiveInterest == desiredInterest)
            {
                return;
            }

            int modifyError = pty_reactor_control(
                this.epollFd,
                ReactorModify,
                context.FileDescriptor,
                context.ActiveToken,
                desiredInterest);
            if (modifyError != 0)
            {
                throw CreateIOException("Modifying PTY epoll interest", modifyError);
            }

            context.ActiveInterest = desiredInterest;
        }

        internal static IOException CreateIOException(string operation, int error)
        {
            return new IOException($"{operation} failed with error {error} ({new Win32Exception(error).Message}).");
        }

        private bool IsStopped => Volatile.Read(ref this.fatalError) != null;

        private static TaskCompletionSource<object?> CreateCompletionSource()
        {
            return new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private void Post(Action command)
        {
            lock (this.commandGate)
            {
                if (this.fatalError is { } failure)
                {
                    throw new IOException("The PTY epoll reactor has stopped.", failure);
                }

                // Signal before publishing while holding the same gate used by the
                // consumer. If signaling fails, no command can run after its caller
                // has observed failure and possibly closed or reused the descriptor.
                int error = pty_reactor_wake(this.wakeFd);
                if (error != 0)
                {
                    throw CreateIOException("Waking the PTY epoll reactor", error);
                }

                this.commands.Enqueue(command);
            }
        }

        private void Run()
        {
            try
            {
                for (;;)
                {
                    this.DrainCommandsAndResignal();

                    int error = pty_reactor_wait(
                        this.epollFd,
                        this.events,
                        this.events.Length,
                        out int count);
                    if (error != 0)
                    {
                        throw CreateIOException("Waiting for PTY readiness", error);
                    }

                    bool wakeReady = false;
                    for (int index = 0; index < count; index++)
                    {
                        if (this.events[index].Token == WakeToken)
                        {
                            wakeReady = true;
                            break;
                        }
                    }

                    if (wakeReady)
                    {
                        error = pty_reactor_drain(this.wakeFd);
                        if (error != 0)
                        {
                            throw CreateIOException("Draining the PTY reactor wakeup", error);
                        }
                    }

                    // Cancellation and disposal commands already queued for this batch win
                    // before readiness is dispatched to a descriptor.
                    this.DrainCommandsAndResignal();

                    for (int index = 0; index < count; index++)
                    {
                        PtyReactorEvent reactorEvent = this.events[index];
                        if (reactorEvent.Token == WakeToken)
                        {
                            continue;
                        }

                        if (this.activeContexts.TryGetValue(reactorEvent.Token, out PtyIoContext? context)
                            && context.ActiveToken == reactorEvent.Token)
                        {
                            this.ExecuteContextAction(
                                context,
                                () => context.ProcessReadyOnReactor(reactorEvent.Events));
                        }
                        else if (this.activeProcesses.TryGetValue(
                            reactorEvent.Token,
                            out PtyProcessState? process)
                            && process.ActiveToken == reactorEvent.Token)
                        {
                            this.ProcessExitReady(process);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Action[] abandonedCommands;
                lock (this.commandGate)
                {
                    this.fatalError = exception;
                    var commands = new List<Action>();
                    while (this.commands.TryDequeue(out Action? command))
                    {
                        commands.Add(command);
                    }

                    abandonedCommands = commands.ToArray();
                }

                foreach (PtyIoContext context in this.contexts)
                {
                    context.FailAfterReactorStopped(exception);
                }

                foreach (PtyProcessState process in this.processes)
                {
                    process.UseFallbackAfterReactorFailure();
                }

                foreach (Action command in abandonedCommands)
                {
                    try
                    {
                        command();
                    }
                    catch
                    {
                        // Contexts have already been failed; no operation may remain pending.
                    }
                }
            }
            finally
            {
                _ = pty_close(this.wakeFd);
                _ = pty_close(this.epollFd);
            }
        }

        private void ExecuteContextAction(PtyIoContext context, Action action)
        {
            if (context.IsStoppedOnReactor)
            {
                action();
                return;
            }

            try
            {
                action();
            }
            catch (Exception exception)
            {
                this.Deactivate(context, ignoreErrors: true);
                context.FailOnReactor(exception);
            }
        }

        private void DrainCommandsAndResignal()
        {
            int processed = 0;
            while (processed < MaxCommandsPerDrain)
            {
                Action? command;
                lock (this.commandGate)
                {
                    if (!this.commands.TryDequeue(out command))
                    {
                        break;
                    }
                }

                command();
                processed++;
            }

            bool commandsRemain;
            lock (this.commandGate)
            {
                commandsRemain = !this.commands.IsEmpty;
            }

            if (commandsRemain)
            {
                int error = pty_reactor_wake(this.wakeFd);
                if (error != 0)
                {
                    throw CreateIOException("Rescheduling PTY reactor commands", error);
                }
            }
        }

        private void Deactivate(PtyIoContext context, bool ignoreErrors)
        {
            ulong token = context.ActiveToken;
            if (token == 0)
            {
                return;
            }

            int error = pty_reactor_control(
                this.epollFd,
                ReactorDelete,
                context.FileDescriptor,
                token,
                0);

            this.activeContexts.Remove(token);
            context.ActiveToken = 0;
            context.ActiveInterest = 0;

            if (!ignoreErrors && error != 0 && error != ENOENT)
            {
                throw CreateIOException("Removing PTY epoll interest", error);
            }
        }

        private void ProcessExitReady(PtyProcessState process)
        {
            if (!process.TryReap(out int exitCode, out Exception? failure))
            {
                return;
            }

            this.DeactivateProcess(process);
            process.CompleteReaping(exitCode, failure);
        }

        private void DeactivateProcess(PtyProcessState process)
        {
            ulong token = process.ActiveToken;
            if (token != 0)
            {
                _ = pty_reactor_control(
                    this.epollFd,
                    ReactorDelete,
                    process.PidFileDescriptor,
                    token,
                    0);
                this.activeProcesses.Remove(token);
                process.ActiveToken = 0;
            }

            this.processes.Remove(process);
            process.ClosePidFileDescriptor();
        }

        private ulong AllocateToken()
        {
            ulong token;
            do
            {
                token = unchecked((ulong)Interlocked.Increment(ref this.nextToken));
            }
            while (token == WakeToken);

            return token;
        }
    }
}
