/* Local synthetic files only. No project runtime or network. */
#include "LogPrivateFile.h"
#include <assert.h>
#include <stdlib.h>
#include <string.h>

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
    assert(unlink("sample.log") == 0);
    puts("PRIVATE_LOG_STREAM_PASS cases=8");
    return 0;
}
