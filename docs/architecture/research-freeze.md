# Frozen research inputs

P09-M03-T001 introduces a BCL-only `FrozenResearchManifest` at the Backtesting
boundary. The manifest is created before an out-of-sample run and retains the
dataset identifier/version, bounded provenance, composition and exclusions,
the complete stream-scoped causal event set, configuration and selection
snapshots, risk/fill policy identifiers, and explicit assumptions. It contains
no result, promotion decision, or inferred threshold.

Event rows retain event time, receive time, availability time, optional exchange
time, stable sequence, stream, identity, and canonical Core payload JSON.
Canonical ordering uses stable sequence and then payload JSON/event identity so
permuted source collections produce the same event hash and manifest identity;
exact duplicate rows remain present in the frozen set. Configuration rows are
canonically ordered and hashed separately. Deserialization rejects unsupported
formats, unknown fields, non-canonical JSON, changed hashes, and a changed
identity.

`FrozenResearchManifest.RunReplay` validates the candidate stream and event
content hash before invoking the existing causal replay runner. A
`FrozenResearchManifestJournal` provides a write-once BCL record. A completed
`FrozenResearchEvaluationSnapshot` stores the manifest identity, report
identity, and the report's canonical JSON at capture time, so later caller
identity, validates that the report carries the same causal event identities
and payloads, and captures the report's canonical JSON at creation time, so
later caller changes to a report object cannot alter the captured evidence. These API
guarantees do not attest to external file immutability or real-world dataset
chronology; the fixtures in this milestone are synthetic.

`PredeclaredPromotionCriteria` is a second immutable record bound to the
manifest identity. It contains stable criterion IDs, questions, required
evidence descriptions, and explicitly unresolved blockers. Criterion and
evidence order is canonical; its SHA-256 identity includes the manifest
identity, so the same questions cannot be attached to another frozen input.
`PredeclaredPromotionCriteriaJournal` is write-once. `FrozenResearchEvaluationContext`
requires the matching criteria record and an empty unresolved-blocker list
before replay or evaluation snapshot capture; replacing a registered record or
using a foreign manifest is rejected. The API records readiness and evidence
links only; it does not compute a promotion decision or introduce a numeric
threshold.

P14-M02-T001 adds `FrozenOosEvaluationDefinition` and
`FrozenOosEvaluator` in Backtesting. The definition is created before replay
and binds the existing research and criteria identities to a multi-source
manifest, a SHA-256 of each typed replay payload plus its full causal ordering
entry, the frozen configuration hash, and a canonical set of model-version
declarations. It leaves all existing manifest formats and IDs unchanged. A
null model version remains null and blocks a resolved evaluation. A changed
research, criteria, multi-source manifest, replay payload, configuration, or
model version marks the prior evaluation `Invalidated` before the projection
callback runs; the report continues to identify the original frozen inputs.

The canonical `FrozenOosEvaluationReport` records the original identities,
per-source coverage/quality references and reasons, the supplied reconciliation
checkpoint, original exclusions, replay diagnostics, predeclared question
assessments and evidence links, blockers, and one causal trace entry per
delivered event. Exact duplicate inputs remain recorded as runner diagnostics,
even when established replay behavior delivers them only once. Each trace entry
preserves event, exchange, receive, and availability times and the source
sequence; an absent projection remains null. Missing quality evidence, incomplete/unknown quality,
unacknowledged or absent reconciliation, unknown required criteria, and
unresolved evidence remain explicit blockers. `Evaluated` means the evidence
assessment completed; it does not mean promoted or authorize execution. The
fixtures are synthetic and no real frozen OOS dataset is present, so they do
not establish strategy performance.

P14-M02-T002 adds the App-level `PromotionEvidenceBundle` and
`PromotionEvidenceSnapshot`. The builder binds the bundle to the frozen
definition and report identities, preserves the existing research, criteria,
multi-source, replay-input, configuration, and model-version identities, and
captures immutable canonical snapshots of supplied quality, reconciliation,
Lane C shadow, and separate research-simulation artifacts. A snapshot retains
the producer-native identity separately from its SHA-256 content hash and
copies causal fields, provenance, exclusions, unknown values, and blockers.
Unknown values remain null or explicitly listed; no numeric promotion
threshold or trading setting is introduced.

