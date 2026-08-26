/*
 * porta_pty.c - Native PTY shim for Porta.Pty
 *
 * This library keeps fork() and execvp() entirely in native code so no
 * managed .NET code runs in the forked child process.
 *
 * On the pidfd-capable path the forked child opens its own pidfd and hands it
 * to the parent over a close-on-exec AF_UNIX socketpair before the parent
 * releases it to chdir and exec, so no descriptor is ever resolved from a
 * numeric PID that could already have been recycled.
 *
 * Copyright (c) Microsoft Corporation. All rights reserved.
 * Licensed under the MIT license.
 */

/* ptsname_r is a glibc extension guarded by __USE_GNU. */
#ifndef _GNU_SOURCE
#define _GNU_SOURCE
#endif

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
#include <sys/socket.h>
#include <sys/syscall.h>
#include <sys/wait.h>
#include <termios.h>
#include <unistd.h>
#include <utmp.h>

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
#ifndef SYS_close_range
#define SYS_close_range 436
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

enum {
    PTY_CONTROL_MAGIC = 0x50545943,
    PTY_CONTROL_RELEASE_MAGIC = 0x50545952,
    PTY_CONTROL_STATUS_PIDFD = 1,
    PTY_CONTROL_STATUS_NO_PIDFD = 2,
    PTY_CONTROL_STATUS_RELEASE = 3,
    PTY_CONTROL_STATUS_EXEC_FAILED = 4,
};

typedef struct {
    uint32_t magic;
    uint32_t status;
    int32_t error;
    int32_t pid;
} pty_control_message_t;

typedef char pty_control_message_size_must_be_16[
    sizeof(pty_control_message_t) == 16 ? 1 : -1];
typedef char pty_control_message_status_offset_must_be_4[
    offsetof(pty_control_message_t, status) == 4 ? 1 : -1];
typedef char pty_control_message_error_offset_must_be_8[
    offsetof(pty_control_message_t, error) == 8 ? 1 : -1];
typedef char pty_control_message_pid_offset_must_be_12[
    offsetof(pty_control_message_t, pid) == 12 ? 1 : -1];

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
    PTY_REACTOR_ONESHOT = 16,
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
static int pidfd_support_probed;
static int pidfd_support_error;

extern char** environ;

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

static int probe_pid_fd_support(void)
{
    int pid_fd = open_pid_fd(getpid());
    if (pid_fd == -1) {
        return errno == 0 ? ENOSYS : errno;
    }

    close(pid_fd);
    return probe_pid_fd_wait_support();
}

static int is_pidfd_unsupported_error(int error)
{
    return error == ENOSYS || error == EINVAL || error == EPERM;
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

/* Entries are borrowed, so only the pointer vector is owned here. */
static void free_environment(char** environment)
{
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

        (*count)--;
        memmove(
            &environment[index],
            &environment[index + 1],
            (*count - index + 1) * sizeof(char*));
    }
}

static void apply_environment_entry(
    char** environment,
    size_t* count,
    const char* entry,
    int empty_means_unset)
{
    const char* separator = strchr(entry, '=');
    if (separator == NULL || separator == entry) {
        return;
    }

    size_t key_length = (size_t)(separator - entry);
    remove_environment_key(environment, count, entry, key_length);
    if (empty_means_unset && separator[1] == '\0') {
        return;
    }

    environment[*count] = (char*)(uintptr_t)entry;
    (*count)++;
    environment[*count] = NULL;
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
    // Inherited keys are already unique on the managed side, so appending
    // directly avoids an O(n^2) rescan per entry before fork.
    // Entries are borrowed, never copied: the caller's strings outlive
    // pty_spawn and the forked child reads its copy-on-write view until exec.
    for (size_t index = 0; index < inherited_count; index++) {
        const char* entry = inherited_environment[index];
        const char* separator = strchr(entry, '=');
        if (separator == NULL || separator == entry) {
            continue;
        }

        environment[count] = (char*)(uintptr_t)entry;
        count++;
        environment[count] = NULL;
    }

    apply_environment_entry(environment, &count, "TERM=xterm-256color", 0);
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
        index < sizeof(fixed_unsets) / sizeof(fixed_unsets[0]);
        index++) {
        remove_environment_key(
            environment,
            &count,
            fixed_unsets[index],
            strlen(fixed_unsets[index]));
    }

    for (size_t index = 0;
        mutations != NULL && mutations[index] != NULL;
        index++) {
        apply_environment_entry(environment, &count, mutations[index], 1);
    }

    *environment_out = environment;
    return 0;
}

