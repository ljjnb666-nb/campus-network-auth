/* Standalone synthetic files, no network or project runtime. */
#include "LogArchiveFile.h"
#include "LogPrivateFile.h"
#include <assert.h>
#include <string.h>
#ifdef _WIN32
#include <direct.h>
#define chdir _chdir
#else
#include <sys/stat.h>
#include <unistd.h>
#endif

static void make_file(const char* path, const char* value)
{
    FILE* f = fopen(path, "wb");
    assert(f != NULL);
    assert(fputs(value, f) >= 0);
    assert(fclose(f) == 0);
}
static void expect_file(const char* path, const char* value)
{
    FILE* f = fopen(path, "rb");
    char data[64] = {0};
    assert(f != NULL);
    assert(fread(data, 1, sizeof(data)-1, f) == strlen(value));
    assert(strcmp(data, value) == 0);
    assert(fclose(f) == 0);
}
static void expect_absent(const char* path)
{
    FILE* f = fopen(path, "rb");
    if (f != NULL) fclose(f);
    assert(f == NULL);
}
static bool archive_current(const char* source, const char* destination)
{
    uint64_t dev = 0, ino = 0;
    return get_file_id(source, &dev, &ino) &&
        archive_log_without_replace(source, destination, dev, ino);
}
int main(int argc, char** argv)
{
#ifdef _WIN32
    if (argc == 3 && strcmp(argv[2], "check-junction") == 0)
    {
        assert(chdir(argv[1]) == 0);
        make_file("ordinary.log", "preserve");
        log_pinned_path checked = {0};
        assert(!log_pin_directories("mapped/missing", &checked, true));
        log_release_path(&checked);
        assert(!open_private_log_stream("mapped/new.log"));
        assert(!archive_current("ordinary.log", "mapped/archive.log"));
        expect_file("ordinary.log", "preserve");
        assert(GetFileAttributesA("plain/missing") == INVALID_FILE_ATTRIBUTES);
        expect_absent("plain/new.log");
        expect_absent("plain/archive.log");
        puts("LOCAL_LOG_ANCESTOR_JUNCTION_REJECTED_PASS");
        return 0;
    }
#endif
    assert(argc == 2 && chdir(argv[1]) == 0);
#ifdef _WIN32
    log_pinned_path checked = {0};
    assert(log_pin_directories("folder/nested", &checked, true));
    const DWORD attrs = GetFileAttributesA("folder/nested");
    assert(attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY));
    log_release_path(&checked);
    assert(RemoveDirectoryA("folder/nested"));
    assert(RemoveDirectoryA("folder"));
#endif
    make_file("active.log", "current");
    make_file("archive.log", "previous");
    assert(!archive_current("active.log", "archive.log"));
    expect_file("active.log", "current");
    expect_file("archive.log", "previous");

    assert(archive_current("active.log", "fresh.log"));
    expect_absent("active.log");
    expect_file("fresh.log", "current");

    assert(!archive_current("missing.log", "new.log"));
    expect_absent("new.log");
    make_file("active.log", "again");
    assert(!archive_current("active.log", "missing-dir/archive.log"));
    expect_file("active.log", "again");
#ifdef _WIN32
    HANDLE reader = CreateFileA("active.log", GENERIC_READ, FILE_SHARE_READ, NULL,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    assert(reader != INVALID_HANDLE_VALUE);
    assert(!archive_current("active.log", "held.log"));
    assert(CloseHandle(reader));
    expect_file("active.log", "again");
    expect_absent("held.log");
    puts("LOCAL_LOG_ARCHIVE_SHARING_DENIAL_PASS");
#endif
    assert(remove("active.log") == 0);
    assert(remove("archive.log") == 0);
    assert(remove("fresh.log") == 0);
    puts("LOCAL_LOG_ARCHIVE_NO_REPLACE_PASS cases=4");
#ifndef _WIN32
    /* A directory or dangling symlink occupies a destination even when
     * fopen on the link would report that its target does not exist. */
    make_file("active.log", "current");
    assert(mkdir("existing-dir", 0700) == 0);
    make_file("existing-dir/sentinel", "keep");
    assert(!archive_current("active.log", "existing-dir"));
    expect_file("active.log", "current");
    expect_file("existing-dir/sentinel", "keep");
    assert(symlink("missing-target", "dangling.log") == 0);
    assert(!archive_current("active.log", "dangling.log"));
    expect_file("active.log", "current");
    char target[64] = {0};
    assert(readlink("dangling.log", target, sizeof(target)) ==
        (ssize_t)strlen("missing-target"));
    assert(strcmp(target, "missing-target") == 0);
    expect_absent("missing-target");
    assert(unlink("dangling.log") == 0);
    assert(remove("active.log") == 0);
    assert(remove("existing-dir/sentinel") == 0);
    assert(rmdir("existing-dir") == 0);
    puts("LOCAL_ARCHIVE_DESTINATION_TYPES_PASS cases=2");
#endif
    /* Preserve the original file under its archived name, then recreate
     * the active path. A stale identity must not authorize that move. */
    FILE* stream = open_private_log_stream("identity.log");
    assert(stream);
    assert(fputs("one", stream) >= 0);
    uint64_t dev = 0, ino = 0;
    assert(get_stream_id(stream, &dev, &ino));
    assert(log_path_matches_identity("identity.log", dev, ino));
    assert(!log_path_matches_identity("identity.log", dev, 0));
    assert(fclose(stream) == 0);
    stream = open_private_log_stream("identity.log");
    assert(stream && fputs("two", stream) >= 0 && fclose(stream) == 0);
    expect_file("identity.log", "onetwo");
    assert(archive_log_without_replace("identity.log", "identity-old.log", dev, ino));
    assert(!log_path_matches_identity("identity.log", dev, ino));
    make_file("identity.log", "new");
    assert(!log_path_matches_identity("identity.log", dev, ino));
    assert(!archive_log_without_replace("identity.log", "unexpected.log", dev, ino));
    expect_absent("unexpected.log");
    assert(log_path_matches_identity("identity-old.log", dev, ino));
    expect_file("identity.log", "new");
    expect_file("identity-old.log", "onetwo");
#ifdef _WIN32
    assert(CreateHardLinkA("identity-link.log", "identity.log", NULL));
#else
    assert(link("identity.log", "identity-link.log") == 0);
#endif
    assert(!open_private_log_stream("identity-link.log"));
    assert(!get_file_id("identity.log", &dev, &ino));
    expect_file("identity.log", "new");
    assert(remove("identity-link.log") == 0);
    assert(remove("identity.log") == 0);
    assert(remove("identity-old.log") == 0);
    puts("LOCAL_LOG_IDENTITY_AND_APPEND_PASS");
    return 0;
}
