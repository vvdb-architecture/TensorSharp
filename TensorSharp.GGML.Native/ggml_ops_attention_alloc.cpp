// Copyright (c) Zhongkai Fu. All rights reserved.
// Licensed under the BSD-3-Clause license in the repository root.
#include "ggml_ops_attention_alloc.h"

#include <algorithm>
#include <cstdint>
#include <limits>
#include <unordered_map>
#include <vector>

namespace
{
    ggml_tensor* storage_root(ggml_tensor* tensor)
    {
        while (tensor != nullptr && tensor->view_src != nullptr)
            tensor = tensor->view_src;
        return tensor;
    }

    struct Lifetime
    {
        ggml_tensor* tensor;
        int first;
        int last;
        std::size_t slot = 0;
        bool input = false;
    };

    struct Slot
    {
        int last;
        std::size_t bytes;
        std::size_t offset = 0;
        std::size_t attention = 0;
    };

    struct Binding
    {
        ggml_tensor* tensor;
        void* old_data;
        ggml_backend_buffer_t old_buffer;
        std::size_t offset;
    };

    bool append_aligned(std::size_t bytes, std::size_t alignment,
                        std::size_t& total, std::size_t& offset)
    {
        const std::size_t max = std::numeric_limits<std::size_t>::max();
        if (bytes > max - (alignment - 1))
            return false;
        bytes = ((bytes + alignment - 1) / alignment) * alignment;
        if (total > max - bytes)
            return false;
        offset = total;
        total += bytes;
        return true;
    }
}

