// Copyright (c) Zhongkai Fu. All rights reserved.
// Licensed under the BSD-3-Clause license in the repository root.
#pragma once

#include "ggml.h"
#include "ggml-backend.h"
#include <functional>

namespace tsg
{
    // Keep ordinary context tensors in unique storage, and share complete
    // attention allocations only after every consumer of the previous value.
    // The graph must already have its final execution order. Shapes must remain
    // within the allocation made here; rebuild/reallocate for shape changes.
    // Without an allocator, ownership/failure match ggml_backend_alloc_ctx_tensors.
    // An optional allocator supplies a buffer of the requested size and retains
    // ownership even on binding failure (for the process's reusable compute slab).
    ggml_backend_buffer_t alloc_ctx_tensors_with_attention_reuse(
        ggml_context* ctx, ggml_cgraph* graph, ggml_backend_t backend,
        const std::function<ggml_backend_buffer_t(std::size_t)>& allocate = {});
}
