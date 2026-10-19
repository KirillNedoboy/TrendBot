# Staged selection

`P05-M03-T001` adds the first bounded selection stage to the BCL-only Core.
`StagedSelectionConfiguration` is an explicit format-one contract: callers
provide a non-empty, non-repeating ordered list of feature keys, a direction
for each key, an algorithm version, and the capacity. The Universe stage
accepts only `Turnover` and `Spread` keys and enforces a capacity of at most
250. Candidate-pool and realtime-monitor capacities are 50 and 20, with the
six minimum screener features allowed there.

`UniverseSelectionStage.Evaluate` first excludes metadata whose local
availability is after the supplied `CausalEventCursor`, then applies the
existing metadata-only eligibility filter. It joins eligible metadata with
`ScreenerSnapshot` evidence by ordinal `InstrumentId`. Exact duplicate
snapshot identities collapse; different identities for one instrument are a
stable conflict exclusion. A snapshot must carry the same cursor as the
stage, and every input observation must satisfy event-time, availability-time,
stable-sequence, and closed-bar bounds. Future observations are excluded before
feature evaluation.

Features are recalculated from the retained snapshot input and its explicit
feature configuration. Only configured features whose recalculated value is
complete, fresh, ready, and numeric can participate. Missing, warmup,
unknown, stale, or conflicting values receive a stable `feature-not-ready`
reason and are never replaced with a numeric value.

Ready candidates are ordered lexicographically by the configured decimal
comparisons; ascending and descending directions are applied per key and the
final tie-break is ordinal `InstrumentId`. Rank is one-based before capacity
truncation. Selected candidates retain metadata, the original screener
snapshot, recalculated features, comparison values, rank, cursor, metadata
filter evidence, and configuration. Capacity exclusions retain the same
provenance and rank with `capacity-exceeded`; every other exclusion retains
its metadata, available snapshot evidence, and stable reasons. No clock,
network, feature threshold, weight, TTL, persistence, setup, or execution
state is introduced by this stage.

`CandidatePoolSelectionStage` accepts a completed Universe result and a new
screener snapshot set. It derives the cursor and membership from the preceding
result, so a caller cannot add a symbol or move the causal boundary. The
result retains a format-one `StagedSelectionStageReference` containing the
preceding stage identity, cursor, and selected membership. New evidence is
indexed by ordinal instrument identity; exact snapshot duplicates collapse,
conflicts are excluded, and unknown instruments receive a `not-in-universe`
exclusion. The same causal validation used by Universe rejects cursor
mismatches, observations after the cursor, and late inputs before recalculation.

Candidate Pool allows all six minimum screener features, but only the keys in
its explicit configuration are required to be ready, complete, fresh, and
numeric. Features are always recalculated from retained snapshot inputs. The
same lexical comparison and ordinal symbol tie-break apply, with a maximum of
50 selected candidates. Its result identity is the lower-case SHA-256 of
canonical JSON without the identity field, and a JSON round trip recomputes
the same identity. No new metadata, thresholds, weights, TTL defaults,
persistence, setup, subscription, or execution state is introduced.

`RealtimeMonitorSelectionStage` accepts only a CandidatePool result and a new
screener snapshot set. It requires the CandidatePool result to retain its
Universe reference, derives the cursor and membership from CandidatePool, and
stores a CandidatePool stage reference with the same cursor and selected
membership. New instruments receive `not-in-candidate-pool`; exact duplicates
collapse, conflicts are excluded, and the same causal validation prevents a
future or late observation from becoming a feature value. Only configured
keys must be complete, fresh, ready, and numeric, and the result is bounded at
20 with the same lexical comparison and ordinal symbol tie-break.

`StagedSelectionChainSnapshot` is the canonical format-one replay envelope for
all three stages. It stores the shared cursor, normalized Universe input,
per-stage screener evidence, each stage configuration and result, and all
provenance/exclusions. Its constructor validates the complete
Universe -> CandidatePool -> RealtimeMonitor identity, cursor, and membership
chain, then rebuilds each stage from the retained inputs. Its lower-case
SHA-256 identity is computed from canonical JSON with the identity excluded;
round trip and pure replay therefore produce the same identity. Unsupported
chain versions, altered links, and skipped stages are rejected. No SQLite
persistence, active setups, subscriptions, proximity, setup, or execution
state is introduced.

Focused Core fixtures cover metadata availability, required readiness,
lexical ranking and ordinal tie-breaks, capacity, duplicate/conflicting
snapshots, future-input rejection, culture/permutation determinism, and
canonical JSON round trips for all three stages and the replay chain.
