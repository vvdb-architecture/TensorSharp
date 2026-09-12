// Copyright (c) Zhongkai Fu. All rights reserved.
// Licensed under the BSD-3-Clause license in the repository root.
// Diagnostic derivative of frozen scratch benchmark SHA 772fef5b... .
// Compare default vs POSIX_MADV_RANDOM only on this process-owned scratch file.
// Remote/server cache state and full-model performance are outside its scope.
#include "include/dsv41_engram.h"
#include "include/dsv41_engram_io.h"

#include <chrono>
#include <cstdio>
#include <cstring>
#include <random>
#include <utility>
#if defined(__linux__)
#include <fcntl.h>
#include <sys/mman.h>
#include <sys/resource.h>
#include <unistd.h>
#endif

static void require(bool value, const char * message) {
    if (!value) throw std::runtime_error(message);
}

static int warm_memory() {
    using clock = std::chrono::steady_clock;
    constexpr size_t table_rows = 65536, head = 256, repetitions = 200, samples = 12;
    tsg_dsv41::engram_data layout;
    layout.max_ngram_size = 4; layout.n_heads = 8; layout.head_dim = head;
    layout.layers.push_back({1, table_rows, {}, {}, {}});
    std::vector<int16_t> table(table_rows * head);
    std::mt19937 random(4101);
    for (auto & value : table) value = int16_t(int(random() % 65536) - 32768);
    auto dequantize = [](const void * source, float * output, size_t count) {
        const auto * values = static_cast<const int16_t *>(source);
        for (size_t i = 0; i < count; ++i) output[i] = float(values[i]) / 127;
    };
    tsg_dsv41::engram_io_pool serial(1), parallel(16);
    for (size_t tokens : {size_t(1), size_t(3)}) {
        std::vector<int32_t> hashes(tokens * layout.hash_columns());
        for (auto & hash : hashes) hash = int32_t(random() % table_rows);
        std::vector<float> expected(hashes.size() * head), actual(expected.size());
        layout.lookup(0, table.data(), table_rows, head * sizeof(int16_t), hashes.data(), tokens,
            expected.data(), dequantize);
        auto lookup = [&](tsg_dsv41::engram_io_pool & pool) {
            layout.lookup(0, table.data(), table_rows, head * sizeof(int16_t), hashes.data(), tokens,
                actual.data(), dequantize, [&](size_t count, auto row) { pool.run(count, row); });
        };
        // Fault the selected pages and start the persistent workers before
        // timing. Alternate serial/parallel block order; report every sample.
        for (size_t i = 0; i < 50; ++i) { lookup(serial); lookup(parallel); }
        std::vector<double> durations[2];
        for (size_t sample = 0; sample < samples; ++sample) {
            for (size_t pass = 0; pass < 2; ++pass) {
                const size_t index = (sample + pass) % 2;
                auto & pool = index ? parallel : serial;
                const auto start = clock::now();
                for (size_t i = 0; i < repetitions; ++i) lookup(pool);
                durations[index].push_back(std::chrono::duration<double, std::micro>(clock::now() - start).count() / repetitions);
                require(std::memcmp(actual.data(), expected.data(), actual.size() * sizeof(float)) == 0,
                        "Warmed small lookup changed row bytes");
            }
        }
        for (size_t index = 0; index < 2; ++index) {
            auto sorted = durations[index]; std::sort(sorted.begin(), sorted.end());
            std::printf("{\"mode\":\"warm_memory\",\"tokens\":%zu,\"selected_rows\":%zu,\"head_dim\":%zu,"
                        "\"threads\":%u,\"repetitions_per_sample\":%zu,\"exact_output\":true,\"median_us\":%.6f,\"samples_us\":[",
                        tokens, hashes.size(), head, index ? parallel.threads() : serial.threads(), repetitions,
                        (sorted[sorted.size() / 2 - 1] + sorted[sorted.size() / 2]) / 2);
            for (size_t i = 0; i < durations[index].size(); ++i)
                std::printf("%s%.6f", i ? "," : "", durations[index][i]);
            std::printf("]}\n");
        }
    }
    return 0;
}