static void initialize_terminal_settings(struct termios* term)
{
    memset(term, 0, sizeof(*term));
    term->c_iflag = ICRNL | IXON | IXANY | IMAXBEL | BRKINT | IUTF8;
    term->c_oflag = OPOST | ONLCR;
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

static int reserve_control_descriptor(int* descriptor)
{
    /* login_tty() in the child dup2()s the slave onto 0, 1 and 2. */
    if (*descriptor >= 3) {
        return 0;
    }

    int reserved;
    do {
        reserved = fcntl(*descriptor, F_DUPFD_CLOEXEC, 3);
    } while (reserved == -1 && errno == EINTR);

    if (reserved == -1) {
        return errno;
    }

    close(*descriptor);
    *descriptor = reserved;
    return 0;
}

/*
 * openpty() creates both ends without O_CLOEXEC, so an unrelated fork+exec
 * elsewhere in the host process inherits the master for its whole lifetime.
 */
static int open_pty_pair(
    int* master_out,
    int* slave_out,
    const struct termios* term,
    const struct winsize* window_size)
{
    *master_out = -1;
    *slave_out = -1;

    int master_fd;
    do {
        master_fd = posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC | O_NONBLOCK);
    } while (master_fd == -1 && errno == EINTR);

    if (master_fd == -1) {
        return errno;
    }

    char slave_name[PATH_MAX];
    int error = 0;
    if (grantpt(master_fd) == -1 || unlockpt(master_fd) == -1) {
        error = errno;
    } else {
        error = ptsname_r(master_fd, slave_name, sizeof(slave_name));
    }

    if (error != 0) {
        close(master_fd);
        return error;
    }

    int slave_fd;
    do {
        slave_fd = open(slave_name, O_RDWR | O_NOCTTY | O_CLOEXEC);
    } while (slave_fd == -1 && errno == EINTR);

    if (slave_fd == -1) {
        error = errno;
        close(master_fd);
        return error;
    }

    /* login_tty's dup2 is a no-op on a matching fd, leaving FD_CLOEXEC set. */
    error = reserve_control_descriptor(&slave_fd);
    if (error != 0) {
        close(slave_fd);
        close(master_fd);
        return error;
    }

    int result;
    do {
        result = tcsetattr(slave_fd, TCSAFLUSH, term);
    } while (result == -1 && errno == EINTR);

    if (result == 0) {
        do {
            result = ioctl(slave_fd, TIOCSWINSZ, window_size);
        } while (result == -1 && errno == EINTR);
    }

    if (result == -1) {
        error = errno;
        close(slave_fd);
        close(master_fd);
        return error;
    }

    *master_out = master_fd;
    *slave_out = slave_fd;
    return 0;
}

/*
 * Runs in the forked child. exec resets caught signals but preserves SIG_IGN,
 * so without this the host's ignored dispositions (the .NET runtime ignores
 * SIGPIPE) leak into the terminal and everything it later spawns.
 */
static void reset_child_signals(void)
{
    struct sigaction action;
    memset(&action, 0, sizeof(action));
    action.sa_handler = SIG_DFL;
    sigemptyset(&action.sa_mask);

    for (int signal_number = 1; signal_number < NSIG; signal_number++) {
        (void)sigaction(signal_number, &action, NULL);
    }

    sigset_t empty;
    sigemptyset(&empty);
    (void)sigprocmask(SIG_SETMASK, &empty, NULL);
}

/*
 * Runs in the forked child, after every descriptor it still needs is wired.
 * keep_fd is the control endpoint, which stays open (and CLOEXEC) so a failed
 * exec can still report its errno; it is always at least 3.
 */
