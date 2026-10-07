# ADR-001 — Technology stack

## Context

The system needs static types, async networking, deterministic time, long-running Linux support, and a maintainable research/runtime codebase.

## Decision

Use C# on pinned .NET 10 LTS. The bootstrap targets `net10.0` and pins SDK `10.0.401` in `global.json`.

## Alternatives considered

Python/NautilusTrader is a reference implementation and research comparison, not the runtime. Rust was considered unnecessary for the expected latency profile.

## Consequences

The project uses .NET tooling, BCL primitives, and xUnit v3. SDK upgrades are explicit, and current package/API compatibility must be checked during each phase.
