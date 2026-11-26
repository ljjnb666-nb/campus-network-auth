#ifndef ESURFING_LOG_ARCHIVE_FILE_H
#define ESURFING_LOG_ARCHIVE_FILE_H

/* Local diagnostic data preservation. Never replace an existing archive.
 * Unsupported filesystem/platform operations fail without a destructive
 * fallback. Windows verifies and renames the same open file.
 */
#include <stdbool.h>
#include <stdio.h>
#include <stdint.h>
#ifdef _WIN32
#include <stddef.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
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

#ifdef _WIN32
typedef struct
{
    WCHAR *name;
    HANDLE *directories;
    size_t count;
} log_pinned_path;

static inline void log_release_path(log_pinned_path *path)
{
    while (path->count != 0) CloseHandle(path->directories[--path->count]);
    free(path->directories);
    free(path->name);
}

static inline WCHAR *log_full_path(const char* source)
{
    const DWORD path_size = GetFullPathNameA(source, 0, NULL, NULL);
    if (path_size == 0 || path_size > 32768) return NULL;
    char* path = malloc(path_size);
    if (!path) return NULL;
    const DWORD path_length = GetFullPathNameA(source, path_size, path, NULL);
    const UINT code_page = AreFileApisANSI() ? CP_ACP : CP_OEMCP;
    const int name_size = path_length != 0 && path_length < path_size ?
        MultiByteToWideChar(code_page, 0, path, -1, NULL, 0) : 0;
    WCHAR* name = name_size > 0 ? malloc((size_t)name_size * sizeof(WCHAR)) : NULL;
    if (name && MultiByteToWideChar(code_page, 0, path, -1, name, name_size) != name_size)
    {
        free(name);
        name = NULL;
    }
    free(path);
    return name;
}

static inline WCHAR* log_root_end(WCHAR* name)
{
    if (name[1] == L':' && name[2] == L'\\') return name + 3;
    if (name[0] != L'\\' || name[1] != L'\\') return NULL;
    WCHAR* server = name + 2;
    if (server[0] == L'?' && server[1] == L'\\')
    {
        server += 2;
        if (wcslen(server) < 3) return NULL;
        if (server[1] == L':' && server[2] == L'\\') return server + 3;
        if (_wcsnicmp(server, L"UNC\\", 4) != 0) return NULL;
        server += 4;
    }
    else if (server[0] == L'.') return NULL; /* Win32 device namespace */
    WCHAR* server_end = wcschr(server, L'\\');
    return server_end ? wcschr(server_end + 1, L'\\') : NULL;
}

static inline bool log_pin_directories(const char* source, log_pinned_path *path, bool create)
{
    path->name = log_full_path(source);
    if (!path->name) return false;
    const size_t length = wcslen(path->name);
    if (length < 4 || path->name[length - 1] == L'\\') return false;

    WCHAR* root_end = log_root_end(path->name);
    if (!root_end) return false;

    size_t count = create ? 2 : 1;
    for (const WCHAR* p = root_end + 1; *p; p++) if (*p == L'\\') count++;
    path->directories = malloc(count * sizeof(HANDLE));
    if (!path->directories) return false;
    for (WCHAR* end = root_end; end;)
    {
        const WCHAR saved = *end;
        *end = L'\0';
        HANDLE dir = CreateFileW(path->name, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE,
            NULL, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, NULL);
        if (dir == INVALID_HANDLE_VALUE && create && end != root_end)
        {
            const DWORD error = GetLastError();
            if ((error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) &&
                (CreateDirectoryW(path->name, NULL) || GetLastError() == ERROR_ALREADY_EXISTS))
                dir = CreateFileW(path->name, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    NULL, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, NULL);
        }
        *end = saved;
        if (dir == INVALID_HANDLE_VALUE) return false;
        path->directories[path->count++] = dir;
        BY_HANDLE_FILE_INFORMATION info;
        if (!GetFileInformationByHandle(dir, &info) ||
            !(info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) ||
            (info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) return false;
        if (!saved) break;
        end = wcschr(end + 1, L'\\');
        if (!end && create) end = path->name + length;
    }
    return true;
}

static inline bool log_rename_handle(HANDLE handle, const WCHAR* destination)
{
    const size_t name_bytes = wcslen(destination) * sizeof(WCHAR);
    const DWORD info_size = (DWORD)(offsetof(FILE_RENAME_INFO, FileName) + name_bytes + sizeof(WCHAR));
    FILE_RENAME_INFO* info = calloc(1, info_size);
    if (!info) return false;
    info->FileNameLength = (DWORD)name_bytes;
    memcpy(info->FileName, destination, name_bytes);
    const bool ok = SetFileInformationByHandle(handle, FileRenameInfo, info, info_size) != 0;
    free(info);
    return ok;
}
#endif

static inline bool archive_log_without_replace(const char* source, const char* destination,
    uint64_t expected_dev, uint64_t expected_ino)
{
    if (expected_ino == 0) return false;
#ifdef _WIN32
    log_pinned_path from = {0}, to = {0};
    bool ok = false;
    if (log_pin_directories(source, &from, false) && log_pin_directories(destination, &to, false))
    {
        HANDLE handle = CreateFileW(from.name, DELETE | FILE_READ_ATTRIBUTES, FILE_SHARE_READ, NULL,
            OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, NULL);
        if (handle != INVALID_HANDLE_VALUE)
        {
            uint64_t dev = 0, ino = 0;
            ok = log_handle_id(handle, &dev, &ino) && dev == expected_dev && ino == expected_ino &&
                log_rename_handle(handle, to.name);
            CloseHandle(handle);
        }
    }
    log_release_path(&to);
    log_release_path(&from);
    return ok;
#else
    /* POSIX rename remains path-based; its identity check is advisory. */
    if (!log_path_matches_identity(source, expected_dev, expected_ino)) return false;
#if defined(__linux__) && defined(SYS_renameat2)
    /* Linux RENAME_NOREPLACE is the stable ABI flag value 1. */
    return syscall(SYS_renameat2, AT_FDCWD, source, AT_FDCWD, destination, 1U) == 0;
#elif defined(__APPLE__) && defined(RENAME_EXCL)
    return renamex_np(source, destination, RENAME_EXCL) == 0;
#else
    (void)source;
    (void)destination;
    return false;
#endif
#endif
}
#endif
