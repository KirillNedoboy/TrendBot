# ADR-011 — Data quality and bounded research capture

## Context

Lane C assumptions need evidence before they can affect decisions, but private execution is a later capability. Market feeds can be stale, duplicated, gapped, incomplete, or timestamped inconsistently. A research dataset without quality metadata can make a clean replay look more reliable than its inputs.

## Decision

Start a bounded, versioned capture from the public market-data path before private execution. Store provenance, coverage, retention, exclusions, event/receive times, stable processing sequence, and quality flags. Emit counters and health evidence for freshness, gaps, duplicates, missing fields, clock skew, and stale providers. Quality failures block the affected confirmation or gate and are preserved in replay manifests.

Lane C capture does not depend on private execution and cannot emit real orders. Retention and provider choices remain explicit configuration decisions until evidence exists.

## Alternatives considered

Wait for the private execution stack before collecting Lane C data, treat missing data as neutral, or store raw payloads without quality metadata. Those choices delay evidence or hide selection and survivorship problems.

## Consequences

Early capture consumes bounded storage and requires monitoring, manifests, gap handling, and fixture tests. Research reports must include data-quality status alongside strategy results.
