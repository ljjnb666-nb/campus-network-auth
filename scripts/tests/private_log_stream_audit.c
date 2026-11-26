/* Local synthetic files only. No project runtime or network. */
#include <fcntl.h>
#include <stdio.h>
#include <sys/stat.h>
#include <unistd.h>
#include <errno.h>
#include <assert.h>
#include <stdlib.h>
#include <string.h>

enum failure_point { NONE, STAT_FAIL, CHMOD_FAIL, STREAM_FAIL };
static enum failure_point failure;
static int observed_fd = -1;

static int fixture_fstat(int fd, struct stat* info)
{
    observed_fd = fd;
    if (failure == STAT_FAIL) { errno = EIO; return -1; }
    return fstat(fd, info);
}
static int fixture_fchmod(int fd, mode_t mode)
{
    if (failure == CHMOD_FAIL) { errno = EPERM; return -1; }
    return fchmod(fd, mode);
}
static FILE* fixture_fdopen(int fd, const char* mode)
{
    if (failure == STREAM_FAIL) { errno = ENOMEM; return NULL; }
    return fdopen(fd, mode);
}

#define fstat fixture_fstat
#define fchmod fixture_fchmod
#define fdopen fixture_fdopen
#include "LogPrivateFile.h"
#undef fstat
#undef fchmod
#undef fdopen

static void check_failed_descriptor(enum failure_point point)
{
    observed_fd = -1;
    failure = point;
    FILE* stream = open_private_log_stream("sample.log");
    failure = NONE;
    assert(stream == NULL && observed_fd >= 0);
    errno = 0;
    assert(fcntl(observed_fd, F_GETFD) == -1 && errno == EBADF);
}

int main(int argc, char** argv)
{
    assert(argc == 2);
    assert(chdir(argv[1]) == 0);
    umask(0);
    FILE* f = open_private_log_stream("sample.log");
    assert(f != NULL);
    int fd = fileno(f);
    struct stat info;
    assert(fstat(fd, &info) == 0 && (info.st_mode & 0777) == 0600);
    assert((fcntl(fd, F_GETFD) & FD_CLOEXEC) != 0);
    assert(fputs("first\n", f) >= 0 && fclose(f) == 0);
    assert(chmod("sample.log", 0644) == 0);
    f = open_private_log_stream("sample.log");
    assert(f != NULL);
    assert(fstat(fileno(f), &info) == 0 && (info.st_mode & 0777) == 0600);
    assert(fputs("second\n", f) >= 0 && fclose(f) == 0);
    f = fopen("sample.log", "r");
    assert(f != NULL);
    char buffer[32] = {0};
    assert(fread(buffer, 1, sizeof(buffer)-1, f) == 13);
    assert(strcmp(buffer, "first\nsecond\n") == 0);
    assert(fclose(f) == 0);

    assert(symlink("sample.log", "alias.log") == 0);
    assert(open_private_log_stream("alias.log") == NULL);
    assert(unlink("alias.log") == 0);
    assert(link("sample.log", "alias.log") == 0);
    assert(open_private_log_stream("alias.log") == NULL);
    assert(unlink("alias.log") == 0);
    assert(mkfifo("pipe.log", 0600) == 0);
    assert(open_private_log_stream("pipe.log") == NULL);
    assert(unlink("pipe.log") == 0);
    assert(open_private_log_stream(".") == NULL);
    assert(open_private_log_stream("missing/file.log") == NULL);
    check_failed_descriptor(STAT_FAIL);
    check_failed_descriptor(CHMOD_FAIL);
    check_failed_descriptor(STREAM_FAIL);
    assert(unlink("sample.log") == 0);
    puts("PRIVATE_LOG_FAILURE_CLEANUP_PASS cases=3");
    puts("PRIVATE_LOG_STREAM_PASS cases=8");
    return 0;
}
