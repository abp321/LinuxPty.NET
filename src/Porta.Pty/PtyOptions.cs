// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Options for spawning a new PTY process.
    /// </summary>
    public class PtyOptions
    {
        /// <summary>
        /// Gets or sets the number of initial rows.
        /// </summary>
        public int Rows { get; set; }

        /// <summary>
        /// Gets or sets the number of initial columns.
        /// </summary>
        public int Cols { get; set; }

        /// <summary>
        /// Gets or sets the working directory for the spawned process.
        /// </summary>
        public string Cwd { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the path to the process to be spawned.
        /// </summary>
        public string App { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the command-line arguments for the process.
        /// </summary>
        public string[] CommandLine { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Gets or sets environment mutations applied after the inherited host
        /// environment and the fixed PTY policy. An empty value unsets the variable.
        /// </summary>
        public IDictionary<string, string> Environment { get; set; } = new Dictionary<string, string>();
    }
}