Each required evidence category is present in the bundle even when no artifact
was supplied. Operational shadow, local-paper, and exchange-test categories
remain missing in the current synthetic fixture set, while research shadow and
simulation snapshots carry reasons that they are not operational proof.
Quality and shadow snapshots validate their linked source/report identities;
simulation configuration and stream links are checked against the frozen
manifest. Canonical bundle restore rejects tampering. `ValidateAgainst`
compares current frozen context and snapshot identities, reports affected
categories as invalidated, and leaves the previous immutable bundle and its
blockers unchanged. The bundle records evidence for review; completed
P14-M02-T003 now preserves the immutable review decision and invalidation
evidence without granting execution authority.

P14-M02-T003 adds the App-level `PromotionReviewRecord` and
`PromotionReviewInvalidationRecord`. A review is bound to the canonical bundle
and separately retains the definition, OOS-report, and bundle identities,
review reasons, provenance, exclusions, every evidence reference/status and
causal field (including null values), blockers, and unknowns. `Blocked`,
`Rejected`, and `AcceptedForReviewGate` are review verdicts only; acceptance is
impossible with unresolved blockers/unknowns, Missing/Unknown/Invalidated
evidence, or synthetic provenance, and no verdict grants execution authority.
The caller must supply the verdict explicitly; the App surface does not
auto-accept a future clean bundle.

`PromotionReviewJournal` keeps review and invalidation records in separate
content-addressed directories. Canonical JSON is written through a flushed
CreateNew temporary file and atomic move without overwrite, then revalidated by
filename and SHA-256 identity on reopen. Identical retries are idempotent;
partial temporary files are ignored and tampered rows fail closed. An
invalidation links to the original decision and records changed context,
evidence, categories, and reasons. It never mutates or reactivates the old
decision, even if sources later return to their prior values; a new review
attempt identity is required. Full bundle identity changes, including
provenance-only changes, are invalidation evidence; a changed canonical
artifact with the same native evidence ID still invalidates its affected
category. Current fixtures remain synthetic and therefore do not supply real
operational shadow, local-paper, exchange-test, performance, or reconciliation
proof.

## P15-M03-T015 signal-study freeze (SR-05.1)

`SignalStudyManifest` is a separate BCL-only signal-research contract; it does
not alter the existing `FrozenResearchManifest` or promotion APIs. Its
canonical identity binds hashes for the dataset, point-in-time universe,
configuration, formula, seed history, labels, horizons, cost model, latency
assumptions, exclusions, and one untouched out-of-sample dataset. It also binds
the primary estimand, null hypothesis, precision, rejection and multiplicity
policies, the chronological split plan, and the complete declared trial
registry. Any unresolved required declaration remains `Unknown`; no threshold
or default is inferred.

Split intervals are UTC and half-open. Each walk-forward fold declares training,
optional development, and validation windows, followed by one OOS window.
Every label records its information cut, dependency interval, availability
time, assignment, and any explicit purge reason. A non-purged label may not
overlap or become available after a later development, validation, or OOS
boundary. Forward embargo intervals are derived from included validation-label
availability. The plan records no evaluation result.

The canonical manifest journal is write-once; evidence must present the exact
manifest identity, so changed inputs require a new identity and invalidate
prior evidence. A separate atomic first-view journal is keyed by the OOS
dataset hash: repeated access by the same study is idempotent, but another
study identity cannot reclassify that dataset as untouched. Unknown manifests
cannot mark OOS data viewed. These contracts do not execute evaluation, prove
strategy performance, grant promotion or execution authority, or represent
fills. Tests use deterministic synthetic fixtures; no real OOS performance
evidence is claimed.

## P15-M03-T016 independent outcome observation (SR-05.2)

`SignalOutcomeObserver` in Backtesting accepts a committed signal/evidence pair,
an explicit decision or named-channel publication boundary, an absolute horizon,
optional expiry/invalidation cut, optional directional levels, and complete
subsequent UTC OHLC bars with availability times. The channel boundary must be
supplied from delivery evidence; absent publication remains UnknownPublication.
No historic delivery timestamp is inferred. Any bar containing the boundary
strictly inside its interval is excluded in full. Published-time signed returns
and extrema use the open of the first complete subsequent bar, not the earlier
decision reference; this is a next-eligible-open observation convention, not
an observed fill or reaction-latency model. Decision-time returns use the signal
reference price. Horizon/expiry cuts inside a bar censor that incomplete
interval; gaps, unavailable bars and missing paths remain explicit.

