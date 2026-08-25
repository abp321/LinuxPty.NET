/*
 * porta_pty.c - Native PTY shim for Porta.Pty
 *
 * This library keeps forkpty() and execvp() entirely in native code so no
 * managed .NET code runs in the forked child process.
 *
 * Copyright (c) Microsoft Corporation. All rights reserved.
 * Licensed under the MIT license.
 */

#include <alloca.h>
#include <errno.h>
#include <fcntl.h>
#include <pthread.h>
#include <pty.h>
#include <signal.h>
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/epoll.h>
#include <sys/eventfd.h>
#include <sys/ioctl.h>
#include <sys/syscall.h>
#include <sys/wait.h>
#include <termios.h>
#include <unistd.h>

#define PTY_EXPORT __attribute__((visibility("default")))

/*
 * Linux assigned these syscall numbers consistently on x86-64 and arm64.
 * Define them when building against the older kernel headers used to preserve
 * the library's glibc 2.27 runtime floor.
 */
#if defined(__x86_64__) || defined(__aarch64__)
#ifndef SYS_pidfd_send_signal
#define SYS_pidfd_send_signal 424
#endif
#ifndef SYS_pidfd_open
#define SYS_pidfd_open 434
#endif
#endif

typedef struct {
    unsigned int c_iflag;
    unsigned int c_oflag;
    unsigned int c_cflag;
    unsigned int c_lflag;
    unsigned char c_cc[32];
    unsigned int c_ispeed;
    unsigned int c_ospeed;
} pty_termios_t;

typedef struct {
    unsigned short ws_row;
    unsigned short ws_col;
    unsigned short ws_xpixel;
    unsigned short ws_ypixel;
} pty_winsize_t;

typedef struct {
    int master_fd;
    int pid;
    int error;
} pty_spawn_result_t;

typedef struct {
    uint64_t token;
    uint32_t events;
    uint32_t reserved;
} pty_reactor_event_t;

typedef char pty_reactor_event_size_must_be_16[
    sizeof(pty_reactor_event_t) == 16 ? 1 : -1];
typedef char pty_reactor_event_events_offset_must_be_8[
    offsetof(pty_reactor_event_t, events) == 8 ? 1 : -1];
typedef char pty_reactor_event_reserved_offset_must_be_12[
    offsetof(pty_reactor_event_t, reserved) == 12 ? 1 : -1];

enum {
    PTY_REACTOR_ADD = 1,
    PTY_REACTOR_MODIFY = 2,
    PTY_REACTOR_DELETE = 3,
};

enum {
    PTY_REACTOR_READ = 1,
    PTY_REACTOR_WRITE = 2,
    PTY_REACTOR_ERROR = 4,
    PTY_REACTOR_HANGUP = 8,
};

enum {
    PTY_REACTOR_MAX_EVENTS = 64,
};

/*
 * Preserve the inherited Linux spawn serialization in this cleanup pass.
 * The child never unlocks its copied mutex: every child path execs or exits.
 */
static pthread_mutex_t pty_spawn_lock = PTHREAD_MUTEX_INITIALIZER;

static int configure_master(int master_fd)
{
    int status_flags;
    do {
        status_flags = fcntl(master_fd, F_GETFL);
    } while (status_flags == -1 && errno == EINTR);

    if (status_flags == -1) {
        return errno;
    }

    int result;
    do {
        result = fcntl(master_fd, F_SETFL, status_flags | O_NONBLOCK);
    } while (result == -1 && errno == EINTR);

    if (result == -1) {
        return errno;
    }

    int descriptor_flags;
    do {
        descriptor_flags = fcntl(master_fd, F_GETFD);
    } while (descriptor_flags == -1 && errno == EINTR);

    if (descriptor_flags == -1) {
        return errno;
    }

    do {
        result = fcntl(master_fd, F_SETFD, descriptor_flags | FD_CLOEXEC);
    } while (result == -1 && errno == EINTR);

    return result == -1 ? errno : 0;
}

/*
 * Spawns a process connected to a pseudoterminal.
 *
 * argv and envp are null-terminated arrays. envp entries use KEY=VALUE form.
 * The returned error is from forkpty() or parent-side master configuration.
 */
