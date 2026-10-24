# Lane C evidence replay

`LaneCEvidenceBundle` in App is a versioned, immutable link across the capture
manifest/checkpoint, quality report, exclusion ledger, feature result, shadow
result, and passive Decision evidence. Its constructor validates each identity
and causal cursor before accepting the bundle, then verifies a SHA-256 identity
over the canonical serialized evidence. The builder applies the caller's
versioned shadow rule table and maps that result through the existing Lane C
Decision evidence adapter. It does not create an order, execution request, or
capture mutation.

`LaneCEvidenceReplayTests` uses synthetic fixed Binance snapshots, depth
updates, trades, recovery gaps, and conflict diagnostics. It persists and
reopens capture, report, and exclusion ledger data, then compares canonical
bundle identities, reason codes, and Decision evidence across repeated replay
and serialization. Causal prefixes verify that equal event timestamps remain
sequenced and delayed availability does not enter an earlier result. Recovered
depth remains usable while the historical gap stays visible; stale provider
state, conflicts, and exclusions retain explicit evidence. The numerical
fixture inputs are test-only and do not define production trading thresholds.
