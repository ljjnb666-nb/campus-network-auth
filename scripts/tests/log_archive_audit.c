/* Standalone synthetic files, no network or project runtime. */
#include "LogArchiveFile.h"
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
int main(int argc, char** argv)
{
    assert(argc == 2 && chdir(argv[1]) == 0);
    make_file("active.log", "current");
    make_file("archive.log", "previous");
    assert(!archive_log_without_replace("active.log", "archive.log"));
    expect_file("active.log", "current");
    expect_file("archive.log", "previous");

    assert(archive_log_without_replace("active.log", "fresh.log"));
    expect_absent("active.log");
    expect_file("fresh.log", "current");

    assert(!archive_log_without_replace("missing.log", "new.log"));
    expect_absent("new.log");
    make_file("active.log", "again");
    assert(!archive_log_without_replace("active.log", "missing-dir/archive.log"));
    expect_file("active.log", "again");
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
    assert(!archive_log_without_replace("active.log", "existing-dir"));
    expect_file("active.log", "current");
    expect_file("existing-dir/sentinel", "keep");
    assert(symlink("missing-target", "dangling.log") == 0);
    assert(!archive_log_without_replace("active.log", "dangling.log"));
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
    return 0;
}