namespace tsg
{
    ggml_backend_buffer_t alloc_ctx_tensors_with_attention_reuse(
        ggml_context* ctx, ggml_cgraph* graph, ggml_backend_t backend,
        const std::function<ggml_backend_buffer_t(std::size_t)>& allocate)
    {
        if (ctx == nullptr || graph == nullptr || backend == nullptr)
            return nullptr;

        const int node_count = ggml_graph_n_nodes(graph);
        std::vector<Lifetime> lifetimes;
        std::unordered_map<ggml_tensor*, std::size_t> attention;
        std::unordered_map<ggml_tensor*, int> node_indices;
        node_indices.reserve(static_cast<std::size_t>(node_count));
        for (int i = 0; i < node_count; ++i)
        {
            ggml_tensor* node = ggml_graph_node(graph, i);
            node_indices.emplace(node, i);
            if (node->op == GGML_OP_FLASH_ATTN_EXT && node->view_src == nullptr &&
                node->data == nullptr && node->buffer == nullptr)
            {
                attention.emplace(node, lifetimes.size());
                lifetimes.push_back({node, i, i});
            }
        }
        if (lifetimes.size() < 2 && !allocate)
            return ggml_backend_alloc_ctx_tensors(ctx, backend);

        // A read through any chain of reshapes/views still keeps its attention
        // allocation live. Include the destination too: in-place writes can be
        // aliases even when the previous value is not an explicit source.
        const auto touch = [&](ggml_tensor* tensor, int index)
        {
            const auto found = attention.find(storage_root(tensor));
            if (found != attention.end())
                lifetimes[found->second].last = std::max(lifetimes[found->second].last, index);
        };
        for (int i = 0; i < node_count; ++i)
        {
            ggml_tensor* node = ggml_graph_node(graph, i);
            touch(node, i);
            for (ggml_tensor* source : node->src)
                touch(source, i);
        }
        for (ggml_tensor* tensor = ggml_get_first_tensor(ctx); tensor != nullptr;
             tensor = ggml_get_next_tensor(ctx, tensor))
        {
            if ((tensor->flags & (GGML_TENSOR_FLAG_INPUT | GGML_TENSOR_FLAG_OUTPUT)) != 0)
                touch(tensor, node_count);
            if ((tensor->flags & GGML_TENSOR_FLAG_INPUT) != 0)
            {
                const auto found = attention.find(storage_root(tensor));
                if (found != attention.end())
                    lifetimes[found->second].input = true;
            }
        }
        // An otherwise unused attention result is a terminal graph result. Keep
        // it readable after execution even if its builder omitted OUTPUT.
        for (Lifetime& lifetime : lifetimes)
            if (lifetime.first == lifetime.last)
                lifetime.last = node_count;

        // Sharing before an independent final consumer completes could force
        // an extra Metal barrier. Seed each bit at that attention's LAST use,
        // then propagate through the existing graph. A new owner must depend
        // on that consumer, not merely on the attention producer. Pinned
        // lifetimes end beyond the graph and deliberately get no completion bit.
        // One word covers the common <=64-attention-layer graph.
        const std::size_t words = (lifetimes.size() + 63) / 64;
        std::vector<std::uint64_t> ancestors(static_cast<std::size_t>(node_count) * words, 0);
        for (std::size_t i = 0; i < lifetimes.size(); ++i)
            if (lifetimes[i].last < node_count)
                ancestors[static_cast<std::size_t>(lifetimes[i].last) * words + i / 64] |=
                    std::uint64_t{1} << (i % 64);
        for (int i = 0; i < node_count; ++i)
        {
            ggml_tensor* node = ggml_graph_node(graph, i);
            for (ggml_tensor* source : node->src)
            {
                const auto parent = node_indices.find(source);
                if (parent != node_indices.end() && parent->second < i)
                    for (std::size_t word = 0; word < words; ++word)
                        ancestors[static_cast<std::size_t>(i) * words + word] |=
                            ancestors[static_cast<std::size_t>(parent->second) * words + word];
            }
        }

        ggml_backend_buffer_type_t buft = ggml_backend_get_default_buffer_type(backend);
        const std::size_t alignment = ggml_backend_buft_get_alignment(buft);
        if (alignment == 0)
            return nullptr;

        std::vector<Slot> slots;
        for (Lifetime& lifetime : lifetimes)
        {
            // Ask the backend for its entire allocation, including ALL scratch.
            // This deliberately knows nothing about Metal's temporary formats,
            // kernel thresholds, or where scratch follows the result tensor.
            const std::size_t bytes = ggml_backend_buft_get_alloc_size(buft, lifetime.tensor);
            std::size_t best = slots.size();
            std::size_t best_growth = std::numeric_limits<std::size_t>::max();
            for (std::size_t i = 0; i < slots.size(); ++i)
            {
                // INPUT storage may be filled before execution starts, so it
                // must not reuse an earlier node's allocation either.
                if (lifetime.input || slots[i].last >= lifetime.first)
                    continue;
                const std::size_t prior = slots[i].attention;
                if ((ancestors[static_cast<std::size_t>(lifetime.first) * words + prior / 64] &
                    (std::uint64_t{1} << (prior % 64))) == 0)
                    continue;
                const std::size_t growth = bytes > slots[i].bytes ? bytes - slots[i].bytes : 0;
                if (best == slots.size() || growth < best_growth)
                {
                    best = i;
                    best_growth = growth;
                }
            }
            if (best == slots.size())
                slots.push_back({lifetime.last, bytes});
            else
            {
                slots[best].last = lifetime.last;
                slots[best].bytes = std::max(slots[best].bytes, bytes);
            }
            lifetime.slot = best;
            slots[best].attention = static_cast<std::size_t>(&lifetime - lifetimes.data());
        }
        if (slots.size() == lifetimes.size() && !allocate)
            return ggml_backend_alloc_ctx_tensors(ctx, backend);

        std::size_t total = 0;
        std::vector<Binding> bindings;
        for (ggml_tensor* tensor = ggml_get_first_tensor(ctx); tensor != nullptr;
             tensor = ggml_get_next_tensor(ctx, tensor))
        {
            if (tensor->data != nullptr || tensor->view_src != nullptr)
                continue;
            std::size_t offset = 0;
            if (attention.find(tensor) == attention.end() &&
                !append_aligned(ggml_backend_buft_get_alloc_size(buft, tensor), alignment, total, offset))
                return nullptr;
            bindings.push_back({tensor, tensor->data, tensor->buffer, offset});
        }
        for (Slot& slot : slots)
            if (!append_aligned(slot.bytes, alignment, total, slot.offset))
                return nullptr;
        for (Binding& binding : bindings)
        {
            const auto found = attention.find(binding.tensor);
            if (found != attention.end())
                binding.offset = slots[lifetimes[found->second].slot].offset;
        }

        // Leave splitting across device buffer limits to ggml's normal allocator.
        // Reserve alignment slack because backend base pointers need not be aligned.
        if (total > std::numeric_limits<std::size_t>::max() - alignment ||
            total + alignment > ggml_backend_buft_get_max_size(buft))
            return allocate ? nullptr : ggml_backend_alloc_ctx_tensors(ctx, backend);
        ggml_backend_buffer_t buffer = allocate ? allocate(total + alignment)
            : ggml_backend_buft_alloc_buffer(buft, total + alignment);
        if (buffer == nullptr)
            return nullptr;
        if (ggml_backend_buffer_get_size(buffer) < total + alignment)
        {
            if (!allocate)
                ggml_backend_buffer_free(buffer);
            return nullptr;
        }

        char* base = static_cast<char*>(ggml_backend_buffer_get_base(buffer));
        const std::size_t padding = (alignment - reinterpret_cast<std::uintptr_t>(base) % alignment) % alignment;
        bool ok = true;
        for (const Binding& binding : bindings)
        {
            if (ggml_backend_tensor_alloc(buffer, binding.tensor, base + padding + binding.offset) != GGML_STATUS_SUCCESS)
            {
                ok = false;
                break;
            }
        }
        if (ok)
        {
            // ggml creates a view after its source, so this also initializes
            // nested views and views of externally bound weights/cache tensors.
            for (ggml_tensor* tensor = ggml_get_first_tensor(ctx); tensor != nullptr;
                 tensor = ggml_get_next_tensor(ctx, tensor))
            {
                if (tensor->view_src != nullptr && tensor->buffer == nullptr)
                {
                    bindings.push_back({tensor, tensor->data, tensor->buffer, 0});
                    if (ggml_backend_view_init(tensor) != GGML_STATUS_SUCCESS)
                    {
                        ok = false;
                        break;
                    }
                }
            }
        }
        if (!ok)
        {
            for (const Binding& binding : bindings)
            {
                binding.tensor->data = binding.old_data;
                binding.tensor->buffer = binding.old_buffer;
            }
            if (!allocate)
                ggml_backend_buffer_free(buffer);
            return nullptr;
        }
        return buffer;
    }
}
