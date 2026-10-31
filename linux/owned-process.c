/* Test-runner child owner: preserve the child's PID until explicitly reaped,
 * and signal it only through a pidfd. EOF cleans up if the runner dies. */
#define _GNU_SOURCE
#include <sys/pidfd.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <poll.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include <errno.h>
#include <fcntl.h>

static int write_number(const char *path, int value)
{
    FILE *file = fopen(path, "w");
    if (!file) return -1;
    int ok = fprintf(file, "%d\n", value) > 0;
    return fclose(file) == 0 && ok ? 0 : -1;
}

int main(int argc, char **argv)
{
    if (argc < 5) return 2;
    int log = open(argv[1], O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0600);
    if (log < 0) return 2;
    pid_t child = fork();
    if (child < 0) return 2;
    if (child == 0) {
        int devnull = open("/dev/null", O_RDONLY);
        if (devnull < 0 || dup2(devnull, STDIN_FILENO) < 0 ||
            dup2(log, STDOUT_FILENO) < 0 || dup2(log, STDERR_FILENO) < 0) _exit(127);
        close(devnull);
        close(log);
        execvp(argv[4], argv + 4);
        _exit(127);
    }
    close(log);
    int pidfd = pidfd_open(child, 0);
    if (pidfd < 0) {
        kill(child, SIGKILL); /* child has not been reaped, so PID is reserved */
        waitpid(child, NULL, 0);
        return 2;
    }
    if (write_number(argv[2], (int)child) != 0) {
        pidfd_send_signal(pidfd, SIGKILL, NULL, 0);
        waitpid(child, NULL, 0);
        return 2;
    }
    int observed = 0;
    for (;;) {
        siginfo_t info = {0};
        if (!observed && waitid(P_PID, child, &info, WEXITED | WNOHANG | WNOWAIT) == 0 && info.si_pid) {
            observed = 1;
            int rc = info.si_code == CLD_EXITED ? info.si_status : 128 + info.si_status;
            if (write_number(argv[3], rc) != 0) break;
        }
        struct pollfd input = { .fd = STDIN_FILENO, .events = POLLIN | POLLHUP };
        int ready = poll(&input, 1, 100);
        if (ready < 0 && errno == EINTR) continue;
        if (ready < 0) break;
        if (ready > 0) {
            char command;
            ssize_t count = read(STDIN_FILENO, &command, 1);
            if (count <= 0) break;
            if (command == 't') pidfd_send_signal(pidfd, SIGTERM, NULL, 0);
            if (command == 'k') pidfd_send_signal(pidfd, SIGKILL, NULL, 0);
            if (command == 'w' && observed) {
                int status;
                while (waitpid(child, &status, 0) < 0 && errno == EINTR) { }
                close(pidfd);
                return WIFEXITED(status) ? WEXITSTATUS(status) : 128 + WTERMSIG(status);
            }
        }
    }
    pidfd_send_signal(pidfd, SIGKILL, NULL, 0);
    waitpid(child, NULL, 0);
    close(pidfd);
    return 2;
}