Single-bar target/invalidation conflicts without a complete detailed path
produce Ambiguous with `[bar open, bar close]` first-passage bounds, never a
chosen favorable ordering. A detailed full-bar path must retain both extrema
and ordered endpoints before it can resolve first passage. Ties have signed
return zero, not a hit. Partial-path MAE/MFE remain identified as censored,
while horizon signed return is null unless the path is complete. An isolated
atomic outcome ledger stores the canonical hash and separate identity material;
exact restart retries deduplicate, changed path content under the same identity
conflicts. The signal and delivery journals are not modified. These outcomes
are research observations, not fills, P/L, OOS evidence or order authority.

## P15-M03-T017 opportunity report (SR-05.3)

`signal-only opportunity-report --capture <coverage-report.json> --ledger
<native-bars.sqlite> --manifest <signal-study.json> [--journal
<signal-journal.sqlite> --symbol <symbol> --profile <profile.json>
--declaration <predeclared-run.json> --freeze <frozen-journal.json>
[--outcomes <directory>]]` reads existing files only. The dataset, PIT universe
and configuration hashes must match the frozen signal-study manifest. With a
journal, the declaration and freeze bind the exact capture report, input ledger,
signal profile, scenario policies, manifest and journal hashes; an uncheckpointed
SQLite WAL or changed input is rejected. These hashes establish content
identity, not independent proof of when a file was created.

Every expected eligible 15m symbol/time interval stays in the table, with
historical modeled-REST and prospective recorded-WS availability distinguished.
Missing capture or absent signal journal is Unknown, not NoSignal; only an
explicit committed `no-confirmed-setup` diagnostic proves NoSignal. Other
committed diagnostics retain blocked/unknown reasons. Signal IDs are unique
within the common universe, multiple families in one interval retain overlap,
and unmatched signal/outcome/public-input identities remain visible. Committed
diagnostics must match their accepted bar revision; signals must match the
profile and accepted public-input provenance; outcomes must match their signal
and evidence hashes. Conflicts fail closed.


Denominators and exclusions separate expected/covered, no-signal, blocked,
unknown, signaled cells, overlap, and complete decision/published outcomes.
Frequency per covered interval is unknown when any covered decision is Unknown;
the denominator remains visible and `signalFrequencyUnknownReason` explains the
missing value. A covered bar without a decision is not a zero-signal decision.
Directional positive/negative/tie counts, signed-return mean, publication
delays, MAE/MFE and first-passage bounds are observations only; null metrics
carry reasons. MA-only control requires frozen kind/period/seed/warmup, and the
price-volume control requires frozen source/formula/normalization; neither is
evaluated by this report. Hypothetical net expectancy stays null without
usable fee/spread/slippage, latency, funding and non-fill inputs; no actual
profitability or OOS comparison is claimed.

The T008 BTCUSDT capture has 99/99 accepted expected 15m intervals across
modeled historical and recorded prospective windows. An authorized exploratory
offline replay produced three committed Unknown diagnostics, no signals or
outbox entries, and no publication or outcome records. The other 96 historical
REST intervals were warmup, not retrospective decisions. Two CLI reports over
the frozen journal and manifest produced identical bytes; all 99 decisions
remain Unknown, with null frequency and hypothetical net expectancy. This is
not evidence of signal readiness or performance.

## T017 exploratory BTCUSDT replay profile

The user authorized a **new** research-only profile after the T008 capture and
before replay. It was not predeclared when those market data were collected, is
not an OOS study, and is not evidence of predictive power. The write-once
`predeclared-run.json`, canonical `profile.json` and `scenario-policies.json`
are in `artifacts/P15-M03-T017/research-t008-c0f7b3bc869f459583f7df9975e09488/`.
Profile SHA-256:
`bff10263128caf7775250527cdf4a7902f5b1190293852c9fe029b367b1bc3a4`;
scenario-policy SHA-256:
`d7221f5953fa5e39641874a278ec00ed9687049feb201b14f58a022ba03f81f6`.

