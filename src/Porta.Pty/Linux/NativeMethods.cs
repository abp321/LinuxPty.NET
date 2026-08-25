// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;

    internal static class NativeMethods
    {
        internal const int SIGHUP = 1;
        internal const int SIGKILL = 9;
        internal const int WaitNoHang = 1;

        internal const int ReactorAdd = 1;
        internal const int ReactorModify = 2;
        internal const int ReactorDelete = 3;

        internal const uint ReactorRead = 1;
        internal const uint ReactorWrite = 2;
        internal const uint ReactorError = 4;
        internal const uint ReactorHangup = 8;

        private const string LibPortaPty = "libporta_pty";

        internal enum TermSpeed : uint
        {
            B38400 = 0x0F,
        }

        [Flags]
        internal enum TermInputFlag : uint
        {
            BRKINT = 0x2,
            ICRNL = 0x100,
            IXON = 0x400,
            IXANY = 0x800,
            IMAXBEL = 0x2000,
            IUTF8 = 0x4000,
        }

        internal enum TermOutputFlag : uint
        {
            NONE = 0,
        }

        [Flags]
        internal enum TermControlFlag : uint
        {
            CS8 = 0x30,
            CREAD = 0x80,
            HUPCL = 0x400,
        }

        [Flags]
        internal enum TermLocalFlag : uint
        {
            ECHOKE = 0x800,
            ECHOE = 0x10,
            ECHOK = 0x20,
            ECHO = 0x8,
            ECHOCTL = 0x200,
            ISIG = 0x1,
            ICANON = 0x2,
            IEXTEN = 0x8000,
        }

        internal enum TermSpecialControlCharacter
        {
            VEOF = 4,
            VEOL = 11,
            VEOL2 = 16,
            VERASE = 2,
            VWERASE = 14,
            VKILL = 3,
            VREPRINT = 12,
            VINTR = 0,
            VQUIT = 1,
            VSUSP = 10,
            VSTART = 8,
            VSTOP = 9,
            VLNEXT = 15,
            VDISCARD = 13,
            VMIN = 6,
            VTIME = 5,
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PtySpawnResult
        {
            public int MasterFd;
            public int Pid;
            public int Error;
        }

        [StructLayout(LayoutKind.Explicit, Size = 16)]
        internal struct PtyReactorEvent
        {
            [FieldOffset(0)]
            public ulong Token;

            [FieldOffset(8)]
            public uint Events;

            [FieldOffset(12)]
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PtyTermios
        {
            public uint IFlag;
            public uint OFlag;
            public uint CFlag;
            public uint LFlag;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] CC;

            public uint ISpeed;
            public uint OSpeed;

            public PtyTermios(
                TermInputFlag inputFlag,
                TermOutputFlag outputFlag,
                TermControlFlag controlFlag,
                TermLocalFlag localFlag,
                TermSpeed speed,
                IDictionary<TermSpecialControlCharacter, sbyte> controlCharacters)
            {
                this.IFlag = (uint)inputFlag;
                this.OFlag = (uint)outputFlag;
                this.CFlag = (uint)controlFlag;
                this.LFlag = (uint)localFlag;
                this.CC = new byte[32];
                foreach (var pair in controlCharacters)
                {
                    this.CC[(int)pair.Key] = (byte)pair.Value;
                }

                this.ISpeed = (uint)speed;
                this.OSpeed = (uint)speed;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PtyWinSize
        {
            public ushort Rows;
            public ushort Cols;
            public ushort XPixel;
            public ushort YPixel;

            public PtyWinSize(ushort rows, ushort cols)
            {
                this.Rows = rows;
                this.Cols = cols;
                this.XPixel = 0;
                this.YPixel = 0;
            }
        }

        [DllImport(LibPortaPty, SetLastError = true)]
        internal static extern PtySpawnResult pty_spawn(
            [MarshalAs(UnmanagedType.LPStr)] string file,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[] argv,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[]? envp,
            [MarshalAs(UnmanagedType.LPStr)] string? workingDir,
            ref PtyTermios termios,
            ref PtyWinSize winsize);

        [DllImport(LibPortaPty, SetLastError = true)]
        internal static extern int pty_resize(int masterFd, ushort rows, ushort cols);

        [DllImport(LibPortaPty, SetLastError = true)]
        internal static extern int pty_kill(int pid, int signal);

        [DllImport(LibPortaPty, SetLastError = true)]
        internal static extern int pty_waitpid(int pid, ref int status, int options);

        [DllImport(LibPortaPty, SetLastError = true)]
        internal static extern int pty_pidfd_open(int pid);

        [DllImport(LibPortaPty, SetLastError = true)]
        internal static extern int pty_pidfd_send_signal(int pidFd, int signal);

        [DllImport(LibPortaPty, SetLastError = true)]
        internal static extern int pty_close(int masterFd);

        [DllImport(LibPortaPty)]
        internal static extern int pty_configure_master(int masterFd);

        [DllImport(LibPortaPty)]
        internal static extern int pty_reactor_create(
            ulong wakeToken,
            out int epollFd,
            out int wakeFd);

        [DllImport(LibPortaPty)]
        internal static extern int pty_reactor_control(
            int epollFd,
            int operation,
            int monitoredFd,
            ulong token,
            uint interests);

        [DllImport(LibPortaPty)]
        internal static extern int pty_reactor_wait(
            int epollFd,
            [Out] PtyReactorEvent[] events,
            int capacity,
            out int count);

        [DllImport(LibPortaPty)]
        internal static extern int pty_reactor_wake(int wakeFd);

        [DllImport(LibPortaPty)]
        internal static extern int pty_reactor_drain(int wakeFd);

        [DllImport(LibPortaPty)]
        internal static extern int pty_io_read(
            int masterFd,
            IntPtr buffer,
            int length,
            out int transferred);

        [DllImport(LibPortaPty)]
        internal static extern int pty_io_write(
            int masterFd,
            IntPtr buffer,
            int length,
            out int transferred);
    }
}
