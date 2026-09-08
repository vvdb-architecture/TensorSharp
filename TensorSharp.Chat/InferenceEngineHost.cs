// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TensorSharp.Runtime;
using TensorSharp.Runtime.Scheduling;

namespace TensorSharp.Server
{
    /// <summary>
    /// Owner of the per-model <see cref="InferenceEngine"/>. Lifecycle-bound to
    /// <see cref="ModelLifecycleService"/>: the engine is constructed lazily on
    /// first access (after a model has been loaded) and rebuilt whenever the
    /// model's KV-state fingerprint changes (i.e. on model swap). Disposing
    /// this service tears down the engine, which joins its worker thread and
    /// frees the paged KV block pool.
    ///
    /// This service is the public substitute for the legacy
    /// <see cref="InferenceQueue"/>: submission is non-blocking, multiple
    /// requests run concurrently (with iteration-level fairness), and the
    /// paged KV pool / continuous-batching scheduler / per-block prefix cache
    /// all live behind this single entry point. Adapters that haven't yet
    /// dropped queue-status chunks still take <see cref="InferenceQueue"/>
    /// tickets, but those tickets grant immediately so the engine remains the
    /// only real concurrency boundary.
    /// </summary>
    public sealed class InferenceEngineHost : IDisposable
    {
        private readonly ModelLifecycleService _lifecycle;
        private readonly ILogger _logger;
        private readonly object _gate = new();
        private InferenceEngine _engine;
        private string _fingerprint;
        private bool _disposed;

        /// <summary>
        /// Engine sizing supplied by the host; null means
        /// <see cref="SchedulerConfig.FromEnvironment"/>. Read when the engine is
        /// (re)built, which the log line below records so an operator can tell which
        /// source sized the KV pool.
        /// </summary>
        public SchedulerConfig SchedulerConfigOverride { get; set; }

        private ComputeGate _computeGate;

        /// <summary>
        /// The gate every engine this host builds runs behind, or null for none.
        ///
        /// <para>
        /// Set once by a host whose platform can take the GPU away — the iOS app, while
        /// it is not frontmost — and handed to each engine as it is built, AND to the
        /// one already standing, because the engine is rebuilt on every model swap and
        /// a gate that only reached the first one would silently stop working the
        /// first time the user changed models. See <see cref="ComputeGate"/>.
        /// </para>
        /// </summary>
        public ComputeGate ComputeGate
        {
            get { lock (_gate) return _computeGate; }
            set
            {
                lock (_gate)
                {
                    _computeGate = value;
                    if (_engine != null)
                        _engine.ComputeGate = value;
                }
            }
        }

        internal InferenceEngineHost(ModelLifecycleService lifecycle, ILogger logger)
        {
            _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>Get the engine for the currently-loaded model, constructing
        /// it if it hasn't been built yet (or rebuilding it if the model has
        /// changed). Returns null when no model is loaded or when the model
        /// supports neither the KV-state snapshot contract nor the batched
        /// paged-attention contract.
        ///
        /// Models that implement <see cref="IBatchedPagedModel"/> serve
        /// parallel requests via <c>ForwardBatch</c> and don't need to swap
        /// KV state between sequences, so they qualify even when
        /// <see cref="ModelBase.SupportsKVStateSnapshot"/> reports false.</summary>
        public InferenceEngine TryGetEngine()
        {
            var model = _lifecycle.Model;
            if (model == null) return null;
            if (!model.SupportsKVStateSnapshot && model is not IBatchedPagedModel) return null;

            string fp = model.KVStateFingerprint ?? string.Empty;
            lock (_gate)
            {
                if (_disposed) return null;
                if (_engine != null && string.Equals(_fingerprint, fp, StringComparison.Ordinal))
                    return _engine;

                _engine?.Dispose();
                SchedulerConfig cfg = SchedulerConfigOverride;
                string cfgSource = cfg != null ? "host" : "environment";
                cfg ??= SchedulerConfig.FromEnvironment();
                _engine = new InferenceEngine(model, cfg, _logger) { ComputeGate = _computeGate };
                _fingerprint = fp;
                var poolStats = _engine.PoolStats;
                _logger.LogInformation(
                    "InferenceEngine constructed for fingerprint {Fingerprint} (blocks={NumBlocks}, blockSize={BlockSize}, kvCapacityTokens={KvCapacity}, maxBatched={MaxBatched}, config={ConfigSource})",
                    fp, poolStats.totalBlocks, poolStats.blockSize,
                    (long)poolStats.totalBlocks * poolStats.blockSize, cfg.MaxNumBatchedTokens, cfgSource);
                return _engine;
            }
        }

        /// <summary>Peek at the live concurrency counters of the already-built
        /// engine without constructing one. Returns false when no engine exists
        /// yet (no model loaded, the model can't use the engine, or no request
        /// has been submitted yet) - in that case the out values are zero.
        ///
        /// This is deliberately side-effect free: a status poll must never
        /// trigger lazy engine construction (and the matching block-pool
        /// allocation) the way <see cref="TryGetEngine"/> does.</summary>
        public bool TryGetLiveStats(out int processing, out int waiting, out long totalCompleted)
        {
            processing = 0;
            waiting = 0;
            totalCompleted = 0;
            lock (_gate)
            {
                if (_disposed || _engine == null)
                    return false;
                processing = _engine.RunningCount;
                waiting = _engine.WaitingCount;
                totalCompleted = _engine.TotalCompleted;
                return true;
            }
        }

        /// <summary>Drop the engine (if any). Called by <see cref="ModelLifecycleService"/>
        /// when the model is unloaded so we don't hold onto a stale block pool.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _engine?.Dispose();
                _engine = null;
                _fingerprint = null;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _engine?.Dispose();
                _engine = null;
            }
        }
    }
}
