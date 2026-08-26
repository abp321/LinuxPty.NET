// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty
{
    using System;

    /// <summary>
    /// Readiness conditions the library asks an external event loop to watch for on a descriptor.
    /// </summary>
    [Flags]
    public enum PtyFdInterests
    {
        /// <summary>
        /// No readiness is requested and the registration stays disarmed.
        /// </summary>
        None = 0,

        /// <summary>
        /// Readiness for reading is requested.
        /// </summary>
        Read = 1,

        /// <summary>
        /// Readiness for writing is requested.
        /// </summary>
        Write = 2,
    }
}
