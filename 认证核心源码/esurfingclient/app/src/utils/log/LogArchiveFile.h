#ifndef ESURFING_LOG_ARCHIVE_FILE_H
#define ESURFING_LOG_ARCHIVE_FILE_H

/* Local diagnostic data preservation. Never replace an existing archive.
 * Unsupported filesystem/platform operations fail without a destructive
 * fallback. This does not establish the identity of the source pathname.
 */
#include <stdbool.h>
#include <stdio.h>
#ifdef _WIN32
#include <windows.h>
#elif defined(__linux__)
#include <fcntl.h>
#include <sys/syscall.h>
#include <unistd.h>
#endif

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
