# Risk model boundary

P02-M01 defines passive Decision (ALLOW, BLOCK, SHADOW) and RiskDecision (APPROVED, BLOCKED) contracts with reason codes. These records do not run the Decision Engine or Risk Engine and authorize no orders.

Decision Engine (Phase 8) determines `ALLOW`, `BLOCK`, or `SHADOW` from strategy evidence. Risk Engine (Phase 11) independently evaluates exposure, sizing, stops, portfolio limits, kill switches, and operational state. Strategy code must not bypass risk.

Phase 9 is intentionally earlier than the full Risk Engine: replay/backtest requires a deterministic simulation of risk, stop-loss, take-profit, partial-fill assumptions, and time exits. That simulation is a research harness, not live authorization.