static void close_inherited_descriptors(int keep_fd)
{
#ifdef SYS_close_range
    int result = 0;
    if (keep_fd > 3) {
        do {
            result = (int)syscall(
                SYS_close_range,
                3U,
                (unsigned int)(keep_fd - 1),
                0U);
        } while (result == -1 && errno == EINTR);
    }

    if (result == 0) {
        do {
            result = (int)syscall(
                SYS_close_range,
                (unsigned int)(keep_fd + 1),
                ~0U,
                0U);
        } while (result == -1 && errno == EINTR);
    }

    if (result == 0) {
        return;
    }
#endif

    long limit = sysconf(_SC_OPEN_MAX);
    if (limit < 0) {
        limit = 4096;
    }

    for (int descriptor = 3; descriptor < (int)limit; descriptor++) {
        if (descriptor != keep_fd) {
            (void)close(descriptor);
        }
    }
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

static int create_control_channel(int descriptors[2])
{
    int result;
    do {
        result = socketpair(
            AF_UNIX,
            SOCK_SEQPACKET | SOCK_CLOEXEC,
            0,
            descriptors);
    } while (result == -1 && errno == EINTR);

    if (result == -1) {
        return errno;
    }

    for (int index = 0; index < 2; index++) {
        int error = reserve_control_descriptor(&descriptors[index]);
        if (error != 0) {
            close(descriptors[0]);
            close(descriptors[1]);
            return error;
        }
    }

    return 0;
}

static int send_control_message(
    int socket_fd,
    const pty_control_message_t* message,
    int transferred_fd)
{
    struct iovec io;
    io.iov_base = (void*)(uintptr_t)message;
    io.iov_len = sizeof(*message);

    union {
        struct cmsghdr alignment;
        char bytes[CMSG_SPACE(sizeof(int))];
    } control;
    memset(&control, 0, sizeof(control));

    struct msghdr header;
    memset(&header, 0, sizeof(header));
    header.msg_iov = &io;
    header.msg_iovlen = 1;

    if (transferred_fd >= 0) {
        header.msg_control = control.bytes;
        header.msg_controllen = sizeof(control.bytes);

        struct cmsghdr* cmsg = CMSG_FIRSTHDR(&header);
        cmsg->cmsg_level = SOL_SOCKET;
        cmsg->cmsg_type = SCM_RIGHTS;
        cmsg->cmsg_len = CMSG_LEN(sizeof(int));
        memcpy(CMSG_DATA(cmsg), &transferred_fd, sizeof(transferred_fd));
    }

    ssize_t sent;
    do {
        sent = sendmsg(socket_fd, &header, MSG_NOSIGNAL);
    } while (sent == -1 && errno == EINTR);

    if (sent == -1) {
        return errno;
    }

    return sent == (ssize_t)sizeof(*message) ? 0 : EPROTO;
}

static int receive_control_message(
    int socket_fd,
    pty_control_message_t* message,
    int* received_fd)
{
    memset(message, 0, sizeof(*message));
    *received_fd = -1;

    struct iovec io;
    io.iov_base = message;
    io.iov_len = sizeof(*message);

    /* Room for two descriptors so an unexpected extra one is observable. */
    union {
        struct cmsghdr alignment;
        char bytes[CMSG_SPACE(sizeof(int) * 2)];
    } control;
    memset(&control, 0, sizeof(control));

    struct msghdr header;
    memset(&header, 0, sizeof(header));
    header.msg_iov = &io;
    header.msg_iovlen = 1;
    header.msg_control = control.bytes;
    header.msg_controllen = sizeof(control.bytes);

    ssize_t received;
    do {
        received = recvmsg(socket_fd, &header, MSG_CMSG_CLOEXEC);
    } while (received == -1 && errno == EINTR);

    if (received == -1) {
        return errno;
    }

    int error = 0;
    for (struct cmsghdr* cmsg = CMSG_FIRSTHDR(&header);
        cmsg != NULL;
        cmsg = CMSG_NXTHDR(&header, cmsg)) {
        if (cmsg->cmsg_level != SOL_SOCKET || cmsg->cmsg_type != SCM_RIGHTS) {
            if (error == 0) {
                error = EPROTO;
            }

            continue;
        }

        size_t payload_length = (size_t)cmsg->cmsg_len - CMSG_LEN(0);
        if (payload_length % sizeof(int) != 0 && error == 0) {
            error = EPROTO;
        }

        for (size_t offset = 0;
            offset + sizeof(int) <= payload_length;
            offset += sizeof(int)) {
            int descriptor;
            memcpy(&descriptor, CMSG_DATA(cmsg) + offset, sizeof(descriptor));
            if (error == 0 && *received_fd < 0) {
                *received_fd = descriptor;
                continue;
            }

            close(descriptor);
            if (error == 0) {
                error = EPROTO;
            }
        }
    }

    if ((header.msg_flags & (MSG_TRUNC | MSG_CTRUNC)) != 0 && error == 0) {
        error = EPROTO;
    }

    if (received == 0) {
        if (error == 0) {
            error = EPIPE;
        }
    } else if (received != (ssize_t)sizeof(*message) && error == 0) {
        error = EPROTO;
    }

    if (error != 0 && *received_fd >= 0) {
        close(*received_fd);
        *received_fd = -1;
    }

    return error;
}

static int validate_control_message(
    const pty_control_message_t* message,
    pid_t pid,
    int received_fd)
{
    if (message->magic != PTY_CONTROL_MAGIC
        || message->pid != (int32_t)pid) {
        return EPROTO;
    }

    if (message->status == PTY_CONTROL_STATUS_PIDFD) {
        return received_fd >= 0 && message->error == 0 ? 0 : EPROTO;
    }

    if (message->status == PTY_CONTROL_STATUS_NO_PIDFD) {
        return received_fd < 0 && message->error != 0 ? 0 : EPROTO;
    }

    return EPROTO;
}

static int send_release_message(int socket_fd)
{
    pty_control_message_t message;
    memset(&message, 0, sizeof(message));
    message.magic = PTY_CONTROL_RELEASE_MAGIC;
    message.status = PTY_CONTROL_STATUS_RELEASE;
    return send_control_message(socket_fd, &message, -1);
}

static int receive_release_message(int socket_fd)
{
    pty_control_message_t message;
    int received_fd = -1;
    int error = receive_control_message(socket_fd, &message, &received_fd);
    if (received_fd >= 0) {
        close(received_fd);
        if (error == 0) {
            error = EPROTO;
        }
    }

    if (error != 0) {
        return error;
    }

    if (message.magic != PTY_CONTROL_RELEASE_MAGIC
        || message.status != PTY_CONTROL_STATUS_RELEASE) {
        return EPROTO;
    }

    return 0;
}

/*
 * Runs in the forked child: nothing here may allocate, use stdio or lock,
 * because the child inherited the parent's libc locks in an arbitrary state.
 */
static int perform_child_handshake(
    int control_fd,
    int acquire_pid_fd,
    int unsupported_error)
{
    pty_control_message_t message;
    memset(&message, 0, sizeof(message));
    message.magic = PTY_CONTROL_MAGIC;
    message.status = PTY_CONTROL_STATUS_NO_PIDFD;
    message.error = unsupported_error;
    message.pid = (int32_t)getpid();

    int pid_fd = -1;
    if (acquire_pid_fd != 0) {
        pid_fd = open_pid_fd(getpid());
        if (pid_fd == -1) {
            message.error = errno == 0 ? ENOSYS : errno;
        } else {
            message.status = PTY_CONTROL_STATUS_PIDFD;
            message.error = 0;
        }
    }

    int send_error = send_control_message(control_fd, &message, pid_fd);
    if (pid_fd >= 0) {
        /* The in-flight SCM_RIGHTS copy holds the kernel reference. */
        close(pid_fd);
    }

    if (send_error != 0) {
        return send_error;
    }

    return receive_release_message(control_fd);
}

/*
 * Runs in the forked child once the control endpoint is the only channel left:
 * a successful exec closes it (CLOEXEC) and the parent sees EOF instead.
 */
static __attribute__((noreturn)) void report_child_exec_failure(
    int control_fd,
    int failure_errno)
{
    pty_control_message_t message;
    memset(&message, 0, sizeof(message));
    message.magic = PTY_CONTROL_MAGIC;
    message.status = PTY_CONTROL_STATUS_EXEC_FAILED;
    message.error = failure_errno;
    message.pid = (int32_t)getpid();
    (void)send_control_message(control_fd, &message, -1);
    _exit(failure_errno);
}

static void abort_spawned_child(
    pid_t pid,
    int master_fd,
    int pid_fd,
    int control_fd,
    int child_held)
{
    if (pid_fd >= 0) {
        (void)send_pid_fd_signal(pid_fd, SIGKILL);
    } else if (child_held != 0) {
        (void)kill(pid, SIGKILL);
    }

    /* Without either, the child already exited and its PID may be recycled. */

    if (control_fd >= 0) {
        close(control_fd);
    }

    (void)wait_for_child(pid, pid_fd, 0);

    if (master_fd >= 0) {
        close(master_fd);
    }

    if (pid_fd >= 0) {
        close(pid_fd);
    }
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

    if (!pidfd_support_probed) {
        int probe_error = probe_pid_fd_support();
        if (probe_error != 0 && !is_pidfd_unsupported_error(probe_error)) {
            /* Transient: latching it would strand a capable process on PIDs. */
            pthread_mutex_unlock(&pty_spawn_lock);
            free_environment(child_environment);
            result.error = probe_error;
            return result;
        }

        pidfd_support_error = probe_error;
        pidfd_support_probed = 1;
    }

    /* The child must read only its own stack copies of these. */
    int unsupported_error = pidfd_support_error;
    int acquire_pid_fd = unsupported_error == 0;

    int descriptors[2];
    int channel_error = create_control_channel(descriptors);
    if (channel_error != 0) {
        pthread_mutex_unlock(&pty_spawn_lock);
        free_environment(child_environment);
        result.error = channel_error;
        return result;
    }

    int slave_fd = -1;
    int pty_error = open_pty_pair(&master_fd, &slave_fd, &term, &window_size);
    if (pty_error != 0) {
        close(descriptors[0]);
        close(descriptors[1]);
        pthread_mutex_unlock(&pty_spawn_lock);
        free_environment(child_environment);
        result.error = pty_error;
        return result;
    }

    pid_t pid = fork();
    int spawn_errno = errno;

    if (pid == -1) {
        close(slave_fd);
        close(master_fd);
        close(descriptors[0]);
        close(descriptors[1]);
        pthread_mutex_unlock(&pty_spawn_lock);
        free_environment(child_environment);
        result.error = spawn_errno;
        return result;
    }

    if (pid == 0) {
        close(master_fd);
        /* login_tty's dup2 onto 0, 1 and 2 clears FD_CLOEXEC on the copies. */
        if (login_tty(slave_fd) == -1) {
            _exit(errno);
        }

        close(descriptors[0]);
        int handshake_error = perform_child_handshake(
            descriptors[1],
            acquire_pid_fd,
            unsupported_error);
        if (handshake_error != 0) {
            _exit(handshake_error);
        }

        close_inherited_descriptors(descriptors[1]);
        reset_child_signals();
        environ = child_environment;
        if (working_dir != NULL && working_dir[0] != '\0') {
            if (chdir(working_dir) == -1) {
                report_child_exec_failure(descriptors[1], errno);
            }
        }

        execvp(file, argv);
        report_child_exec_failure(descriptors[1], errno);
    }

    close(slave_fd);

    /* Closing the parent's copy of the child endpoint makes its EOF visible. */
    close(descriptors[1]);
    descriptors[1] = -1;

    pty_control_message_t message;
    int received_fd = -1;
    int error = receive_control_message(descriptors[0], &message, &received_fd);

    /* Only a zero-length receive proves the child could already have exited. */
    int child_held = error != EPIPE && error != ECONNRESET;
    if (error == 0) {
        error = validate_control_message(&message, pid, received_fd);
        if (error != 0 && received_fd >= 0) {
            close(received_fd);
            received_fd = -1;
        }
    }

    int pid_fd = received_fd;
    int pid_fd_error = 0;
    if (error == 0 && message.status == PTY_CONTROL_STATUS_NO_PIDFD) {
        pid_fd_error = message.error;
        if (acquire_pid_fd != 0 && !is_pidfd_unsupported_error(pid_fd_error)) {
            /* Proven support plus a local failure must not silently downgrade. */
            error = pid_fd_error;
        } else {
            pidfd_support_error = pid_fd_error;
        }
    }

    if (error == 0) {
        /* The release fence proves the child is still parked pre-exec whenever
         * an abort path must fall back to the numeric PID. */
        error = send_release_message(descriptors[0]);
        if (error == EPIPE || error == ECONNRESET) {
            /* The child endpoint is gone, so the child may already be reaped. */
            child_held = 0;
        }
    }

    if (error == 0) {
        /* CLOEXEC closes the child endpoint on a successful exec, so EOF here
         * is the only proof exec happened; a message means it failed. */
        pty_control_message_t exec_message;
        int exec_fd = -1;
        int exec_error = receive_control_message(
            descriptors[0],
            &exec_message,
            &exec_fd);
        if (exec_error == EPIPE || exec_error == ECONNRESET) {
            child_held = 0;
        } else if (exec_error != 0) {
            error = exec_error;
        } else if (exec_fd >= 0) {
            close(exec_fd);
            error = EPROTO;
        } else if (exec_message.magic == PTY_CONTROL_MAGIC
            && exec_message.status == PTY_CONTROL_STATUS_EXEC_FAILED
            && exec_message.pid == (int32_t)pid) {
            error = exec_message.error != 0 ? exec_message.error : EIO;
        } else {
            error = EPROTO;
        }
    }

    if (error != 0) {
        abort_spawned_child(pid, master_fd, pid_fd, descriptors[0], child_held);
        pthread_mutex_unlock(&pty_spawn_lock);
        free_environment(child_environment);
        result.error = error;
        return result;
    }

    close(descriptors[0]);
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

        if ((interests & PTY_REACTOR_ONESHOT) != 0) {
            native_event.events |= EPOLLONESHOT;
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
