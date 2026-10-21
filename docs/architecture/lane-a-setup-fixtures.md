# Lane A setup fixture suite

`LaneASetupFixtureTests` is the test-only cross-family fixture suite for the two
V1 Lane A setup families, Boundary Rejection and Center Retest. It does not add
production contracts, persistence, lifecycle, risk, or execution behavior. The
suite uses the real evaluators and `CoreJsonSerializer`; only the synthetic
price-context/quality snapshots are test fixtures.

## Hand-specified matrix

The matrix uses two shared snapshot bases:

- LONG: center 100, prior center 99, lower 95, upper 105, width 10, LocalTrend
  Bull, HTF Bull, Boundary minimum width 10.
- SHORT: center 99, prior center 100, lower 94, upper 104, width 10, LocalTrend
  Bear, HTF Bear, Boundary minimum width 10.

Quality evidence contains exactly one ready Channel, LocalTrend, and HtfContext
record plus an unready VWAP record, so `FeatureQualityEvaluation.IsReady` is
false while the Lane A three-feature gate passes.

| Case | Signal bar O/H/L/C | Boundary | Center |
|---|---|---|---|
| `both-long-ready` | 100/102/94/101 | Ready Long | Ready Long |
| `center-long-only` | 100/102/96/101 | NoSetup Long | Ready Long |
| `boundary-long-only` | 94/98/94/96 | Ready Long | Invalidated Long |
| `both-long-invalidated` | 100/102/94/94 | Invalidated Long | Invalidated Long |
| `center-long-invalidated-only` | 99/101/94/99 | Ready Long | Invalidated Long |
| `center-long-no-width` | 100/102/94/101, width 0 | NoSetup Long | Ready Long |
| `both-long-unknown-quality` | both-ready bar, no Channel quality | UnknownQuality | UnknownQuality |
| `both-short-ready` | 99/106/98/98 | Ready Short | Ready Short |
| `center-short-only` | 99/99/98/98 | NoSetup Short | Ready Short |
| `boundary-short-only` | 104/106/103/103 | Ready Short | Invalidated Short |
| `both-short-invalidated` | 104/106/104/105 | Invalidated Short | Invalidated Short |
| `center-short-invalidated-only` | 100/101/98/100 | NoSetup Short | Invalidated Short |
| `center-short-no-width` | 99/106/98/98, width 0 | NoSetup Short | Ready Short |
| `both-short-unknown-quality` | both-ready bar, no Channel quality | UnknownQuality | UnknownQuality |

The `boundary-*-only` cases are not contradictory: the bar is a valid Boundary
Rejection, but it closes through the Center, so Center Retest records
`Invalidated` for that direction. They demonstrate that the families use
independent rules on the same causal snapshot. The no-width cases prove that
Center Retest consults only `PriceContextChannel.Center`; Boundary Rejection
still applies its configured minimum width.

## Evidence invariants

For both families the suite asserts:

- both directions are always present and `Observations.Count == 2`;
- `IsReady` matches the observation states;
- `Ready` implies all conditions, a confirmed bar, and ready quality (plus the
  minimum-width condition for Boundary Rejection);
- `Invalidated` implies matching context/trend, a center/band touch, a strict
  close-through, a confirmed bar, and ready quality, with the holding condition
  false;
- `NoSetup` is not all-conditions-passed;
- unknown states are not ready and retain stable reasons;
- reasons are ordinal-sorted and deduplicated;
- the signal bar is the exact `PriceContext.LocalTrend.CurrentBar`;
- Boundary evidence retains the configured minimum width; Center evidence has
  no width field;
- the complete `PriceContext`, `Quality`, `Configuration`, and cursor are
  retained.

## Serialized replay

For every matrix case the suite serializes and deserializes both
`PriceContextFeatureResult` and `FeatureQualityEvaluation`, re-evaluates both
families, and compares the results with the direct evaluation. It also
round-trips both setup result types and re-evaluates from the restored
`PriceContext`/`Quality`/`Configuration`. Unknown JSON members are rejected.
This proves that Lane A evidence is serializable and replayable without
recreating the original calculator objects.

## Cursor-prefix replay

A real shared history is replayed at `Time(1)`, `Time(2)`, `Time(3)`,
`Time(4)`, `Time(5)`, `Time(10)`, `Time(15)`, `Time(20)`, `Time(25)`, and
`Time(30)`. At each cursor the setup/HTF prefixes are calculated, both families
are evaluated, and the price-context/quality snapshot is serialized,
deserialized, and evaluated again. Every cursor must reproduce identical
evidence. The final cursor produces Ready Long for both families; earlier
cursors remain warmup/unknown/no-setup.

## No-lookahead and determinism

For the same cursors, the suite compares the full-history calculation with the
prefix calculation. Historical channel, trend, signal-bar, and confirmed-HTF
values must be identical; the full history with future bars must not become
Ready at an earlier cursor, and its future/incomplete reasons remain visible.
Only the final cursor has no future bars and repeats the canonical Ready result.

The suite also reverses the setup/HTF input order and evaluates under `fr-FR`
and `en-US`; evidence must remain identical. The final guard checks that
`SetupType` exposes only Boundary Rejection and Center Retest and that the Lane A
result/observation types expose no `Decision`, `RiskDecision`, `OrderIntent`,
`Position`, `Setup`, or `SetupState` properties.

## Boundaries

This suite is test-only. It adds no versioned setup evidence schema (P07-M02),
persistence, lifecycle identity, decision, risk, order, or execution behavior.
If a fixture fails, that is a real uncovered defect in the owning family and is
handled as a focused RED/GREEN; the suite does not authorize an order by itself.
