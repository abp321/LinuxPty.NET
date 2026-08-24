// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    using System;
    using System.IO;
    using Microsoft.Win32.SafeHandles;

    /// <summary>
    /// A synchronous stream connected to a Linux PTY.
    /// </summary>
    internal sealed class PtyStream : FileStream
    {
        public PtyStream(int fd, FileAccess fileAccess)
            : base(new SafeFileHandle((IntPtr)fd, ownsHandle: false), fileAccess, bufferSize: 1024, isAsync: false)
        {
        }

        public override bool CanSeek => false;
    }
}
