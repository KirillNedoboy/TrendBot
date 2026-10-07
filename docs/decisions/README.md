# Architecture decisions

`docs/decisions/` is the canonical ADR catalog for TradingBot. Each ADR uses the format Context, Decision, Alternatives considered, and Consequences. The source plan called the directory `docs/adr` in one section and `docs/decisions` in another; ADR-010 records the resolution and `docs/adr/README.md` preserves the compatibility path.

| ADR | Decision |
|---|---|
| ADR-001 | Technology stack: C# and pinned .NET 10 |
| ADR-002 | Own Binance USD-M exchange integration; verify current Algo Service and `ALGO_UPDATE` before use |
| ADR-003 | Causal deterministic event and market-data transport |
| ADR-004 | SQLite/Parquet persistence, durable intents, reservations, and reconciliation |
| ADR-005 | Setup strategy state machine |
| ADR-006 | Shared backtest/live Core and shared risk math with separate fill simulation |
| ADR-007 | Orderbook storage and recovery |
| ADR-008 | Linux VPS and systemd deployment |
| ADR-009 | Durable execution idempotency and reconciliation |
| ADR-010 | Fail-closed policy and explicit real-money activation gate |
| ADR-011 | Bounded research capture and data-quality observability |
| ADR-012 | Frozen out-of-sample and predeclared promotion gates |

The canonical decisions make the source amendments explicit in working documents. Unknown parameters, missing external sources, and remote GitHub CI status remain unresolved until their own evidence exists.
