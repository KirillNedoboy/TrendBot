# Phase 0 architecture overview

TradingBot is a C#/.NET 10 modular monolith. A shared deterministic Core receives normalized market events, while exchange, persistence, replay, application composition, and data tooling remain replaceable boundaries. Live and replay paths must use the same Core decisions.

The bootstrap graph is:

```text
Core <- Exchange.Binance
Core <- Infrastructure
Core <- Backtesting
Core + Exchange.Binance + Infrastructure + Backtesting <- App
Infrastructure + Backtesting <- DataTool
```

The graph is deliberately acyclic. No external exchange SDK is part of Core. No strategy, risk, or execution behavior is present in Phase 0–1.

Phase 3 persistence begins at the Infrastructure boundary. [`SqliteEventJournal`](../../src/TradingBot.Infrastructure/SqliteEventJournal.cs)
uses Microsoft.Data.Sqlite with a versioned, file-backed SQLite schema to retain
artificial market events and their causal fields across process restarts. The
journal assigns no stable sequences, filters no replay view, and exposes no
order or execution authorization state; those concerns remain in later tasks.
The schema and append/read guarantees are recorded in
[`persistence.md`](persistence.md).
