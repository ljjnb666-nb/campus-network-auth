#ifndef ESURFING_LOG_ARCHIVE_FILE_H
#define ESURFING_LOG_ARCHIVE_FILE_H

/* Local diagnostic data preservation. Never replace an existing archive.
 * Unsupported filesystem/platform operations fail without a destructive
 * fallback. This does not establish the identity of the source pathname.
 */
#include <stdbool.h>
#include <stdio.h>
#include <stdint.h>
#ifdef _WIN32
#include <windows.h>
#include <io.h>
#else
#include <sys/stat.h>
#include <unistd.h>
#endif
#ifdef __linux__
#include <fcntl.h>
#include <sys/syscall.h>
#include <unistd.h>
#endif

/* Observed identity does not make a subsequent pathname rename atomic. */
#ifdef _WIN32
static inline bool log_handle_id(HANDLE handle, uint64_t* dev, uint64_t* ino)
{
    BY_HANDLE_FILE_INFORMATION info;
    if (GetFileType(handle) != FILE_TYPE_DISK ||
        !GetFileInformationByHandle(handle, &info) ||
        (info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) ||
        info.nNumberOfLinks != 1) return false;
    const uint64_t id = ((uint64_t)info.nFileIndexHigh << 32) | info.nFileIndexLow;
    if (!id) return false;
    *dev = info.dwVolumeSerialNumber;
    *ino = id;
    return true;
}
#endif

static inline bool get_stream_id(FILE* stream, uint64_t* dev, uint64_t* ino)
{
    if (!stream) return false;
#ifdef _WIN32
    const int fd = _fileno(stream);
    if (fd < 0) return false;
    const intptr_t handle = _get_osfhandle(fd);
    if (handle == -1) return false;
    return log_handle_id((HANDLE)handle, dev, ino);
#else
    struct stat st;
    const int fd = fileno(stream);
    if (fd < 0 || fstat(fd, &st) != 0 || !S_ISREG(st.st_mode) ||
        st.st_nlink != 1 || !st.st_ino) return false;
    *dev = (uint64_t)st.st_dev;
    *ino = (uint64_t)st.st_ino;
    return true;
#endif
}

static inline bool get_file_id(const char* path, uint64_t* dev, uint64_t* ino)
{
#ifdef _WIN32
    HANDLE handle = CreateFileA(path, FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL,
        OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, NULL);
    if (handle == INVALID_HANDLE_VALUE) return false;
    const bool ok = log_handle_id(handle, dev, ino);
    CloseHandle(handle);
    return ok;
#else
    struct stat st;
    if (lstat(path, &st) != 0 || !S_ISREG(st.st_mode) ||
        st.st_nlink != 1 || !st.st_ino) return false;
    *dev = (uint64_t)st.st_dev;
    *ino = (uint64_t)st.st_ino;
    return true;
#endif
}

static inline bool log_path_matches_identity(const char* path, uint64_t dev, uint64_t ino)
{
    uint64_t actual_dev = 0, actual_ino = 0;
    return ino != 0 && get_file_id(path, &actual_dev, &actual_ino) &&
        actual_dev == dev && actual_ino == ino;
}

static inline bool archive_log_without_replace(const char* source, const char* destination)
{
#ifdef _WIN32
    return MoveFileExA(source, destination, 0) != 0;
#elif defined(__linux__) && defined(SYS_renameat2)
    /* Linux RENAME_NOREPLACE is the stable ABI flag value 1. */
    return syscall(SYS_renameat2, AT_FDCWD, source, AT_FDCWD, destination, 1U) == 0;
#elif defined(__APPLE__) && defined(RENAME_EXCL)
    return renamex_np(source, destination, RENAME_EXCL) == 0;
#else
    (void)source;
    (void)destination;
    return false;
#endif
}
#endif
