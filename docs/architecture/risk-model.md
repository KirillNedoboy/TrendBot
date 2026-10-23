# Risk model boundary

P02-M01 defines passive Decision (ALLOW, BLOCK, SHADOW) and RiskDecision (APPROVED, BLOCKED) contracts with reason codes. These records do not run the Decision Engine or Risk Engine and authorize no orders.

P02-M02 defines `IRiskMathCalculator` as the common deterministic, side-effect-free sizing and exposure contract for replay, paper simulation, and execution planning. Each result carries a `RiskMathVersion` and a full immutable `RiskMathInput` snapshot: instrument, direction, equity, risk fraction, entry and protective-stop prices, fee per quantity unit, and estimated slippage per quantity unit. Results expose the risk budget, loss per raw quantity unit, raw quantity, and notional exposure. Money values retain the input currency, and result constructors validate local invariants without recomputing formulas. Raw quantity is before venue lot, tick, or margin constraints. No execution mode or authorization is represented; `RiskDecision` remains a separate contract.

P09-M02-T001 supplies the first shared implementation, `LinearPerpetualRiskMathCalculator`, with fixed version `linear-perpetual-risk-v1`. It computes `riskBudget = equity * riskFraction`, `lossPerUnit = abs(entry - stop) + feePerUnit + slippagePerUnit`, `rawQuantity = riskBudget / lossPerUnit`, and `notionalExposure = entry * rawQuantity` using checked `decimal` arithmetic. It performs no venue tick/step rounding and rejects overflow or a result that becomes zero at decimal precision. Planning calls this implementation through `IRiskMathCalculator`; `SimulatedRiskSimulator` receives the same contract and rejects any returned result whose complete input snapshot or version differs from the plan. Long/short numerical vectors, exact decimal JSON round-trip, invalid currencies/stops, overflow/zero-precision cases, and end-to-end replay/planning parity are covered by focused tests. A matching risk result proves calculation consistency only; it does not prove profitability or replace the later Risk Engine.

P02-M02-T003 adds a separate `IFillSimulator` research boundary. `FillSimulationInput` is a hypothetical request identified by a research ID, with direction, positive quantity, optional reference price, request time, assumptions, and an immutable `CausalMarketEvent` snapshot. Snapshot events must belong to the input instrument and be available no later than the request time, so the input cannot introduce lookahead. `FillSimulationAssumptions` stores non-empty descriptions for fees, slippage, latency, liquidity/queue treatment, partial fills, intrabar ordering, and data limitations. Unknown values are recorded in those descriptions; no numeric assumption defaults are supplied.

`FillSimulationResult` carries a `FillSimulationVersion`, a defensive full copy of the input, and only immutable `ResearchFill` observations. Research fills contain an identifier, positive quantity, price, fill time, and stable causal sequence; they do not contain `OrderIntent`, `RiskDecision`, risk-math, executor, or authorization state. The interface has only `Version` and `Simulate(FillSimulationInput)`. A fill simulator therefore reports research observations and cannot authorize or submit an order.

P02-M02-T002 and P02-M02-T003 add the passive contracts. P09-M01-T002 adds
`SimulatedRiskSimulator` in Backtesting. It injects `IRiskMathCalculator` and
uses the returned versioned quantity and protective stop without reimplementing
risk math. Entry is the open of the first bar opened after the decision; the
entry bar is not used for exits. Configured TP1/TP2 fractions, breakeven
activation, stop-first OHLCV ambiguity, stop-gap execution, time exits, fees,
and per-fill slippage are explicit in `SimulatedRiskPlan` and
`SimulatedFillAssumptions`. Unfilled remainder stays open at end of data.
When breakeven movement is enabled, its stop price is required explicitly;
there is no entry-price fallback.
The plan carries an explicit Core `Decision` link, and the fill assumptions
carry an explicit `FillSimulationVersion`; either missing required link blocks
simulation. Results contain only risk output, immutable simulated fills, costs,
and diagnostics; they expose no order, authorization, or execution adapter
state.
Fill-generation algorithms remain replaceable research components and do not
authorize orders or introduce trading defaults.

P09-M02-T002 makes that replacement seam executable. `SimulatedRiskSimulator`
keeps its original calculator-only constructor, and a second constructor accepts
an `IFillSimulator`; the default `OhlcvFillSimulator` is versioned as
`ohlcv-fill-v1`. The harness owns entry/stop/TP/time trigger selection and target
fractions. For each selected hypothetical fill it creates a fresh immutable
`FillSimulationInput` containing the plan direction, caller-supplied reference
price, explicit fill descriptions, and the replay prefix available at the bar's
close. A model may return one `ResearchFill` observation, but the harness rejects
mismatched model version/input, overfill, future timestamps, and stable sequences
outside that prefix. It then applies the plan's numeric fee/slippage assumptions
and maps the observation to the causal event reference. Replacing the model can
change prices or quantities while leaving the shared `RiskMathResult` unchanged.
The model surface contains only research fill observations; it has no order,
authorization, risk-decision, or execution adapter path.

The completed `BacktestReport` now retains the fill-model version next to the
versioned `RiskMathResult`, the full numeric/prose assumptions, simulated costs,
causal fill references, diagnostics, and any open remainder. The report
fingerprint includes both versions and the assumption snapshot. This gives
regression checks an auditable calculation/model identity without presenting a
research outcome as evidence of profitability or live execution quality.

P03-M02-T002 adds passive `PortfolioId`, `PortfolioLimits`, and
`PortfolioReservation` contracts for explicit caller-supplied monetary-risk,
notional-exposure, and position-slot capacity. A portfolio's monetary values
must use one currency; negative amounts and negative slot counts are rejected,
and no capacity default is supplied. Infrastructure persists registered
limits, existing order intents, and reservations together. It reconstructs
reservation totals from canonical JSON and uses checked decimal arithmetic in
one immediate serializable transaction, returning `Reserved`,
`AlreadyReserved`, or `InsufficientCapacity`. This is a durable capacity
boundary and does not calculate risk, make a risk decision, release capacity,
or handle fills and positions; those transitions remain future work.

P03-M02-T003 adds passive operational fill and reconciliation persistence
around the reservation boundary. `Fill` carries exact positive quantity and
price plus optional fee and the full causal timestamp/sequence tuple; it does
not calculate risk. Infrastructure checks the checked decimal aggregate of
persisted fill quantities against the caller-supplied OrderIntent quantity.
Position snapshots and reconciliation checkpoints preserve known, manual, and
unknown state. New observations invalidate acknowledgement, and retry remains
closed until the current revision is acknowledged with no unresolved markers.
The complete reservation remains counted through partial fills and is released
only after an acknowledged checkpoint confirms no open intent and a closed
associated position. No sizing formula, risk decision, or trading default is
introduced.

Decision Engine (Phase 8) determines `ALLOW`, `BLOCK`, or `SHADOW` from strategy evidence. Risk Engine (Phase 11) independently evaluates exposure, sizing, stops, portfolio limits, kill switches, and operational state. Strategy code must not bypass risk.

Phase 9 is intentionally earlier than the full Risk Engine: replay/backtest requires a deterministic simulation of risk, stop-loss, take-profit, partial-fill assumptions, and time exits. That simulation is a research harness, not live authorization.
