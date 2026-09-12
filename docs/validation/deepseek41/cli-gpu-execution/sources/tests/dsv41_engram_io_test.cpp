// Copyright (c) Zhongkai Fu. All rights reserved.
// Licensed under the BSD-3-Clause license in the repository root.
#include "../dsv41_engram.h"
#include "../dsv41_engram_io.h"

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>

static void require(bool value, const char * message) {
    if (!value) { std::fprintf(stderr, "%s\n", message); std::exit(1); }
}
template<typename Function> static void expect_error(Function function) {
    bool caught = false;
    try { function(); } catch (const std::runtime_error &) { caught = true; }
    require(caught, "Expected an Engram I/O error");
}

int main() {
    using tsg_dsv41::engram_io_pool;
    expect_error([] { engram_io_pool invalid(0); });
    expect_error([] { engram_io_pool invalid(33); });
    tsg_dsv41::engram_data layout;
    // Published V4.1 geometry: 24 independently hashed 256-element rows
    // per token. Cover decode and the former serial small-prefill path.
    layout.max_ngram_size = 4; layout.n_heads = 8; layout.head_dim = 256;
    layout.layers.push_back({1, 1024, {}, {}, {}});
    std::vector<int16_t> table(1024 * layout.head_dim);
    for (size_t i = 0; i < table.size(); ++i) table[i] = int16_t(int((i * 71) % 511) - 255);
    std::vector<int32_t> hashes(257 * layout.hash_columns());
    for (size_t i = 0; i < hashes.size(); ++i) hashes[i] = int32_t((i * 257 + i / 3) % 1024);
    auto dequantize = [](const void * source, float * output, size_t count) {
        const auto * values = static_cast<const int16_t *>(source);
        for (size_t i = 0; i < count; ++i) output[i] = float(values[i]) / 127;
    };
    const size_t row_bytes = layout.head_dim * sizeof(int16_t);
    std::vector<float> expected(hashes.size() * layout.head_dim), actual(expected.size());
    layout.lookup(0, table.data(), 1024, row_bytes, hashes.data(), 257, expected.data(), dequantize);
    for (unsigned threads : {1, 2, 8, 16, 32}) {
        engram_io_pool pool(threads);
        for (size_t tokens : {size_t(1), size_t(3), size_t(257)}) {
            const size_t elements = tokens * layout.hash_columns() * layout.head_dim;
            for (int repeat = 0; repeat < 10; ++repeat) {
                std::fill(actual.begin(), actual.end(), -98765.0f);
                layout.lookup(0, table.data(), 1024, row_bytes, hashes.data(), tokens, actual.data(), dequantize,
                    [&](size_t count, auto row) { pool.run(count, row); });
                require(std::memcmp(actual.data(), expected.data(), elements * sizeof(float)) == 0,
                        "Parallel Engram lookup changed row order or arithmetic");
                require(std::all_of(actual.begin() + elements, actual.end(), [](float value) { return value == -98765.0f; }),
                        "Small Engram lookup wrote beyond its output rows");
            }
            // Validate every hash before submitting any row, including a
            // missing last row after otherwise valid decode/prefill hashes.
            auto invalid = hashes;
            const size_t last = tokens * layout.hash_columns() - 1;
            for (int32_t missing : {-1, 1024}) {
                invalid[last] = missing;
                std::atomic<int> calls{0};
                expect_error([&] {
                    layout.lookup(0, table.data(), 1024, row_bytes, invalid.data(), tokens, actual.data(),
                        [&](const void *, float *, size_t) { ++calls; },
                        [&](size_t count, auto row) { pool.run(count, row); });
                });
                require(calls == 0, "Invalid Engram hash submitted partial work");
            }
        }
        for (size_t count : {size_t(24), size_t(72), size_t(129)}) {
            expect_error([&] { pool.run(count, [](size_t i) { if (i == 13) throw std::runtime_error("test"); }); });
            std::vector<std::atomic<int>> recovered(count);
            for (auto & visits : recovered) visits = 0;
            pool.run(count, [&](size_t i) { ++recovered[i]; });
            for (const auto & visits : recovered) require(visits == 1, "Small pool job did not recover after exception");
        }
        expect_error([&] {
            layout.lookup(0, nullptr, 1024, row_bytes, hashes.data(), 1, actual.data(), dequantize,
                [&](size_t count, auto row) { pool.run(count, row); });
        });
        std::vector<std::atomic<int>> visits(257);
        for (auto & count : visits) count = 0;
        pool.run(visits.size(), [&](size_t i) { ++visits[i]; });
        for (const auto & count : visits) require(count == 1, "Pool did not recover after worker exception");
        std::vector<std::thread> submitters;
        for (int i = 0; i < 4; ++i) submitters.emplace_back([&] {
            for (int repeat = 0; repeat < 5; ++repeat) pool.run(visits.size(), [&](size_t row) { ++visits[row]; });
        });
        for (auto & thread : submitters) thread.join();
        for (const auto & count : visits) require(count == 21, "Concurrent pool submissions overlapped incorrectly");
    }
    std::vector<uint8_t> pages(17 * 1024 * 1024 + 13);
    for (size_t i = 0; i < pages.size(); ++i) pages[i] = uint8_t(i * 19 + i / 17);
    const auto original = pages;
    engram_io_pool serial(1), parallel(16);
    const auto checksum = serial.warm(pages.data() + 13, pages.size() - 13);
    require(checksum == parallel.warm(pages.data() + 13, pages.size() - 13), "Warming checksum changed");
    require(pages == original, "Warming modified table bytes");
    require(parallel.warm(nullptr, 0) == 0, "Empty warming failed");
    expect_error([&] { parallel.warm(nullptr, 1); });
    // Model delayed page reads deterministically; timing is diagnostic, while
    // peak overlap verifies the optimization without a flaky speed threshold.
    for (size_t rows : {size_t(24), size_t(72)}) for (auto * pool : {&serial, &parallel}) {
        std::atomic<int> active{0}, peak{0};
        const auto start = std::chrono::steady_clock::now();
        pool->run(rows, [&](size_t) {
            const int current = ++active;
            int previous = peak.load();
            while (previous < current && !peak.compare_exchange_weak(previous, current)) {}
            std::this_thread::sleep_for(std::chrono::milliseconds(2));
            --active;
        });
        require(peak <= int(pool->threads()), "Pool exceeded its worker bound");
        if (pool->threads() > 1) require(peak > 1, "Delayed I/O was still serialized");
        std::printf("Simulated blocked reads: rows=%zu threads=%u peak=%d elapsed_ms=%.2f\n", rows, pool->threads(), int(peak),
            std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count());
    }
    std::puts("Engram 1/3/257-token serial/parallel/warm, invalid rows, lifecycle, concurrent submission and exception tests passed");
}
