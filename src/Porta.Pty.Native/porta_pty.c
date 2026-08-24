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
#include <pthread.h>
#include <pty.h>
#include <signal.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/wait.h>
#include <termios.h>
#include <unistd.h>

#define PTY_EXPORT __attribute__((visibility("default")))

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

/*
 * Preserve the inherited Linux spawn serialization in this cleanup pass.
 * The child never unlocks its copied mutex: every child path execs or exits.
 */
static pthread_mutex_t pty_spawn_lock = PTHREAD_MUTEX_INITIALIZER;

/*
 * Spawns a process connected to a pseudoterminal.
 *
 * argv and envp are null-terminated arrays. envp entries use KEY=VALUE form.
 * The returned error is errno captured immediately after forkpty().
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
    if (pid != 0) {
        pthread_mutex_unlock(&pty_spawn_lock);
    }

    if (pid == -1) {
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

    result.master_fd = master_fd;
    result.pid = pid;
    return result;
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

PTY_EXPORT int pty_close(int master_fd)
{
    return close(master_fd);
}
