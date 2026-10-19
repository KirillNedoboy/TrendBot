# Feature quality and readiness

`TradingBot.Core.FeatureQualityEvaluator` is an independent, pure quality
boundary for the existing `rolling-v1` and `price-context-v2` calculators. It
accepts their immutable results and a caller-supplied `FeatureQualityPolicy`;
the calculator formulas, serialized inputs, and algorithm versions remain
unchanged. The evaluator never reads a clock, supplies a default TTL, performs
I/O, or changes a feature value.

The policy has four nullable TTLs: rolling bars, setup bars, HTF bars, and exact
price-volume observations. A missing or non-positive TTL produces
`FeatureQualityFreshness.Unknown`. With a known positive TTL, the evaluator
computes two independent ages at the result cursor: market-data age uses bar
close time (or trade event time), and availability age uses local availability
time. Both ages must be at or below the TTL for `Fresh`; either age above the
TTL is `Stale`. An age equal to the TTL is therefore fresh.

Each rolling output and price-context output receives immutable,
version-independent evidence containing the original state and completeness,
the derived freshness, both ages, the applied TTL, known/required observation
counts, a bounded coverage ratio, and ordinal-stable reasons. Exact duplicate
observations are counted once by the source calculators and their duplicate
diagnostic is retained. Gaps, conflicts, incomplete input, non-ready original
state, incomplete coverage, and unknown or stale freshness block derived
readiness. The result carries `feature-quality-v1` and the source formula
versions for later snapshot persistence.

The quality layer is deliberately separate from setup selection and execution;
it reports evidence only and cannot authorize an order.

`FeatureQualitySnapshot` is format 1 and uses `feature-quality-v1`. It retains
the complete rolling and price-context inputs, explicit policy, causal cursor,
source formula versions, and the evaluator result. Its lower-case SHA-256
identity is computed from canonical Core JSON with the identity field excluded.
`SqliteFeatureQualitySnapshotStore` persists that JSON in the schema-9
`feature_quality_snapshots` table using immediate serializable transactions;
retries are idempotent and reads validate row metadata, supported versions,
canonical JSON, identity, and quality recomputation. The
`FeatureQualityMetricsPublisher` exposes readiness, stable reason counts, both
ages, and known coverage through fixed low-cardinality Meter instruments;
unknown values remain explicit rather than being replaced with defaults.
