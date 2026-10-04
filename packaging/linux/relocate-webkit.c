/* Ubuntu's production WebKit ignores WEBKIT_EXEC_PATH. Relocate ONLY its three
 * compiled-in helper paths into a private mktemp directory, without changing
 * system libraries or disabling the WebKit sandbox. Input is our bundled ELF.
 * The caller owns a fresh mode-0700 directory; all replacements retain offsets.
 */
#define _GNU_SOURCE
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>

static void fail(const char *message) { fprintf(stderr, "WebKit relocation: %s\n", message); exit(1); }

int main(int argc, char **argv)
{
    if (argc != 3) fail("expected input library and private directory");
    const char *original[] = {"/usr/lib/x86_64-linux-gnu/webkit2gtk-4.1", "/usr/bin/bwrap", "/usr/bin/xdg-dbus-proxy"};
    const char *suffix[] = {"/w", "/b", "/d"};
    FILE *input = fopen(argv[1], "rb");
    if (!input || fseek(input, 0, SEEK_END)) fail("cannot read bundled library");
    long size = ftell(input);
    if (size < 4 || size > 512L * 1024 * 1024 || fseek(input, 0, SEEK_SET)) fail("invalid library size");
    char *data = malloc((size_t)size);
    if (!data || fread(data, 1, (size_t)size, input) != (size_t)size) fail("cannot read library");
    fclose(input);
    if (memcmp(data, "\177ELF", 4)) fail("not an ELF library");
    for (int i = 0; i < 3; ++i) {
        char replacement[256];
        int length = snprintf(replacement, sizeof replacement, "%s%s", argv[2], suffix[i]);
        size_t old_length = strlen(original[i]);
        if (length < 0 || (size_t)length > old_length) fail("private path is too long");
        int count = 0;
        for (size_t offset = 0; offset + old_length < (size_t)size; ++offset) {
            /* Only whole C strings; never rewrite prefixes or arbitrary code. */
            if ((offset == 0 || data[offset - 1] == 0) &&
                !memcmp(data + offset, original[i], old_length + 1)) {
                memset(data + offset, 0, old_length);
                memcpy(data + offset, replacement, (size_t)length);
                count++;
            }
        }
        if (!count) fail("upstream helper paths changed; rebuild packaging support");
    }
    char output_path[512];
    if (snprintf(output_path, sizeof output_path, "%s/libwebkit2gtk-4.1.so.0", argv[2]) >= (int)sizeof output_path)
        fail("output path is too long");
    FILE *output = fopen(output_path, "wbx");
    if (!output) fail("cannot create private library");
    if (fwrite(data, 1, (size_t)size, output) != (size_t)size || fclose(output)) fail("cannot write library");
    free(data);
    return 0;
}