PTY_EXPORT pty_spawn_result_t pty_spawn(
    const char* file,
    char* const argv[],
    char* const envp[],
    const char* working_dir,
    const pty_termios_t* termios_settings,
    const pty_winsize_t* winsize_settings)
{
    pty_spawn_result_t result = { -1, -1, 0 };

    struct termios term;
    struct termios* term_ptr = NULL;
    if (termios_settings != NULL) {
        memset(&term, 0, sizeof(term));
        term.c_iflag = termios_settings->c_iflag;
        term.c_oflag = termios_settings->c_oflag;
        term.c_cflag = termios_settings->c_cflag;
        term.c_lflag = termios_settings->c_lflag;

        size_t cc_size = sizeof(term.c_cc);
        if (cc_size > 32) {
            cc_size = 32;
        }

        memcpy(term.c_cc, termios_settings->c_cc, cc_size);
        cfsetispeed(&term, termios_settings->c_ispeed);
        cfsetospeed(&term, termios_settings->c_ospeed);
        term_ptr = &term;
    }

    struct winsize window_size;
    struct winsize* window_size_ptr = NULL;
    if (winsize_settings != NULL) {
        window_size.ws_row = winsize_settings->ws_row;
        window_size.ws_col = winsize_settings->ws_col;
        window_size.ws_xpixel = winsize_settings->ws_xpixel;
        window_size.ws_ypixel = winsize_settings->ws_ypixel;
        window_size_ptr = &window_size;
    }

    int master_fd = -1;
    pthread_mutex_lock(&pty_spawn_lock);
    pid_t pid = forkpty(&master_fd, NULL, term_ptr, window_size_ptr);
    int spawn_errno = errno;

    if (pid == -1) {
        pthread_mutex_unlock(&pty_spawn_lock);
        result.error = spawn_errno;
        return result;
    }

    if (pid == 0) {
        if (working_dir != NULL && working_dir[0] != '\0') {
            if (chdir(working_dir) == -1) {
                _exit(errno);
            }
        }

        if (getenv("TERM") == NULL) {
            setenv("TERM", "xterm-256color", 0);
        }

        if (envp != NULL) {
            for (int index = 0; envp[index] != NULL; index++) {
                char* separator = strchr(envp[index], '=');
                if (separator != NULL) {
                    size_t key_length = (size_t)(separator - envp[index]);
                    char* key = (char*)alloca(key_length + 1);
                    memcpy(key, envp[index], key_length);
                    key[key_length] = '\0';

                    const char* value = separator + 1;
                    if (value[0] == '\0') {
                        unsetenv(key);
                    } else {
                        setenv(key, value, 1);
                    }
                }
            }
        }

        execvp(file, argv);
        _exit(errno);
    }

    int configure_error = configure_master(master_fd);
    if (configure_error != 0) {
        kill(pid, SIGKILL);
        close(master_fd);

        int status;
        while (waitpid(pid, &status, 0) == -1 && errno == EINTR) {
        }

        pthread_mutex_unlock(&pty_spawn_lock);
        result.error = configure_error;
        return result;
    }

    pthread_mutex_unlock(&pty_spawn_lock);

    result.master_fd = master_fd;
    result.pid = pid;
    return result;
}

PTY_EXPORT int pty_configure_master(int master_fd)
{
    return configure_master(master_fd);
}

PTY_EXPORT int pty_reactor_create(
    uint64_t wake_token,
    int* epoll_fd_out,
    int* wake_fd_out)
{
    if (epoll_fd_out == NULL || wake_fd_out == NULL) {
        return EINVAL;
    }

    *epoll_fd_out = -1;
    *wake_fd_out = -1;

    int epoll_fd;
    do {
        epoll_fd = epoll_create1(EPOLL_CLOEXEC);
    } while (epoll_fd == -1 && errno == EINTR);

    if (epoll_fd == -1) {
        return errno;
    }

    int wake_fd;
    do {
        wake_fd = eventfd(0, EFD_CLOEXEC | EFD_NONBLOCK);
    } while (wake_fd == -1 && errno == EINTR);

    if (wake_fd == -1) {
        int error = errno;
        close(epoll_fd);
        return error;
    }

    struct epoll_event wake_event;
    memset(&wake_event, 0, sizeof(wake_event));
    wake_event.events = EPOLLIN;
    wake_event.data.u64 = wake_token;

    int result;
    do {
        result = epoll_ctl(epoll_fd, EPOLL_CTL_ADD, wake_fd, &wake_event);
    } while (result == -1 && errno == EINTR);

    if (result == -1) {
        int error = errno;
        close(wake_fd);
        close(epoll_fd);
        return error;
    }

    *epoll_fd_out = epoll_fd;
    *wake_fd_out = wake_fd;
    return 0;
}

PTY_EXPORT int pty_reactor_control(
    int epoll_fd,
    int operation,
    int monitored_fd,
    uint64_t token,
    uint32_t interests)
{
    int native_operation;
    switch (operation) {
        case PTY_REACTOR_ADD:
            native_operation = EPOLL_CTL_ADD;
            break;
        case PTY_REACTOR_MODIFY:
            native_operation = EPOLL_CTL_MOD;
            break;
        case PTY_REACTOR_DELETE:
            native_operation = EPOLL_CTL_DEL;
            break;
        default:
            return EINVAL;
    }

    struct epoll_event native_event;
    struct epoll_event* native_event_ptr = NULL;
    if (native_operation != EPOLL_CTL_DEL) {
        memset(&native_event, 0, sizeof(native_event));
        if ((interests & PTY_REACTOR_READ) != 0) {
            native_event.events |= EPOLLIN;
        }

        if ((interests & PTY_REACTOR_WRITE) != 0) {
            native_event.events |= EPOLLOUT;
        }

        native_event.data.u64 = token;
        native_event_ptr = &native_event;
    }

    int result;
    do {
        result = epoll_ctl(
            epoll_fd,
            native_operation,
            monitored_fd,
            native_event_ptr);
    } while (result == -1 && errno == EINTR);

    return result == -1 ? errno : 0;
}

