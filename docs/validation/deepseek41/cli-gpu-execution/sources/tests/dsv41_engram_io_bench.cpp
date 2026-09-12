// Copyright (c) Zhongkai Fu. All rights reserved.
// Licensed under the BSD-3-Clause license in the repository root.
// Portable warm-memory lookup control, plus a Linux-only filesystem benchmark
// that creates/removes its own scratch file. Never evicts live model pages.
#include "../dsv41_engram.h"
#include "../dsv41_engram_io.h"

#include <chrono>
#include <cstdio>
#include <cstring>
#include <random>
#if defined(__linux__)
#include <fcntl.h>
#include <sys/mman.h>
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

int main(int argc, char ** argv) {
    if (argc != 2) { std::fprintf(stderr, "usage: %s SCRATCH_DIRECTORY | --warm-memory\n", argv[0]); return 2; }
    if (std::strcmp(argv[1], "--warm-memory") == 0) return warm_memory();
#if defined(__linux__)
    constexpr size_t bytes = 64 * 1024 * 1024, row_bytes = 64, page = 4096;
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
    tsg_dsv41::engram_data layout;
    layout.max_ngram_size = 3; layout.n_heads = 2; layout.head_dim = 32;
    layout.layers.push_back({1, bytes / row_bytes, {}, {}, {}});
    std::vector<int32_t> hashes(1024);
    for (size_t i = 0; i < hashes.size(); ++i) hashes[i] = int32_t(random() % (bytes / row_bytes));
    auto dequantize = [](const void * source, float * output, size_t count) {
        const auto * values = static_cast<const int16_t *>(source);
        for (size_t i = 0; i < count; ++i) output[i] = float(values[i]) / 127;
    };
    std::vector<float> expected(hashes.size() * 32), actual(expected.size());
    layout.lookup(0, source.data(), bytes / row_bytes, row_bytes, hashes.data(), 256, expected.data(), dequantize);
    for (bool warm : {false, true}) for (unsigned threads : {1, 16}) {
        require(posix_fadvise(fd, 0, bytes, POSIX_FADV_DONTNEED) == 0, "Cannot evict private benchmark file pages");
        void * mapped = mmap(nullptr, bytes, PROT_READ, MAP_SHARED, fd, 0);
        require(mapped != MAP_FAILED, "Cannot map benchmark file");
        std::vector<unsigned char> residency(bytes / page);
        require(mincore(mapped, bytes, residency.data()) == 0, "Cannot inspect benchmark page residency");
        size_t resident = 0;
        for (auto page_status : residency) resident += page_status & 1;
        tsg_dsv41::engram_io_pool pool(threads);
        double warm_seconds = 0;
        uint64_t warm_checksum = 0;
        if (warm) {
            const auto start = std::chrono::steady_clock::now();
            warm_checksum = pool.warm(mapped, bytes);
            warm_seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
        }
        const auto start = std::chrono::steady_clock::now();
        layout.lookup(0, mapped, bytes / row_bytes, row_bytes, hashes.data(), 256, actual.data(), dequantize,
            [&](size_t count, auto row) { pool.run(count, row); });
        const double seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
        require(actual == expected, "Cold/parallel/warm sparse lookup changed results");
        std::printf("{\"threads\":%u,\"warm\":%s,\"bytes\":%zu,\"selected_rows\":%zu,"
                    "\"initial_resident_pages\":%zu,\"total_pages\":%zu,\"warm_seconds\":%.9f,"
                    "\"lookup_seconds\":%.9f,\"warm_checksum\":%llu,\"exact_output\":true}\n",
            threads, warm ? "true" : "false", bytes, hashes.size(), resident, bytes / page,
            warm_seconds, seconds, (unsigned long long) warm_checksum);
        std::fflush(stdout);
        require(munmap(mapped, bytes) == 0, "Cannot unmap benchmark file");
    }
#else
    std::fprintf(stderr, "Filesystem benchmark requires Linux; use --warm-memory on this platform.\n");
    return 2;
#endif
}
