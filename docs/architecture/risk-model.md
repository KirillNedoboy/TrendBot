# Risk model boundary

P02-M01 defines passive Decision (ALLOW, BLOCK, SHADOW) and RiskDecision (APPROVED, BLOCKED) contracts with reason codes. These records do not run the Decision Engine or Risk Engine and authorize no orders.

P02-M02 defines `IRiskMathCalculator` as the common deterministic, side-effect-free sizing and exposure contract for replay, paper simulation, and execution planning. Each result carries a `RiskMathVersion` and a full immutable `RiskMathInput` snapshot: instrument, direction, equity, risk fraction, entry and protective-stop prices, fee per quantity unit, and estimated slippage per quantity unit. Results expose the risk budget, loss per raw quantity unit, raw quantity, and notional exposure. Money values retain the input currency, and result constructors validate local invariants without recomputing formulas. Raw quantity is before venue lot, tick, or margin constraints. No execution mode or authorization is represented; `RiskDecision` remains a separate contract.

T002 adds contracts only. A calculator implementation and cross-mode calculation parity are deferred to P09-M02-T001; this contract does not introduce trading defaults, sizing formulas, portfolio policy, or fill simulation.

Decision Engine (Phase 8) determines `ALLOW`, `BLOCK`, or `SHADOW` from strategy evidence. Risk Engine (Phase 11) independently evaluates exposure, sizing, stops, portfolio limits, kill switches, and operational state. Strategy code must not bypass risk.

Phase 9 is intentionally earlier than the full Risk Engine: replay/backtest requires a deterministic simulation of risk, stop-loss, take-profit, partial-fill assumptions, and time exits. That simulation is a research harness, not live authorization.
