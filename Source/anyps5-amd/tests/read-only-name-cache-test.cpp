// Native test of ReadOnlyNameCache (Source/anyps5-amd/0001), on Linux, against the patched AnyPS5 tree:
//   g++ -std=c++20 -Wall -Wextra -I <anyps5>/core/libs/prx/libc/include read-only-name-cache-test.cpp -o t && ./t <scratch dir>
// Native (Linux) test of ReadOnlyNameCache's lookup logic, with a std::filesystem lister standing in for
// the Windows build's FindFirstFileExW one.
#include "ReadOnlyNameCache.hpp"

#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>
#include <unistd.h>

namespace fs = std::filesystem;

static int failures = 0;
static int listings = 0;

static void Check(bool ok, const std::string& what) {
    std::printf("%s %s\n", ok ? "ok  " : "FAIL", what.c_str());
    if (!ok) ++failures;
}

static void Touch(const fs::path& p) {
    fs::create_directories(p.parent_path());
    std::ofstream(p) << "x";
}

static std::optional<std::vector<ReadOnlyNameCache::Entry>> List(const fs::path& directory) {
    ++listings;
    std::error_code error;
    fs::directory_iterator it(directory, error), end;
    if (error) return std::nullopt;
    std::vector<ReadOnlyNameCache::Entry> entries;
    for (; it != end; it.increment(error)) {
        if (error) return std::nullopt;
        entries.push_back({it->path().filename().string(), it->path().filename()});
    }
    return entries;
}

int main(int argc, char** argv) {
    if (argc < 2) { std::fprintf(stderr, "usage: test <scratch directory>\n"); return 2; }
    const auto base = fs::path(argv[1]) / ("namecache-test-" + std::to_string(::getpid()));
    const auto root = base / "RunHybrid";
    const auto dump = base / "dump";
    const auto absent = root / "_aps5_absent";
    fs::create_directories(absent);
    Touch(dump / "data" / "prein" / "bg" / "odx" / "Foo.odxb");
    Touch(dump / "data" / "prein" / "bg" / "odx" / "bar.odxb");
    Touch(dump / "data" / "prein" / "twin" / "A.txt");
    Touch(dump / "data" / "prein" / "twin" / "a.txt");
    Touch(root / "app0" / "sce_sys" / "param.json");
    Touch(root / "app0" / "sce_module" / "libfoo.sprx.GUEST.PRX");
    Touch(root / "app0" / "sce_module" / "libbar.sprx");
    Touch(root / "app0" / "sce_module" / "libbar.sprx.guest.prx");
    Touch(root / "app0" / "sce_module" / "dir.guest.prx");
    fs::create_directory_symlink(dump / "data", root / "app0" / "data");   // as the Linux setup links app0

    ReadOnlyNameCache cache(List);
    using V = std::vector<std::string>;
    auto resolve = [&](V names) { return cache.Resolve(root, names, absent); };

    auto hit = resolve({"app0", "data", "prein", "bg", "odx", "foo.ODXB"});
    Check(hit && *hit == root / "app0" / "data" / "prein" / "bg" / "odx" / "Foo.odxb", "a name found keeps its on-disk spelling, through the data symlink");
    Check(hit && fs::is_regular_file(*hit), "and names a real file");

    auto exact = resolve({"app0", "data", "prein", "bg", "odx", "bar.odxb"});
    Check(exact && *exact == root / "app0" / "data" / "prein" / "bg" / "odx" / "bar.odxb", "exact spelling");

    auto miss = resolve({"APP0", "Data", "prein", "bg", "odx", "missing.odxb"});
    Check(miss && *miss == absent / "missing.odxb", "a missing name resolves into the empty directory");
    Check(miss && !fs::exists(*miss) && fs::is_directory(miss->parent_path()), "where it does not exist, under an existing directory (ENOENT, not ENOTDIR)");

    auto missingDirectory = resolve({"app0", "data", "nope", "deeper", "x.bin"});
    Check(missingDirectory && *missingDirectory == absent / "nope" / "deeper" / "x.bin", "a missing directory keeps the rest of the path below it");

    const int before = listings;
    for (int i = 0; i < 1000; ++i) resolve({"app0", "data", "prein", "bg", "odx", "m" + std::to_string(i) + ".odxb"});
    Check(listings == before, "1,000 more lookups in listed directories list nothing again");

    Check(!resolve({"app0", "data", "prein", "twin", "a.txt"}), "two entries differing only in case: the usual way (which reports the ambiguity)");
    Check(!resolve({"app0", "sce_sys", "param.json", "inside"}), "a file used as a directory: the usual way (ENOTDIR)");
    Check(!resolve({"app0", "data", "caf\xc3\xa9"}), "non-ASCII names: the usual way (Wine folds Unicode, the resolver ASCII)");
    Check(!resolve({"app0", "data", ""}), "trailing separator: the usual way");
    Check(!resolve({"app0", "data", "."}), "dot: the usual way");
    Check(!resolve({"app0", "da*ta"}), "wildcards: the usual way");
    Check(!resolve({}), "empty path: the usual way");

    auto param = resolve({"app0", "SCE_SYS", "Param.Json"});
    Check(param && *param == root / "app0" / "sce_sys" / "param.json", "case-mismatched path to a real file");

    // Module lookups (sceKernelStat, lstat, read-only sceKernelOpen): a missing last name with a relinked module
    // beside it (<name>.guest.prx, any case) goes to the resolver, which returns that module instead.
    const std::string suffix = ".guest.prx";
    auto module = [&](V names) { return cache.Resolve(root, names, absent, suffix); };
    Check(!module({"app0", "sce_module", "libfoo.sprx"}), "module: a missing name with a relinked module beside it: the usual way");
    auto plainFoo = resolve({"app0", "sce_module", "libfoo.sprx"});
    Check(plainFoo && *plainFoo == absent / "libfoo.sprx", "not a module lookup: the same name is simply missing");
    auto bar = module({"app0", "sce_module", "LIBBAR.SPRX"});
    Check(bar && *bar == root / "app0" / "sce_module" / "libbar.sprx", "module: a name that is there is found, relinked module or not");
    auto odx = module({"app0", "data", "prein", "bg", "odx", "missing.odxb"});
    Check(odx && *odx == absent / "missing.odxb", "module: a missing data file with no module beside it resolves into the empty directory");
    auto deeper = module({"app0", "sce_module", "dir", "x.sprx"});
    Check(deeper && *deeper == absent / "dir" / "x.sprx", "module: only the last name is checked for a relinked module");

    Touch(dump / "data" / "prein" / "bg" / "odx" / "late.odxb");
    auto late = resolve({"app0", "data", "prein", "bg", "odx", "late.odxb"});
    Check(late && *late == absent / "late.odxb", "a file created after its directory was listed is not seen (the contents are read-only)");

    std::printf("%zu directories cached, %d listings\n", cache.Directories(), listings);
    fs::remove_all(base);
    return failures == 0 ? 0 : 1;
}
