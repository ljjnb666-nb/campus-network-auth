#ifndef ESURFING_LOG_PRIVATE_FILE_H
#define ESURFING_LOG_PRIVATE_FILE_H

/* Reject final-component links and nonregular files before returning a
 * writable stream. POSIX establishes owner-only permissions; Windows
 * inherits the directory ACL, not an independent ACL guarantee.
 * Ancestor directories are not independently validated.
 */
#ifdef _WIN32
#include "LogArchiveFile.h"
#include <fcntl.h>
#include <io.h>

static inline FILE* open_private_log_stream(const char* path)
{
    HANDLE handle = CreateFileA(path, GENERIC_WRITE | FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS,
        FILE_FLAG_OPEN_REPARSE_POINT, NULL);
    if (handle == INVALID_HANDLE_VALUE) return NULL;
    uint64_t dev = 0, ino = 0;
    if (!log_handle_id(handle, &dev, &ino))
    {
        CloseHandle(handle);
        return NULL;
    }
    const int fd = _open_osfhandle((intptr_t)handle, _O_WRONLY | _O_APPEND |
        _O_TEXT | _O_NOINHERIT);
    if (fd < 0)
    {
        CloseHandle(handle);
        return NULL;
    }
    FILE* stream = _fdopen(fd, "a");
    if (!stream) _close(fd);
    return stream;
}
#else
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