The fixed PIT universe is BTCUSDT; setup/context are native 15m/1h from the
T008 recorded history start. EMA20 and ATR14 are historical examples in
canonical source §16, not validated defaults; HTF 21 and two confirmations are
research seeds in §8. The HTF fast period 9, 2/2 pivots, RMA ATR, unit channel
and level multipliers, 21/23-bar warmups, and two-interval freshness limits are
explicit software-replay choices, not optimised or validated strategy settings.
The Boundary minimum full-channel width is 0.10 price units, derived from the
BTCUSDT `PRICE_FILTER.tickSize` in the frozen T008 point-in-time snapshot. The
24-hour expiry, zero-delay rearm requiring a new anchor, and explicit/opposite
invalidation are research-only policy choices. Historical REST availability
remains modeled; only recorded closed WebSocket 15m receipts trigger journal
decisions. No signal, publication, outcome, fill or performance result is
assumed by this declaration.

### Extended public capture and the initial v2 blocker

An explicitly authorized extension predeclared the same numerical profile and
policies before capture; only the profile version and recorded history
provenance changed. Capture
`20261112T184159Z-107922243e1b4a54b7b867df9c41850f` covered 338/338 historical
15m and 84/84 historical 1h intervals with modeled REST availability, plus
2/2 prospective 15m and 1/1 prospective 1h intervals with recorded WebSocket
availability. A third 15m close was accepted immediately before its nominal
close time, so it is retained as an unmatched public input rather than added
to the declared coverage denominator.

The manifest was frozen before offline replay. The replay checked every input
against the original event/close/availability/sequence cut and produced three
committed Unknown diagnostics, no signals and no outbox entries; reopening
preserved journal bytes and semantic state with zero network attempts. Two
CLI runs over the frozen inputs produced identical canonical JSON SHA-256
`77bde54d5edcd25065e4558899124b1dbf857d14518df6c45588fdcbb7a5cd5c`:
340 expected/covered, 340 Unknown, 0 NoSignal, 340 exclusions, and null
frequency and hypothetical net expectancy.

At the last trigger, 341 setup and 85 context bars were available, but both
setup structure and HTF remained `UnknownWarmup`. One early setup pivot lacked
confirmation-time ATR even though 95 later setup pivots had ATR.
`CalculateStructuralLevels` accumulates `atrWarmup` for any such earlier pivot
and does not clear it when later levels appear. The contract also allows
levels to reverse roles on breaks, so discarding the early unknown level or
inventing a reset could produce a false Ready/NoSignal. At that point T017
remained blocked pending a causally justified recovery and regression proof.

An investigated sufficient reset would replace both structural slots with
ATR-backed pivots at the same causal cut and restart HTF confirmation; this
does not require deciding what an unknown earlier level did. A read-only scan
of the captured closed-bar windows found two 15m double pivots but no 1h double
pivot after the nominal ATR14 seed; individual ATR availability for the 15m
pivots was not checked. This condition therefore cannot unblock the observed
HTF decisions. More general recovery must conservatively account
for uncertain breaks and role reversals; changing the fixed
`price-context-v2` behavior without versioned replay semantics would alter
the meaning of existing frozen evidence.

### Versioned recovery and T017 software acceptance

The explicit v3 recovery tracks uncertain slots conservatively; on the
captured data it recovered setup structure but not HTF. V4 retains a set of
correlated possible level pairs, branching on a missing buffer only when a
break is possible. It becomes Ready only when every branch converges to the
same fully known pair, then requires fresh HTF confirmation. V2 and v3 reopen
without changing their frozen journal bytes. No past journal was rewritten.

The v4 profile and formula were developed after inspecting earlier results;
they were frozen before this replay, not before market-data capture. This is
exploratory software evidence, not a predeclared OOS study. The frozen v4
manifest identity is
`46c8e103de18ae9bbeed4c2ba908ea0c52ea2bfa081e84ca9d4e6afb95e35fa8`.
Offline replay of three recorded triggers produced one genuine committed
signal and output-only outbox row and two committed NoSignal diagnostics;
there was no delivery, publication, outcome or network request. The third
trigger is outside the declared coverage window.

Two CLI runs over the frozen journal and manifest produced identical canonical
JSON SHA-256
`cb74a791e3c350ef2085554e41691e57bb5da09d9ba69a4971efe41c5100b6f2`:
340 expected/covered, 338 Unknown warmup cells, one NoSignal, one signaled
cell, 338 exclusions and one unmatched public input. Frequency and hypothetical
net expectancy remain null. The signal-study manifest remains Unknown for
undeclared research criteria; T017 acceptance is software/reporting only and
does not establish performance, promotion or execution authority.
