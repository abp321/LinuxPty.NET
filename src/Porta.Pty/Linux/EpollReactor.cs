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
        private const ulong TimerToken = 1;
        private const int EventCapacity = 64;
        private const int MaxCommandsPerDrain = 16;
        private const int ENOENT = 2;
        private const int FallbackBasePollMilliseconds = 20;
        private const int FallbackMaxPollMilliseconds = 500;
        private const int FallbackPollGrowthFactor = 2;
        private const int FallbackScanBatchLimit = 64;

        private static readonly Lock SharedGate = new();
        private static EpollReactor? shared;

        private readonly IReactorBackend backend;
        private readonly Queue<ReactorCommand> commands = new();
        private readonly Lock commandGate = new();
        private readonly Dictionary<ulong, PtyIoContext> activeContexts = new();
        private readonly Dictionary<ulong, PtyProcessState> activeProcesses = new();
        private readonly HashSet<PtyIoContext> contexts = new();
        private readonly HashSet<PtyProcessState> processes = new();
        private readonly List<PtyProcessState> fallbackProcesses = new();
        private readonly unsafe PtyReactorEvent* events;
        private readonly Thread? thread;
        private readonly bool isExternal;
        private long nextToken;
        private int fallbackPollIntervalMilliseconds = FallbackBasePollMilliseconds;
        private int fallbackScanCursor;
        private bool fallbackTimerArmed;
        private long fallbackTimerDeadline;
        private bool fallbackRegistered;
        private Exception? fatalError;

        private unsafe EpollReactor(IReactorBackend backend)
        {
            this.backend = backend;
            try
            {
                this.thread = new Thread(this.Run)
                {
                    IsBackground = true,
                    Name = "LinuxPty.NET epoll reactor",
                };
                this.events = (PtyReactorEvent*)NativeMemory.Alloc(
                    (nuint)(EventCapacity * sizeof(PtyReactorEvent)));
                this.thread.Start();
            }
            catch
            {
                NativeMemory.Free(this.events);
                backend.Close();
                throw;
            }
        }

        /// <summary>
        /// Creates an engine driven by a caller-supplied event loop: no reactor thread, no event
        /// buffer, and no <see cref="Run"/> loop, so every entry arrives through the backend.
        /// </summary>
        internal EpollReactor(IPtyEventLoop eventLoop)
        {
            this.isExternal = true;
            this.backend = new ExternalReactorBackend(this, eventLoop);
        }

        internal static EpollReactor Shared
        {
            get
            {
                lock (SharedGate)
                {
                    return shared ??= new EpollReactor(new EpollReactorBackend(WakeToken, TimerToken));
                }
            }
        }

        /// <summary>
        /// Registers the context and, when present, the child's pidfd in one reactor round trip.
        /// A faulted task means the context registration failed and the spawn must fail; a non-null
        /// result means only the pidfd registration failed and the caller falls back to polling.
        /// </summary>
        internal Task<Exception?> RegisterConnectionAsync(PtyIoContext context, PtyProcessState? process)
        {
            var completion = new TaskCompletionSource<Exception?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            this.Post(() =>
            {
                try
                {
                    if (this.IsStopped)
                    {
                        throw new IOException("The PTY epoll reactor has stopped.", this.fatalError);
                    }

                    this.contexts.Add(context);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                    return;
                }

                if (process is null)
                {
                    completion.TrySetResult(null);
                    return;
                }

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
                        pidFd => this.backend.AddInterest(pidFd, token, ReactorRead));
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
                            pidFd => _ = this.backend.RemoveInterest(pidFd, token));
                    }

                    completion.TrySetResult(exception);
                }
            });

            return completion.Task;
        }

        /// <summary>
        /// Adopts a child that has no usable pidfd into the reactor's timerfd poll.
        /// </summary>
        internal void RegisterFallbackProcess(PtyProcessState process)
        {
            this.Post(() =>
            {
                if (this.IsStopped)
                {
                    // The death fan-out suppresses exceptions from abandoned commands, so a
                    // throwing emergency registration would strand the child unreaped.
                    try
                    {
                        PtyProcessReaper.Shared.Register(process);
                    }
                    catch
                    {
                        process.AbandonFallbackForEmergencyReap();
                    }

                    return;
                }

                if (this.fallbackProcesses.Contains(process))
                {
                    return;
                }

                this.fallbackProcesses.Add(process);
                this.fallbackRegistered = true;
                this.fallbackPollIntervalMilliseconds = FallbackBasePollMilliseconds;

                // A pending scan deadline may be pulled earlier, never pushed later.
                if (!this.fallbackTimerArmed
                    || Environment.TickCount64 + FallbackBasePollMilliseconds < this.fallbackTimerDeadline)
                {
                    this.ArmFallbackTimer(FallbackBasePollMilliseconds);
                }
            });
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
                        this.CloseExternalBackendIfIdle();
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
                int addError = this.backend.AddInterest(
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

            int modifyError = this.backend.ModifyInterest(
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

        /// <summary>
        /// Runs one reactor iteration over the <paramref name="count"/> events the backend reported
        /// into the engine-owned buffer: reserved-token detection, the wake and timer drains, the
        /// pre-dispatch command drain, readiness dispatch, and the fallback reap scan.
        /// </summary>
        internal unsafe void ProcessReadyBatch(int count)
        {
            bool wakeReady = false;
            bool timerFired = false;
            for (int index = 0; index < count; index++)
            {
                ulong token = this.events[index].Token;
                if (token == WakeToken)
                {
                    wakeReady = true;
                }
                else if (token == TimerToken)
                {
                    timerFired = true;
                }
            }

            if (wakeReady)
            {
                int error = this.backend.DrainWake();
                if (error != 0)
                {
                    throw CreateIOException("Draining the PTY reactor wakeup", error);
                }
            }

            if (timerFired)
            {
                this.fallbackTimerArmed = false;
                int error = this.backend.DrainTimer();
                if (error != 0)
                {
                    throw CreateIOException("Draining the PTY fallback poll timer", error);
                }
            }

            // Cancellation and disposal commands already queued for this batch win
            // before readiness is dispatched to a descriptor.
            this.DrainCommandsAndResignal();

            for (int index = 0; index < count; index++)
            {
                PtyReactorEvent reactorEvent = this.events[index];
                if (reactorEvent.Token == WakeToken || reactorEvent.Token == TimerToken)
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

            if (timerFired)
            {
                this.ScanFallbackProcesses();
            }
        }

        /// <summary>
        /// Drains queued commands on behalf of the external loop's wake descriptor.
        /// </summary>
        internal void ExternalWake()
        {
            this.RunExternalEntry(this.DrainCommandsAndResignal);
        }

        /// <summary>
        /// Dispatches one external readiness delivery for <paramref name="token"/>.
        /// </summary>
        internal void ExternalReadiness(ulong token, uint nativeEvents)
        {
            this.RunExternalEntry(() =>
            {
                // Cancellation and disposal commands win before readiness is dispatched to a
                // descriptor, exactly as the batch loop orders them.
                this.DrainCommandsAndResignal();

                if (this.activeContexts.TryGetValue(token, out PtyIoContext? context)
                    && context.ActiveToken == token)
                {
                    this.DispatchReadyOnReactor(context, nativeEvents);
                }
                else if (this.activeProcesses.TryGetValue(token, out PtyProcessState? process)
                    && process.HasReactorToken(token))
                {
                    this.ProcessExitReady(process, token);
                }
            });
        }

        /// <summary>
        /// Runs the fallback reap scan on behalf of the external loop's timer descriptor.
        /// </summary>
        internal void ExternalTimer()
        {
            this.RunExternalEntry(() =>
            {
                this.DrainCommandsAndResignal();
                this.fallbackTimerArmed = false;
                this.ScanFallbackProcesses();
            });
        }

        /// <summary>
        /// Fails the engine from the external backend, once: a stopped engine ignores it.
        /// </summary>
        internal void ExternalFail(Exception exception)
        {
            if (this.IsStopped)
            {
                return;
            }

            this.FailReactor(exception);
        }

        internal bool IsStopped => Volatile.Read(ref this.fatalError) != null;

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
                    int error = this.backend.Wake();
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

                    int error = this.backend.Wait(this.events, EventCapacity, out int count);
                    if (error != 0)
                    {
                        throw CreateIOException("Waiting for PTY readiness", error);
                    }

                    this.ProcessReadyBatch(count);
                }
            }
            catch (Exception exception)
            {
                this.FailReactor(exception);
            }
            finally
            {
                NativeMemory.Free(this.events);
                this.backend.Close();
            }
        }

        private void FailReactor(Exception exception)
        {
            ReactorCommand[] abandonedCommands;
            lock (this.commandGate)
            {
                this.fatalError = exception;
                abandonedCommands = this.commands.ToArray();
                this.commands.Clear();
            }

            if (!this.isExternal)
            {
                // Uncache before the failure fan-out so callers racing the fault get a
                // fresh reactor instead of this one, which now rejects every post.
                lock (SharedGate)
                {
                    if (ReferenceEquals(shared, this))
                    {
                        shared = null;
                    }
                }
            }

            PtyIoContext[] failedContexts = new PtyIoContext[this.contexts.Count];
            this.contexts.CopyTo(failedContexts);
            PtyProcessState[] failedProcesses = new PtyProcessState[this.processes.Count];
            this.processes.CopyTo(failedProcesses);
            PtyProcessState[] fallbackChildren = new PtyProcessState[this.fallbackProcesses.Count];
            this.fallbackProcesses.CopyTo(fallbackChildren);
            this.activeContexts.Clear();
            this.contexts.Clear();
            this.activeProcesses.Clear();
            this.processes.Clear();
            this.fallbackProcesses.Clear();

            foreach (PtyIoContext context in failedContexts)
            {
                context.FailAfterReactorStopped(exception);
            }

            foreach (PtyProcessState process in failedProcesses)
            {
                process.UseFallbackAfterReactorFailure();
            }

            foreach (PtyProcessState child in fallbackChildren)
            {
                try
                {
                    PtyProcessReaper.Shared.Register(child);
                }
                catch
                {
                    child.AbandonFallbackForEmergencyReap();
                }
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

            if (this.isExternal)
            {
                // No Run loop reaches a finally here, so releasing the caller's loop is this
                // method's job in external mode.
                this.backend.Close();
            }
        }

        private void CloseExternalBackendIfIdle()
        {
            if (!this.isExternal
                || this.contexts.Count != 0
                || this.processes.Count != 0
                || this.activeContexts.Count != 0
                || this.activeProcesses.Count != 0
                || this.fallbackProcesses.Count != 0)
            {
                return;
            }

            // The connection's descriptor close is gated on StopAsync's task and reaping completes
            // through ProcessExitReady or the fallback scan, so once every set is empty no further
            // callback is owed and the caller's loop can be released.
            lock (this.commandGate)
            {
                this.fatalError ??= new IOException("The PTY epoll reactor has stopped.");
            }

            this.backend.Close();
        }

        private void RunExternalEntry(Action entry)
        {
            if (this.IsStopped)
            {
                return;
            }

            try
            {
                entry();
            }
            catch (Exception exception)
            {
                this.FailReactor(exception);
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
                int error = this.backend.Wake();
                if (error != 0)
                {
                    throw CreateIOException("Rescheduling PTY reactor commands", error);
                }
            }
        }

        private void ScanFallbackProcesses()
        {
            int count = this.fallbackProcesses.Count;
            int visits = Math.Min(count, FallbackScanBatchLimit);
            if (this.fallbackScanCursor >= count)
            {
                this.fallbackScanCursor = 0;
            }

            // The list is not mutated while it is being walked, so the visits distinct
            // offsets from the cursor map to visits distinct entries.
            var reaped = new List<FallbackReapResult>();
            for (int visited = 0; visited < visits; visited++)
            {
                PtyProcessState process =
                    this.fallbackProcesses[(this.fallbackScanCursor + visited) % count];
                if (process.TryReap(out int exitCode, out Exception? failure))
                {
                    reaped.Add(new FallbackReapResult(process, exitCode, failure));
                }
            }

            this.fallbackScanCursor += visits;

            foreach (FallbackReapResult result in reaped)
            {
                int index = this.fallbackProcesses.IndexOf(result.Process);
                int last = this.fallbackProcesses.Count - 1;
                this.fallbackProcesses[index] = this.fallbackProcesses[last];
                this.fallbackProcesses.RemoveAt(last);
                if (index < this.fallbackScanCursor)
                {
                    this.fallbackScanCursor--;
                }

                result.Process.FinishReapingFromFallback(result.ExitCode, result.Failure);
            }

            if (this.fallbackProcesses.Count == 0)
            {
                this.fallbackScanCursor = 0;
            }
            else
            {
                this.fallbackScanCursor %= this.fallbackProcesses.Count;
            }

            if (visits < count)
            {
                // Children past the batch cap went unvisited this fire; scan them promptly.
                this.fallbackPollIntervalMilliseconds = FallbackBasePollMilliseconds;
            }
            else
            {
                // A registration during this scan already reset the interval; growing now would undo it.
                this.fallbackPollIntervalMilliseconds = reaped.Count != 0 || this.fallbackRegistered
                    ? FallbackBasePollMilliseconds
                    : Math.Min(
                        this.fallbackPollIntervalMilliseconds * FallbackPollGrowthFactor,
                        FallbackMaxPollMilliseconds);
            }

            this.fallbackRegistered = false;

            if (this.fallbackProcesses.Count != 0)
            {
                this.ArmFallbackTimer(this.fallbackPollIntervalMilliseconds);
                return;
            }

            this.CloseExternalBackendIfIdle();
        }

        private void ArmFallbackTimer(int milliseconds)
        {
            int error = this.backend.ArmTimer(milliseconds);
            if (error != 0)
            {
                // A reactor that cannot arm its poll timer must die so the death fan-out
                // rehomes every fallback child.
                throw CreateIOException("Arming the PTY fallback poll timer", error);
            }

            this.fallbackTimerDeadline = Environment.TickCount64 + milliseconds;
            this.fallbackTimerArmed = true;
        }

        private void Deactivate(PtyIoContext context, bool ignoreErrors)
        {
            ulong token = context.ActiveToken;
            if (token == 0)
            {
                return;
            }

            int error = this.backend.RemoveInterest(context.FileDescriptor, token);

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
                        int error = this.backend.RemoveInterest(pidFd, token);
                        if (error != 0 && error != ENOENT)
                        {
                            // A pidfd left registered would keep spinning the reactor,
                            // so fail it and let every child fall back instead.
                            throw CreateIOException("Removing PTY pidfd epoll interest", error);
                        }
                    });
                this.activeProcesses.Remove(token);
                this.processes.Remove(process);
                this.CloseExternalBackendIfIdle();
                return;
            }

            failure = process.DetachAfterReapingFromReactor(
                token,
                pidFd => _ = this.backend.RemoveInterest(pidFd, token),
                failure);
            this.activeProcesses.Remove(token);
            this.processes.Remove(process);
            process.CompleteReaping(exitCode, failure);
            this.CloseExternalBackendIfIdle();
        }

        private ulong AllocateToken()
        {
            ulong token;
            do
            {
                token = unchecked((ulong)Interlocked.Increment(ref this.nextToken));
            }
            while (token == WakeToken || token == TimerToken);

            return token;
        }

        private readonly record struct ReactorCommand(PtyIoContext? Context, Action Action);

        private readonly record struct FallbackReapResult(
            PtyProcessState Process,
            int ExitCode,
            Exception? Failure);
    }
}
