/*
 * porta_pty.c - Native PTY shim for Porta.Pty
 *
 * This library keeps forkpty() and execvp() entirely in native code so no
 * managed .NET code runs in the forked child process.
 *
 * Copyright (c) Microsoft Corporation. All rights reserved.
 * Licensed under the MIT license.
 */

#include <errno.h>
#include <fcntl.h>
#include <limits.h>
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

/* Linux's P_PIDFD id type, including on headers predating Linux 5.4. */
#define PTY_P_PIDFD 3

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
    int master_fd;
    int pid;
    int pid_fd;
    int pid_fd_error;
    int error;
} pty_spawn_result_t;

typedef struct {
    int state;
    int exit_code;
    int signal_number;
    int error;
} pty_wait_result_t;

typedef struct {
    uint64_t token;
    uint32_t events;
    uint32_t reserved;
} pty_reactor_event_t;

typedef char pty_spawn_result_size_must_be_20[
    sizeof(pty_spawn_result_t) == 20 ? 1 : -1];
typedef char pty_spawn_result_pid_offset_must_be_4[
    offsetof(pty_spawn_result_t, pid) == 4 ? 1 : -1];
typedef char pty_spawn_result_pid_fd_offset_must_be_8[
    offsetof(pty_spawn_result_t, pid_fd) == 8 ? 1 : -1];
typedef char pty_spawn_result_pid_fd_error_offset_must_be_12[
    offsetof(pty_spawn_result_t, pid_fd_error) == 12 ? 1 : -1];
typedef char pty_spawn_result_error_offset_must_be_16[
    offsetof(pty_spawn_result_t, error) == 16 ? 1 : -1];

typedef char pty_wait_result_size_must_be_16[
    sizeof(pty_wait_result_t) == 16 ? 1 : -1];
typedef char pty_wait_result_exit_code_offset_must_be_4[
    offsetof(pty_wait_result_t, exit_code) == 4 ? 1 : -1];
typedef char pty_wait_result_signal_offset_must_be_8[
    offsetof(pty_wait_result_t, signal_number) == 8 ? 1 : -1];
typedef char pty_wait_result_error_offset_must_be_12[
    offsetof(pty_wait_result_t, error) == 12 ? 1 : -1];

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

enum {
    PTY_WAIT_RUNNING = 0,
    PTY_WAIT_EXITED = 1,
    PTY_WAIT_SIGNALED = 2,
    PTY_WAIT_FAILED = 3,
    PTY_WAIT_UNAVAILABLE = 4,
};

/*
 * Preserve the inherited Linux spawn serialization in this cleanup pass.
 * The child never unlocks its copied mutex: every child path execs or exits.
 */
static pthread_mutex_t pty_spawn_lock = PTHREAD_MUTEX_INITIALIZER;
static int pidfds_unavailable;
static int pidfds_unavailable_error;
static int pidfd_wait_supported;

extern char** environ;

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

static int open_pid_fd(pid_t pid)
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

static int probe_pid_fd_wait_support(void)
{
    siginfo_t info;
    memset(&info, 0, sizeof(info));

    int wait_result;
    do {
        wait_result = waitid(
            (idtype_t)PTY_P_PIDFD,
            (id_t)INT_MAX,
            &info,
            WEXITED | WNOHANG);
    } while (wait_result == -1 && errno == EINTR);

    /*
     * A kernel that recognizes P_PIDFD resolves the deliberately invalid file
     * descriptor and returns EBADF. Older kernels reject the id type with
     * EINVAL. Probe before pidfd_open so unsupported waiting never causes a
     * stable identity to be acquired and then downgraded to a numeric-PID wait.
     */
    if (wait_result == -1 && errno == EBADF) {
        return 0;
    }

    return wait_result == -1 ? errno : EIO;
}

static pty_wait_result_t wait_for_child(
    pid_t pid,
    int pid_fd,
    int non_blocking)
{
    pty_wait_result_t result = { PTY_WAIT_FAILED, 0, 0, 0 };
    if (pid_fd >= 0) {
        siginfo_t info;
        int wait_result;
        do {
            memset(&info, 0, sizeof(info));
            wait_result = waitid(
                (idtype_t)PTY_P_PIDFD,
                (id_t)pid_fd,
                &info,
                WEXITED | (non_blocking ? WNOHANG : 0));
        } while (wait_result == -1 && errno == EINTR);

        if (wait_result == -1) {
            result.error = errno;
            if (result.error == ECHILD) {
                result.state = PTY_WAIT_UNAVAILABLE;
            }
        } else if (info.si_pid == 0) {
            result.state = PTY_WAIT_RUNNING;
        } else if (info.si_code == CLD_EXITED) {
            result.state = PTY_WAIT_EXITED;
            result.exit_code = info.si_status;
        } else if (info.si_code == CLD_KILLED
            || info.si_code == CLD_DUMPED) {
            result.state = PTY_WAIT_SIGNALED;
            result.signal_number = info.si_status;
        } else {
            result.error = EIO;
        }

        return result;
    }

    int status = 0;
    pid_t waited_pid;
    do {
        waited_pid = waitpid(pid, &status, non_blocking ? WNOHANG : 0);
    } while (waited_pid == -1 && errno == EINTR);

    if (waited_pid == 0) {
        result.state = PTY_WAIT_RUNNING;
    } else if (waited_pid == -1) {
        result.error = errno;
        if (result.error == ECHILD) {
            result.state = PTY_WAIT_UNAVAILABLE;
        }
    } else if (WIFEXITED(status)) {
        result.state = PTY_WAIT_EXITED;
        result.exit_code = WEXITSTATUS(status);
    } else if (WIFSIGNALED(status)) {
        result.state = PTY_WAIT_SIGNALED;
        result.signal_number = WTERMSIG(status);
    } else {
        result.error = EIO;
    }

    return result;
}

