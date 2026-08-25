// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Provides the ability to spawn new processes under a Linux pseudoterminal.
    /// </summary>
    public static class PtyProvider
    {
        private static readonly IDictionary<string, string> LinuxPtyEnvironment =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "TERM", "xterm-256color" },
                { "TMUX", string.Empty },
                { "TMUX_PANE", string.Empty },
                { "STY", string.Empty },
                { "WINDOW", string.Empty },
                { "WINDOWID", string.Empty },
                { "TERMCAP", string.Empty },
                { "COLUMNS", string.Empty },
                { "LINES", string.Empty },
            };

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

            if (options.Environment == null)
            {
                throw new ArgumentNullException(nameof(options.Environment));
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
                Environment = MergeEnvironment(
                    options.Environment,
                    MergeEnvironment(LinuxPtyEnvironment, null)),
            };

            return Linux.PtySpawnQueue.Shared.Enqueue(preparedOptions, cancellationToken);
        }

        private static IDictionary<string, string> MergeEnvironment(
            IDictionary<string, string> environmentToMerge,
            IDictionary<string, string>? environment)
        {
            if (environment == null)
            {
                environment = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                {
                    if (entry.Key.ToString() is not { } key)
                    {
                        continue;
                    }

                    environment[key] = entry.Value?.ToString() ?? string.Empty;
                }
            }

            foreach (var pair in environmentToMerge)
            {
                if (string.IsNullOrEmpty(pair.Value))
                {
                    environment.Remove(pair.Key);
                }
                else
                {
                    environment[pair.Key] = pair.Value;
                }
            }

            return environment;
        }
    }
}
