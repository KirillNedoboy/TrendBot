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
