// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using static Porta.Pty.Linux.NativeMethods;

    /// <summary>
    /// Provides PTY connections on Linux.
    /// </summary>
    internal static class PtyProvider
    {
        internal static async Task<IPtyConnection> StartTerminalAsync(
            PtyOptions options,
            CancellationToken cancellationToken)
        {
            string?[] terminalArgs = GetExecvpArgs(options);

            string?[]? environmentMutations = null;
            if (options.Environment.Count > 0)
            {
                environmentMutations = options.Environment
                    .Select(pair => $"{pair.Key}={pair.Value}")
                    .Concat(new string?[] { null })
                    .ToArray();
            }

            // This synchronous native call always runs on PtySpawnQueue's dedicated worker.
            PtySpawnResult result = pty_spawn(
                options.App,
                terminalArgs,
                environmentMutations,
                options.Cwd,
                (ushort)options.Rows,
                (ushort)options.Cols);

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

            if (cancellationToken.IsCancellationRequested)
            {
                CleanupUntrackedChild(result.MasterFd, result.Pid, result.PidFd);
                throw new OperationCanceledException(cancellationToken);
            }

            PtyProcessState? processState = null;
            PtyIoContext? ioContext = null;
            try
            {
                processState = new PtyProcessState(
                    result.Pid,
                    result.PidFd,
                    result.PidFdError);
                await processState.StartAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                ioContext = await PtyIoContext.CreateAsync(result.MasterFd).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                return new PtyConnection(result.MasterFd, result.Pid, ioContext, processState);
            }
            catch
            {
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

        private static string GetErrorMessage(int errno)
        {
            if (errno <= 0)
            {
                return $"not an errno ({errno}); the native result struct did not carry one";
            }

            return new System.ComponentModel.Win32Exception(errno).Message;
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
                processState.ReapSynchronouslyAfterTrackingFailure();
                await processState.ExitTask.ConfigureAwait(false);
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
