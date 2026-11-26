#ifndef ESURFING_LOG_PRIVATE_FILE_H
#define ESURFING_LOG_PRIVATE_FILE_H

/* POSIX-only: establish private permissions before returning a writable
 * stream. Never follow a final-component symlink or write a nonregular file.
 * This does not validate every ancestor directory or protect against root.
 */
#ifndef _WIN32
#include <fcntl.h>
#include <stdio.h>
#include <sys/stat.h>
#include <unistd.h>

static inline FILE* open_private_log_stream(const char* path)
{
    const int fd = open(path, O_WRONLY | O_APPEND | O_CREAT | O_NOFOLLOW |
        O_NONBLOCK | O_CLOEXEC, S_IRUSR | S_IWUSR);
    if (fd < 0) return NULL;

    struct stat info;
    if (fstat(fd, &info) != 0 || !S_ISREG(info.st_mode) ||
        info.st_uid != geteuid() || info.st_nlink != 1 ||
        fchmod(fd, S_IRUSR | S_IWUSR) != 0)
    {
        close(fd);
        return NULL;
    }

    FILE* stream = fdopen(fd, "a");
    if (stream == NULL) close(fd);
    return stream;
}
#endif
#endif