PTY_EXPORT int pty_reactor_wait(
    int epoll_fd,
    pty_reactor_event_t* events,
    int capacity,
    int* count_out)
{
    if (events == NULL || count_out == NULL || capacity <= 0
        || capacity > PTY_REACTOR_MAX_EVENTS) {
        return EINVAL;
    }

    *count_out = 0;
    struct epoll_event native_events[PTY_REACTOR_MAX_EVENTS];

    int count;
    do {
        count = epoll_wait(epoll_fd, native_events, capacity, -1);
    } while (count == -1 && errno == EINTR);

    if (count == -1) {
        return errno;
    }

    for (int index = 0; index < count; index++) {
        uint32_t translated_events = 0;
        if ((native_events[index].events & EPOLLIN) != 0) {
            translated_events |= PTY_REACTOR_READ;
        }

        if ((native_events[index].events & EPOLLOUT) != 0) {
            translated_events |= PTY_REACTOR_WRITE;
        }

        if ((native_events[index].events & EPOLLERR) != 0) {
            translated_events |= PTY_REACTOR_ERROR;
        }

        if ((native_events[index].events & EPOLLHUP) != 0) {
            translated_events |= PTY_REACTOR_HANGUP;
        }

        events[index].token = native_events[index].data.u64;
        events[index].events = translated_events;
        events[index].reserved = 0;
    }

    *count_out = count;
    return 0;
}

PTY_EXPORT int pty_reactor_wake(int wake_fd)
{
    const uint64_t increment = 1;
    ssize_t written;
    do {
        written = write(wake_fd, &increment, sizeof(increment));
    } while (written == -1 && errno == EINTR);

    if (written == (ssize_t)sizeof(increment) || (written == -1 && errno == EAGAIN)) {
        return 0;
    }

    return written == -1 ? errno : EIO;
}

PTY_EXPORT int pty_reactor_drain(int wake_fd)
{
    uint64_t value;
    ssize_t count;
    do {
        count = read(wake_fd, &value, sizeof(value));
    } while (count == -1 && errno == EINTR);

    if (count == (ssize_t)sizeof(value) || (count == -1 && errno == EAGAIN)) {
        return 0;
    }

    return count == -1 ? errno : EIO;
}

PTY_EXPORT int pty_io_read(
    int master_fd,
    void* buffer,
    int length,
    int* transferred_out)
{
    if (buffer == NULL || length <= 0 || transferred_out == NULL) {
        return EINVAL;
    }

    *transferred_out = 0;
    ssize_t count;
    do {
        count = read(master_fd, buffer, (size_t)length);
    } while (count == -1 && errno == EINTR);

    if (count == -1) {
        return errno;
    }

    *transferred_out = (int)count;
    return 0;
}

PTY_EXPORT int pty_io_write(
    int master_fd,
    const void* buffer,
    int length,
    int* transferred_out)
{
    if (buffer == NULL || length <= 0 || transferred_out == NULL) {
        return EINVAL;
    }

    *transferred_out = 0;
    ssize_t count;
    do {
        count = write(master_fd, buffer, (size_t)length);
    } while (count == -1 && errno == EINTR);

    if (count == -1) {
        return errno;
    }

    *transferred_out = (int)count;
    return 0;
}

PTY_EXPORT int pty_resize(int master_fd, unsigned short rows, unsigned short cols)
{
    struct winsize window_size;
    window_size.ws_row = rows;
    window_size.ws_col = cols;
    window_size.ws_xpixel = 0;
    window_size.ws_ypixel = 0;

    return ioctl(master_fd, TIOCSWINSZ, &window_size);
}

PTY_EXPORT int pty_kill(int pid, int signal_number)
{
    return kill(pid, signal_number);
}

PTY_EXPORT int pty_waitpid(int pid, int* status, int options)
{
    return waitpid(pid, status, options);
}

PTY_EXPORT int pty_pidfd_open(int pid)
{
#ifdef SYS_pidfd_open
    int pid_fd;
    do {
        pid_fd = (int)syscall(SYS_pidfd_open, pid, 0);
    } while (pid_fd == -1 && errno == EINTR);

    return pid_fd;
#else
    (void)pid;
    errno = ENOSYS;
    return -1;
#endif
}

PTY_EXPORT int pty_pidfd_send_signal(int pid_fd, int signal_number)
{
#ifdef SYS_pidfd_send_signal
    int result;
    do {
        result = (int)syscall(
            SYS_pidfd_send_signal,
            pid_fd,
            signal_number,
            NULL,
            0);
    } while (result == -1 && errno == EINTR);

    return result;
#else
    (void)pid_fd;
    (void)signal_number;
    errno = ENOSYS;
    return -1;
#endif
}

PTY_EXPORT int pty_close(int master_fd)
{
    return close(master_fd);
}
