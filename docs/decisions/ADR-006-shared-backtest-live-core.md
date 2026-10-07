# ADR-006 — Shared backtest/live Core and risk math

## Context

Research results are only useful if replay, backtest, shadow, paper, and execution planning apply identical feature, strategy, decision, and risk semantics. Fill behavior cannot be inferred from OHLCV alone and must not silently change the risk calculation.

## Decision

Core contracts and deterministic engines are shared by replay, backtest, shadow, paper, and live modes. One versioned risk-math contract produces sizing, exposure, protective levels, and risk decisions. A separate fill simulator models fees, slippage, latency, and fill assumptions for research; it is never an authorization path. Adapters provide data and side effects around that Core.

Local paper simulation and exchange test-environment execution remain separate evidence lanes. Private execution development is separate from the real-money activation gate, which requires explicit evidence, review, and a user command.

## Alternatives considered

Separate backtest and live implementations, using a fill simulator as a risk engine, a third-party trading runtime, or a strategy-only shared library. These approaches permit semantic drift or can turn a research assumption into an accidental order authorization.

## Consequences

The API must be side-effect conscious and time-provider driven. Every phase adds parity and determinism tests before moving toward real orders. Any change to shared risk math invalidates the affected replay, paper, and gate evidence until re-run.
