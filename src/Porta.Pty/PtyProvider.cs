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
            // Validation runs against the snapshot, not the caller's instance, so concurrent
            // mutation cannot slip unchecked values past these guards.
            string app = options.App;
            string cwd = options.Cwd;
            int rows = options.Rows;
            int cols = options.Cols;
            string[] commandLine = options.CommandLine;
            IDictionary<string, string> environment = options.Environment;

            if (string.IsNullOrEmpty(app))
            {
                throw new ArgumentNullException(nameof(options.App));
            }

            if (string.IsNullOrEmpty(cwd))
            {
                throw new ArgumentNullException(nameof(options.Cwd));
            }

            if (commandLine == null)
            {
                throw new ArgumentNullException(nameof(options.CommandLine));
            }

            string[] commandLineSnapshot = (string[])commandLine.Clone();
            for (int i = 0; i < commandLineSnapshot.Length; i++)
            {
                string argument = commandLineSnapshot[i];
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

            ArgumentOutOfRangeException.ThrowIfNegative(rows, nameof(options.Rows));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, ushort.MaxValue, nameof(options.Rows));
            ArgumentOutOfRangeException.ThrowIfNegative(cols, nameof(options.Cols));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(cols, ushort.MaxValue, nameof(options.Cols));

            if (environment == null)
            {
                throw new ArgumentNullException(nameof(options.Environment));
            }

            var preparedOptions = new PtyOptions
            {
                App = app,
                Cwd = cwd,
                Rows = rows,
                Cols = cols,
                CommandLine = commandLineSnapshot,
                Environment = new Dictionary<string, string>(environment, StringComparer.Ordinal),
            };

            foreach (KeyValuePair<string, string> pair in preparedOptions.Environment)
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

            return Linux.PtySpawnQueue.Shared.Enqueue(preparedOptions, cancellationToken);
        }
    }
}