int main(int argc, char ** argv) try {
    if (argc != 2) { std::fprintf(stderr, "usage: %s SCRATCH_DIRECTORY | --warm-memory\n", argv[0]); return 2; }
    if (std::strcmp(argv[1], "--warm-memory") == 0) return warm_memory();
#if defined(__linux__)
    constexpr size_t bytes = 64 * 1024 * 1024;
    const long system_page = sysconf(_SC_PAGESIZE);
    require(system_page > 0 && bytes % size_t(system_page) == 0, "Unsupported benchmark page size");
    const size_t page = size_t(system_page);
    std::string pattern = std::string(argv[1]) + "/dsv41-engram-io-XXXXXX";
    std::vector<char> path(pattern.begin(), pattern.end()); path.push_back(0);
    const int fd = mkstemp(path.data());
    require(fd >= 0, "Cannot create benchmark scratch file");
    struct cleanup {
        int fd; const char * path;
        ~cleanup() { close(fd); unlink(path); }
    } cleanup{fd, path.data()};
    std::mt19937 random(4101);
    std::vector<int16_t> source(bytes / sizeof(int16_t));
    for (auto & value : source) value = int16_t(int(random() % 65536) - 32768);
    for (size_t offset = 0; offset < bytes;) {
        const ssize_t wrote = write(fd, reinterpret_cast<char *>(source.data()) + offset, bytes - offset);
        require(wrote > 0, "Cannot write benchmark file"); offset += size_t(wrote);
    }
    require(fsync(fd) == 0, "Cannot flush benchmark file");
    auto dequantize = [](const void * source, float * output, size_t count) {
        const auto * values = static_cast<const int16_t *>(source);
        for (size_t i = 0; i < count; ++i) output[i] = float(values[i]) / 127;
    };
    tsg_dsv41::engram_io_pool serial(1), parallel(16);
    // Reuse established workers, like the model. Only the benchmark's own
    // file is advised away; remote filesystem/server caches are not controlled.
    for (int i = 0; i < 5; ++i) parallel.run(24, [](size_t) {});
    struct scenario { size_t tokens; uint32_t max_ngram, heads, head_dim; };
    const scenario cases[] = {{1, 4, 8, 256}, {3, 4, 8, 256}};
    for (const auto & test : cases) {
        tsg_dsv41::engram_data layout;
        layout.max_ngram_size = test.max_ngram; layout.n_heads = test.heads; layout.head_dim = test.head_dim;
        const size_t row_bytes = layout.head_dim * sizeof(int16_t), table_rows = bytes / row_bytes;
        layout.layers.push_back({1, table_rows, {}, {}, {}});
        std::vector<int32_t> hashes(test.tokens * layout.hash_columns());
        for (auto & hash : hashes) hash = int32_t(random() % table_rows);
        std::vector<unsigned char> selected(bytes / page, 0);
        uint64_t hash_fingerprint = UINT64_C(14695981039346656037);
        for (int32_t hash : hashes) {
            for (unsigned byte = 0; byte < 4; ++byte) {
                hash_fingerprint ^= (uint32_t(hash) >> (byte * 8)) & 255;
                hash_fingerprint *= UINT64_C(1099511628211);
            }
            const size_t first = size_t(hash) * row_bytes / page;
            const size_t last = (size_t(hash) * row_bytes + row_bytes - 1) / page;
            for (size_t i = first; i <= last; ++i) selected[i] = 1;
        }
        const size_t selected_pages = std::count(selected.begin(), selected.end(), 1);
        std::vector<float> expected(hashes.size() * layout.head_dim), actual(expected.size());
        layout.lookup(0, source.data(), table_rows, row_bytes, hashes.data(), test.tokens, expected.data(), dequantize);
        // Three paired samples per thread count. Both advice order and thread
        // block order alternate. A new VMA isolates RANDOM from the default.
        for (size_t sample = 0; sample < 3; ++sample) for (size_t thread_order = 0; thread_order < 2; ++thread_order) {
            const size_t thread_index = (sample + thread_order) % 2;
            auto & pool = thread_index ? parallel : serial;
            for (size_t order = 0; order < 2; ++order) {
                const bool random_advice = (sample + order) % 2 != 0;
                require(posix_fadvise(fd, 0, bytes, POSIX_FADV_DONTNEED) == 0, "Cannot evict private benchmark file pages");
                void * mapped = mmap(nullptr, bytes, PROT_READ, MAP_SHARED, fd, 0);
                require(mapped != MAP_FAILED, "Cannot map benchmark file");
                struct unmap_on_exit {
                    void * address; size_t length;
                    ~unmap_on_exit() { munmap(address, length); }
                } mapping{mapped, bytes};
                if (random_advice)
                    require(posix_madvise(mapped, bytes, POSIX_MADV_RANDOM) == 0, "Cannot apply owned-mapping RANDOM advice");
                std::vector<unsigned char> residency(bytes / page);
                auto inspect = [&]() {
                    require(mincore(mapped, bytes, residency.data()) == 0, "Cannot inspect benchmark page residency");
                    size_t resident = 0, selected_resident = 0;
                    for (size_t i = 0; i < residency.size(); ++i) {
                        resident += residency[i] & 1;
                        selected_resident += (residency[i] & 1) && selected[i];
                    }
                    return std::pair<size_t, size_t>{resident, selected_resident};
                };
                const auto before_pages = inspect();
                std::fill(actual.begin(), actual.end(), -98765.0f);
                struct rusage before{}, after{};
                require(getrusage(RUSAGE_SELF, &before) == 0, "Cannot read initial process fault counters");
                const auto start = std::chrono::steady_clock::now();
                layout.lookup(0, mapped, table_rows, row_bytes, hashes.data(), test.tokens, actual.data(), dequantize,
                    [&](size_t count, auto row) { pool.run(count, row); });
                const double seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
                require(getrusage(RUSAGE_SELF, &after) == 0, "Cannot read final process fault counters");
                const auto after_pages = inspect();
                require(std::memcmp(actual.data(), expected.data(), actual.size() * sizeof(float)) == 0,
                        "Advice changed sparse lookup output");
                std::printf("{\"mode\":\"scratch_advice\",\"tokens\":%zu,\"head_dim\":%u,\"row_bytes\":%zu,"
                            "\"dequantization\":\"synthetic_int16_div127\",\"sample\":%zu,\"thread_order\":%zu,\"advice_order\":%zu,"
                            "\"threads\":%u,\"advice\":\"%s\",\"bytes\":%zu,\"page_bytes\":%zu,\"selected_rows\":%zu,"
                            "\"hashes_fnv1a64\":\"%016llx\",\"selected_pages\":%zu,\"total_pages\":%zu,"
                            "\"initial_resident_pages\":%zu,\"initial_selected_resident_pages\":%zu,"
                            "\"final_resident_pages\":%zu,\"final_selected_resident_pages\":%zu,\"cold_pages_confirmed\":%s,"
                            "\"minor_faults_delta\":%ld,\"major_faults_delta\":%ld,\"lookup_seconds\":%.9f,\"exact_output\":true}\n",
                            test.tokens, layout.head_dim, row_bytes, sample, thread_order, order,
                            pool.threads(), random_advice ? "random" : "default", bytes, page, hashes.size(),
                            (unsigned long long) hash_fingerprint, selected_pages, bytes / page,
                            before_pages.first, before_pages.second, after_pages.first, after_pages.second,
                            before_pages.second == 0 ? "true" : "false", after.ru_minflt - before.ru_minflt,
                            after.ru_majflt - before.ru_majflt, seconds);
                std::fflush(stdout);
            }
        }
    }
#else
    std::fprintf(stderr, "Filesystem benchmark requires Linux; use --warm-memory on this platform.\n");
    return 2;
#endif
} catch (const std::exception & error) {
    std::fprintf(stderr, "Engram I/O benchmark failed: %s\n", error.what());
    return 1;
}
