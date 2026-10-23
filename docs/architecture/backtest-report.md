# Backtest regression report

P09-M01-T003 adds the `BacktestReport` boundary in `TradingBot.Backtesting`.
The report is a versioned, immutable-in-use snapshot of one completed
historical replay. It retains the canonical event references (event, receive,
availability, exchange, virtual, and stable-sequence fields), setup identity
references, lifecycle transition references, Decision/reason references,
versioned injected risk output, simulated fills, fees, slippage, diagnostics,
and any open remainder.

P09-M02 adds the injected `FillSimulationVersion` to the report payload beside
the complete `RiskPlan.FillAssumptions` snapshot. A completed simulation report
therefore names both the shared risk-math version (inside `RiskResult`) and the
fill-model version (`ohlcv-fill-v1` for the default model, or the caller's
replacement), while retaining numeric fee/slippage values and the prose
assumption descriptions. `BacktestRegression` includes these values in its
deterministic fingerprint, so changing a model or assumption is visible in the
regression result even when the event stream is unchanged.

`BacktestRegression.Compare` computes a lower-case SHA-256 fingerprint over
the canonical result while excluding the caller's `RunId`. Equal historical
events therefore compare equally across input permutations and Core JSON
round-trips. The full `CanonicalJson` includes the report format version and
run identity for audit/export. `BacktestReportJournal.WriteAtomic` writes a
UTF-8 temporary file beside the destination, flushes it, and replaces the
completed destination in one rename operation. It never creates orders,
authorization, or execution state.

The regression fixtures pass existing Core lifecycle transition and Decision
contracts through the report, preserving setup fingerprints, transition states,
decision outcomes, reasons, and input snapshot references without introducing a
separate BacktestStrategy abstraction.

Every report carries explicit OHLCV limits: bar data cannot reconstruct
intrabar sequence or prove actual fills, spread, queue position, Level 2 state,
or true point of control. Take-profit portions are management assumptions;
liquidity and exchange partial fills are not proven. These limitations describe
the evidence boundary and do not become hidden trading defaults. Deterministic
replay, risk/fill version links, and a stable report fingerprint establish
reproducibility of the research computation only; they do not establish
profitability, venue liquidity, or actual exchange execution quality.