static void free_environment(char** environment)
{
    if (environment == NULL) {
        return;
    }

    for (size_t index = 0; environment[index] != NULL; index++) {
        free(environment[index]);
    }

    free(environment);
}

static void remove_environment_key(
    char** environment,
    size_t* count,
    const char* key,
    size_t key_length)
{
    size_t index = 0;
    while (index < *count) {
        if (strncmp(environment[index], key, key_length) != 0
            || environment[index][key_length] != '=') {
            index++;
            continue;
        }

        free(environment[index]);
        (*count)--;
        memmove(
            &environment[index],
            &environment[index + 1],
            (*count - index + 1) * sizeof(char*));
    }
}

static int apply_environment_entry(
    char** environment,
    size_t* count,
    const char* entry,
    int empty_means_unset)
{
    const char* separator = strchr(entry, '=');
    if (separator == NULL || separator == entry) {
        return 0;
    }

    size_t key_length = (size_t)(separator - entry);
    remove_environment_key(environment, count, entry, key_length);
    if (empty_means_unset && separator[1] == '\0') {
        return 0;
    }

    char* copied_entry = strdup(entry);
    if (copied_entry == NULL) {
        return ENOMEM;
    }

    environment[*count] = copied_entry;
    (*count)++;
    environment[*count] = NULL;
    return 0;
}

static int prepare_environment(
    char* const inherited_environment[],
    char* const mutations[],
    char*** environment_out)
{
    size_t inherited_count = 0;
    if (inherited_environment != NULL) {
        while (inherited_environment[inherited_count] != NULL) {
            inherited_count++;
        }
    }

    size_t mutation_count = 0;
    if (mutations != NULL) {
        while (mutations[mutation_count] != NULL) {
            mutation_count++;
        }
    }

    if (inherited_count > SIZE_MAX - mutation_count) {
        return ENOMEM;
    }

    size_t entry_capacity = inherited_count + mutation_count;
    if (entry_capacity > SIZE_MAX - 2
        || entry_capacity + 2 > SIZE_MAX / sizeof(char*)) {
        return ENOMEM;
    }

    size_t capacity = entry_capacity + 2;
    char** environment = (char**)calloc(capacity, sizeof(char*));
    if (environment == NULL) {
        return ENOMEM;
    }

    size_t count = 0;
    int error = 0;
    for (size_t index = 0; error == 0 && index < inherited_count; index++) {
        error = apply_environment_entry(
            environment,
            &count,
            inherited_environment[index],
            0);
    }

    if (error == 0) {
        error = apply_environment_entry(
            environment,
            &count,
            "TERM=xterm-256color",
            0);
    }
    const char* fixed_unsets[] = {
        "TMUX",
        "TMUX_PANE",
        "STY",
        "WINDOW",
        "WINDOWID",
        "TERMCAP",
        "COLUMNS",
        "LINES",
    };
    for (size_t index = 0;
        error == 0 && index < sizeof(fixed_unsets) / sizeof(fixed_unsets[0]);
        index++) {
        remove_environment_key(
            environment,
            &count,
            fixed_unsets[index],
            strlen(fixed_unsets[index]));
    }

    for (size_t index = 0;
        error == 0 && mutations != NULL && mutations[index] != NULL;
        index++) {
        error = apply_environment_entry(
            environment,
            &count,
            mutations[index],
            1);
    }

    if (error != 0) {
        free_environment(environment);
        return error;
    }

    *environment_out = environment;
    return 0;
}

static void initialize_terminal_settings(struct termios* term)
{
    memset(term, 0, sizeof(*term));
    term->c_iflag = ICRNL | IXON | IXANY | IMAXBEL | BRKINT | IUTF8;
    term->c_oflag = 0;
    term->c_cflag = CREAD | CS8 | HUPCL;
    term->c_lflag = ICANON | ISIG | IEXTEN | ECHO | ECHOE | ECHOK
        | ECHOKE | ECHOCTL;
    term->c_cc[VEOF] = 4;
    term->c_cc[VEOL] = (cc_t)-1;
    term->c_cc[VEOL2] = (cc_t)-1;
    term->c_cc[VERASE] = 0x7f;
    term->c_cc[VWERASE] = 23;
    term->c_cc[VKILL] = 21;
    term->c_cc[VREPRINT] = 18;
    term->c_cc[VINTR] = 3;
    term->c_cc[VQUIT] = 0x1c;
    term->c_cc[VSUSP] = 26;
    term->c_cc[VSTART] = 17;
    term->c_cc[VSTOP] = 19;
    term->c_cc[VLNEXT] = 22;
    term->c_cc[VDISCARD] = 15;
    term->c_cc[VMIN] = 1;
    term->c_cc[VTIME] = 0;
    cfsetispeed(term, B38400);
    cfsetospeed(term, B38400);
}

