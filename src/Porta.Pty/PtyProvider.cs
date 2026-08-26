// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Provides the ability to spawn new processes under a Linux pseudoterminal.
    /// </summary>
    public static class PtyProvider
    {
        /// <summary>
        /// Spawns a new process connected to a Linux pseudoterminal.
        /// </summary>
        /// <param name="options">The options for creating the pseudoterminal.</param>
        /// <param name="cancellationToken">
        /// Cancels a queued spawn, or cleans up a process whose native spawn is already in progress.
        /// </param>
        /// <returns>A task containing the spawned connection.</returns>
        public static Task<IPtyConnection> SpawnAsync(
            PtyOptions options,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(options.App))
            {
                throw new ArgumentNullException(nameof(options.App));
            }

            if (string.IsNullOrEmpty(options.Cwd))
            {
                throw new ArgumentNullException(nameof(options.Cwd));
            }

            if (options.CommandLine == null)
            {
                throw new ArgumentNullException(nameof(options.CommandLine));
            }

            for (int i = 0; i < options.CommandLine.Length; i++)
            {
                string argument = options.CommandLine[i];
                if (argument is null)
                {
                    throw new ArgumentException(
                        $"Command line element at index {i} is null.",
                        nameof(options.CommandLine));
                }

                if (argument.Contains('\0'))
                {
                    throw new ArgumentException(
                        $"Command line element at index {i} contains an embedded null character.",
                        nameof(options.CommandLine));
                }
            }

            ArgumentOutOfRangeException.ThrowIfNegative(options.Rows, nameof(options.Rows));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Rows, ushort.MaxValue, nameof(options.Rows));
            ArgumentOutOfRangeException.ThrowIfNegative(options.Cols, nameof(options.Cols));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Cols, ushort.MaxValue, nameof(options.Cols));

            if (options.Environment == null)
            {
                throw new ArgumentNullException(nameof(options.Environment));
            }

            foreach (KeyValuePair<string, string> pair in options.Environment)
            {
                if (pair.Key.Length == 0)
                {
                    throw new ArgumentException(
                        "Environment contains an entry with an empty key.",
                        nameof(options.Environment));
                }

                if (pair.Key.Contains('=') || pair.Key.Contains('\0'))
                {
                    throw new ArgumentException(
                        $"Environment key '{pair.Key}' contains '=' or an embedded null character.",
                        nameof(options.Environment));
                }

                if (pair.Value.Contains('\0'))
                {
                    throw new ArgumentException(
                        $"Environment value for key '{pair.Key}' contains an embedded null character.",
                        nameof(options.Environment));
                }
            }

            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException("LinuxPty.NET supports Linux only.");
            }

            var preparedOptions = new PtyOptions
            {
                App = options.App,
                Cwd = options.Cwd,
                Rows = options.Rows,
                Cols = options.Cols,
                CommandLine = (string[])options.CommandLine.Clone(),
                Environment = new Dictionary<string, string>(
                    options.Environment,
                    StringComparer.Ordinal),
            };

            return Linux.PtySpawnQueue.Shared.Enqueue(preparedOptions, cancellationToken);
        }
    }
}
