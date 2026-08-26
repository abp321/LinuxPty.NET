// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using static Porta.Pty.Linux.NativeMethods;

    /// <summary>
    /// Provides PTY connections on Linux.
    /// </summary>
    internal static class PtyProvider
    {
        // The native pty_spawn already serializes across callers with a process-wide
        // mutex; this gate only bounds the managed side to one pool worker at a time,
        // and covers only the synchronous native spawn section below.
        private static readonly SemaphoreSlim SpawnGate = new(1, 1);

        internal static async Task<IPtyConnection> StartTerminalAsync(
            PtyOptions options,
            IPtyEventLoop? eventLoop,
            CancellationToken cancellationToken)
        {
            string?[] terminalArgs = GetExecvpArgs(options);

            string?[]? environmentMutations = GetEnvironmentMutations(options);

            // This runs on a transient pool worker. Capture the .NET managed process
            // environment at execution time, immediately before entering the
            // synchronous native spawn call.
            string?[] inheritedEnvironment = GetInheritedEnvironment();

            await SpawnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            PtySpawnResult result;
            try
            {
                result = pty_spawn(
                    options.App,
                    terminalArgs,
                    inheritedEnvironment,
                    environmentMutations,
                    options.Cwd,
                    (ushort)options.Rows,
                    (ushort)options.Cols);
            }
            finally
            {
                SpawnGate.Release();
            }

            if (result.Pid == -1)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new InvalidOperationException(
                    $"pty_spawn failed for '{options.App}': error={result.Error} "
                    + $"({GetErrorMessage(result.Error)}), masterFd={result.MasterFd}, pid={result.Pid}");
            }

            EpollReactor? reactor = null;
            PtyProcessState? processState = null;
            PtyIoContext? ioContext = null;
            try
            {
                reactor = eventLoop is null ? EpollReactor.Shared : new EpollReactor(eventLoop);
                processState = new PtyProcessState(
                    result.Pid,
                    result.PidFd,
                    result.PidFdError);
                processState.AttachReactor(reactor);
                PtyProcessState? pidFdProcess = processState.TryBeginEpollRegistration()
                    ? processState
                    : null;
                Exception? pidFdFailure;
                try
                {
                    (ioContext, pidFdFailure) = await PtyIoContext
                        .CreateAsync(result.MasterFd, pidFdProcess, reactor)
                        .ConfigureAwait(false);
                }
                catch when (pidFdProcess is not null)
                {
                    // The fused command faulted before claiming the pidfd, so the child is
                    // still parked in RegisteringEpoll and nothing would ever reap it.
                    pidFdProcess.UseFallbackAfterFailedRegistration();
                    throw;
                }

                if (pidFdFailure is not null)
                {
                    processState.UseFallbackAfterFailedRegistration();
                }

                cancellationToken.ThrowIfCancellationRequested();

                return new PtyConnection(result.MasterFd, result.Pid, ioContext, processState);
            }
            catch (Exception exception)
            {
                if (eventLoop is not null && reactor is not null)
                {
                    // An external engine that never took ownership of anything can never reach
                    // idle-close, so its backend registrations and descriptors would leak. This
                    // runs on a spawn worker, so the teardown is marshalled to the loop.
                    reactor.PostExternalFailure(exception);
                }

                if (processState is null)
                {
                    CleanupUntrackedChild(result.MasterFd, result.Pid, result.PidFd);
                }
                else
                {
                    await CleanupTrackedChildAsync(
                        result.MasterFd,
                        processState,
                        ioContext).ConfigureAwait(false);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw;
            }
        }

        private static string?[] GetExecvpArgs(PtyOptions options)
        {
            if (options.CommandLine.Length == 0)
            {
                return new[] { options.App, null };
            }

            var result = new string?[options.CommandLine.Length + 2];
            Array.Copy(options.CommandLine, 0, result, 1, options.CommandLine.Length);
            result[0] = options.App;
            return result;
        }

        private static string?[] GetInheritedEnvironment()
        {
            IDictionary snapshot = Environment.GetEnvironmentVariables();
            var entries = new List<string?>(snapshot.Count + 1);
            foreach (DictionaryEntry pair in snapshot)
            {
                if (pair.Key is string key
                    && pair.Value is string value
                    && IsValidEnvironmentEntry(key, value))
                {
                    entries.Add($"{key}={value}");
                }
            }

            entries.Sort(StringComparer.Ordinal);
            entries.Add(null);
            return entries.ToArray();
        }

        private static string?[]? GetEnvironmentMutations(PtyOptions options)
        {
            if (options.Environment.Count == 0)
            {
                return null;
            }

            string?[] entries = options.Environment
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}")
                .Concat(new string?[] { null })
                .ToArray();
            return entries.Length == 1 ? null : entries;
        }

        private static bool IsValidEnvironmentEntry(string key, string value)
        {
            return key.Length != 0
                && !key.Contains('=')
                && !key.Contains('\0')
                && !value.Contains('\0');
        }

        private static string GetErrorMessage(int errno)
        {
            if (errno <= 0)
            {
                return $"not an errno ({errno}); the native result struct did not carry one";
            }

            return Marshal.GetPInvokeErrorMessage(errno);
        }

        private static void CleanupUntrackedChild(int masterFd, int pid, int pidFd)
        {
            try
            {
                _ = pty_cleanup_untracked(masterFd, pid, pidFd);
            }
            catch
            {
                // Cleanup must not replace the setup exception or cancellation.
            }
        }

        private static async Task CleanupTrackedChildAsync(
            int masterFd,
            PtyProcessState processState,
            PtyIoContext? ioContext)
        {
            TrySendSignal(processState, SIGKILL);
            if (ioContext is not null)
            {
                try
                {
                    await ioContext.StopAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Continue closing and reaping the child.
                }
            }

            TryClose(masterFd);
            try
            {
                await processState.EnsureReapedAsync().ConfigureAwait(false);
            }
            catch
            {
                // Cleanup must not replace the setup exception or cancellation.
            }
        }

        private static void TrySendSignal(PtyProcessState processState, int signal)
        {
            try
            {
                _ = processState.SendSignal(signal);
            }
            catch
            {
                // Continue closing and reaping the child.
            }
        }

        private static void TryClose(int fileDescriptor)
        {
            try
            {
                // Never retry close after EINTR because Linux may already have reused the fd.
                _ = pty_close(fileDescriptor);
            }
            catch
            {
                // Continue reaping the child.
            }
        }
    }
}