/*
 * Spawns a process connected to a pseudoterminal.
 *
 * argv, inherited_environment, and environment_mutations are null-terminated
 * arrays. Environment entries use KEY=VALUE form. Empty inherited values are
 * preserved; an empty mutation value means unset.
 */
PTY_EXPORT pty_spawn_result_t pty_spawn(
    const char* file,
    char* const argv[],
    char* const inherited_environment[],
    char* const environment_mutations[],
    const char* working_dir,
    unsigned short rows,
    unsigned short cols)
{
    pty_spawn_result_t result = { -1, -1, -1, 0, 0 };

    struct termios term;
    initialize_terminal_settings(&term);

    struct winsize window_size;
    window_size.ws_row = rows;
    window_size.ws_col = cols;
    window_size.ws_xpixel = 0;
    window_size.ws_ypixel = 0;

    int master_fd = -1;
    char** child_environment = NULL;
    pthread_mutex_lock(&pty_spawn_lock);
    int environment_error = prepare_environment(
        inherited_environment,
        environment_mutations,
        &child_environment);
    if (environment_error != 0) {
        pthread_mutex_unlock(&pty_spawn_lock);
        result.error = environment_error;
        return result;
    }

    pid_t pid = forkpty(&master_fd, NULL, &term, &window_size);
    int spawn_errno = errno;

    if (pid == -1) {
        pthread_mutex_unlock(&pty_spawn_lock);
        free_environment(child_environment);
        result.error = spawn_errno;
        return result;
    }

    if (pid == 0) {
        environ = child_environment;
        if (working_dir != NULL && working_dir[0] != '\0') {
            if (chdir(working_dir) == -1) {
                _exit(errno);
            }
        }

        execvp(file, argv);
        _exit(errno);
    }

    int configure_error = configure_master(master_fd);
    if (configure_error != 0) {
        kill(pid, SIGKILL);
        close(master_fd);
        (void)wait_for_child(pid, -1, 0);

        pthread_mutex_unlock(&pty_spawn_lock);
        free_environment(child_environment);
        result.error = configure_error;
        return result;
    }

    int pid_fd = -1;
    int pid_fd_error = 0;
    if (pidfds_unavailable) {
        pid_fd_error = pidfds_unavailable_error;
    } else {
        if (!pidfd_wait_supported) {
            pid_fd_error = probe_pid_fd_wait_support();
            if (pid_fd_error == 0) {
                pidfd_wait_supported = 1;
            } else {
                pidfds_unavailable = 1;
                pidfds_unavailable_error = pid_fd_error;
            }
        }

        if (!pidfds_unavailable) {
            pid_fd = open_pid_fd(pid);
            if (pid_fd == -1) {
                pid_fd_error = errno;
                if (pid_fd_error == ENOSYS || pid_fd_error == EINVAL
                    || pid_fd_error == EPERM) {
                    pidfds_unavailable = 1;
                    pidfds_unavailable_error = pid_fd_error;
                }
            }
        }
    }

    pthread_mutex_unlock(&pty_spawn_lock);
    free_environment(child_environment);

    result.master_fd = master_fd;
    result.pid = pid;
    result.pid_fd = pid_fd;
    result.pid_fd_error = pid_fd_error;
    return result;
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

PTY_EXPORT pty_wait_result_t pty_wait_child(
    int pid,
    int pid_fd,
    int non_blocking)
{
    return wait_for_child(pid, pid_fd, non_blocking);
}

static int send_pid_fd_signal(int pid_fd, int signal_number)
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

PTY_EXPORT int pty_pidfd_send_signal(int pid_fd, int signal_number)
{
    return send_pid_fd_signal(pid_fd, signal_number);
}

PTY_EXPORT int pty_cleanup_untracked(int master_fd, int pid, int pid_fd)
{
    int first_error = 0;
    int signal_result = pid_fd >= 0
        ? send_pid_fd_signal(pid_fd, SIGKILL)
        : kill(pid, SIGKILL);
    if (signal_result == -1 && errno != ESRCH) {
        first_error = errno;
    }

    pty_wait_result_t wait_result = wait_for_child(pid, pid_fd, 0);
    if (wait_result.state == PTY_WAIT_FAILED
        && first_error == 0) {
        first_error = wait_result.error;
    }

    /* Never retry close after EINTR; either descriptor may already be reused. */
    if (master_fd >= 0 && close(master_fd) == -1 && first_error == 0) {
        first_error = errno;
    }

    if (pid_fd >= 0 && close(pid_fd) == -1 && first_error == 0) {
        first_error = errno;
    }

    return first_error;
}

PTY_EXPORT int pty_close(int master_fd)
{
    return close(master_fd);
}
