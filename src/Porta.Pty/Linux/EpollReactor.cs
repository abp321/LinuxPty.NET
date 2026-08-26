// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.IO;
    using System.Runtime.InteropServices;
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

        private static readonly Lock SharedGate = new();
        private static EpollReactor? shared;

        private readonly int epollFd;
        private readonly int wakeFd;
        private readonly Queue<ReactorCommand> commands = new();
        private readonly Lock commandGate = new();
        private readonly Dictionary<ulong, PtyIoContext> activeContexts = new();
        private readonly Dictionary<ulong, PtyProcessState> activeProcesses = new();
        private readonly HashSet<PtyIoContext> contexts = new();
        private readonly HashSet<PtyProcessState> processes = new();
        private readonly unsafe PtyReactorEvent* events;
        private readonly Thread thread;
        private long nextToken;
        private Exception? fatalError;

        private unsafe EpollReactor()
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
                this.events = (PtyReactorEvent*)NativeMemory.Alloc(
                    (nuint)(EventCapacity * sizeof(PtyReactorEvent)));
                this.thread.Start();
            }
            catch
            {
                NativeMemory.Free(this.events);
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
                ulong token = 0;
                bool reactorRegistered = false;
                bool activeProcessAdded = false;
                bool processAdded = false;
                try
                {
                    if (this.IsStopped)
                    {
                        throw new IOException("The PTY epoll reactor has stopped.", this.fatalError);
                    }

                    token = this.AllocateToken();
                    int error = process.RegisterWithReactor(
                        token,
                        pidFd => pty_reactor_control(
                            this.epollFd,
                            ReactorAdd,
                            pidFd,
                            token,
                            ReactorRead));
                    if (error != 0)
                    {
                        throw CreateIOException("Registering a pidfd with epoll", error);
                    }

                    reactorRegistered = true;
                    this.activeProcesses.Add(token, process);
                    activeProcessAdded = true;
                    if (!this.processes.Add(process))
                    {
                        throw new InvalidOperationException("The PTY child is already registered.");
                    }

                    processAdded = true;
                    completion.TrySetResult(null);
                }
                catch (Exception exception)
                {
                    if (activeProcessAdded)
                    {
                        this.activeProcesses.Remove(token);
                    }

                    if (processAdded)
                    {
                        this.processes.Remove(process);
                    }

                    if (reactorRegistered)
                    {
                        process.RollBackReactorRegistration(
                            token,
                            pidFd => _ = pty_reactor_control(
                                this.epollFd,
                                ReactorDelete,
                                pidFd,
                                token,
                                0));
                    }

                    completion.TrySetException(exception);
                }
            });

            return completion.Task;
        }

        internal void PostContextCommand(PtyIoContext context, Action action)
        {
            this.Post(new ReactorCommand(context, action));
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
                    desiredInterest | ReactorOneShot);
                if (addError != 0)
                {
                    throw CreateIOException("Adding PTY epoll interest", addError);
                }

                context.ActiveToken = token;
                this.activeContexts.Add(token, context);
                return;
            }

            // One-shot disarms the descriptor on every dispatched event, including the
            // EPOLLERR and EPOLLHUP that a zero mask cannot suppress, so wanting nothing
            // costs no syscall and wanting anything must re-arm even if the mask is
            // unchanged.
            if (desiredInterest == 0)
            {
                return;
            }

            int modifyError = pty_reactor_control(
                this.epollFd,
                ReactorModify,
                context.FileDescriptor,
                context.ActiveToken,
                desiredInterest | ReactorOneShot);
            if (modifyError != 0)
            {
                throw CreateIOException("Modifying PTY epoll interest", modifyError);
            }
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
            this.Post(new ReactorCommand(null, command));
        }

        private void Post(ReactorCommand command)
        {
            lock (this.commandGate)
            {
                if (this.fatalError is { } failure)
                {
                    throw new IOException("The PTY epoll reactor has stopped.", failure);
                }

                // An undrained command under this gate has not been dequeued yet, and the
                // drain re-signals under the same gate for anything it leaves behind, so
                // the reactor is already guaranteed to observe this one.
                if (this.commands.Count == 0)
                {
                    // Signal before publishing while holding the same gate used by the
                    // consumer. If signaling fails, no command can run after its caller
                    // has observed failure and possibly closed or reused the descriptor.
                    int error = pty_reactor_wake(this.wakeFd);
                    if (error != 0)
                    {
                        throw CreateIOException("Waking the PTY epoll reactor", error);
                    }
                }

                this.commands.Enqueue(command);
            }
        }

        private unsafe void Run()
        {
            try
            {
                for (;;)
                {
                    this.DrainCommandsAndResignal();

                    int error = pty_reactor_wait(
                        this.epollFd,
                        this.events,
                        EventCapacity,
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
                            this.DispatchReadyOnReactor(context, reactorEvent.Events);
                        }
                        else if (this.activeProcesses.TryGetValue(
                            reactorEvent.Token,
                            out PtyProcessState? process)
                            && process.HasReactorToken(reactorEvent.Token))
                        {
                            this.ProcessExitReady(process, reactorEvent.Token);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                ReactorCommand[] abandonedCommands;
                lock (this.commandGate)
                {
                    this.fatalError = exception;
                    abandonedCommands = this.commands.ToArray();
                    this.commands.Clear();
                }

                // Uncache before the failure fan-out so callers racing the fault get a
                // fresh reactor instead of this one, which now rejects every post.
                lock (SharedGate)
                {
                    if (ReferenceEquals(shared, this))
                    {
                        shared = null;
                    }
                }

                PtyIoContext[] failedContexts = new PtyIoContext[this.contexts.Count];
                this.contexts.CopyTo(failedContexts);
                PtyProcessState[] failedProcesses = new PtyProcessState[this.processes.Count];
                this.processes.CopyTo(failedProcesses);
                this.activeContexts.Clear();
                this.contexts.Clear();
                this.activeProcesses.Clear();
                this.processes.Clear();

                foreach (PtyIoContext context in failedContexts)
                {
                    context.FailAfterReactorStopped(exception);
                }

                foreach (PtyProcessState process in failedProcesses)
                {
                    process.UseFallbackAfterReactorFailure();
                }

                foreach (ReactorCommand command in abandonedCommands)
                {
                    try
                    {
                        this.Execute(command);
                    }
                    catch
                    {
                        // Contexts have already been failed; no operation may remain pending.
                    }
                }
            }
            finally
            {
                NativeMemory.Free(this.events);
                _ = pty_close(this.wakeFd);
                _ = pty_close(this.epollFd);
            }
        }

        private void Execute(ReactorCommand command)
        {
            if (command.Context is { } context)
            {
                this.ExecuteContextAction(context, command.Action);
                return;
            }

            command.Action();
        }

        private void DispatchReadyOnReactor(PtyIoContext context, uint events)
        {
            if (context.IsStoppedOnReactor)
            {
                context.ProcessReadyOnReactor(events);
                return;
            }

            try
            {
                context.ProcessReadyOnReactor(events);
            }
            catch (Exception exception)
            {
                this.Deactivate(context, ignoreErrors: true);
                context.FailOnReactor(exception);
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
                ReactorCommand command;
                lock (this.commandGate)
                {
                    if (!this.commands.TryDequeue(out command))
                    {
                        break;
                    }
                }

                this.Execute(command);
                processed++;
            }

            bool commandsRemain;
            lock (this.commandGate)
            {
                commandsRemain = this.commands.Count != 0;
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

            if (!ignoreErrors && error != 0 && error != ENOENT)
            {
                throw CreateIOException("Removing PTY epoll interest", error);
            }
        }

        private void ProcessExitReady(PtyProcessState process, ulong token)
        {
            if (!process.TryReap(out int exitCode, out Exception? failure))
            {
                // The pidfd is level-triggered, so a readable but not-yet-reapable child
                // (one still held by a tracer) would spin the reactor on every wait.
                process.UseFallbackAfterDeclinedReap(
                    token,
                    pidFd =>
                    {
                        int error = pty_reactor_control(
                            this.epollFd,
                            ReactorDelete,
                            pidFd,
                            token,
                            0);
                        if (error != 0 && error != ENOENT)
                        {
                            // A pidfd left registered would keep spinning the reactor,
                            // so fail it and let every child fall back instead.
                            throw CreateIOException("Removing PTY pidfd epoll interest", error);
                        }
                    });
                this.activeProcesses.Remove(token);
                this.processes.Remove(process);
                return;
            }

            failure = process.DetachAfterReapingFromReactor(
                token,
                pidFd => _ = pty_reactor_control(
                    this.epollFd,
                    ReactorDelete,
                    pidFd,
                    token,
                    0),
                failure);
            this.activeProcesses.Remove(token);
            this.processes.Remove(process);
            process.CompleteReaping(exitCode, failure);
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

        private readonly record struct ReactorCommand(PtyIoContext? Context, Action Action);
    }
}
