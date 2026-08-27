// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    /// <summary>
    /// Identifies a reactor command whose work is dispatched from state instead of a delegate, so
    /// the hot post paths allocate nothing.
    /// </summary>
    internal enum ReactorCommandKind
    {
        General = 0,
        EnqueueRead,
        EnqueueReadFallback,
        EnqueueWrite,
        EnqueueWriteFallback,
        CancelRead,
        CancelWrite,
        KickReads,
        KickWrites,
    }
}
