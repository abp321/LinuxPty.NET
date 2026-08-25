// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    using System;
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
        private const int EINTR = 4;

        internal static async Task<IPtyConnection> StartTerminalAsync(
            PtyOptions options,
            CancellationToken cancellationToken)
        {
            var terminalSize = new PtyWinSize((ushort)options.Rows, (ushort)options.Cols);
            string?[] terminalArgs = GetExecvpArgs(options);

            string?[]? environment = null;
            if (options.Environment.Count > 0)
            {
                environment = options.Environment
                    .Select(pair => $"{pair.Key}={pair.Value}")
                    .Concat(new string?[] { null })
                    .ToArray();
            }

            var controlCharacters = new Dictionary<TermSpecialControlCharacter, sbyte>
            {
                { TermSpecialControlCharacter.VEOF, 4 },
                { TermSpecialControlCharacter.VEOL, -1 },
                { TermSpecialControlCharacter.VEOL2, -1 },
                { TermSpecialControlCharacter.VERASE, 0x7f },
                { TermSpecialControlCharacter.VWERASE, 23 },
                { TermSpecialControlCharacter.VKILL, 21 },
                { TermSpecialControlCharacter.VREPRINT, 18 },
                { TermSpecialControlCharacter.VINTR, 3 },
                { TermSpecialControlCharacter.VQUIT, 0x1c },
                { TermSpecialControlCharacter.VSUSP, 26 },
                { TermSpecialControlCharacter.VSTART, 17 },
                { TermSpecialControlCharacter.VSTOP, 19 },
                { TermSpecialControlCharacter.VLNEXT, 22 },
                { TermSpecialControlCharacter.VDISCARD, 15 },
                { TermSpecialControlCharacter.VMIN, 1 },
                { TermSpecialControlCharacter.VTIME, 0 },
            };

            var termios = new PtyTermios(
                inputFlag: TermInputFlag.ICRNL | TermInputFlag.IXON | TermInputFlag.IXANY
                    | TermInputFlag.IMAXBEL | TermInputFlag.BRKINT | TermInputFlag.IUTF8,
                outputFlag: TermOutputFlag.NONE,
                controlFlag: TermControlFlag.CREAD | TermControlFlag.CS8 | TermControlFlag.HUPCL,
                localFlag: TermLocalFlag.ICANON | TermLocalFlag.ISIG | TermLocalFlag.IEXTEN
                    | TermLocalFlag.ECHO | TermLocalFlag.ECHOE | TermLocalFlag.ECHOK
                    | TermLocalFlag.ECHOKE | TermLocalFlag.ECHOCTL,
                speed: TermSpeed.B38400,
                controlCharacters: controlCharacters);

            // This synchronous native call always runs on PtySpawnQueue's dedicated worker.
            PtySpawnResult result = pty_spawn(
                options.App,
                terminalArgs,
                environment,
                options.Cwd,
                ref termios,
                ref terminalSize);

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
                CleanupUntrackedChild(result.MasterFd, result.Pid);
                throw new OperationCanceledException(cancellationToken);
            }

            PtyProcessState? processState = null;
            PtyIoContext? ioContext = null;
            bool masterClosed = false;
            try
            {
                int configureError = pty_configure_master(result.MasterFd);
                if (configureError != 0)
                {
                    throw new InvalidOperationException(
                        $"Configuring PTY master fd {result.MasterFd} as non-blocking failed: "
                        + $"error={configureError} ({GetErrorMessage(configureError)}).");
                }

                processState = new PtyProcessState(result.Pid);
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
                    CleanupUntrackedChild(result.MasterFd, result.Pid);
                    masterClosed = true;
                }
                else
                {
                    _ = processState.SendSignal(SIGKILL);
                    if (ioContext is not null)
                    {
                        await ioContext.StopAsync().ConfigureAwait(false);
                    }

                    _ = pty_close(result.MasterFd);
                    masterClosed = true;
                    try
                    {
                        if (processState.HasReapingOwner)
                        {
                            await processState.ExitTask.ConfigureAwait(false);
                        }
                        else
                        {
                            processState.ReapSynchronouslyAfterTrackingFailure();
                        }
                    }
                    catch
                    {
                        // Cleanup must not replace the setup exception.
                    }
                }

                if (!masterClosed)
                {
                    _ = pty_close(result.MasterFd);
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

        private static void CleanupUntrackedChild(int masterFd, int pid)
        {
            TryKill(pid, SIGKILL);
            _ = pty_close(masterFd);

            int status = 0;
            while (pty_waitpid(pid, ref status, 0) == -1
                && Marshal.GetLastWin32Error() == EINTR)
            {
            }
        }

        private static void TryKill(int pid, int signal)
        {
            try
            {
                _ = pty_kill(pid, signal);
            }
            catch
            {
                // Continue closing and reaping the child.
            }
        }
    }
}
